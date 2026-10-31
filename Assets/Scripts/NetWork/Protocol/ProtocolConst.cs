namespace Network.Protocol
{
    /// <summary>
    /// 协议常量
    /// </summary>
    public static class ProtocolConst
    {
        /// <summary>
        /// 协议头大小：4字节 Length + 4字节 MsgID
        /// </summary>
        public const int HeaderSize = 8;

        /// <summary>
        /// 单包最大限制（64KB），防止恶意超大包攻击内存
        /// </summary>
        public const int MaxPacketSize = 1024 * 64;

        // 系统级消息号（心跳）
        public const int MsgIdPing = 1;
        public const int MsgIdPong = 2;
    }
}
