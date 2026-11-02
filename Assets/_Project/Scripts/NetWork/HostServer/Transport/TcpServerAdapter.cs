using System;
using System.Collections.Generic;
using Network.Transport;

namespace Network.Server
{
    public class TcpServerAdapter : IServerTransport
    {
        private readonly TcpServer _tcpServer;
        private readonly Dictionary<TcpConnection, ServerSession> _connToSession = new Dictionary<TcpConnection, ServerSession>();

        public int ConnectionCount => _tcpServer.ConnectionCount;
        public event Action<ServerSession> OnClientConnected;
        public event Action<ServerSession> OnClientDisconnected;
        public event Action<ServerSession, byte[]> OnClientBytesReceived;
        public bool HasMessageBoundary => false;

        private int _nextPlayerId = 1;
        public TcpServerAdapter()
        {
            _tcpServer = new TcpServer();
            _tcpServer.OnClientConnected += HandleTcpConnected;
            _tcpServer.OnClientDisconnected += HandleTcpDisconnected;
            _tcpServer.OnClientBytesReceived += HandleTcpBytes;
        }
        /// <summary>
        /// 开启服务器
        /// </summary>
        /// <param name="port"></param>
        public void Start(int port) => _tcpServer.Start(port);
        public void Update() { }
        /// <summary>
        /// 关闭服务器
        /// </summary>
        public void Stop()
        {
            _tcpServer.Stop();
            _connToSession.Clear();   // 再清自己的字典
        }
        /// <summary>
        /// 服务器广播
        /// </summary>
        /// <param name="data"></param>
        public void Broadcast(byte[] data) => _tcpServer.Broadcast(data);

        private void HandleTcpConnected(TcpConnection conn)
        {
            // 为玩家分配一个唯一 ID (这里直接借用 TcpConnection 的 HashCode 做个伪 ID，或者你自己维护自增)
            int playerId = _nextPlayerId++; 
            var session = new ServerSession(playerId, conn.Send, conn.Close);

            _connToSession[conn] = session;
            OnClientConnected?.Invoke(session);
        }

        private void HandleTcpDisconnected(TcpConnection conn, ChannelError error)
        {
            if (_connToSession.TryGetValue(conn, out var session))
            {
                _connToSession.Remove(conn);
                OnClientDisconnected?.Invoke(session);
            }
        }

        private void HandleTcpBytes(TcpConnection conn, byte[] data)
        {
            if (_connToSession.TryGetValue(conn, out var session))
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
            TcpConnection targetConn = null;
            foreach (var kv in _connToSession)
            {
                if (kv.Value == session) { targetConn = kv.Key; break; }
            }

            if (targetConn != null)
            {              
                // 清理 TcpServer 的字典并关闭连接
                _tcpServer.KickConnection(targetConn);
            }
        }
    }
}