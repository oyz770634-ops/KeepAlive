using ProtoBuf;

namespace Network.Shared.Messages
{
    /// <summary>
    /// 客户端发聊天用 C2S_Chat msgId，服务端广播用 S2C_Chat msgId，body 结构相同。
    /// </summary>
    [ProtoContract]
    public class ChatMsg
    {
        [ProtoMember(1)] public string Text;
    }
}
