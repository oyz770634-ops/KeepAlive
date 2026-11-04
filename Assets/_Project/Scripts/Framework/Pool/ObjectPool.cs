using System;
using System.Collections.Generic;
using UnityEngine;

namespace KeepAlive.Framework.Pool
{
    public class ObjectPool<T> : IObjectPool where T : Component
    {
        /// <summary>
        /// 模板
        /// </summary>
        private readonly T _prefab;
        /// <summary>
        /// 父节点（PoolRoot 下）
        /// </summary>
        private readonly Transform _parentRoot;
        /// <summary>
        /// 最大存在上限,0 = 无限
        /// </summary>
        private readonly int _maxSize;
        /// <summary>
        /// 空闲队列
        /// </summary>
        private readonly Queue<T> _poolQueue = new();
        /// <summary>
        ///现在 _inPool 只由 ReturnToPool（Add）和 Get（Remove）维护，Release/Clear 都不读它。它是 _poolQueue 的镜像，语义上是"池中对象的快速查找表"，未来如果想做 Contains(obj) 查询可以用。如果你想彻底删掉它也可以，不影响功能——但那是另一个改动，先不混进来。
        /// </summary>
        private readonly HashSet<T> _inPool = new();

        /// <summary>
        /// 新增：活跃对象集合（已 Get 未 Release）
        /// </summary>
        private readonly HashSet<T> _activeObjects = new();

        /// <summary>
        /// 在外的对象数
        /// </summary>
        public int ActiveCount { get; private set; }
        public int InactiveCount => _poolQueue.Count;
        /// <summary>
        /// 用于指明当前Pool是否被销毁
        /// </summary>
        public bool IsDisposed { get; private set; }

        public ObjectPool(T prefab, int initialSize = 0, Transform root = null, int maxSize = 0)
        {
            _prefab = prefab;
            _parentRoot = root;
            _maxSize = maxSize;
            Prewarm(initialSize);
        }

        // ── 预热 ─────────────────────────────────────────────
        /// <summary>
        /// 提前缓存加载，Prewarm 同步：构造函数用，适合小数量（比如 10 个），一次性 Instantiate 可接受。
        /// </summary>
        /// <param name="count"></param>
        public void Prewarm(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var obj = CreateObject();
                if (obj != null) ReturnToPool(obj);   // ★ 判空
               
            }
        }

        /// <summary>
        /// 分帧预热（Loading 阶段推荐），避免一次性 Instantiate 卡帧。
        /// PrewarmAsync 分帧：Loading 阶段用，大数量时避免卡帧。
        /// </summary>
        public async System.Threading.Tasks.Task PrewarmAsync(int count, int perFrame = 10)
        {
            for (int i = 0; i < count; i++)
            {
                var obj = CreateObject();
                if (obj != null) ReturnToPool(obj);   // ★ 判空

                if ((i + 1) % perFrame == 0)
                    await System.Threading.Tasks.Task.Yield();
            }
        }

        // ── 取 / 还 ──────────────────────────────────────────
        /// <summary>
        /// 从池中取出一个Object
        /// </summary>
        /// <returns></returns>
        public T Get()
        {
            if (IsDisposed)
            {
                KeepAlive.Core.Logger.Error("ObjectPool",$"[Pool] 池子已销毁，无法 Get: {_prefab?.name}");
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
                    KeepAlive.Core.Logger.Error("ObjectPool",$"[Pool] 超过 MaxSize({_maxSize})，拒绝扩容: {_prefab?.name}");
                    return null;
                }
                obj = CreateObject();
                if (obj == null) return null;   // ★ CreateObject 内部失败（见下文）
                KeepAlive.Core.Logger.Warning("ObjectPool",$"[Pool] 对象池自动扩容: {_prefab?.name}");
            }

            obj.gameObject.SetActive(true);
            // ★ 钩子独立 try-catch：失败不影响后续状态修正
            bool spawnOk = true;
            if (obj is IPoolable poolable)
            {
                try { poolable.OnSpawn(); }
                catch (Exception e)
                {
                    spawnOk = false;
                   KeepAlive.Core.Logger. Error("ObjectPool",$"[Pool] OnSpawn 异常，对象将销毁不回池: {_prefab?.name} | {e.Message}");
                }
            }
            if (!spawnOk)
            {
                // 对象状态不可信 → 直接销毁，不返回给调用方
                UnityEngine.Object.Destroy(obj.gameObject);
                return null;
            }

            _activeObjects.Add(obj);   // ★ 登记活跃
            ActiveCount++;
            return obj;
        }
        /// <summary>
        /// 归还一个Object回池中
        /// </summary>
        /// <param name="obj"></param>
        public void Release(T obj)
        {
            if (obj == null) return;

            // 池子已销毁：外部未归还的对象直接销毁，不干扰计数
            if (IsDisposed)
            {
                UnityEngine.Object.Destroy(obj.gameObject);
                return;
            }
            // 不在活跃集合 → 重复 Release / 跨池 Release，拒绝
            // 替代原来的 _inPool.Contains 判断，同时防重复和跨池污染）
            // 防重复入池：用集合判断，不依赖 activeSelf
            if (!_activeObjects.Remove(obj))
            {
               KeepAlive.Core.Logger.Warning("ObjectPool",$"[Pool] Release 非活跃对象，忽略: {(obj != null ? obj.name : "null")}");
                return;
            }

            // ★ 状态先闭合：对象已从活跃集合移除，计数立即减，不依赖钩子
            ActiveCount--;

            // ★ 钩子独立 try-catch
            bool despawnOk = true;
            if (obj is IPoolable poolable)
            {
                try { poolable.OnDespawn(); }
                catch (Exception e)
                {
                    despawnOk = false;
                   KeepAlive.Core.Logger.Error("ObjectPool",$"[Pool] OnDespawn 异常，对象将销毁不回池: {_prefab?.name} | {e.Message}");
                }
            }

            // 根据钩子成败决定去向
            if (despawnOk && obj != null)
            {
                ReturnToPool(obj);
            }
            else if (obj != null)
            {
                // OnDespawn 失败（或对象已被 Unity 销毁）→ 不回池
                UnityEngine.Object.Destroy(obj.gameObject);
            }
        }

        // ── 销毁 ─────────────────────────────────────────────

        /// <summary>
        /// 销毁整个池子（切场景时由 PoolManager.ClearAll 调用）。
        /// ★ 真正 Object.Destroy 每一个池中对象。
        /// </summary>
        public void Clear()
        {
            if (IsDisposed) return;
            // ① 活跃对象：OnDespawn → Destroy
            if (_activeObjects.Count > 0)
            {
                var snapshot = new List<T>(_activeObjects);   // 快照，防遍历中修改
                _activeObjects.Clear();
                ActiveCount = 0;

                foreach (var obj in snapshot)
                {
                    if (obj == null) continue;
                    try
                    {
                        if (obj is IPoolable poolable) poolable.OnDespawn();
                    }
                    catch (System.Exception e)
                    {
                       KeepAlive.Core.Logger. Error("ObjectPool",$"[Pool] Clear 时 OnDespawn 异常: {e.Message}");
                    }
                    UnityEngine.Object.Destroy(obj.gameObject);
                }
            }
            // ② 标记销毁，阻断后续 Get/Release
            IsDisposed = true;
            // ③ 空闲对象：已在归还时 Despawn 过，直接 Destroy
            while (_poolQueue.Count > 0)
            {
                var obj = _poolQueue.Dequeue();
                if (obj != null) UnityEngine.Object.Destroy(obj.gameObject);
            }
            _inPool.Clear();
            ActiveCount = 0;
        }

        // ── 内部辅助 ─────────────────────────────────────────

        private T CreateObject()
        {
            // ★ prefab 被外部销毁（如 ResourceManager.ClearAll 强制清理、业务提前 Dispose 句柄）
            //   Instantiate 会抛 ArgumentException，提前拦截为"日志 + 返回 null"
            if (_prefab == null)   // Unity 伪 null：prefab 已被外部销毁
            {
                KeepAlive.Core.Logger.Error( "Pool",
            "池的 prefab 已失效（可能被 ResourceManager.ClearAll 清理或业务提前释放句柄），无法创建对象。" +
            "请确认：1) PoolManager.ClearAll 在 ResourceManager.ClearAll 之前调用；" +
            "2) 池使用期间业务持有 prefab 的资源句柄。");
                return null;
            }
            T obj = UnityEngine.Object.Instantiate(_prefab, _parentRoot);
            obj.gameObject.SetActive(false);
            if (obj is IPoolable poolable)
            {
                try { poolable.OnCreate(); }
                catch (Exception e)
                {
                    KeepAlive.Core.Logger. Error("ObjectPool",$"[Pool] OnCreate 异常，销毁对象: {_prefab?.name} | {e.Message}");
                    UnityEngine.Object.Destroy(obj.gameObject);
                    return null;
                }
            }
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
