using System;
using System.Collections.Generic;
using UnityEngine;

namespace KeepAlive.Core
{
    public interface IEvent { }

    public static class EventBus
    {
        // 主线程模型：订阅/发布都在主线程，无需锁和并发字典
        private static readonly Dictionary<Type, Delegate> _eventDict = new();
        /// <summary>
        /// 注册事件委托
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="callback"></param>
        public static void Subscribe<T>(Action<T> callback) where T : IEvent
        {
            if (callback == null) return;
            var key = typeof(T);
            _eventDict.TryGetValue(key, out var existing);
            _eventDict[key] = Delegate.Combine(existing, callback);
        }
        /// <summary>
        /// 注销事件委托
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="callback"></param>
        public static void Unsubscribe<T>(Action<T> callback) where T : IEvent
        {
            if (callback == null) return;
            var key = typeof(T);
            if (!_eventDict.TryGetValue(key, out var existing)) return;

            var newDelegate = Delegate.Remove(existing, callback);
            if (newDelegate == null) _eventDict.Remove(key);
            else _eventDict[key] = newDelegate;
        }
        /// <summary>
        /// 发布事件
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="eventData"></param>
        public static void Publish<T>(T eventData) where T : IEvent
        {
            if (!_eventDict.TryGetValue(typeof(T), out var existing) || existing == null) return;

            // 快照：防止回调内部订阅/退订破坏遍历
            var list = existing.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    // 强类型调用，无反射、无装箱
                    ((Action<T>)list[i]).Invoke(eventData);
                }
                catch (Exception e)
                {
                   KeepAlive.Core.Logger.Error("EventBus",
                        $"[EventBus] {typeof(T).Name} 回调 " +
                        $"{list[i].Method.DeclaringType?.Name}.{list[i].Method.Name} 异常:\n{e}");
                }
            }
        }

        public static void Clear() => _eventDict.Clear();

        /// <summary>
        /// 原因：UIBase 追踪订阅时需要按 (Type, Delegate) 批量取消，泛型版本无法通过反射高效调用，加一个非泛型重载最直接。
        /// </summary>
        /// <param name="eventType"></param>
        /// <param name="callback"></param>
        internal static void Unsubscribe(Type eventType, Delegate callback)
        {
            if (callback == null) return;
            if (!_eventDict.TryGetValue(eventType, out var existing)) return;
            var newDelegate = Delegate.Remove(existing, callback);
            if (newDelegate == null) _eventDict.Remove(eventType);
            else _eventDict[eventType] = newDelegate;
        }
    }
}