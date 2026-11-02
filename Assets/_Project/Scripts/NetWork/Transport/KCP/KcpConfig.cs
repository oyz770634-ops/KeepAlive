using System.Collections;
using System.Collections.Generic;
namespace Network.Transport
{
    /// <summary>
    /// Kcp协议参数
    /// </summary>
    public class KcpConfig
    {
        // KCP 参数
        /// <summary>
        /// 1 = 开启极速模式
        /// </summary>
        public uint NoDelay = 1;
        /// <summary>
        /// 内部刷新间隔 10ms
        /// </summary>
        public uint Interval = 10;
        /// <summary>
        /// 快速重传阈值
        /// </summary>
        public int Resend = 2;
        /// <summary>
        /// true = 关闭拥塞控制
        /// </summary>
        public bool NoCongestion = true;
        public uint SendWindow = 256;
        public uint ReceiveWindow = 256;
        public uint Mtu = 1200;

        /// <summary>
        /// 应用层读缓冲区大小
        /// </summary>
        public int ReceiveBufferLength = 4096;
        /// <summary>
        /// 会话超时时间（秒）。超过这个时间没收到客户端任何包，就断开。
        /// </summary>
        public float TimeoutSec = 30f;
    }
}