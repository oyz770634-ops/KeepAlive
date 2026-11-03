using KeepAlive.Core;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KeepAlive.Framework.UI
{
    public abstract class UIBase : MonoBehaviour
    {
        public string UIKey { get; private set; }
        public UILayer Layer { get; private set; }
        /// <summary>
        /// ★ 追踪所有订阅，OnDestroy 时批量取消
        /// </summary>
        private readonly List<(Type type, Delegate callback)> _subscriptions = new();
        internal void Init(string key, UILayer layer)
        {
            UIKey = key;
            Layer = layer;
        }

        public virtual void OnInit() { }
        public virtual void OnShow() { }
        public virtual void OnHide() { }

        public virtual void OnDestroyUI()
        {
            // 可以在子类重写时调用，用于彻底清理
        }

        // ── 便捷的事件绑定，自动在 GameObject 销毁时取消 ──
        /// <summary>
        /// 订阅事件。UI 销毁时自动取消（通过 OnDestroy 批量清理）。
        /// </summary>
        protected void Subscribe<T>(Action<T> callback) where T : IEvent
        {
            if (callback == null) return;
            EventBus.Subscribe(callback);
            _subscriptions.Add((typeof(T), callback));
        }

        /// <summary>
        /// 手动取消订阅。一般不需要，OnDestroy 会兜底。
        /// </summary>
        protected void Unsubscribe<T>(Action<T> callback) where T : IEvent
        {
            if (callback == null) return;
            EventBus.Unsubscribe(callback);
            _subscriptions.RemoveAll(s => s.type == typeof(T) && s.callback.Equals(callback));
        }

        /// <summary>
        /// 兜底：UI 销毁时自动取消所有订阅。
        /// 子类若重写 OnDestroy，必须调用 base.OnDestroy()，否则订阅不会被清理。
        /// </summary>
        protected virtual void OnDestroy()
        {
            foreach (var (type, callback) in _subscriptions)
                EventBus.Unsubscribe(type, callback);
            _subscriptions.Clear();
        }
    }
}
