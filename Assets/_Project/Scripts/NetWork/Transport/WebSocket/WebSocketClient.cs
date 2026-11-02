#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using KeepAlive.Core;
namespace Network.Transport
{
    public class WebSocketClient : IClientChannel
    {
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private ChannelState _state = ChannelState.Disconnected;

        // WebSocket 的 SendAsync 不允许并发调用，必须加锁保护
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private string _token;

        public ChannelState State => _state;

        public bool HasMessageBoundary => true;

        public event Action OnConnected;
        public event Action<ChannelError, string> OnConnectFailed;
        public event Action<byte[]> OnBytesReceived;
        public event Action<ChannelError> OnDisconnected;

        // 允许外部注入 Token，用于 WebSocket 握手鉴权
        public WebSocketClient(string token = null)
        {
            _token = token;
        }

        public async void Connect(string host, int port)
        {
            if (_state == ChannelState.Connecting || _state == ChannelState.Connected) return;

            _state = ChannelState.Connecting;
            _cts = new CancellationTokenSource();

            try
            {
                _ws = new ClientWebSocket();
                if (!string.IsNullOrEmpty(_token))
                {
                    // WebSocket 握手本质是 HTTP 请求，可以带 Header
                    _ws.Options.SetRequestHeader("Authorization", "Bearer " + _token);
                }

                // 默认使用 ws://，如果需要 wss:// 请在上层传入时约定好，这里简化处理
                string url = $"ws://{host}:{port}";
                Uri uri = new Uri(url);
                //Debug.Log("WebSocket开始异步连接");
                await _ws.ConnectAsync(uri, _cts.Token);
                //Debug.Log("WebSocket开始异步连接结果返回");
                if (_ws.State == WebSocketState.Open)
                {
                   
                    _state = ChannelState.Connected;
                    MainThreadDispatcher.Enqueue(() => OnConnected?.Invoke());

                    // 启动后台接收循环
                    _ = ReceiveLoop();
                }
                else
                {
                    HandleConnectFailed(ChannelError.Unknown, $"WebSocket状态异常: {_ws.State}");
                }
            }
            catch (Exception e)
            {
                HandleConnectFailed(ChannelErrorMapper.FromException(e), e.Message);
            }
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[4096];
            var messageBuffer = new System.IO.MemoryStream();

            try
            {
                while (_ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // 收到对方的 Close 帧，必须回应 CloseOutputAsync，不能死等
                        await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ok", CancellationToken.None);
                        HandleDisconnect(ChannelError.NormalClose);
                        return;
                    }

                    // 写入缓存（虽然 WebSocket 有边界，但大包可能分多帧，EndOfMessage 才是完整包）
                    messageBuffer.Write(buffer, 0, result.Count);

                    if (result.EndOfMessage)
                    {
                        byte[] data = messageBuffer.ToArray();
                        messageBuffer.SetLength(0); // 清空复用

                        // 切回主线程抛给 NetworkService
                        MainThreadDispatcher.Enqueue(() => OnBytesReceived?.Invoke(data));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 主动 Close 导致的取消，忽略
            }
            catch (Exception e)
            {
                if (_state == ChannelState.Connected)
                {
                    HandleDisconnect(ChannelErrorMapper.FromException(e));
                }
            }
        }

        public void Send(byte[] data)
        {
            if (_state != ChannelState.Connected || data == null || data.Length == 0) return;
            _ = SendAsyncInternal(data);
        }

        private async Task SendAsyncInternal(byte[] data)
        {
            await _sendLock.WaitAsync();
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    // WebSocket 发送二进制帧，endOfMessage: true 表示这是一条完整消息
                    await _ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, _cts.Token);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WebSocketClient] 发送异常: {e.Message}");
                HandleDisconnect(ChannelErrorMapper.FromException(e));
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // WebSocket 底层有 IO 线程，不需要应用层驱动
        public void Update() { }

        public void Close()
        {
            if (_state == ChannelState.Disconnected) return;

            _state = ChannelState.Disconnected;
            _cts?.Cancel();

            if (_ws != null)
            {
                try
                {
                    if (_ws.State == WebSocketState.Open)
                        _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client close", CancellationToken.None).Wait(500);
                    _ws.Dispose();
                }
                catch { }
                _ws = null;
            }
            _cts?.Dispose();
            _cts = null;
        }

        public void ForceDisconnect(ChannelError error)
        {
            if (_state == ChannelState.Disconnected) return;
            HandleDisconnect(error);
        }

        private void HandleConnectFailed(ChannelError error, string reason)
        {
            if (_state == ChannelState.Disconnected) return;
            _state = ChannelState.Disconnected;
            Close();
            MainThreadDispatcher.Enqueue(() => OnConnectFailed?.Invoke(error, reason));
        }

        private void HandleDisconnect(ChannelError error)
        {
            if (_state == ChannelState.Disconnected) return;
            _state = ChannelState.Disconnected;
            Close();
            MainThreadDispatcher.Enqueue(() => OnDisconnected?.Invoke(error));
        }
    }
}
#endif