using System;
using System.Collections.Generic;
using UnityEngine;

namespace Network.Protocol
{
    /// <summary>
    /// 核心是处理 TCP 的粘包和半包问题，同时兼顾 KCP 自带消息边界的特性。
    /// </summary>
    public class MessageCodec
    {
        // TCP 专用缓存区，用于存放还没凑齐一个完整包的半包数据
        private readonly List<byte> _tcpCache = new List<byte>(4096);

        /// <summary>
        /// 编码：把 MsgID 和 Body 加上包头拼装成最终要发送的 byte[]
        /// 通用（TCP和KCP都适用）
        /// </summary>
        public byte[] Encode(int msgId, byte[] body)
        {
            if (body == null) body = Array.Empty<byte>();

            int totalLength = ProtocolConst.HeaderSize + body.Length;

            if (totalLength > ProtocolConst.MaxPacketSize)
            {
                throw new Exception($"[MessageCodec] 包体过大，超限: {totalLength}");
            }

            byte[] packet = new byte[totalLength];

            // 1. 写入 Length (4字节)
            // 这里使用小端序（C# 默认），前后端需要约定好
            byte[] lengthBytes = BitConverter.GetBytes(totalLength);
            Buffer.BlockCopy(lengthBytes, 0, packet, 0, 4);

            // 2. 写入 MsgID (4字节)
            byte[] msgIdBytes = BitConverter.GetBytes(msgId);
            Buffer.BlockCopy(msgIdBytes, 0, packet, 4, 4);

            // 3. 写入 Body
            Buffer.BlockCopy(body, 0, packet, ProtocolConst.HeaderSize, body.Length);

            return packet;
        }

        /// <summary>
        /// KCP 解码：KCP自带消息边界，每次收到的直接就是完整包
        /// </summary>
        public bool DecodeKcp(byte[] data, int length, out int msgId, out byte[] body)
        {
            msgId = 0;
            body = null;

            if (length < ProtocolConst.HeaderSize) return false;

            // 校验长度
            int totalLength = BitConverter.ToInt32(data, 0);
            if (totalLength != length)
            {
                Debug.LogError($"[MessageCodec] KCP 包长度异常: 头部声明={totalLength}, 实际={length}");
                return false;
            }

            msgId = BitConverter.ToInt32(data, 4);

            int bodyLength = totalLength - ProtocolConst.HeaderSize;
            body = new byte[bodyLength];
            if (bodyLength > 0)
            {
                Buffer.BlockCopy(data, ProtocolConst.HeaderSize, body, 0, bodyLength);
            }

            return true;
        }

        /// <summary>
        /// TCP 解码：处理半包和粘包
        /// 将收到的数据喂入缓冲区，通过回调吐出解析出的完整包
        /// </summary>
        public void DecodeTcp(byte[] data, int length, Action<int, byte[]> onMessage)
        {
            if (data == null || length <= 0) return;

            // 1. 将新收到的数据追加到缓存区
            for (int i = 0; i < length; i++)
            {
                _tcpCache.Add(data[i]);
            }

            // 2. 循环尝试拆包（可能一次收到好几个完整的包）
            while (true)
            {
                // 连包头（8字节）都不够，说明是半包，退出等待下次数据
                if (_tcpCache.Count < ProtocolConst.HeaderSize) break;

                // 读取声明的总长度
                int totalLength = BitConverter.ToInt32(_tcpCache.ToArray(), 0);

                // 安全校验：防止恶意超大包或解析错误导致的死循环
                if (totalLength < ProtocolConst.HeaderSize || totalLength > ProtocolConst.MaxPacketSize)
                {
                    Debug.LogError($"[MessageCodec] 非法包长度: {totalLength}，强制清空缓存防崩溃");
                    _tcpCache.Clear();
                    return;
                }

                // 缓存里的数据还不够一个完整包，说明是半包，退出等待
                if (_tcpCache.Count < totalLength) break;

                // 凑齐了一个完整包，开始提取
                byte[] packet = _tcpCache.GetRange(0, totalLength).ToArray();

                // 从缓存中移除已处理的数据
                _tcpCache.RemoveRange(0, totalLength);

                // 解析出 MsgID 和 Body
                int msgId = BitConverter.ToInt32(packet, 4);
                int bodyLength = totalLength - ProtocolConst.HeaderSize;

                byte[] body = null;
                if (bodyLength > 0)
                {
                    body = new byte[bodyLength];
                    Buffer.BlockCopy(packet, ProtocolConst.HeaderSize, body, 0, bodyLength);
                }

                // 触发回调
                onMessage?.Invoke(msgId, body);
            }
        }

        /// <summary>
        /// 断线重连时清空缓存
        /// </summary>
        public void ClearTcpCache()
        {
            _tcpCache.Clear();
        }
    }
}