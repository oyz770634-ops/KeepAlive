using Network.Transport;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
namespace Network.Transport
{
    public class TcpServer
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly Dictionary<int, TcpConnection> _connections = new Dictionary<int, TcpConnection>();
        private readonly object _lock = new object();
        private readonly ChannelConfig _config;
        //自增ID用于客户端的唯一标识
        private int _nextConnectionId = 0;
        private bool _isRunning;

        /// <summary>
        /// 订阅方法，执行时机：新客户连接时进行回调
        /// </summary>
        public event Action<TcpConnection> OnClientConnected;
        /// <summary>
        /// 订阅方法，执行时机：客户端断开时进行回调
        /// </summary>
        public event Action<TcpConnection, ChannelError> OnClientDisconnected;
        /// <summary>
        /// 服务端接收消息，让上层能知道消息来自哪个连接
        /// </summary>
        //public event Action<int, byte[]> OnClientBytesReceived;
        public event Action<TcpConnection, byte[]> OnClientBytesReceived;
        /// <summary>
        /// 只读，连接客户端的数量，加锁读取不可写，写入不可读取
        /// </summary>
        public int ConnectionCount
        {
            get
            {
                lock (_lock) { return _connections.Count; }
            }
        }
        public TcpServer(ChannelConfig config = null)
        {
            _config = config ?? new ChannelConfig();
        }
        /// <summary>
        /// 服务端启动方法
        /// </summary>
        /// <param name="port"></param>
        public void Start(int port)
        {
            if (_isRunning)
            {
                Debug.LogWarning("[TcpServer] 已在运行，重复 Start 被忽略");
                return;
            }

            _isRunning = true;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start(_config.ListenBacklog);

            Debug.Log($"[TcpServer] 开始监听 {port}，backlog={_config.ListenBacklog}");
            _ = AcceptLoop();
        }


        /// <summary>
        /// 异步循环监听客户端连接
        /// </summary>
        /// <returns></returns>
        private async Task AcceptLoop()
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    TcpClient tcpClient = await _listener.AcceptTcpClientAsync();
                    //原子操作自增减
                    int id = Interlocked.Increment(ref _nextConnectionId);
                    var connection = new TcpConnection(tcpClient, _config);
                    //tcpClient.NoDelay = true;

                    lock (_lock) { _connections[id] = connection; }
                    //底层的接收数据会调用，因此注册一个方法，使得每次读取数据都能调用这个方法，方法是订阅了sever的receive的方法组
                    connection.OnBytesReceived += data =>
                    {
                        // 第一版直接透传给上层，后续协议层会在这里处理粘包拆包
                        // 这里不区分是哪个连接的消息，上层如果需要知道来源，后续要扩展
                        OnClientBytesReceived?.Invoke(connection, data);
                    };
                    //由于这些状态事实上是底层才真正触发，但是上层经过封装后一些信息底层没有因此无法直接调用，所以通过上层提供委托与再封装实现功能
                    connection.OnDisconnected += error =>
                    {
                        HandleClientDisconnected(id, connection, error);
                    };

                    connection.StartReceive();
                    MainThreadDispatcher.Enqueue(() => OnClientConnected?.Invoke(connection));
                }
            }
            catch (OperationCanceledException)
            {
                // Stop() 导致的取消，正常退出
            }
            catch (ObjectDisposedException) { }
            catch (Exception e)
            {
                if (_isRunning)
                    Debug.LogError($"[TcpServer] Accept 异常：{e.Message}");
            }
        }


        /// <summary>
        /// 执行客户端断开后的回调句柄
        /// </summary>
        /// <param name="id"></param>
        /// <param name="connection"></param>
        private void HandleClientDisconnected(int id, TcpConnection connection, ChannelError error)
        {
            lock (_lock) { _connections.Remove(id); }
            connection.Close();
            MainThreadDispatcher.Enqueue(() => OnClientDisconnected?.Invoke(connection, error));
        }
        /// <summary>
        /// 服务器广播功能
        /// </summary>
        /// <param name="data"></param>
        public void Broadcast(byte[] data)
        {
            List<TcpConnection> snapshot;
            lock (_lock) { snapshot = new List<TcpConnection>(_connections.Values); }

            foreach (var conn in snapshot)
            {
                try
                {
                    conn.Send(data);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[TcpServer] 广播失败：{e.Message}");
                }
            }
        }
        /// <summary>
        /// 关闭服务器，并清理资源
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;

            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }

            List<TcpConnection> snapshot;
            lock (_lock)
            {
                snapshot = new List<TcpConnection>(_connections.Values);
                _connections.Clear();
            }

            foreach (var conn in snapshot) { conn.Close(); }

            _cts?.Dispose();
            _cts = null;
            _listener = null;

            Debug.Log("[TcpServer] 已停止");
        }
        /// <summary>
        /// 踢人同时用于心跳超时
        /// </summary>
        /// <param name="conv"></param>
        public void KickConnection(TcpConnection conn)
        {
            if (conn == null) return;
            // 反查 ID 并移除，防止字典残留
            int idToRemove = -1;
            lock (_lock)
            {
                foreach (var kv in _connections)
                {
                    if (kv.Value == conn) { idToRemove = kv.Key; break; }
                }
            }
            if (idToRemove != -1)
            {
                HandleClientDisconnected(idToRemove, conn, ChannelError.NormalClose);   // 走统一路径
            }
        }
    }
}