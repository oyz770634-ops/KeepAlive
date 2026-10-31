using Network.Transport;
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Unity.VisualScripting;
using Unity.VisualScripting.FullSerializer;
using UnityEngine;
namespace Network.Transport
{
    public class TcpConnection
    {
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private CancellationTokenSource _cts;
        private readonly ChannelConfig _config;
        private bool _isClosed;
        private ChannelState _state = ChannelState.Connected;
        /// <summary>
        /// 时机：TcpConnection.ReceiveLoop 的 ReadAsync 读到字节，拷贝成新数组，经 TcpConnection.OnBytesReceived 转发，再经 TcpNetClient 转发，最终在主线程触发。
        /// </summary>
        public event Action<byte[]> OnBytesReceived;
        /// <summary>
        /// 时机：TcpConnection.ReceiveLoop 中 ReadAsync 返回 0（对端正常关闭）或抛异常（异常断开），触发 TcpConnection.OnDisconnected，转发到 TcpNetClient。触发前 _isConnected 已置 false。
        /// </summary>
        public event Action<ChannelError> OnDisconnected;
        public ChannelState State => _state;
        public TcpConnection(TcpClient tcpClient, ChannelConfig config = null)
        {
            _tcpClient = tcpClient;
            _config = config ?? new ChannelConfig();
            _tcpClient.NoDelay = _config.NoDelay;
            if (_config.ReceiveBufferSize > 0) _tcpClient.ReceiveBufferSize = _config.ReceiveBufferSize;
            if (_config.SendBufferSize > 0) _tcpClient.SendBufferSize = _config.SendBufferSize;
            _stream = tcpClient.GetStream();
            _cts = new CancellationTokenSource();
            _state = ChannelState.Connected;
        }

        /// <summary>
        /// TcpConnection 对外暴露的“启动接收循环”的入口
        /// </summary>
        public void StartReceive()
        {
            //_ 在 C# 里叫 discard（弃元），表示“这个值我不要，直接丢掉”。
            _ = ReceiveLoop();
        }
        /// <summary>
        /// 同步发送数据
        /// </summary>
        /// <param name="data"></param>
        public void Send(byte[] data)
        {
            if (_state != ChannelState.Connected)
            {
                Debug.LogWarning($"[TcpConnection] 状态 {_state}，发送被忽略");
                return;
            }
            if (_isClosed)
            {
                Debug.LogWarning("[TcpConnection] 连接已关闭，发送被忽略");
                return;
            }

            if (data == null || data.Length == 0) return;

            try
            {
                // NetworkStream.Write 内部已经循环发送直到写完或抛异常，不会部分写入
                _stream.Write(data, 0, data.Length);
            }
            catch (Exception e)
            {
                var error = ChannelErrorMapper.FromException(e);
                Debug.LogWarning($"[TcpConnection] 发送异常：{error}，{e.Message}");
                MainThreadDispatcher.Enqueue(() => HandleDisconnected(error));
            }
        }
        /// <summary>
        /// 异步接收数据循环,可取消
        /// </summary>
        /// <returns></returns>
        private async Task ReceiveLoop()
        {
            var buffer = new byte[_config.ReceiveBufferLength];

            try
            {
                while (!_cts.Token.IsCancellationRequested)//循环前先判断是否被外部取消
                {
                    int n = await _stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);

                    if (n == 0)
                    {
                        // 对端正常关闭（发了 FIN）
                        MainThreadDispatcher.Enqueue(() => HandleDisconnected(ChannelError.NormalClose));
                        return;
                    }

                    byte[] data = new byte[n];
                    Array.Copy(buffer, data, n);

                    MainThreadDispatcher.Enqueue(() => OnBytesReceived?.Invoke(data));
                }
            }
            catch (OperationCanceledException)
            {
                // 主动 Close 导致的取消，不回调断开
            }
            catch (Exception e)
            {

                var error = ChannelErrorMapper.FromException(e);
                Debug.LogWarning($"[TcpConnection] 接收异常：{e.Message}");
                MainThreadDispatcher.Enqueue(() => HandleDisconnected(error));
            }
        }

        /// <summary>
        /// 执行OnDisconnected订阅的句柄
        /// </summary>
        private void HandleDisconnected(ChannelError error)
        {
            if (_isClosed) return;
            _isClosed = true;
            _state = ChannelState.Disconnected;
            //由于接收循环导致的并发问题又一次触发了，现在的问题是看这里的我委托是否幂等
            OnDisconnected?.Invoke(error);
        }

        public void Close()
        {
            if (_isClosed) return;
            _isClosed = true;
            _state = ChannelState.Closing;
            _cts?.Cancel();//用于CancellationTokenSource取消异步执行

            try { _stream?.Close(); } catch { }
            try { _tcpClient?.Close(); } catch { }

            _cts?.Dispose();
            _cts = null;
            _state = ChannelState.Disconnected;

            /*
           _cts.Cancel()
           这是关键的第一步。ReceiveLoop 正阻塞在 await _stream.ReadAsync(..., _cts.Token) 上。
           Cancel 让 ReadAsync 抛 OperationCanceledException，循环退出。
           如果不先 Cancel 就直接关流，ReceiveLoop 可能还在读，关流会让它抛未处理异常。

           _stream?.Close()
           关闭 NetworkStream。
           TcpClient.GetStream() 返回的流 ownsSocket = true，所以关闭流会连带关闭底层 Socket。
           这一步释放内核接收/发送缓冲区，发送 FIN。

           _tcpClient?.Close()
           关闭 TcpClient。
           因为上一步 _stream.Close() 已经关了底层 Socket，这一步是冗余但安全的（重复关闭不报错）。
           保留它是为了防御：如果 _stream 为 null 或 Close 抛异常，这一步仍能兜底关闭连接。

           try-catch 包住两次 Close
           Close 可能抛 ObjectDisposedException（已关闭）或 SocketException（连接已断）。
           吞掉异常，保证 Close 本身不抛，调用方不用再包 try-catch。

           _cts.Dispose() + 置空
           释放 CancellationTokenSource 的非托管资源。置空防止后续误用已释放的对象。

           顺序为什么是 Cancel → Close stream → Close client → Dispose cts：
           先让接收循环退出，再关流和连接，最后释放取消令牌。反过来的话，关流时接收循环还在跑，可能抛未处理异常。
           */
        }
    }
}