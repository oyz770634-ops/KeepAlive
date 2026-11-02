namespace Network.Transport
{

    /// <summary>
    /// 连接配置类用于统一设置与解耦，后续结合ScriptGameObject实现快速配置，以及UI对外显示
    /// </summary>
    public class ChannelConfig
    {
        // 连接参数
        /// <summary>
        /// IP地址
        /// </summary>
        public string Host { get; set; } = "127.0.0.1";
        /// <summary>
        /// 端口号
        /// </summary>
        public int Port { get; set; } = 8888;
        /// <summary>
        /// 超时连接时间
        /// </summary>
        public int ConnectTimeoutMs { get; set; } = 5000;

        // Socket 参数
        /// <summary>
        /// 控制 TCP_NODELAY，也就是是否禁用 Nagle 算法。
        /// </summary>
        public bool NoDelay { get; set; } = true;
        /// <summary>
        /// 内核接收缓冲区的大小，单位字节。对应 Socket.ReceiveBufferSize。
        /// </summary>
        public int ReceiveBufferSize { get; set; } = 0;   // 0 = 系统默认
        /// <summary>
        /// 内核发送缓冲区的大小。对应 Socket.SendBufferSize。
        /// </summary>
        public int SendBufferSize { get; set; } = 0;
        /// <summary>
        /// 应用层接收缓冲区参数，自己的代码里 ReadAsync 用的 byte[] 的长度。不是 Socket 参数，是应用层的。
        /// </summary>
        public int ReceiveBufferLength { get; set; } = 4096;  // 应用层读缓冲区大小

        // 服务端参数
        /// <summary>
        /// ListenBacklog，默认 128
        /// TcpListener.Start(backlog) 的参数，等待 Accept 的连接队列长度。
        ///它影响什么： 客户端完成三次握手后，连接进入“已完成握手、等待应用 Accept”的队列。如果服务器 Accept 慢（比如处理逻辑耗时），队列会堆积。队列满了，新连接可能被拒绝（RST）或丢弃（取决于平台）。
        /// </summary>
        public int ListenBacklog { get; set; } = 128;

        // 重连参数（第三阶段用，先留好）
        /// <summary>
        /// 最大重连次数。超过后 ConnectionManager 触发 OnReconnectFailed，不再自动重连
        /// </summary>
        public int MaxRetry { get; set; } = 10;
        /// <summary>
        /// 重连退避的基础延迟，单位秒。第一次重连等待约 BaseRetryDelaySec * random(0.5, 1.5)。
        /// </summary>
        public float BaseRetryDelaySec { get; set; } = 1f;
        /// <summary>
        /// 退避延迟的上限，单位秒。指数增长到这里就封顶，不再翻倍。
        /// </summary>
        public float MaxRetryDelaySec { get; set; } = 30f;
    }
}
