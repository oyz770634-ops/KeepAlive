using System;
using System.Net;
using System.Net.Sockets;

namespace kcp2k
{
    /// <summary>
    /// 这个 Extensions.cs 文件是 kcp2k 中极其核心的底层网络收发工具类。它通过 C# 的扩展方法（this 关键字），为原生的 Socket 类“贴”上了非阻塞的 UDP 收发功能。
    /// </summary>
    public static class Extensions
    {
        // ArraySegment as HexString for convenience
        /// <summary>
        /// 作用：把字节数组打印成十六进制的字符串（如 01-AF-3C）。
        ///场景：纯粹的调试工具。当你抓包发现数据不对时，用这个打个 Log 方便排查。
        /// </summary>
        /// <param name="segment"></param>
        /// <returns></returns>
        public static string ToHexString(this ArraySegment<byte> segment) =>
            BitConverter.ToString(segment.Array, segment.Offset, segment.Count);

        // non-blocking UDP send.
        // allows for reuse when overwriting KcpServer/Client (i.e. for relays).
        // => wrapped with Poll to avoid WouldBlock allocating new SocketException.
        // => wrapped with try-catch to ignore WouldBlock exception.
        // make sure to set socket.Blocking = false before using this!
        /// <summary>
        /// 作用：非阻塞地向指定的远端（remoteEP）发送数据。
        ///场景：服务端向特定的客户端发送数据。因为服务端可能面对成百上千个不同的客户端 IP 和端口，必须指定目标地址。
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="data"></param>
        /// <param name="remoteEP"></param>
        /// <returns></returns>
        public static bool SendToNonBlocking(this Socket socket, ArraySegment<byte> data, EndPoint remoteEP)
        {
            try
            {
                
                if (!socket.Poll(0, SelectMode.SelectWrite)) return false;
                socket.SendTo(data.Array, data.Offset, data.Count, SocketFlags.None, remoteEP);
                return true;
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode == SocketError.WouldBlock) return false;
                throw;
            }
        }

        // non-blocking UDP send.
        // allows for reuse when overwriting KcpServer/Client (i.e. for relays).
        // => wrapped with Poll to avoid WouldBlock allocating new SocketException.
        // => wrapped with try-catch to ignore WouldBlock exception.
        // make sure to set socket.Blocking = false before using this!
        /// <summary>
        /// 作用：非阻塞地发送数据。
        ///场景：客户端向服务端发送数据。因为客户端 Connect 之后，Socket 已经“绑定”了服务端的地址，所以不需要再传 remoteEP，直接用 Send 即可。这减少了一次参数传递和地址解析。
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public static bool SendNonBlocking(this Socket socket, ArraySegment<byte> data)
        {
            try
            {
                // when using non-blocking sockets, SendTo may return WouldBlock.
                // in C#, WouldBlock throws a SocketException, which is expected.
                // unfortunately, creating the SocketException allocates in C#.
                // let's poll first to avoid the WouldBlock allocation.
                // note that this entirely to avoid allocations.
                // non-blocking UDP doesn't need Poll in other languages.
                // and the code still works without the Poll call.
                if (!socket.Poll(0, SelectMode.SelectWrite)) return false;

                // SendTo allocates. we used bound Send.
                socket.Send(data.Array, data.Offset, data.Count, SocketFlags.None);
                return true;
            }
            catch (SocketException e)
            {
                // for non-blocking sockets, SendTo may throw WouldBlock.
                // in that case, simply drop the message. it's UDP, it's fine.
                if (e.SocketErrorCode == SocketError.WouldBlock) return false;

                // otherwise it's a real socket error. throw it.
                throw;
            }
        }

        // non-blocking UDP receive.
        // allows for reuse when overwriting KcpServer/Client (i.e. for relays).
        // => wrapped with Poll to avoid WouldBlock allocating new SocketException.
        // => wrapped with try-catch to ignore WouldBlock exception.
        // make sure to set socket.Blocking = false before using this!
        /// <summary>
        /// 作用：非阻塞地接收数据，并输出数据来源（ref EndPoint remoteEP）。
        ///场景：服务端接收数据。必须知道这包数据是哪个客户端发来的，才能回发。
        ///注意：代码注释里提到了一个坑。ReceiveFrom 为了返回发送方的 IP 和端口，底层的 Mono/C# 实现会 new 一个 IPEndPoint 对象，这会产生 内存分配（GC Alloc）。作者在注释里也无奈地标明了这一点。
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="recvBuffer"></param>
        /// <param name="data"></param>
        /// <param name="remoteEP"></param>
        /// <returns></returns>
        public static bool ReceiveFromNonBlocking(this Socket socket, byte[] recvBuffer, out ArraySegment<byte> data, ref EndPoint remoteEP)
        {
            data = default;

            try
            {
                if (!socket.Poll(0, SelectMode.SelectRead)) return false;
                int size = socket.ReceiveFrom(recvBuffer, 0, recvBuffer.Length, SocketFlags.None, ref remoteEP);
                data = new ArraySegment<byte>(recvBuffer, 0, size);
                return true;
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode == SocketError.WouldBlock) return false;
                throw;
            }
        }

        // non-blocking UDP receive.
        // allows for reuse when overwriting KcpServer/Client (i.e. for relays).
        // => wrapped with Poll to avoid WouldBlock allocating new SocketException.
        // => wrapped with try-catch to ignore WouldBlock exception.
        // make sure to set socket.Blocking = false before using this!
        /// <summary>
        /// 作用：非阻塞地接收数据。
        /// 场景：客户端接收服务端的数据。客户端只有一个对端（服务端），不需要知道是谁发的，直接用 Receive 读数据，这个方法没有任何内存分配（Zero Allocation）。
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="recvBuffer"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public static bool ReceiveNonBlocking(this Socket socket, byte[] recvBuffer, out ArraySegment<byte> data)
        {
            data = default;

            try
            {
                //以非阻塞的方式，瞬间检查一下 Socket 当前是否“可读”。
                if (!socket.Poll(0, SelectMode.SelectRead)) return false;
                int size = socket.Receive(recvBuffer, 0, recvBuffer.Length, SocketFlags.None);
                data = new ArraySegment<byte>(recvBuffer, 0, size);
                return true;
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode == SocketError.WouldBlock) return false;
                throw;
            }
        }
    }
}