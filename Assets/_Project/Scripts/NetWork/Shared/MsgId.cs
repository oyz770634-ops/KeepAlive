namespace Network.Shared
{
    /// <summary>
    /// 消息号枚举
    /// </summary>
    public enum MsgId
    {
        // 系统级 (1 - 99)
        Ping = 1,
        Pong = 2,

        // 业务级 (1000+)
        C2S_Login = 1001,   // Client to Server
        S2C_Login = 1002,   // Server to Client

        C2S_Chat = 2001,
        S2C_Chat = 2002,
    }
}