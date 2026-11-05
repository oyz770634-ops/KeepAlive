using System.Collections.Generic;
using UnityEngine;
using KeepAlive.Core;

namespace KeepAlive.Framework.Pool
{
    public class PoolManager : MonoBehaviour
    {
        public static PoolManager Instance { get; private set; }

        private readonly Dictionary<Component, IObjectPool> _pools = new();
        private Transform _poolRoot;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            _poolRoot = new GameObject("[PoolRoot]").transform;
            _poolRoot.SetParent(transform);
        }

        // ── 获取池子 / 取用 / 归还 ───────────────────────────
        /// <summary>
        /// 获取或创建池。
        /// 
        /// ★ 重要契约：
        ///   池会持续持有 prefab 引用用于 Instantiate。
        ///   调用方必须在池的生命周期内（直到 PoolManager.ClearAll）持有 prefab 的资源句柄，
        ///   否则 ResourceHandle.Dispose 会导致 prefab 被卸载，池内的 CreateObject 会失败。
        /// 
        /// ★ 清理顺序：
        ///   PoolManager.ClearAll 必须在 ResourceManager.ClearAll 之前调用。
        ///   SceneLoader.CleanupModules() 已经保证了这一点。
        /// </summary>
        public ObjectPool<T> GetPool<T>(T prefab, int prewarmCount = 10, int maxSize = 0)
            where T : Component
        {
            if (prefab == null)
            {
               KeepAlive.Core.Logger.Error("Pool","[Pool] GetPool 时 prefab 为 null");
                return null;
            }         
            if (_pools.TryGetValue(prefab, out var existing))
            {
                // ★ 用 is 模式匹配，避免 as 返回 null 后 NRE
                if (existing is ObjectPool<T> typedPool) return typedPool;

                KeepAlive.Core.Logger.Error("Pool",$"[Pool] 类型不匹配: {prefab.name} 已用其他类型创建池子");
                return null;
            }

            var newPool = new ObjectPool<T>(prefab, prewarmCount, _poolRoot, maxSize);
            _pools.Add(prefab, newPool);
            return newPool;
        }
        /// <summary>
        /// 取用一个对象
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="prefab"></param>
        /// <param name="prewarmCount"></param>
        /// <param name="maxSize"></param>
        /// <returns></returns>
        public T Spawn<T>(T prefab, int prewarmCount = 10, int maxSize = 0)
            where T : Component
        {
            return GetPool(prefab, prewarmCount, maxSize)?.Get();
        }
        /// <summary>
        /// 归还一个对象
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="prefab"></param>
        /// <param name="instance"></param>
        public void Despawn<T>(T prefab, T instance) where T : Component
        {
            if (instance == null) return;

            // ★ prefab 已销毁（跨场景），不访问 .name，直接销毁 instance
            if (prefab == null)
            {
                KeepAlive.Core.Logger.Warning("Pool", "Despawn 时 prefab 已失效，直接销毁 instance");
                Object.Destroy(instance.gameObject);
                return;
            }

            if (_pools.TryGetValue(prefab, out var existing) && existing is ObjectPool<T> typedPool)
            {
                typedPool.Release(instance);
                return;
            }

            KeepAlive.Core.Logger.Error("Pool", $"找不到池子，销毁 instance: {instance.name}");
            Object.Destroy(instance.gameObject);
        }

        // ── 清理 ─────────────────────────────────────────────

        /// <summary>
        /// 清理所有池子（切场景时必须调用）。
        /// 
        /// ★ 调用顺序要求：必须在 ResourceManager.ClearAll() 之前调用。
        ///   否则池内的 _prefab 会先被 ResourceManager 销毁，导致池"死掉"（每次 Get 都返回 null）。
        /// 
        /// SceneLoader.CleanupModules() 已经保证了这个顺序：
        ///   UIManager.CloseAll → AudioManager.ClearAudio → PoolManager.ClearAll → ResourceManager.ClearAll
        /// 
        /// 如果业务绕过 SceneLoader 直接调 ResourceManager.ClearAll，必须手动先调本方法。
        /// </summary>
        public void ClearAll()
        {
            // ★ 快照：防止池内对象的 OnDestroy 回调修改 _pools（如回调里调 GetPool）
            var pools = new List<IObjectPool>(_pools.Values);
            foreach (var pool in _pools.Values)
                pool.Clear();

            _pools.Clear();
            KeepAlive.Core.Logger.Info("Pool", $"已清理所有池子");
        }

      
    }
}