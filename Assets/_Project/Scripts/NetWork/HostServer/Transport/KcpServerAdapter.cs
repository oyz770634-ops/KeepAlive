using System;
using System.Collections.Generic;
using Network.Transport;

namespace Network.Server
{
    public class KcpServerAdapter : IServerTransport
    {
        private readonly KcpServer _kcpServer;
        private readonly Dictionary<uint, ServerSession> _convToSession = new Dictionary<uint, ServerSession>();

        public int ConnectionCount => _kcpServer.ConnectionCount;
        public event Action<ServerSession> OnClientConnected;
        public event Action<ServerSession> OnClientDisconnected;
        public event Action<ServerSession, byte[]> OnClientBytesReceived;
        public bool HasMessageBoundary => true;

        private int _nextPlayerId = 1;
        public KcpServerAdapter(KcpConfig config)
        {
            _kcpServer = new KcpServer(config);
            _kcpServer.OnClientConnected += HandleKcpConnected;
            _kcpServer.OnClientDisconnected += HandleKcpDisconnected;
            _kcpServer.OnClientBytesReceived += HandleKcpBytes;
        }

        public void Start(int port) => _kcpServer.Start(port);
        public void Update() => _kcpServer.Update(); // KCP 必须要驱动 Update
        public void Stop()
        { 
            _kcpServer.Stop();
            _convToSession.Clear();
        }
        public void Broadcast(byte[] data) => _kcpServer.Broadcast(data);

        private void HandleKcpConnected(KcpConnection conn)
        {
            // KCP 的 Conv 是 uint。为了做 PlayerId，可以强转，或者自己生成
            int playerId = _nextPlayerId++;

            // 注意：KcpConnection.Close 带有默认参数，需要包装成无参 Action
            var session = new ServerSession(playerId, conn.Send, () => conn.Close(ChannelError.NormalClose));

            _convToSession[conn.Conv] = session;
            OnClientConnected?.Invoke(session);
        }

        private void HandleKcpDisconnected(KcpConnection conn, ChannelError error)
        {
            if (_convToSession.TryGetValue(conn.Conv, out var session))
            {
                _convToSession.Remove(conn.Conv);
                OnClientDisconnected?.Invoke(session);
            }
        }

        private void HandleKcpBytes(KcpConnection conn, byte[] data)
        {
            if (_convToSession.TryGetValue(conn.Conv, out var session))
            {
                session.LastRecvTime = UnityEngine.Time.realtimeSinceStartup;
                OnClientBytesReceived?.Invoke(session, data);
            }
        }
        /// <summary>
        /// 踢人
        /// </summary>
        /// <param name="session"></param>
        public void Kick(ServerSession session)
        {
            uint targetConv = 0;
            bool found = false;

            // 遍历找到对应的 conv
            foreach (var kv in _convToSession)
            {
                if (kv.Value == session)
                {
                    targetConv = kv.Key;
                    found = true;
                    break;
                }
            }

            if (found)
            {
                // 直接交给底层 KcpServer 执行安全关闭
                _kcpServer.Kick(targetConv);
            }
        }
    }
}