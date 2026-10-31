using System.IO;

namespace kcp2k
{
    // KCP Segment Definition
    /// <summary>
    /// 报文段结构
    /// </summary>
    internal class Segment
    {
        /// <summary>
        /// 客户端ID
        /// </summary>
        internal uint conv;     // conversation
        /// <summary>
        /// 报文类型
        /// </summary>
        internal uint cmd;      // command, e.g. Kcp.CMD_ACK etc.
        /// <summary>
        /// 分片编号
        /// </summary>
        internal uint frg;
        /// <summary>
        /// 滑动窗口大小
        /// </summary>
        internal uint wnd;      // window size that the receive can currently receive
        /// <summary>
        /// 当前发送的时间戳
        /// </summary>
        internal uint ts;       // timestamp
        /// <summary>
        /// 序列号，用于重发
        /// </summary>
        internal uint sn;       // sequence number
        /// <summary>
        /// 未确认的序列号
        /// </summary>
        internal uint una;
        /// <summary>
        /// 超时重传时间戳
        /// </summary>
        internal uint resendts; // resend timestamp
        /// <summary>
        /// 重传超时时间RTO
        /// </summary>
        internal int  rto;
        /// <summary>
        /// 快速确认计数用于快速重传
        /// </summary>
        internal uint fastack;
        /// <summary>
        /// 重传次数
        /// </summary>
        internal uint xmit;     // retransmit count
        /// <summary>
        /// 数据段
        /// </summary>
        // we need an auto scaling byte[] with a WriteBytes function.
        // MemoryStream does that perfectly, no need to reinvent the wheel.
        // note: no need to pool it, because Segment is already pooled.
        // -> default MTU as initial capacity to avoid most runtime resizing/allocations
        //
        // .data is only used for Encode(), which always fits it into a buffer.
        // the buffer is always Kcp.buffer. Kcp ctor creates the buffer of size:
        // (mtu + OVERHEAD) * 3 bytes.
        // in other words, Encode only ever writes up to the above amount of bytes.
        internal MemoryStream data = new MemoryStream(Kcp.MTU_DEF);

        // ikcp_encode_seg
        // encode a segment into buffer.
        // buffer is always Kcp.buffer. Kcp ctor creates the buffer of size:
        // (mtu + OVERHEAD) * 3 bytes.
        // in other words, Encode only ever writes up to the above amount of bytes.
        internal int Encode(byte[] ptr, int offset)
        {
            int previousPosition = offset;

            offset += Utils.Encode32U(ptr, offset, conv);
            offset += Utils.Encode8u(ptr, offset, (byte)cmd);
            // IMPORTANT kcp encodes 'frg' as 1 byte.
            // so we can only support up to 255 fragments.
            // (which limits max message size to around 288 KB)
            offset += Utils.Encode8u(ptr, offset, (byte)frg);
            offset += Utils.Encode16U(ptr, offset, (ushort)wnd);
            offset += Utils.Encode32U(ptr, offset, ts);
            offset += Utils.Encode32U(ptr, offset, sn);
            offset += Utils.Encode32U(ptr, offset, una);
            offset += Utils.Encode32U(ptr, offset, (uint)data.Position);

            int written = offset - previousPosition;
            return written;
        }

        // reset to return a fresh segment to the pool
        internal void Reset()
        {
            conv = 0;
            cmd = 0;
            frg = 0;
            wnd = 0;
            ts  = 0;
            sn  = 0;
            una = 0;
            rto = 0;
            xmit = 0;
            resendts = 0;
            fastack  = 0;

            // keep buffer for next pool usage, but reset length (= bytes written)
            data.SetLength(0);
        }
    }
}
