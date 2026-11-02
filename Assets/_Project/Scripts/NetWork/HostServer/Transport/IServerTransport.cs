using System;

namespace Network.Server
{
    public interface IServerTransport
    {
        int ConnectionCount { get; }
        bool HasMessageBoundary { get; } // 新增：TCP为false，KCP为true
        event Action<ServerSession> OnClientConnected;
        event Action<ServerSession> OnClientDisconnected;
        event Action<ServerSession, byte[]> OnClientBytesReceived;

        void Start(int port);
        void Update(); // 供 KCP 轮询驱动
        void Stop();

        // 全服广播
        void Broadcast(byte[] data);

        void Kick(ServerSession session); // 新增：强制踢人
    }
}