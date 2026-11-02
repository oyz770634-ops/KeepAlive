using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace Network.Transport
{

    /// <summary>
    /// Disconnected：未连接，或已断开。Connecting：连接中（仅客户端用）。Connected：已连接，可收发。Closing：正在关闭，过渡状态。
    /// </summary>
    public enum ChannelState
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Closing = 3
    }
}
