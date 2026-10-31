using kcp2k;
using Network.Protocol;
using System;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace Network.Transport
{
    public class KcpClient:IClientChannel
    {
        private Socket _socket;
        private EndPoint _serverEP;
        private KcpConnection _connection;
        private KcpConfig _config;
        /// <summary>
        /// 复用的 UDP 接收缓冲区。
        /// 因为每帧都要收包，如果每次都 new byte[] 会产生大量 GC，所以用一个固定大小的数组复用。
        /// </summary>
        private readonly byte[] _udpRecvBuffer;
        /// <summary>
        /// 记录调用 Connect 的时间戳。因为 UDP 没有握手，客户端发完探路包后，需要等 2 秒。
        /// 如果超时还没收到服务端回包，就判定连接失败。
        /// </summary>
        private float _connectStartTime;
        /// <summary>
        /// 标记是否已经完成了应用层握手（收到了服务端的第一个回包）。只有收到回包后，状态才会切为 Connected。
        /// </summary>
        private bool _isHandshaked;
        /// <summary>
        /// 记录最后一次收到网络包的时间。用于在 Connected 状态下，检测心跳是否超时（假死）。
        /// </summary>
        private float _lastRecvTime;
        //private bool _isConnecting; // KCP 连接是瞬间的，这里用来防并发
        private ChannelState _state = ChannelState.Disconnected;
        public ChannelState State => _state;

        public bool HasMessageBoundary => true;

        //public ChannelState State => _connection?.State ?? ChannelState.Disconnected;假连问题
        /// <summary>
        /// kcp连接成功时触发
        /// </summary>
        public event Action OnConnected;
        /// <summary>
        /// kcp接收消息时触发
        /// </summary>
        public event Action<byte[]> OnBytesReceived;
        /// <summary>
        /// kcp关闭连接时触发，断开，连上了后来断了
        /// </summary>
        public event Action<ChannelError> OnDisconnected;
        /// <summary>
        /// 建连失败，还没连上就失败了。
        /// </summary>
        public event Action<ChannelError, string> OnConnectFailed;

        public KcpClient(KcpConfig config)
        {
            _config = config;
            // UDP 接收缓冲区，给 kcp2k 的 Extensions 用
            _udpRecvBuffer = new byte[config.Mtu * 2];
        }

        public void Connect(string host, int port)
        {
            
            /*/if (State == ChannelState.Connected) return;

            //_socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            //_socket.Blocking = false; // 必须设置非阻塞
            //_socket.Bind(new IPEndPoint(IPAddress.Any, 0)); // 随机分配本地端口

            //_serverEP = new IPEndPoint(IPAddress.Parse(host), port);

            //// 客户端生成一个随机的 conv，防止多客户端冲突
            //// 真实项目中通常通过 HTTP/TCP 登录去服务端申请 conv，这里为了纯 UDP 流程采用随机数
            ////_connection = new KcpConnection(_socket, _serverEP, 1, _config);
            //uint randomConv = (uint)UnityEngine.Random.Range(10000, 999999);
            //_connection = new KcpConnection(_socket, _serverEP, randomConv, _config);
            //_connection.OnBytesReceived += (data) => OnBytesReceived?.Invoke(data);
            //_connection.OnDisconnected += (err) => OnDisconnected?.Invoke(err);
            //Debug.Log($"[KcpClient] 连接成功，分配本地 conv = {randomConv}");
            //Debug.Log("[KcpClient] 连接成功 (无握手)");
            //// KCP 没有握手，直接认为连接成功
            //OnConnected?.Invoke();
            //if (State == ChannelState.Connected || _isConnecting) return;
            //_isConnecting = true;*/
            if (_state == ChannelState.Connected || _state == ChannelState.Connecting) return;
            Close();
            _state = ChannelState.Connecting;
            _isHandshaked = false;
            _connectStartTime = Time.realtimeSinceStartup;

            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _socket.Blocking = false;
                _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                _serverEP = new IPEndPoint(IPAddress.Parse(host), port);

                // 关键：每次重连都生成一个全新的 conv，防止服务端混淆历史包
                uint randomConv = (uint)UnityEngine.Random.Range(10000, 999999);
                _connection = new KcpConnection(_socket, _serverEP, randomConv, _config);
                _connection.OnBytesReceived += HandleBytesReceived;
                _connection.OnDisconnected += (err) =>
                {
                    if (_state == ChannelState.Disconnected) return;
                    if (_state == ChannelState.Connecting)
                    {
                        _state = ChannelState.Disconnected;
                        OnConnectFailed?.Invoke(err, "握手阶段底层断开");
                    }
                    else if (_state == ChannelState.Connected)
                    {
                        _state = ChannelState.Disconnected;
                        OnDisconnected?.Invoke(err);
                    }
                };
                /*/_connection.OnBytesReceived += (data) =>
                //{
                //    _lastRecvTime = Time.realtimeSinceStartup; // 刷新活跃时间
                //    OnBytesReceived?.Invoke(data);
                //};
                这里的问题是没有判断处于fial还是disconect导致上层连续触发两次重新连接

                // 【核心1】立刻发送一个空包(或者特定字节)作为“握手探路”
                // 只要有包发出去，服务端收到后就会创建会话并回包
                //_connection.Send(new byte[] { 0x01 });
                //_lastRecvTime = Time.realtimeSinceStartup;
                //_isConnecting = false;
                //OnConnected?.Invoke();*/

                var pingPacket = new byte[8];
                Array.Copy(BitConverter.GetBytes(8), 0, pingPacket, 0, 4); // Length = 8
                Array.Copy(BitConverter.GetBytes(ProtocolConst.MsgIdPing), 0, pingPacket, 4, 4); // MsgId = Ping
                _connection.Send(pingPacket);
            }
            catch (Exception e)
            {
                _state = ChannelState.Disconnected;
                //_isConnecting = false;
                OnConnectFailed?.Invoke(ChannelErrorMapper.FromException(e), e.Message);
                Close();
            }


        }

        // 必须由外部（如 Unity 的 Update）高频调用
        public void Update()
        {
            /*/if (_socket == null || _connection == null) return;
            //if (_socket == null || _connection == null || State != ChannelState.Connected) return;
            // 1. 非阻塞从 UDP 收取所有可能的数据，喂给 KCP
            // ReceiveNonBlocking 是 kcp2k Extensions.cs 里的方法
            //while (_socket.ReceiveNonBlocking(_udpRecvBuffer, out var segment))
            //{
            //    _connection.Input(segment.Array, segment.Count);
            //    _lastRecvTime = Time.realtimeSinceStartup; // 只要收到包就刷新
            //}

            // 驱动 KCP 内部时钟
            //uint time = (uint)(Time.realtimeSinceStartup * 1000);
            //_connection.Tick(time);

            // 2. 客户端超时检测（UDP的无连接特性导致必须客户端自己判超时）
            //if (Time.realtimeSinceStartup - _lastRecvTime > _config.TimeoutSec)
            //{
            //    Debug.LogWarning("[KcpClient] 客户端超时，判定掉线");
            //    _connection.Close(ChannelError.TimedOut);
            //    Close();
            //    return;
            //}
            //_connection.Tick((uint)(Time.realtimeSinceStartup * 1000));*/
            if (_socket == null || _connection == null || _state == ChannelState.Disconnected) return;

            // 【核心2】防御 UDP 底层异常（ICMP端口不可达）
            try
            {
                while (_socket.ReceiveNonBlocking(_udpRecvBuffer, out var segment))
                {
                    _connection.Input(segment.Array, segment.Count);//接收消息并刷新时间
                    _lastRecvTime = Time.realtimeSinceStartup;
                }
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode == SocketError.ConnectionReset)
                {
                    Debug.LogWarning("[KcpClient] 收到 ICMP 端口不可达，服务端已崩溃");
                    // 区分报错是握手阶段还是已连接阶段
                    if (_state == ChannelState.Connecting)
                    {
                        // 握手阶段失败，必须告诉 ConnectionManager 是 ConnectFailed，否则它等不到结果会卡死
                        _state = ChannelState.Disconnected;
                        OnConnectFailed?.Invoke(ChannelError.ConnectionReset, "ICMP 端口不可达");
                    }
                    else if (_state == ChannelState.Connected)
                    {
                        // 已连接后断开，走 Disconnected
                        _state = ChannelState.Disconnected;
                        OnDisconnected?.Invoke(ChannelError.ConnectionReset);
                    }

                    Close();
                    return;
                }
                throw;
                //    _state = ChannelState.Disconnected;
                //    OnDisconnected?.Invoke(ChannelError.ConnectionReset);
                //    Close();
                //    return;
                //}
                //throw;

            }

            // 【核心3】连接中状态检测
            if (_state == ChannelState.Connecting)
            {
                // 如果 2 秒还没收到服务端回包，判定握手超时
                if (Time.realtimeSinceStartup - _connectStartTime > 2.0f)
                {
                    Debug.LogWarning("[KcpClient] UDP 握手超时");
                    _state = ChannelState.Disconnected;
                    OnConnectFailed?.Invoke(ChannelError.TimedOut, "握手超时");
                    Close();
                    return;
                }

                _connection.Tick((uint)(Time.realtimeSinceStartup * 1000));
                return;
            }

            // 【核心4】已连接状态超时检测
            if (_state == ChannelState.Connected)
            {
                if (Time.realtimeSinceStartup - _lastRecvTime > _config.TimeoutSec)
                {
                    Debug.LogWarning("[KcpClient] 连接超时，判定掉线");
                    _state = ChannelState.Disconnected;
                    OnDisconnected?.Invoke(ChannelError.TimedOut);
                    Close();
                    return;
                }

                _connection.Tick((uint)(Time.realtimeSinceStartup * 1000));
            }
        }
        /// <summary>
        /// 接收消息后的回调，此时kcp刷新状态为连接状态
        /// </summary>
        /// <param name="data"></param>
        private void HandleBytesReceived(byte[] data)
        {
            _lastRecvTime = Time.realtimeSinceStartup;

            // 如果当前在握手阶段，收到回包意味着连接真正建立
            if (!_isHandshaked && _state == ChannelState.Connecting)
            {
                _isHandshaked = true;
                _state = ChannelState.Connected;
                Debug.Log("[KcpClient] 收到服务端回包，握手成功");
                OnConnected?.Invoke();
                return; // 握手包的垃圾数据不抛给业务层
            }

            OnBytesReceived?.Invoke(data);
        }

        public void Send(byte[] data)
        {
            //_connection?.Send(data);
            if (_state == ChannelState.Connected) _connection?.Send(data);
        }

        public void Close()
        {
            _socket?.Close();
            _socket = null;
            _connection = null;
            _state = ChannelState.Disconnected;
            //_isConnecting = false;
            _isHandshaked = false;
        }
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
            _connection = null; // KCP 里是 _connection
            try { _socket?.Close(); } catch { }
            _socket = null;
            OnDisconnected?.Invoke(error);
        }
    }
}