using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace ResourcesWork
{
    public static class ResourceManager
    {
        private class CacheItem
        {
            public ResourceHandleBase RealHandle;   // 真实持有底层句柄，永不直接暴露
            public int RefCount;
            public int Generation;                  // 创建该项时的场景世代
        }

        private static readonly Dictionary<string, CacheItem> _cacheDict
            = new Dictionary<string, CacheItem>();
        private static readonly Dictionary<string, TaskCompletionSource<object>> _loadingTasks
            = new Dictionary<string, TaskCompletionSource<object>>();

        private static int _generation;

        /// <summary>
        /// 异步加载资源（支持并发去重）。
        /// 无论首次加载还是缓存命中，返回的都是 View 句柄。
        /// </summary>
        public static async Task<ResourceHandle<T>> LoadAssetAsync<T>(string key)
            where T : UnityEngine.Object
        {
            // ── 1. 缓存命中 ──────────────────────────────────────────
            if (_cacheDict.TryGetValue(key, out var cacheItem))
            {
                if (cacheItem.RealHandle is not ResourceHandle<T> typed)
                {
                    Debug.LogError($"[Resource] 类型冲突 {key}: 已按其他类型加载，本次请求失败");
                    return null;
                }
                cacheItem.RefCount++;
                return typed.CreateView();          // 永远返回 View
            }

            // ── 2. 并发拦截 ──────────────────────────────────────────
            if (_loadingTasks.TryGetValue(key, out var pending))
            {
                var loaded = await pending.Task;
                if (loaded == null) return null;
                return await LoadAssetAsync<T>(key);   // 唤醒后重新走缓存逻辑
            }

            // ── 3. 全新加载 ──────────────────────────────────────────
            var tcs = new TaskCompletionSource<object>();
            _loadingTasks[key] = tcs;
            int gen = _generation;

            try
            {
                var opHandle = Addressables.LoadAssetAsync<T>(key);
                var asset = await opHandle.Task;

                if (asset == null)
                    throw new Exception($"加载失败，资源为空: {key}");

                // 加载期间切过场景 -> 丢弃结果并主动释放底层句柄
                if (gen != _generation)
                {
                    if (opHandle.IsValid()) Addressables.Release(opHandle);
                    tcs.TrySetResult(null);
                    return null;
                }

                // 创建 RealHandle（内部持有 opHandle），只放进缓存，不直接返回
                var realHandle = new ResourceHandle<T>(key, asset, opHandle, gen);

                _cacheDict[key] = new CacheItem
                {
                    RealHandle = realHandle,
                    RefCount = 1,
                    Generation = gen
                };

                tcs.TrySetResult(asset);            // 先写缓存、再叫醒等待者（顺序不可变）
                return realHandle.CreateView();     // 首次加载也返回 View
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Resource] 加载异常 {key}: {ex.Message}");
                tcs.TrySetResult(null);             // 失败也必须叫醒等待者，避免永久挂起
                return null;
            }
            finally
            {
                if (_loadingTasks.TryGetValue(key, out var cur) && cur == tcs)
                    _loadingTasks.Remove(key);
            }
        }

        /// <summary>
        /// 释放资源（由 ResourceHandle.Dispose 触发）。
        /// 通过 (Generation, Owner) 双重校验，跨世代或非本缓存项发出的句柄都会被安全忽略。
        /// </summary>
        internal static void Release(IResourceHandle handle)
        {
            if (handle is not ResourceHandleBase h) return;

            if (!_cacheDict.TryGetValue(h.Key, out var cacheItem)) return;

            // ── 身份校验 1：世代一致 ─────────────────────────────
            // 旧世代句柄（ClearAll 之前发出）在此被拒绝，不再误伤新缓存项
            if (cacheItem.Generation != h.Generation)
            {
                Debug.Log($"[Resource] 忽略上一世代句柄: {h.Key} " +
                          $"(handle.gen={h.Generation}, cache.gen={cacheItem.Generation})");
                return;
            }

            // ── 身份校验 2：Owner 必须是当前缓存项的 RealHandle ──
            // 防止同一世代内若将来新增 Unload(key) 之类的"绕过句柄"接口时重蹈覆辙
            if (!ReferenceEquals(h.Owner, cacheItem.RealHandle))
            {
                Debug.LogWarning($"[Resource] 句柄与缓存项不匹配，忽略: {h.Key}");
                return;
            }

            cacheItem.RefCount--;

            if (cacheItem.RefCount > 0)
            {
                Debug.Log($"[Resource] 释放一次: {h.Key} | 剩余引用: {cacheItem.RefCount}");
                return;
            }

            _cacheDict.Remove(h.Key);
            cacheItem.RealHandle.ReleaseAddressableHandle();
            Debug.Log($"[Resource] 引用归零，已彻底卸载: {h.Key}");
        }

        /// <summary>
        /// 场景切换清空。
        /// 所有已发出的 View 句柄通过 _generation++ 自动作废，后续 Dispose 会被 Release 安全忽略。
        /// 
        /// 全量清理所有缓存的资源句柄和在途加载。
        /// ★ 无"保留常驻资源"的机制：所有通过 LoadAssetAsync 加载的资源都会被释放。
        ///   如有跨场景常驻需求，请在业务层绕开本方法（用 Resources.Load 或自行维护重建逻辑）。
        /// </summary>
        public static void ClearAll()
        {
            _generation++;   // 让所有在途加载结果、所有已发出的旧句柄失效

            foreach (var item in _cacheDict.Values)
                item.RealHandle.ReleaseAddressableHandle();
            _cacheDict.Clear();

            var pendingList = new List<TaskCompletionSource<object>>(_loadingTasks.Values);
            _loadingTasks.Clear();
            foreach (var tcs in pendingList)
                tcs.TrySetResult(null);
        }
    }
}