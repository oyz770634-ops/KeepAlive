// kcp specific error codes to allow for error switching, localization,
// translation to Mirror errors, etc.
namespace kcp2k
{
    /// <summary>
    /// 这个枚举 ErrorCode 是 kcp2k 用来定义和分类网络错误类型的。当你的连接出现问题时，kcp2k 会在底层的回调（比如 OnError）中把这个枚举传给你，告诉你到底发生了什么。
    /// </summary>
    public enum ErrorCode : byte
    {
        /// <summary>
        /// DnsResolve (域名解析失败)
        ///含义：无法将你提供的域名（如 "game.example.com"）解析为 IP 地址。
        ///触发场景：客户端 Connect 时。
        ///排查方向：检查玩家是否有网、域名是否拼写错误、DNS 服务器是否宕机（国内某些网络环境下很容易出现）。
        /// </summary>
        DnsResolve,       // failed to resolve a host name
        /// <summary>
        /// Timeout (超时)
        ///含义：Ping 超时或连接彻底断开（死链）。
        ///触发场景：在游戏运行过程中，客户端和服务器长时间没有收到对方的任何数据（超过了 KCP 配置的 Timeout 时间）。
        ///排查方向：玩家网络极度不稳定、手机切后台导致断网、或者服务器端崩溃了。
        /// </summary>
        Timeout,          // ping timeout or dead link
        /// <summary>
        /// Congestion (拥塞)
        ///含义：发送的消息数量超过了传输层或网络能处理的极限。
        ///触发场景：你在一瞬间往网络里狂塞了几百个大包，导致底层缓冲区爆满，数据发不出去。
        ///排查方向：检查你的业务逻辑，是不是在短时间内疯狂发送位置同步或大量技能特效数据，需要做限流或分帧处理。
        /// </summary>
        Congestion,       // more messages than transport / network can process
        /// <summary>
        /// InvalidReceive (收到无效数据)
        ///含义：接收到了非法的数据包。
        ///触发场景：黑客攻击，或者有人用其他程序故意向你的服务器/客户端端口发送垃圾 UDP 包。
        ///排查方向：这是底层的安全防线，通常不需要普通业务层处理。kcp2k 发现包格式不对，会直接丢弃并报这个错，保护你的游戏不被崩溃。
        /// </summary>
        InvalidReceive,   // recv invalid packet (possibly intentional attack)
        /// <summary>
        /// InvalidSend (发送无效数据)
        ///含义：开发者（你）试图发送不符合规范的数据。
        ///触发场景：比如数据包超出了 KCP 的最大分片限制，或者尝试在一个已关闭的客户端上发数据。
        ///排查方向：检查你调用 Send() 之前的逻辑，确保数据大小合规。
        /// </summary>
        InvalidSend,      // user tried to send invalid data
        /// <summary>
        /// ConnectionClosed (连接已关闭)
        ///含义：连接被主动关闭，或者意外断开。
        ///触发场景：服务器踢人、客户端主动退出游戏调用 Disconnect()，或者收到对方的 FIN 包。
        ///排查方向：这是正常的网络生命周期，但在 OnError 里出现，说明可能是在你不知情的情况下被动断开了。
        /// </summary>
        ConnectionClosed, // connection closed voluntarily or lost involuntarily
        /// <summary>
        /// Unexpected (未知错误)
        ///含义：预料之外的异常（Exception），需要开发者修复。
        ///触发场景：比如代码里发生了空指针异常（NullReferenceException），或者除零错误等真正的 Bug。
        ///排查方向：这最严重！如果是这个错误，你需要去查看 Unity 控制台里的详细堆栈日志（StackTrace），看看是不是自己改坏了什么代码。
        /// </summary>
        Unexpected        // unexpected error / exception, requires fix.
    }
}