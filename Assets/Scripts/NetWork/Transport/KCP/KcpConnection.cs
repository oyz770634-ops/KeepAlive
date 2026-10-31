using kcp2k;
using System;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace Network.Transport
{
    public class KcpConnection
    {
        private readonly Socket _socket;
        private readonly EndPoint _remoteEP;
        private readonly KcpConfig _config;
        private readonly Kcp _kcp;
        private readonly byte[] _recvBuffer;

        /// <summary>
        /// Conv 是 KCP 协议的"会话号"
        /// TCP 不需要：每个 TCP 连接是独立的 Socket，内核用四元组（源 IP、源端口、目的 IP、目的端口）区分。服务端每个 TcpConnection 对应一个独立 Socket，TcpConnection 对象本身就是"哪个连接"的标识。
        ///KCP 必须用：UDP 没有连接概念，所有 KCP 会话共用一个 UDP Socket。服务端 KcpServer 只有一个 _socket，所有客户端的包都从这个 Socket 进来。怎么区分这个包是哪个客户端的？只能靠包里的 conv。
        /// </summary>
        public uint Conv { get; private set; }
        public ChannelState State { get; private set; } = ChannelState.Connected;
        /// <summary>
        /// 用于服务端检测超时
        /// </summary>
        public float LastRecvTime { get; private set; }

        /// <summary>
        /// kcp接收数据时触发
        /// </summary>
        public event Action<byte[]> OnBytesReceived;
        /// <summary>
        /// 断开连接时触发
        /// </summary>
        public event Action<ChannelError> OnDisconnected;

        public KcpConnection(Socket socket, EndPoint remoteEP, uint conv, KcpConfig config)
        {
            _socket = socket;
            _remoteEP = remoteEP;
            _config = config;
            _recvBuffer = new byte[config.ReceiveBufferLength];
            Conv = conv;
            LastRecvTime = Time.realtimeSinceStartup;

            // 初始化 KCP 引擎，传入 Output 回调
            _kcp = new Kcp(conv, OnKcpOutput);

            // 配置参数
            _kcp.SetNoDelay(config.NoDelay, config.Interval, config.Resend, config.NoCongestion);
            _kcp.SetWindowSize(config.SendWindow, config.ReceiveWindow);
            _kcp.SetMtu(config.Mtu);
        }

        /// <summary>
        /// --- 核心对接逻辑 1：KCP 输出给 UDP ---
        /// OnKcpOutput 里的 data，已经是经过 KCP 算法（切片、封包、加序列号、重传计算）处理完的最终字节流了。 这里的 Socket.SendTo 只是充当一个“没有感情的搬运工”，把 KCP 做好的“熟饭”端到网卡上。
        /// </summary>
        /// <param name="data"></param>
        /// <param name="size"></param>
        private void OnKcpOutput(byte[] data, int size)
        {
            if (State != ChannelState.Connected) return;
            try
            {
                _socket.SendTo(data, 0, size, SocketFlags.None, _remoteEP);
            }
            catch (Exception e)
            {
                //Debug.LogWarning($"[KcpConnection] UDP 发送异常: {e.Message}");
                Debug.LogWarning($"[KcpConnection {Conv}] UDP 发送异常: {e.Message}");
                HandleDisconnect(ChannelError.NetworkUnreachable);
                //OnDisconnected?.Invoke(ChannelError.NetworkUnreachable);
            }
        }

        /// <summary>
        /// --- 核心对接逻辑 2：UDP 输入给 KCP ---
        /// Input 是接收端的总入口。
        ///当你的底层 Socket 从网络上抓到一个 UDP 数据包时，你不能直接解析它，而是要把这包原始的字节流喂（Input）给 KCP 引擎。
        /// </summary>
        /// <param name="data"></param>
        /// <param name="size"></param>
        public void Input(byte[] data, int size)
        {
            if (State != ChannelState.Connected) return;
            LastRecvTime = Time.realtimeSinceStartup;
            _kcp.Input(data, 0, size);
        }

        /// <summary>
        /// --- 核心对接逻辑 3：驱动时钟与拉取数据 ---
        /// </summary>
        /// <param name="currentTimeMs"></param>
        public void Tick(uint currentTimeMs)
        {
            if (State != ChannelState.Connected) return;
            // 1. 驱动 KCP 内部状态（重传、ACK发送等，内部会触发 OnKcpOutput）
            _kcp.Update(currentTimeMs);

            // 2. 尝试从 KCP 读取重组好的完整消息
            while (true)
            {
                int size = _kcp.Receive(_recvBuffer, _recvBuffer.Length);
                if (size < 0) break; // < 0 表示没有完整消息
                byte[] msg = new byte[size];
                Array.Copy(_recvBuffer, msg, size);
                OnBytesReceived?.Invoke(msg);
            }
        }
        /// <summary>
        /// KCP 的 Send 只是放入发送队列，不会立即发送，等待 Tick 里的 Update 打包发送
        /// -----真正的上层调用用于通过kcp协议封装数据，最后封装的数据会通过onKcpOutput回调发送
        /// </summary>
        /// <param name="data"></param>
        public void Send(byte[] data)
        {
            if (State != ChannelState.Connected) return;
            _kcp.Send(data, 0, data.Length);
        }
        public void Close(ChannelError error = ChannelError.NormalClose)
        {
            //HandleDisconnect(error);
            if (State == ChannelState.Disconnected) return;
            State = ChannelState.Disconnected;
        }
        private void HandleDisconnect(ChannelError error)
        {
            if (State == ChannelState.Disconnected) return;
            State = ChannelState.Disconnected;
            OnDisconnected?.Invoke(error);
        }
    }
}