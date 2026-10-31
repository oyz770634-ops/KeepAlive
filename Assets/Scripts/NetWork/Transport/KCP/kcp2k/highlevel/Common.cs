using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace kcp2k
{
    public static class Common
    {
        // helper function to resolve host to IPAddress
        /// <summary>
        ///  域名解析 (ResolveHostname)
        ///作用：把传入的网址（如 "game.example.com"）转换成具体的 IP 地址。
        ///代码亮点：使用了 try-catch 捕获 SocketException。如果解析失败，会打印日志并返回 false。
        ///避坑指南（重点）：注意代码里的注释 
        ///NOTE: dns lookup is blocking. this can take a second.。DNS 解析是阻塞操作，如果网络不好可能会卡顿 1 秒甚至更久。千万不要在 Unity 的主线程（如 Start 或 Update）中直接调用它，否则你的游戏画面会直接卡死。建议在异步任务或连接前专门处理。
        /// </summary>
        /// <param name="hostname"></param>
        /// <param name="addresses"></param>
        /// <returns></returns>
        public static bool ResolveHostname(string hostname, out IPAddress[] addresses)
        {
            try
            {
                // NOTE: dns lookup is blocking. this can take a second.
                addresses = Dns.GetHostAddresses(hostname);
                return addresses.Length >= 1;
            }
            catch (SocketException exception)
            {
                Log.Info($"[KCP] Failed to resolve host: {hostname} reason: {exception}");
                addresses = null;
                return false;
            }
        }

        // if connections drop under heavy load, increase to OS limit.
        // if still not enough, increase the OS limit.
        /// <summary>
        /// Socket 缓冲区配置 (ConfigureSocketBuffers)
        ///作用：动态调整操作系统的 UDP 接收和发送缓冲区大小。
        ///代码亮点：
        ///它在设置前记录了初始大小，设置后打印了日志：RecvBuf = 初始=>现在(增大倍数)，方便你排查网络问题。
        ///使用了 try-catch，因为某些操作系统或平台（比如部分移动端）可能会限制你修改底层缓冲区大小。
        ///避坑指南：UDP 在丢包严重或高并发时会吞掉大量数据包（导致你的 KCP 一直重传）。如果发现游戏重连频繁或延迟极高，可以适当调大这两个值（但要受限于操作系统的最大限制）。
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="recvBufferSize"></param>
        /// <param name="sendBufferSize"></param>
        public static void ConfigureSocketBuffers(Socket socket, int recvBufferSize, int sendBufferSize)
        {
            // log initial size for comparison.
            // remember initial size for log comparison
            int initialReceive = socket.ReceiveBufferSize;
            int initialSend    = socket.SendBufferSize;

            // set to configured size
            try
            {
                socket.ReceiveBufferSize = recvBufferSize;
                socket.SendBufferSize    = sendBufferSize;
            }
            catch (SocketException)
            {
                Log.Warning($"[KCP] failed to set Socket RecvBufSize = {recvBufferSize} SendBufSize = {sendBufferSize}");
            }


            Log.Info($"[KCP] RecvBuf = {initialReceive}=>{socket.ReceiveBufferSize} ({socket.ReceiveBufferSize/initialReceive}x) SendBuf = {initialSend}=>{socket.SendBufferSize} ({socket.SendBufferSize/initialSend}x)");
        }

        // generate a connection hash from IP+Port.
        //
        // NOTE: IPEndPoint.GetHashCode() allocates.
        //  it calls m_Address.GetHashCode().
        //  m_Address is an IPAddress.
        //  GetHashCode() allocates for IPv6:
        //  https://github.com/mono/mono/blob/bdd772531d379b4e78593587d15113c37edd4a64/mcs/class/referencesource/System/net/System/Net/IPAddress.cs#L699
        //
        // => using only newClientEP.Port wouldn't work, because
        //    different connections can have the same port.
        /// <summary>
        /// 连接标识哈希 (ConnectionHash)
        ///作用：为每一个客户端连接生成一个唯一的数字 ID（通常用于 Dictionary 的 Key）。
        ///代码亮点：作者在注释里吐了个槽。原本可以直接用 Port（端口）作为标识，但不同 IP 的客户端可能恰好用同一个端口。所以这里用了 EndPoint.GetHashCode()（结合 IP + 端口生成）。
        ///底层细节：注释提到 IPEndPoint.GetHashCode() 在 Mono 环境下（Unity 的旧版运行时）会产生内存分配（GC）。对于性能要求极致的项目，这是一个已知的微小开销点，但在大多数场景下可以忽略不计。
        /// </summary>
        /// <param name="endPoint"></param>
        /// <returns></returns>
        public static int ConnectionHash(EndPoint endPoint) =>
            endPoint.GetHashCode();

        // cookies need to be generated with a secure random generator.
        // we don't want them to be deterministic / predictable.
        // RNG is cached to avoid runtime allocations.

        static readonly RNGCryptoServiceProvider cryptoRandom = new RNGCryptoServiceProvider();
        static readonly byte[] cryptoRandomBuffer = new byte[4];
        /// <summary>
        /// 安全 Cookie 生成 (GenerateCookie)
        ///作用：生成一个 4 字节的无符号整数（uint），主要用于 KCP 的安全握手（防止伪造 IP 发起 DDoS 攻击）。
        /// </summary>
        /// <returns></returns>
        public static uint GenerateCookie()
        {
            cryptoRandom.GetBytes(cryptoRandomBuffer);
            return BitConverter.ToUInt32(cryptoRandomBuffer, 0);
        }
    }
}
