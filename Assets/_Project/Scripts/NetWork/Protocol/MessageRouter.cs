using System;
using System.Collections.Generic;
using UnityEngine;

namespace Network.Protocol
{
    /// <summary>
    /// 消息路由分发器
    /// </summary>
    public class MessageRouter
    {
        /// <summary>
        /// 核心字典：MsgID -> 对应的处理函数
        /// </summary>
        private readonly Dictionary<int, Action<byte[]>> _handlers = new Dictionary<int, Action<byte[]>>();

        /// <summary>
        /// 注册消息监听（业务层调用）
        /// </summary>
        public void Register(int msgId, Action<byte[]> handler)
        {
            if (handler == null) return;

            if (_handlers.ContainsKey(msgId))
            {
                // 允许一个消息号有多个监听者（使用多播委托）
                _handlers[msgId] += handler;
            }
            else
            {
                _handlers[msgId] = handler;
            }
        }

        /// <summary>
        /// 注销消息监听
        /// </summary>
        public void Unregister(int msgId, Action<byte[]> handler)
        {
            if (handler == null) return;

            if (_handlers.TryGetValue(msgId, out var existingHandler))
            {
                existingHandler -= handler;

                // 如果所有监听者都移除了，就把 Key 删掉
                if (existingHandler == null)
                {
                    _handlers.Remove(msgId);
                }
                else
                {
                    _handlers[msgId] = existingHandler;
                }
            }
        }

        /// <summary>
        /// 分发消息（协议层收到网络数据后调用）
        /// </summary>
        public void Dispatch(int msgId, byte[] body)
        {
            if (_handlers.TryGetValue(msgId, out var handler))
            {
                try
                {
                    // 执行注册的业务回调
                    handler?.Invoke(body);
                }
                catch (Exception e)
                {
                    // 防御：某个业务处理异常不能导致整个网络循环崩溃
                    Debug.LogError($"[MessageRouter] 处理 MsgId={msgId} 时发生异常: {e}");
                }
            }
            else
            {
                Debug.LogWarning($"[MessageRouter] 收到未注册的消息 MsgId={msgId}");
            }
        }

        /// <summary>
        /// 清空所有注册（通常在断线重连或退出游戏时调用）
        /// </summary>
        public void Clear()
        {
            _handlers.Clear();
        }
    }
}