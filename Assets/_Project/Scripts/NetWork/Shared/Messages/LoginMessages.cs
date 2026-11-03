using ProtoBuf;

namespace Network.Shared.Messages
{
    [ProtoContract]
    public class LoginReq
    {
        [ProtoMember(1)] public string Name;
        [ProtoMember(2)] public string Password;
    }

    [ProtoContract]
    public class LoginRsp
    {
        [ProtoMember(1)] public int PlayerId;
        [ProtoMember(2)] public string Welcome;
    }
}