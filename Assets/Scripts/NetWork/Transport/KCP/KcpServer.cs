using kcp2k;
using Network.Server;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace Network.Transport
{
    public class KcpServer
    {
        private Socket _socket;
        //private KcpConnection _connection;
        private KcpConfig _config;
        private readonly byte[] _udpRecvBuffer;
        private bool _isRunning;

        // 多客户端会话字典：Key = conv, Value = KcpConnection
        private readonly Dictionary<uint, KcpConnection> _connections = new Dictionary<uint, KcpConnection>();
        private readonly object _lock = new object();
        public int ConnectionCount { get { lock (_lock) { return _connections.Count; } } }


        //private EndPoint _clientEP;
        //private bool _hasClient;
        /// <summary>
        /// 客户端连接时触发
        /// </summary>
        public event Action<KcpConnection> OnClientConnected;
        /// <summary>
        /// 接收到数据时触发
        /// </summary>
        public event Action<KcpConnection, byte[]> OnClientBytesReceived;
        /// <summary>
        /// 客户端断开连接时触发
        /// </summary>
        public event Action<KcpConnection, ChannelError> OnClientDisconnected;

        
        public KcpServer(KcpConfig config)
        {
            _config = config;
            _udpRecvBuffer = new byte[config.Mtu * 2];
        }

        public void Start(int port)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Blocking = false;
            _socket.Bind(new IPEndPoint(IPAddress.Any, port));
            //_hasClient = false;
            _isRunning = true;
            Debug.Log($"[KcpServer] 监听 UDP 端口 {port}");
        }

        // 必须由外部（如 Unity 的 Update）高频调用
        public void Update()
        {
            //if (_socket == null) return;
            if (!_isRunning || _socket == null) return;
            // 1. 非阻塞收取 UDP 数据
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (_socket.ReceiveFromNonBlocking(_udpRecvBuffer, out var segment, ref remote))
            {
                /*/// 如果是第一个包，建立连接
                //if (!_hasClient)
                //{
                //    _clientEP = remote;
                //    // 第一阶段硬编码 conv = 1
                //    _connection = new KcpConnection(_socket, _clientEP, 1, _config);
                //    _connection.OnBytesReceived += (data) => OnClientBytesReceived?.Invoke(data);
                //    _connection.OnDisconnected += (err) => OnClientDisconnected?.Invoke(err);

                //    _hasClient = true;
                //    Debug.Log($"[KcpServer] 接受来自 {_clientEP} 的 KCP 会话");
                //    OnClientConnected?.Invoke();
                //}
                // 只处理当前绑定客户端的包
                //if (_hasClient && remote.Equals(_clientEP))
                //{
                //    _connection.Input(segment.Array, segment.Count);
                //}*/
                // 解析包头前 4 字节获取 conv (KCP 头部前 4 字节就是 conv，小端序)
                if (segment.Count >= 4)
                {
                    // 这里直接借用 kcp2k 自带的 Utils 解码
                    Utils.Decode32U(segment.Array, segment.Offset, out uint conv);

                    KcpConnection conn;
                    lock (_lock)
                    {
                        _connections.TryGetValue(conv, out conn);
                    }

                    if (conn == null)
                    {
                        // 发现新 conv，建立新连接
                        conn = new KcpConnection(_socket, remote, conv, _config);

                        //它没区分“握手阶段的探路包”和“真正的业务数据”——只要 KcpConnection 吐出数据，就原样上抛。
                        //而 0x01 是客户端在 Connect 里发的探路包，服务端第一次收到它时建会话、回 0x02，然后这个 0x01 被 KCP 引擎当作普通数据重组出来，一路送到协议层。
                        // 挂载事件
                        conn.OnBytesReceived += (data) => OnClientBytesReceived?.Invoke(conn, data);

                       
                        conn.OnDisconnected += (err) => HandleClientDisconnected(conv, conn, err);

                        lock (_lock) { _connections[conv] = conn;}

                        Debug.Log($"[KcpServer] 新客户端连入: {remote}, conv = {conv}");
                        OnClientConnected?.Invoke(conn);

                        // 【新增】立刻给客户端回个包，告诉他握手成功
                        //conn.Send(new byte[] { 0x02 });
                    }

                    // 喂给对应的会话
                    conn.Input(segment.Array, segment.Count);
                }
            }

            /*/ 2. 驱动 KCP
            //if (_hasClient)
            //{
            //    uint time = (uint)(Time.realtimeSinceStartup * 1000);
            //    _connection.Tick(time);
            //}*/
            // 2. 驱动所有会话的时钟 & 检测超时
            float now = Time.realtimeSinceStartup;
            uint currentMs = (uint)(now * 1000);
            List<KcpConnection> snapshot;

            lock (_lock)
            {
                snapshot = new List<KcpConnection>(_connections.Values);
            }

            foreach (var conn in snapshot)
            {
                // 超时检测
                if (now - conn.LastRecvTime > _config.TimeoutSec)
                {
                    Debug.LogWarning($"[KcpServer] 会话超时: {conn.Conv}");
                    //HandleClientDisconnected(conn.Conv, conn, ChannelError.TimedOut);
                    conn.Close(ChannelError.TimedOut);
                    HandleClientDisconnected(conn.Conv, conn, ChannelError.TimedOut);
                    continue;
                }

                conn.Tick(currentMs);
            }
        }
        private void HandleClientDisconnected(uint conv, KcpConnection conn, ChannelError error)
        {
            //lock (_lock)
            //{
            //    if (_connections.ContainsKey(conv))
            //    {
            //        _connections.Remove(conv);
            //    }
            //}
            //Debug.Log($"[KcpServer] 客户端断开: conv = {conv}, 原因 = {error}");
            //OnClientDisconnected?.Invoke(conn, error);

            bool removed = false;

            lock (_lock)
            {
                // 【核心修复】利用 Remove 的返回值做幂等守卫。
                // 如果字典里有这个 conv 并且成功移除，返回 true。
                // 如果已经被移除过了（并发或重复触发），返回 false。
                removed = _connections.Remove(conv);
            }
            // 只有第一次成功移除时，才向上抛事件
            if (removed)
            {
                Debug.Log($"[KcpServer] 客户端断开: conv = {conv}, 原因 = {error}");
                OnClientDisconnected?.Invoke(conn, error);
            }
        }
        public void Broadcast(byte[] data)
        {
            //_connection?.Send(data);
            // 如果列表为空，直接返回
            if (_connections == null || _connections.Count == 0) return;

            // 1. 拷贝一份快照，防止在遍历时刚好有玩家断线导致 Dictionary 修改异常
            List<KcpConnection> snapshot;
            lock (_lock)
            {
                snapshot = new List<KcpConnection>(_connections.Values);
            }

            // 2. 遍历发送
            foreach (var conn in snapshot)
            {
                try
                {
                    // 注意：KCP 的 Send 只是把数据放进发送队列
                    // 真正的网络发送会在下一次 Update() -> Flush() 中执行
                    conn.Send(data);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[KcpServer] 广播失败: {e.Message}");
                }
            }
        }

        public void Stop()
        {
            //_socket?.Close();
            //_socket = null;
            //_hasClient = false;
            //_connection = null;
            _isRunning = false;
            _socket?.Close();
            _socket = null;

            List<KcpConnection> snapshot;
            lock (_lock)
            {
                snapshot = new List<KcpConnection>(_connections.Values);
                _connections.Clear();
            }

            foreach (var conn in snapshot)
            {
                conn.Close(ChannelError.NormalClose);
            }
        }
        /// <summary>
        /// 踢人同时用于心跳超时
        /// </summary>
        /// <param name="conv"></param>
        public void Kick(uint conv)
        {
            KcpConnection conn = null;
            lock (_lock)
            {
                if (!_connections.TryGetValue(conv, out conn)) return;
            }
            conn.Close(ChannelError.NormalClose);   // 不手动 Remove，让 HandleClientDisconnected 的 Remove 返回 true
            HandleClientDisconnected(conv, conn, ChannelError.NormalClose);//清理资源
        }
    }
}