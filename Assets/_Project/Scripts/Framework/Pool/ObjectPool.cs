using System.Collections.Generic;
using UnityEngine;

namespace KeepAlive.Framework.Pool
{
    public class ObjectPool<T> : IObjectPool where T : Component
    {
        private readonly T _prefab;
        private readonly Transform _parentRoot;
        private readonly int _maxSize;   // 0 = 无限
        private readonly Queue<T> _poolQueue = new();
        private readonly HashSet<T> _inPool = new();   // ★ 池中对象集合，防重复入池

        public int ActiveCount { get; private set; }
        public int InactiveCount => _poolQueue.Count;
        public bool IsDisposed { get; private set; }

        public ObjectPool(T prefab, int initialSize = 0, Transform root = null, int maxSize = 0)
        {
            _prefab = prefab;
            _parentRoot = root;
            _maxSize = maxSize;
            Prewarm(initialSize);
        }

        // ── 预热 ─────────────────────────────────────────────

        public void Prewarm(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var obj = CreateObject();
                ReturnToPool(obj);
            }
        }

        /// <summary>
        /// 分帧预热（Loading 阶段推荐），避免一次性 Instantiate 卡帧。
        /// </summary>
        public async System.Threading.Tasks.Task PrewarmAsync(int count, int perFrame = 10)
        {
            for (int i = 0; i < count; i++)
            {
                var obj = CreateObject();
                ReturnToPool(obj);

                if ((i + 1) % perFrame == 0)
                    await System.Threading.Tasks.Task.Yield();
            }
        }

        // ── 取 / 还 ──────────────────────────────────────────

        public T Get()
        {
            if (IsDisposed)
            {
                Debug.LogError($"[Pool] 池子已销毁，无法 Get: {_prefab?.name}");
                return null;
            }

            T obj;
            if (_poolQueue.Count > 0)
            {
                obj = _poolQueue.Dequeue();
                _inPool.Remove(obj);
            }
            else
            {
                if (_maxSize > 0 && ActiveCount >= _maxSize)
                {
                    Debug.LogError($"[Pool] 超过 MaxSize({_maxSize})，拒绝扩容: {_prefab?.name}");
                    return null;
                }
                obj = CreateObject();
                Debug.LogWarning($"[Pool] 对象池自动扩容: {_prefab?.name}");
            }

            obj.gameObject.SetActive(true);
            if (obj is IPoolable poolable) poolable.OnSpawn();

            ActiveCount++;
            return obj;
        }

        public void Release(T obj)
        {
            if (obj == null) return;

            // ★ 池子已销毁：外部未归还的对象直接销毁，不干扰计数
            if (IsDisposed)
            {
                Object.Destroy(obj.gameObject);
                return;
            }

            // ★ 防重复入池：用集合判断，不依赖 activeSelf
            if (_inPool.Contains(obj)) return;

            if (obj is IPoolable poolable) poolable.OnDespawn();

            ReturnToPool(obj);
            ActiveCount--;
        }

        // ── 销毁 ─────────────────────────────────────────────

        /// <summary>
        /// 销毁整个池子（切场景时由 PoolManager.ClearAll 调用）。
        /// ★ 真正 Object.Destroy 每一个池中对象。
        /// </summary>
        public void Clear()
        {
            IsDisposed = true;

            while (_poolQueue.Count > 0)
            {
                var obj = _poolQueue.Dequeue();
                if (obj != null) Object.Destroy(obj.gameObject);
            }
            _inPool.Clear();
            ActiveCount = 0;
        }

        // ── 内部辅助 ─────────────────────────────────────────

        private T CreateObject()
        {
            T obj = Object.Instantiate(_prefab, _parentRoot);
            obj.gameObject.SetActive(false);
            if (obj is IPoolable poolable) poolable.OnCreate();   // ★ 首次创建钩子
            return obj;
        }

        private void ReturnToPool(T obj)
        {
            obj.gameObject.SetActive(false);
            _poolQueue.Enqueue(obj);
            _inPool.Add(obj);
        }
    }

    /// <summary>
    /// 非泛型接口，供 PoolManager.ClearAll 统一遍历调用。
    /// </summary>
    internal interface IObjectPool
    {
        void Clear();
    }
}
