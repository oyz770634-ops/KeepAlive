using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Network.Protocol; // 复用客户端的 MessageCodec

namespace Network.Server
{
    public class ServerNetworkService: IDisposable
    {
        private readonly GameServerManager _server;
        private readonly MessageCodec _codec;

        // 上层业务层的心跳超时时间（10 秒）
        private const float BusinessTimeout = 10f;

        // 服务端专属路由表：MsgId -> Action<发消息的玩家, 消息体>
        private readonly Dictionary<int, Action<ServerSession, byte[]>> _handlers = new Dictionary<int, Action<ServerSession, byte[]>>();
        private readonly List<ServerSession> _timeoutBuffer = new List<ServerSession>();
        public ServerNetworkService(GameServerManager server)
        {
            _server = server;
            _codec = new MessageCodec();

            // 监听底层的事件
            _server.OnSessionConnected += HandleSessionConnected;
            _server.OnSessionDisconnected += HandleSessionDisconnected;
            _server.OnSessionBytesReceived += HandleSessionBytesReceived;
            _server.OnServerUpdate += CheckClientTimeout;
        }

        // ================= 业务层注册与发送接口 =================

        /// <summary>
        /// 业务层注册消息处理
        /// </summary>
        public void RegisterHandler(int msgId, Action<ServerSession, byte[]> handler)
        {
            if (_handlers.ContainsKey(msgId))
                _handlers[msgId] += handler;
            else
                _handlers[msgId] = handler;
        }
        /// <summary>
        /// 业务层注销消息处理
        /// </summary>
        /// <param name="msgId"></param>
        /// <param name="handler"></param>
        public void UnregisterHandler(int msgId, Action<ServerSession, byte[]> handler)
        {
            if (handler == null) return;

            if (_handlers.TryGetValue(msgId, out var existing))
            {
                existing -= handler;
                if (existing == null)
                    _handlers.Remove(msgId);
                else
                    _handlers[msgId] = existing;
            }
        }
        /// <summary>
        /// 业务层清除功能
        /// </summary>
        public void ClearHandlers()
        {
            _handlers.Clear();
        }

        /// <summary>
        /// 如果你想让代码更符合 C# 规范，可以继承 IDisposable，用于显式调用，表示需要释放处理的所有对象
        /// 包括：事件订阅（你的场景），定时器（System.Threading.Timer 实现 IDisposable，Dispose 停止定时器），CancellationTokenSource（实现 IDisposable，Dispose 释放内部 WaitHandle）
        ///，SemaphoreSlim（实现 IDisposable，Dispose 释放内部资源），HttpClient（实现 IDisposable，Dispose 关闭连接池）
        /// </summary>
        public void Dispose()
        {
            ClearHandlers();
            // 顺便取消对底层事件的监听
            _server.OnSessionConnected -= HandleSessionConnected;
            _server.OnSessionDisconnected -= HandleSessionDisconnected;
            _server.OnSessionBytesReceived -= HandleSessionBytesReceived;
            _server.OnServerUpdate -= CheckClientTimeout;
        }

        /// <summary>
        /// 向指定玩家发送数据
        /// </summary>
        public void SendToSession(ServerSession session, int msgId, byte[] body)
        {
            if (session == null) return;
            byte[] packet = _codec.Encode(msgId, body);
            session.Send(packet);
        }

        /// <summary>
        /// 全服广播
        /// </summary>
        public void Broadcast(int msgId, byte[] body)
        {
            byte[] packet = _codec.Encode(msgId, body);
            _server.Broadcast(packet);
        }

        // ================= 底层事件拦截与处理 =================

        private void HandleSessionConnected(ServerSession session)
        {
            Debug.Log($"[ServerNet] 玩家 {session.PlayerId} 接入协议层");
        }

        private void HandleSessionDisconnected(ServerSession session)
        {
            Debug.Log($"[ServerNet] 玩家 {session.PlayerId} 断开协议层");
        }

        private void HandleSessionBytesReceived(ServerSession session, byte[] rawData)
        {
            // 解析出 MsgId 和 Body
            Action<int, byte[]> onDecoded = (msgId, body) =>
            {
                Dispatch(session, msgId, body);
            };

            if (_server.HasMessageBoundary)
            {
                // KCP / WebSocket 模式
                if (_codec.DecodeKcp(rawData, rawData.Length, out int msgId, out byte[] body))
                {
                    onDecoded(msgId, body);
                }
            }
            else
            {
                // TCP 模式（服务端必须也是每个客户端维护一个独立的 Codec 缓存，否则粘包会串）
                // 注意：这里如果是 TCP 且多个客户端并发，共用一个 MessageCodec 会导致不同客户端的数据混在一起。
                // 所以服务端通常需要每个 Session 维护一个独立的 Codec，但为了简化，如果底层是 TCP，在 Session 里存 Codec。
                // 考虑到这里的 ServerNetworkService 是单例，直接用 _codec 仅在单客户端测试没问题。
                // 生产环境更严谨的做法是把 MessageCodec 塞进 ServerSession 里。
                //_codec.DecodeTcp(rawData, rawData.Length, onDecoded);

                // TCP 模式：【必须使用 session.Codec】，绝对不能共用 _codec
                session.Codec.DecodeTcp(rawData, rawData.Length, onDecoded);
            }
        }
        /// <summary>
        /// 它是服务端的"消息分拣口"——收到一条已解码的 (msgId, body)，决定这条消息是系统级的（Ping，自己处理）还是业务级的（查 _handlers 交给业务层），并且把"来自哪个玩家（session）"一起传下去。
        /// </summary>
        /// <param name="session"></param>
        /// <param name="msgId"></param>
        /// <param name="body"></param>
        private void Dispatch(ServerSession session, int msgId, byte[] body)
        {
            if (msgId == ProtocolConst.MsgIdPing)
            {
                // 直接把 Pong 塞回给客户端
                SendToSession(session, ProtocolConst.MsgIdPong, null);
                return; // 不回给业务层
            }

            if (_handlers.TryGetValue(msgId, out var handler))
            {
                try
                {
                    handler?.Invoke(session, body);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[ServerNet] 处理 MsgId={msgId} 异常: {e}");
                }
            }
            else
            {
                Debug.LogWarning($"[ServerNet] 收到未注册的消息 MsgId={msgId}，来自玩家 {session.PlayerId}");
            }
        }
        /// <summary>
        /// 服务端主动踢掉假死玩家（比如 15 秒没收到任何包）
        /// </summary>
        private void CheckClientTimeout()
        {
            float now = Time.realtimeSinceStartup;
            _timeoutBuffer.Clear();

            foreach (var session in _server.GetAllSessions())
            {
                if (now - session.LastRecvTime > BusinessTimeout)
                {
                    _timeoutBuffer.Add(session);
                }
            }

            foreach (var session in _timeoutBuffer)
            {
                Debug.LogWarning($"[ServerNet] 玩家 {session.PlayerId} 心跳超时，踢出房间");
                _server.KickPlayer(session.PlayerId);
            }
        }
    }
}