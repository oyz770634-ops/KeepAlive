using Network.Protocol;
using System;
using UnityEngine;

namespace Network.Server
{
    /// <summary>
    /// ServerSession 不是 server 的别名——它是"单个客户端"的代理
    /// 它只持有两个委托，不持有 IServerTransport，也不持有 TcpConnection/KcpConnection，也不持有 GameServerManager。
    /// 它是"最小依赖"的——只需要知道"怎么给这个客户端发数据"和"怎么关这个客户端"，其他一概不知道。
    /// </summary>
    public class ServerSession//ServerSession 是"一个客户端"的代表
    {
        /// <summary>
        /// 客户端ID
        /// </summary>
        public int PlayerId { get; private set; }
        public bool IsAuthenticated { get; set; } // 是否已登录验证

        // 记录最后一次收到该玩家消息的时间（用于服务端心跳剔除）
        public float LastRecvTime { get; set; }

        // 【核心新增】每个 Session 必须有自己独立的 TCP 拆包缓存
        public MessageCodec Codec { get; private set; }

        private readonly Action<byte[]> _sendAction;// 只持有"怎么发"
        private readonly Action _closeAction; // 只持有"怎么关"

        public ServerSession(int playerId, Action<byte[]> sendAction, Action closeAction)
        {
            PlayerId = playerId;
            _sendAction = sendAction;
            _closeAction = closeAction;
            IsAuthenticated = false;
            //这里使用的是Unity内部类因此可能限制一定移值性
            LastRecvTime = Time.realtimeSinceStartup;
            Codec = new MessageCodec(); // 实例化独立的拆包器
        }

        public void Send(byte[] data)
        {
            _sendAction?.Invoke(data);
        }

        public void Close()
        {
            _closeAction?.Invoke();
        }
    }
}
