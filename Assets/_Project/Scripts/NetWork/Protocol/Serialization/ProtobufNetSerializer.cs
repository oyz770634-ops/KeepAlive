using System;
using System.IO;

namespace Network.Protocol
{
    /// <summary>
    /// 基于 protobuf-net 的序列化实现。
    /// 消息类需要加 [ProtoContract]，字段加 [ProtoMember(n)]。
    /// </summary>
    public class ProtobufNetSerializer : IMessageSerializer
    {
        public byte[] Serialize<T>(T message)
        {
            if (message == null) return Array.Empty<byte>();

            using (var ms = new MemoryStream())
            {
                ProtoBuf.Serializer.Serialize(ms, message);
                return ms.ToArray();
            }
        }

        public T Deserialize<T>(byte[] data)
        {
            if (data == null || data.Length == 0) return default;

            using (var ms = new MemoryStream(data))
            {
                return ProtoBuf.Serializer.Deserialize<T>(ms);
            }
        }

        /// <summary>
        /// 预热指定类型的序列化器。在 AOT 平台（iOS/IL2CPP）上建议在启动时调用，
        /// 避免首次运行时反射构建元数据的开销和潜在问题。
        /// </summary>
        public static void Prepare<T>()
        {
            ProtoBuf.Serializer.PrepareSerializer<T>();
        }
    }
}
