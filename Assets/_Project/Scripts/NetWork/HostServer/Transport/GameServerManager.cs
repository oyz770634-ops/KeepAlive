using kcp2k;
using Network.Transport;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Network.Server
{
    public enum ServerProtocol
    {
        Tcp,
        Kcp
    }

    public class GameServerManager:MonoBehaviour
    {
        /// <summary>
        /// 持有一个具体的Server，并非管理多个而是一一对应的关系
        /// </summary>
        private IServerTransport _transport;

        private readonly Dictionary<int, ServerSession> _sessions = new Dictionary<int, ServerSession>();
        public List<ServerSession> GetAllSessions() => new List<ServerSession>(_sessions.Values);
        public int SessionCount => _sessions.Count;
        public bool IsRunning { get; private set; }

        // 供上层（第二阶段协议层）监听的事件
        public event Action<ServerSession> OnSessionConnected;
        public event Action<ServerSession> OnSessionDisconnected;
        public event Action<ServerSession, byte[]> OnSessionBytesReceived;
        public event Action OnServerUpdate; 
        public bool HasMessageBoundary => _transport?.HasMessageBoundary ?? false;

        //private int _nextPlayerId = 1; // 新增自增 ID
        public void StartServer(ServerProtocol protocol, int port, Network.Transport.KcpConfig kcpConfig = null)
        {
            if (IsRunning) return;

            // 1. 根据配置实例化不同的底层适配器
            switch (protocol)
            {
                case ServerProtocol.Tcp:
                    _transport = new TcpServerAdapter();
                    break;
                case ServerProtocol.Kcp:
                    //_transport = new KcpServerAdapter();
                    if (kcpConfig == null) kcpConfig = new Network.Transport.KcpConfig(); // 给个默认配置
                    _transport = new KcpServerAdapter(kcpConfig);
                    break;                   
            }

            // 2. 事件桥接
            _transport.OnClientConnected += HandleClientConnected;
            _transport.OnClientDisconnected += HandleClientDisconnected;
            _transport.OnClientBytesReceived += HandleClientBytesReceived;

            // 3. 启动底层
            _transport.Start(port);
            IsRunning = true;
            Debug.Log($"[GameServer] 服务器启动，协议: {protocol}，端口: {port}");
        }

        public void StopServer()
        {
            if (!IsRunning) return;
            _transport.Stop();
            _sessions.Clear();
            IsRunning = false;
            Debug.Log("[GameServer] 服务器已停止");
        }

        // ================= 事件处理 =================
        private void HandleClientConnected(ServerSession session)
        {
            //// 这里的 session 是底层传上来的，我们要给它重新分配业务 ID
            //int assignedId = _nextPlayerId++;
            //// 重新包装一次，赋予正数 ID
            //var realSession = new ServerSession(assignedId, session.Send, session.Close);
            _sessions[session.PlayerId] = session;
            Debug.Log($"[GameServer] 玩家连入，分配 PlayerId: {session.PlayerId}，当前在线: {_sessions.Count}");

            OnSessionConnected?.Invoke(session);
        }

        /// <summary>
        ///  // 如果有业务层，也在这里清理
        /// </summary>
        /// <param name="session"></param>
        private void HandleClientDisconnected(ServerSession session)
        {
            //_sessions.Remove(session.PlayerId);
            //Debug.Log($"[GameServer] 玩家断开，PlayerId: {session.PlayerId}，当前在线: {_sessions.Count}");

            //OnSessionDisconnected?.Invoke(session);
            if (_sessions.Remove(session.PlayerId))
            {
                Debug.Log($"[GameServer] 玩家断开清理，PlayerId: {session.PlayerId}");
                OnSessionDisconnected?.Invoke(session);
                // 如果有业务层，也在这里清理
            }
        }

        private void HandleClientBytesReceived(ServerSession session, byte[] data)
        {
            // 更新心跳活跃时间
            session.LastRecvTime = Time.realtimeSinceStartup;

            // 转发给业务层/协议层
            OnSessionBytesReceived?.Invoke(session, data);
        }

        // ================= 提供给上层的发送方法 =================
        public void SendToPlayer(int playerId, byte[] data)
        {
            if (_sessions.TryGetValue(playerId, out var session))
            {
                session.Send(data);
            }
        }

        public void Broadcast(byte[] data)
        {
            _transport?.Broadcast(data);
        }

        public void KickPlayer(int playerId)
        {
            if (_sessions.TryGetValue(playerId, out var session))
            {
                Debug.LogWarning($"[GameServer] 强制踢出玩家 {playerId}");
                // 交给底层 Adapter 处理，底层一定会触发一次 OnClientDisconnected 回来
                _transport.Kick(session);
            }
        }

        private void Update()
        {

            // 驱动 KCP 轮询（对 TCP 是空实现）
            if (IsRunning)
            {
                _transport.Update();
                OnServerUpdate?.Invoke(); // 驱动心跳检测
            }
        }
        private void OnDestroy() => StopServer();
    }
}