using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Network.Transport
{
    public struct ConnectResult
    {
        public bool Success;
        public ChannelError Error;
        public string Reason;

        public static ConnectResult Ok() => new ConnectResult
        {
            Success = true,
            Error = ChannelError.None,
            Reason = ""
        };

        public static ConnectResult Fail(ChannelError error, string reason) => new ConnectResult
        {
            Success = false,
            Error = error,
            Reason = reason ?? ""
        };
    }
    /// <summary>
    /// 重连管理器
    /// </summary>
    public class ConnectionManager:IClientChannel
    {
        private readonly ChannelConfig _config;
        private readonly ReconnectPolicy _policy;
        private readonly Func<IClientChannel> _clientFactory; // 工厂

        //private TcpNetClient _client;//每次重连都会新建立一个client
        private IClientChannel _client;
        private string _host;
        private int _port;
        /// <summary>
        /// 重连次数首次连接为0，失败后重连为1
        /// </summary>
        private int _retryCount;
        private CancellationTokenSource _reconnectCts; //异步取消
        /// <summary>
        /// 意图标记：这次连接关闭，是用户主动要求的，还是网络被动断开的
        /// </summary>
        private bool _isManualClose; 
        /// <summary>
        /// 是否处于正在重连的阶段
        /// </summary>
        private bool _isReconnecting;

        /// <summary>
        /// 首次连接成功调用，给协议层用的。只要底层通了，就触发，用于重置心跳。
        /// </summary>
        public event Action OnConnected;
        /// <summary>
        /// 首次连接失败（之后仍会自动重连）调用
        /// </summary>
        public event Action<ChannelError, string> OnConnectFailed;
        /// <summary>
        /// 收到字节调用
        /// </summary>
        public event Action<byte[]> OnBytesReceived;
        /// <summary>
        /// 被动断开调用
        /// </summary>
        public event Action<ChannelError> OnDisconnected;
        /// <summary>
        /// 重连成功调用
        /// </summary>
        public event Action OnReconnected;
        /// <summary>
        /// 重连最终失败（用尽次数或不可重连的错误）调用
        /// </summary>
        public event Action OnReconnectFailed;
        /// <summary>
        /// 正在重连：参数是第几次、等待秒数调用
        /// </summary>
        public event Action<int, float> OnReconnecting;

        public ChannelState State => _client?.State ?? ChannelState.Disconnected;
        public bool IsReconnecting => _isReconnecting;

        public bool HasMessageBoundary => _client?.HasMessageBoundary ?? false;

        public ConnectionManager(ChannelConfig config ,Func<IClientChannel> clientFactory)
        {
            _config = config;
            _clientFactory = clientFactory;
            _policy = new ReconnectPolicy(_config);
        }

        public void Connect(string host, int port)
        {
            _host = host;
            _port = port;
            _isManualClose = false;
            _retryCount = 0;
            _reconnectCts = new CancellationTokenSource();
            _ = FirstConnect();
        }

        /// <summary>
        /// ---------- 首次连接 ----------
        /// </summary>
        /// <returns></returns>

        private async Task FirstConnect()
        {
            //bool success = await TryConnectOnce(out var error, out var reason);
            var result = await TryConnectOnce();
            if (_isManualClose) return;// 分支 0：连的过程中用户关了

            if (result.Success)// 分支 1：成功
            {
                MainThreadDispatcher.Enqueue(() => OnConnected?.Invoke());
                return;
            }

            MainThreadDispatcher.Enqueue(() => OnConnectFailed?.Invoke(result.Error, result.Reason));// 分支 2：失败

            if (!_policy.ShouldRetry(result.Error)) return;// 分支 2a：错误不可重试，到此为止

            _ = ReconnectLoop(); // 分支 2b：进入重连
        }

        /// <summary>
        /// ---------- 重连循环 ----------
        /// </summary>
        /// <returns></returns>

        private async Task ReconnectLoop()
        {
            if (_isReconnecting) return;
            _isReconnecting = true;// 置标记

            while (!_isManualClose && _retryCount < _config.MaxRetry)
            {
                _retryCount++;// 先加计数
                float delay = _policy.GetDelay(_retryCount);// 算退避秒数

                int attempt = _retryCount;
                MainThreadDispatcher.Enqueue(() => OnReconnecting?.Invoke(attempt, delay));// 通知 UI
                try
                {
                    //执行退避时间
                    await Task.Delay(TimeSpan.FromSeconds(delay), _reconnectCts.Token);// 退避等待
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (_isManualClose) return;// 等待期间用户关了

                //bool success = await TryConnectOnce(out var error, out _);
                var result = await TryConnectOnce();// 再试一次

                if (result.Success) // 成功
                {
                    _isReconnecting = false;
                    _retryCount = 0;  // 计数归零（下次断线重新从 1 开始）                 
                    MainThreadDispatcher.Enqueue(() => OnReconnected?.Invoke());
                    return;
                }

                if (!_policy.ShouldRetry(result.Error))// 错误不可重试
                {
                    _isReconnecting = false;
                    MainThreadDispatcher.Enqueue(() => OnReconnectFailed?.Invoke());
                    return;
                } // 否则继续 while，下一轮
            }

            if (!_isManualClose) // 循环正常结束 = 次数用尽
            {
                _isReconnecting = false;
                MainThreadDispatcher.Enqueue(() => OnReconnectFailed?.Invoke());
            }
        }

        /// <summary>
        /// ---------- 单次连接尝试 ----------
        /// </summary>
        /// <param name="error"></param>
        /// <param name="reason"></param>
        /// <returns></returns>

        private async Task<ConnectResult> TryConnectOnce()
        {
           
            _client?.Close();// ① 旧 client 丢弃
            //_client = new TcpNetClient(_config); // ② 全新 client
            _client = _clientFactory(); // 通过工厂创建

            var tcs = new TaskCompletionSource<ConnectResult>();//用于自己控制任务是否成功的控制器
            //var capturedError = ChannelError.Unknown;
            //var capturedReason = "";

            //_client.OnConnected += () => tcs.TrySetResult(ConnectResult.Ok());// ③ 成功信号
            //_client.OnConnectFailed += (error, reason) =>{ tcs.TrySetResult(ConnectResult.Fail(error, reason)); };// ③ 失败信号
            //_client.OnDisconnected += HandleDisconnected;// ④ 运行期断线
            //_client.OnBytesReceived += data => OnBytesReceived?.Invoke(data);// ⑤ 数据转发
            BindClientEvents(_client, tcs);
            _client.Connect(_host, _port);// ⑥ 启动连接（async void，不 await）
            return await tcs.Task;// ⑦ 等事件把 TCS 填上
        }
        // 【核心方法】事件桥接
        private void BindClientEvents(IClientChannel client, TaskCompletionSource<ConnectResult> tcs)
        {
            client.OnConnected += () => tcs.TrySetResult(ConnectResult.Ok());
            client.OnConnectFailed += (err, reason) => tcs.TrySetResult(ConnectResult.Fail(err, reason));

            // 数据转发
            client.OnBytesReceived += (data) => OnBytesReceived?.Invoke(data);

            // 断开事件处理
            client.OnDisconnected += (err) =>
            {
                //// 1. 如果正在首次连接/重连尝试中，直接唤醒 Task
                //tcs.TrySetResult(ConnectResult.Fail(err, "连接中断"));

                //// 2. 如果是已连接状态下断开，通知上层并触发重连
                //if (!_isManualClose && !_isReconnecting)
                //{
                //    MainThreadDispatcher.Enqueue(() => OnDisconnected?.Invoke(err));
                //    if (_policy.ShouldRetry(err)) _ = ReconnectLoop();
                //}


                // 如果 TCS 还没被填过 → 说明还在建连阶段 → 唤醒它，让 FirstConnect/ReconnectLoop 走正常失败流程
                if (tcs.TrySetResult(ConnectResult.Fail(err, "连接中断")))
                    return;

                // TCS 已经被填过（OnConnected 已触发过）→ 说明是已连接后断开 → 走正常断线处理
                HandleDisconnected(err);
            };

        }
        /// <summary>
        /// ---------- 被动断线处理 ----------
        /// </summary>
        /// <param name="error"></param>

        private void HandleDisconnected(ChannelError error)
        {
            // 只要不是手动关闭，且不是正在重连循环中触发的断开
            if (_isManualClose || _isReconnecting) return;
            MainThreadDispatcher.Enqueue(() => OnDisconnected?.Invoke(error)); // 通知上层"断了"

            if (_policy.ShouldRetry(error)) _ = ReconnectLoop();  // 错误不可重试，到此为止

            //_ = ReconnectLoop();  // 否则尝试重连
        }

        // ---------- 对外接口 ----------

        public void Send(byte[] data)
        {
            if (State != ChannelState.Connected || _client == null)
            {
                Debug.LogWarning("[ConnectionManager] 未连接，发送被忽略");
                return;
            }
            _client.Send(data);
        }

        public void Close()
        {
            _isManualClose = true;
            _isReconnecting = false;

            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _reconnectCts = null;

            _client?.Close();
            _client = null;
        }

        public void Update()
        {
            // 如果客户端存在，且是连接状态，驱动它
            // TCP 的 Update 是空实现，KCP 的 Update 是轮询，完美解耦
            _client?.Update();
        }
        /// <summary>
        /// 强制断开（心跳超时由 NetworkService 触发）
        /// </summary>
        /// <param name="error"></param>
        public void ForceDisconnect(ChannelError error)
        {
            _client?.ForceDisconnect(error);
            // 注意：一旦底层断开，会自动触发 HandleDisconnected，进而走重连
        }

       
    }
   
}
