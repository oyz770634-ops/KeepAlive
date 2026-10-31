using Network.Transport;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
namespace Network.Transport
{

    public class TcpNetClient : IClientChannel
    {
        private TcpClient _tcpClient;//C#的TcpClient
        private TcpConnection _connection;
        private readonly ChannelConfig _config;
        private ChannelState _state = ChannelState.Disconnected;
        private CancellationTokenSource _cts;
        ////状态机 已连接 与 正在连接
        //private bool _isConnecting;
        //private bool _isConnected;

        /// <summary>
        ///TcpNetClient订阅 
        ///时机：ConnectAsync 成功，TcpConnection 已创建、事件已订阅、接收循环已启动。此时连接完全就绪，外部可以立即 Send。
        ///参数：无
        ///典型用途：隐藏“连接中”UI、启用发送按钮、开始心跳定时器、触发登录消息。
        /// </summary>
        public event Action OnConnected;
        /// <summary>
        /// TcpNetClient订阅
        /// 时机：连接失败。三条路径都走它：超时、await connectTask 抛异常、Connected 为 false。由 HandleConnectFailed 触发，且防重复，只触发一次。
        /// 参数：reason 是失败原因字符串，比如 "连接超时（5000ms）"、"Connection refused"、"连接未建立"
        /// 典型用途：显示失败提示、弹重试按钮、记录日志、决定是否自动重连。
        /// 注意： 这个事件触发后，TcpNetClient 处于未连接状态，可以再次调 ConnectAsync。
        /// </summary>
        public event Action<ChannelError, string> OnConnectFailed;
        /// <summary>
        /// TcpNetClient订阅
        /// 时机：TcpConnection.ReceiveLoop 的 ReadAsync 读到字节，拷贝成新数组，经 TcpConnection.OnBytesReceived 转发，再经 TcpNetClient 转发，最终在主线程触发。
        ///参数：data 是本次读到的字节数组。注意它不保证是一条完整消息，可能是半条，也可能是多条粘在一起。消息边界是协议层的事，TcpNetClient 只管“收到字节”。
        ///典型用途：把字节喂给协议层做粘包拆包、反序列化、消息路由。
        /// </summary>
        public event Action<byte[]> OnBytesReceived;
        /// <summary>
        /// TcpNetClient订阅
        /// 时机：TcpConnection.ReceiveLoop 中 ReadAsync 返回 0（对端正常关闭）或抛异常（异常断开），触发 TcpConnection.OnDisconnected，转发到 TcpNetClient。触发前 _isConnected 已置 false。
        ///参数：无。
        ///典型用途：显示“连接断开”UI、停止心跳、触发重连、清理会话。
        /// </summary>
        public event Action<ChannelError> OnDisconnected;

        public ChannelState State => _state;

        public bool HasMessageBoundary => false;

        public TcpNetClient(ChannelConfig config = null)
        {
            _config = config ?? new ChannelConfig();
        }
        /// <summary>
        /// 带超时的连接
        /// </summary>
        /// <param name="host"></param>
        /// <param name="port"></param>
        /// <param name="timeoutMs"></param>
        public async void Connect(string host, int port)
        {
            //if (_isConnecting || _isConnected)
            //{
            //    Debug.LogWarning("[TcpClient] 已在连接或已连接，重复 Connect 被忽略");
            //    return;
            //}
            //_isConnecting = true;
            if (_state == ChannelState.Connecting || _state == ChannelState.Connected)
            {
                Debug.LogWarning($"[TcpNetClient] 状态 {_state}，重复 Connect 被忽略");
                return;
            }
            Close();
            _state = ChannelState.Connecting;
            _cts = new CancellationTokenSource();//取消功能 超时取消
            int timeout = _config.ConnectTimeoutMs;

            try
            {
                _tcpClient = new TcpClient();
                _tcpClient.NoDelay = _config.NoDelay;

                var connectTask = _tcpClient.ConnectAsync(host, port);
                var timeoutTask = Task.Delay(timeout);

                var completed = await Task.WhenAny(connectTask, timeoutTask);//返回传入的任务


                if (completed == timeoutTask)
                {
                    // 超时
                    _tcpClient.Close();
                    //HandleConnectFailed($"连接超时（{timeout}ms）");
                    HandleConnectFailed(ChannelError.TimedOut, $"连接超时（{timeout}ms）");
                    return;
                }

                //首先这里的问题是连接会有的几种情况，首先，要么连接报错，要么连接没报错
                //没报错的情况下要么连接成功，要么连接失败

                // connectTask 抛异常会在这里传播 直接截断向外抛异常
                await connectTask;

                if (!_tcpClient.Connected)
                {
                    //HandleConnectFailed((ChannelError.Unknown,"连接未建立");
                    HandleConnectFailed(ChannelError.Unknown, "连接未建立");
                    return;
                }
                //_isConnecting = false;
                //_isConnected = true;
                _state = ChannelState.Connected;

                //连接成功后，把底层 TcpClient 包装成 TcpConnection。
                //TcpConnection 构造函数内部会 GetStream() 拿到 NetworkStream，创建自己的 CancellationTokenSource。
                //从此以后，收发都走 _connection，不再直接操作 _tcpClient。
                //连接成功才进行包装，此时connection拿到的是可用的连接
                _connection = new TcpConnection(_tcpClient, _config);

                _connection.OnBytesReceived += data =>
                {
                    MainThreadDispatcher.Enqueue(() => OnBytesReceived?.Invoke(data));
                };

                _connection.OnDisconnected += error =>
                {
                    if (_state == ChannelState.Disconnected) return;
                    //_isConnected = false;
                    if (_state == ChannelState.Connected) _state = ChannelState.Disconnected;
                    MainThreadDispatcher.Enqueue(() => OnDisconnected?.Invoke(error));
                };

                _connection.StartReceive();

                MainThreadDispatcher.Enqueue(() => OnConnected?.Invoke());
            }
            catch (Exception e)
            {
                //HandleConnectFailed(e.Message);
                var error = ChannelErrorMapper.FromException(e);
                HandleConnectFailed(error, e.Message);
            }
        }
        /// <summary>
        /// HandleConnectFailed 是连接失败的统一收口方法。
        /// 所有失败路径——超时、连接异常、Connected 为 false——最终都走到这里。
        /// 它做四件事：防重复触发、清理状态、释放资源、通知上层。
        /// </summary>
        /// <param name="reason"></param>
        private void HandleConnectFailed(ChannelError error, string reason)
        {
            //if (!_isConnecting && !_isConnected) return;

            //_isConnecting = false;
            //_isConnected = false;
            if (_state == ChannelState.Disconnected) return;

            _state = ChannelState.Disconnected;


            try { _tcpClient?.Close(); } catch { }
            _tcpClient = null;
            _connection = null;

            Debug.LogWarning($"[TcpNetClient] 连接失败：{error}，{reason}");
            MainThreadDispatcher.Enqueue(() => OnConnectFailed?.Invoke(error, reason));
        }
        /// <summary>
        /// 检查连接状态：_isConnected 和 _connection 是否有效。未连接时拒绝发送，打警告。
        ///转发：通过检查后，转发给 _connection.Send。
        ///对外接口：上层只调 TcpNetClient.Send，不需要知道 TcpConnection 的存在。
        /// </summary>
        /// <param name="data"></param>
        public void Send(byte[] data)
        {
            if (_state != ChannelState.Connected || _connection == null)
            {
                Debug.LogWarning("[TcpClient] 未连接，发送被忽略");
                return;
            }
            _connection.Send(data);
        }


        public void Close()
        {
            //_isConnected = false;
            //_isConnecting = false;
            _state = ChannelState.Disconnected;

            _connection?.Close();
            _connection = null;

            try { _tcpClient?.Close(); } catch { }
            _tcpClient = null;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            /*
            _connection?.Close();
            调用 TcpConnection.Close，执行上面那套优雅关闭：Cancel 接收循环、关流、关底层连接、释放 cts。这一步已经把底层 Socket 关了。
            */

        }

        public void Update() { }

        //public void Connect(string host, int port)
        //{
        //    throw new NotImplementedException();
        //}
        /// <summary>
        /// 心跳超时调用
        /// </summary>
        /// <param name="error"></param>
        public void ForceDisconnect(ChannelError error)
        {
            if (_state == ChannelState.Disconnected) return;

            Debug.LogWarning($"[Channel] 被协议层强制断开，原因: {error}");
            _state = ChannelState.Disconnected;

            // 清理资源
            try { _connection?.Close(); } catch { }
            _connection = null;
            try { _tcpClient?.Close(); } catch { }
            _tcpClient = null;

            //执行一次断开异常
            OnDisconnected?.Invoke(error);
        }
    }
}
