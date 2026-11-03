using Network.Transport;
using ProtoBuf;
using System;
using System.Collections.Generic;
using UnityEngine;
using static UnityEditorInternal.VersionControl.ListControl;

namespace Network.Protocol
{
    public class NetworkService
    {
        private readonly IClientChannel _channel;
        /// <summary>
        /// 编码器
        /// </summary>
        private readonly MessageCodec _codec;
        private readonly MessageRouter _router;

        /// <summary>
        /// 记录底层是 TCP 还是 KCP，决定解码方式
        /// </summary>
        //private readonly bool _isKcp;

        // --- 心跳配置与状态 ---
        private const float HeartbeatSendInterval = 3f; // 每 3 秒发一次 Ping
        private const float HeartbeatTimeout = 10f;     // 超过 10 秒没收到任何包，判定假死

        /// <summary>
        /// 最后一次收到数据的时间
        /// </summary>
        private float _lastRecvTime;
        /// <summary>
        /// 最后一次发送 Ping 的时间
        /// </summary>
        private float _lastSendPingTime;

        /// <summary>
        /// 对外暴露底层的连接状态事件：连接成功调用
        /// </summary>
        public event Action OnConnected;
        /// <summary>
        /// 对外暴露底层的连接状态事件：连接失败调用
        /// </summary>
        public event Action<ChannelError, string> OnConnectFailed;
        /// <summary>
        /// 对外暴露底层的连接状态事件：连接断开调用
        /// </summary>
        public event Action<ChannelError> OnDisconnected;


        // ★ 新增：序列化器
        private readonly IMessageSerializer _serializer;

        // ★ 新增：泛型 handler 注册映射，用于反注册
        private readonly Dictionary<int, Dictionary<Delegate, Action<byte[]>>> _genericHandlers
            = new Dictionary<int, Dictionary<Delegate, Action<byte[]>>>();

        public ChannelState State => _channel?.State ?? ChannelState.Disconnected;
        private ChannelState _lastState = ChannelState.Disconnected;
        public NetworkService(IClientChannel channel,IMessageSerializer serializer = null)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _codec = new MessageCodec();
            _router = new MessageRouter();
            _serializer = serializer ?? new ProtobufNetSerializer();
            // 通过类型判断底层是 TCP 还是 KCP
            //_isKcp = _channel is KcpClient;

            /*
            NetworkService 只订阅了 OnConnected，但 ConnectionManager 重连成功走的是 OnReconnected——HandleConnected 不会被调用，_lastRecvTime 和 _lastSendPingTime 保持断线前的旧值。
            只要断线 + 重连总耗时超过 10 秒，重连成功后的第一次 Update 就会判定心跳超时，立即强制断开，又重连，无限循环。这在切 WiFi、电梯、地铁隧道等场景很容易触发。
            isKcp 是"中"（侥幸能跑但错误低效），重连心跳不重置是"高"（直接功能失效）。
            */
            // 监听底层的四大事件
            _channel.OnConnected += HandleConnected;
            _channel.OnConnectFailed += HandleConnectFailed;
            _channel.OnDisconnected += HandleDisconnected;
            _channel.OnBytesReceived += HandleRawBytes;
        }

        // ================= 底层事件拦截 =================

        private void HandleConnected()
        {
            // 连接成功，初始化心跳计时器
            _lastRecvTime = Time.realtimeSinceStartup;
            _lastSendPingTime = Time.realtimeSinceStartup;
            OnConnected?.Invoke();
        }

        private void HandleConnectFailed(ChannelError error, string reason)
        {
            OnConnectFailed?.Invoke(error, reason);
        }

        private void HandleDisconnected(ChannelError error)
        {
            // 断线时，必须清空 TCP 缓存，防止半包污染下一次连接
            _codec.ClearTcpCache();
            OnDisconnected?.Invoke(error);
        }

        // ================= 核心数据流转 =================

        /// <summary>
        /// 1. 接收：底层收包 -> 解码 -> 路由分发
        /// HandleRawBytes 通过isKcp决定解码路径
        /// </summary>
        /// <param name="rawData"></param>
        private void HandleRawBytes(byte[] rawData)
        {
            // 收到任何数据，都刷新最后接收时间
            _lastRecvTime = Time.realtimeSinceStartup;

            Action<int, byte[]> onDecoded = (msgId, body) =>
            {
                // 拦截系统级 Pong 包，不给业务层处理
                if (msgId == ProtocolConst.MsgIdPong)
                {
                    return;
                }
                _router.Dispatch(msgId, body);
            };

            if (_channel.HasMessageBoundary)
            {
                // KCP 自带消息边界，直接解码
                if (_codec.DecodeKcp(rawData, rawData.Length, out int msgId, out byte[] body))
                {
                    //_router.Dispatch(msgId, body);
                    onDecoded(msgId, body);
                }
            }
            else
            {
                // TCP 字节流，需要处理粘包/半包
                //_codec.DecodeTcp(rawData, rawData.Length, (msgId, body) =>{_router.Dispatch(msgId, body);});
                _codec.DecodeTcp(rawData, rawData.Length, onDecoded);
            }
        }

        /// <summary>
        /// 2. 发送：业务层发数据 -> 编码 -> 底层发包
        /// </summary>
        /// <param name="msgId"></param>
        /// <param name="body"></param>
        public void Send(int msgId, byte[] body)
        {
            if (State != ChannelState.Connected)
            {
                Debug.LogWarning($"[NetworkService] 当前状态 {State}，发送 MsgId={msgId} 被丢弃");
                return;
            }

            byte[] packet = _codec.Encode(msgId, body);
            _channel.Send(packet);
        }


        // ★ 新增：泛型发送
        /// <summary>
        /// 对象发送：序列化器把 message 转成 body，再加包头，再交给底层。
        /// </summary>
        public void Send<T>(int msgId, T message)
        {
            if (State != ChannelState.Connected)
            {
                Debug.LogWarning($"[NetworkService] 当前状态 {State}，发送 MsgId={msgId} 被丢弃");
                return;
            }

            byte[] body = _serializer.Serialize(message);
            byte[] packet = _codec.Encode(msgId, body);
            _channel.Send(packet);
        }

        // ★ 新增：泛型注册
        /// <summary>
        /// 对象接收：内部包一层 Action&lt;byte[]&gt; 注册到 MessageRouter，
        /// 收到字节时先反序列化成 T，再交给业务 handler。
        /// </summary>
        public void RegisterHandler<T>(int msgId, Action<T> handler)
        {
            if (handler == null) return;

            Action<byte[]> wrapper = bytes =>
            {
                T msg = _serializer.Deserialize<T>(bytes);
                handler.Invoke(msg);
            };

            // 记录 handler → wrapper 映射，供反注册用
            if (!_genericHandlers.TryGetValue(msgId, out var map))
            {
                map = new Dictionary<Delegate, Action<byte[]>>();
                _genericHandlers[msgId] = map;
            }
            map[handler] = wrapper;

            _router.Register(msgId, wrapper);
        }

        // ★ 新增：泛型反注册
        public void UnregisterHandler<T>(int msgId, Action<T> handler)
        {
            if (handler == null) return;

            if (_genericHandlers.TryGetValue(msgId, out var map)
                && map.TryGetValue(handler, out var wrapper))
            {
                map.Remove(handler);
                _router.Unregister(msgId, wrapper);
                if (map.Count == 0) _genericHandlers.Remove(msgId);
            }
        }




        // ================= 对外接口 =================

        public void RegisterHandler(int msgId, Action<byte[]> handler)
        {
            _router.Register(msgId, handler);
        }

        public void UnregisterHandler(int msgId, Action<byte[]> handler)=>_router.Unregister(msgId, handler);
        
        
        public void Connect(string host, int port)
        {
            _channel.Connect(host, port);

        }

        public void Close()
        {
            _channel.Close();
            // 主动关闭不会触发 OnDisconnected，手动做 HandleDisconnected 该做的事
            _codec.ClearTcpCache();

            // 重置心跳状态，避免下次 Connect 后误判
            _lastRecvTime = Time.realtimeSinceStartup;
            _lastSendPingTime = Time.realtimeSinceStartup;
            _lastState = ChannelState.Disconnected;
            // _router 不清：保持"注册一次永久有效"，需要彻底清时业务层调 ClearHandlers()
        }
        /// <summary>
        /// 退出游戏时调用
        /// </summary>
        public void ClearHandlers()
        {
            _router.Clear();
            _genericHandlers.Clear();
        }
        /// <summary>
        /// 驱动底层KCP必须每帧调用Tick，心跳驱动
        /// </summary>
        public void Update()
        {
            _channel.Update();

            // 状态从非 Connected 变成 Connected（首次连接或重连成功），重置计时器
            if (State == ChannelState.Connected && _lastState != ChannelState.Connected)
            {
                _lastRecvTime = Time.realtimeSinceStartup;
                _lastSendPingTime = Time.realtimeSinceStartup;
            }

            _lastState = State;
            if (State != ChannelState.Connected) return;
            float now = Time.realtimeSinceStartup;

            // 1. 发送心跳 Ping
            if (now - _lastSendPingTime >= HeartbeatSendInterval)
            {
                _lastSendPingTime = now;
                byte[] pingPacket = _codec.Encode(ProtocolConst.MsgIdPing, null);
                _channel.Send(pingPacket);
            }

            // 2. 检测心跳超时（假死）
            if (now - _lastRecvTime > HeartbeatTimeout)
            {
                Debug.LogWarning($"[NetworkService] 心跳超时 ({HeartbeatTimeout}s 未收到数据)，强制断开");

                // 通知底层强制断开（如果是 ConnectionManager，会触发重连）
                _channel.ForceDisconnect(ChannelError.TimedOut);
            }
        }
    }
}
