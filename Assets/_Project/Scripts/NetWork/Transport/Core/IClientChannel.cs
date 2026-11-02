using System;

namespace Network.Transport
{
    public interface IClientChannel
    {
        ChannelState State { get; }

        event Action OnConnected;
        event Action<ChannelError, string> OnConnectFailed;
        event Action<byte[]> OnBytesReceived;
        event Action<ChannelError> OnDisconnected;
        bool HasMessageBoundary { get; } // 新增：底层是否自带消息边界
        void Connect(string host, int port);
        void Send(byte[] data);
        void Close();
        void Update();

        // 【新增】由协议层（NetworkService）在心跳超时时调用，触发异常断开
        void ForceDisconnect(ChannelError error);
    }
}