namespace Network.Protocol
{
    /// <summary>
    /// 序列化器接口：对象 ↔ 字节数组。
    /// 协议层通过它把业务对象转成 body，再交给 MessageCodec 加包头。
    /// </summary>
    public interface IMessageSerializer
    {
        /// <summary>
        /// 序列化：对象 → 字节数组。message 为 null 时返回空数组。
        /// </summary>
        byte[] Serialize<T>(T message);

        /// <summary>
        /// 反序列化：字节数组 → 对象。data 为 null 或空时返回 default(T)。
        /// </summary>
        T Deserialize<T>(byte[] data);
    }
}
