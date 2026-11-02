using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UIElements;

namespace ResourcesWork
{
    public static class ResourceManager
    {
        // 缓存结构中直接存我们的泛型 Handle 对象
        private class CacheItem
        {
            public IResourceHandle Handle;     // 实际存的是 ResourceHandle<T>
            public int RefCount;//引用计数用于释放
        }
        //缓存已加载正在使用
        private static readonly Dictionary<string, CacheItem> _cacheDict = new Dictionary<string, CacheItem>();

        // 正在加载中的任务，防止并发重复加载
        private static readonly Dictionary<string, TaskCompletionSource<object>> _loadingTasks= new Dictionary<string, TaskCompletionSource<object>>();
        private static int _generation;   // 场景代次
        /// <summary>
        /// 异步加载资源（支持并发去重）
        /// </summary>
        public static async Task<ResourceHandle<T>> LoadAssetAsync<T>(string key) where T : UnityEngine.Object
        {
            // 1. 缓存命中
            if (_cacheDict.TryGetValue(key, out var cacheItem))
            {
                if (cacheItem.Handle is not ResourceHandle<T> typed)   // 改 1
                {
                    Debug.LogError($"[Resource] 类型冲突 {key}: 已按其他类型加载，本次请求失败");
                    return null;
                }
                cacheItem.RefCount++;
                // 返回一个新的 Handle 包装，但底层共享同一个 Addressables 句柄
                return new ResourceHandle<T>(key, typed.Asset, default);
            }

            // 2. 并发拦截：如果当前资源正在加载，等待它加载完
            if (_loadingTasks.TryGetValue(key, out var pending))
            {
                var loaded = await pending.Task;
                if (loaded == null) return null;
                return await LoadAssetAsync<T>(key); // 等待加载完毕后，重新走缓存逻辑
            }

            // 3. 全新加载
            var tcs = new TaskCompletionSource<object>();
            _loadingTasks[key] = tcs;
            int gen = _generation;

            try
            {
                // 使用 Addressables 进行底层加载
                var opHandle = Addressables.LoadAssetAsync<T>(key);
                var asset = await opHandle.Task; // 原生 await 等待加载完成

                if (asset == null)
                {
                    throw new Exception($"加载失败，资源为空: {key}");
                }

                // 加载期间切过场景 → 丢弃结果并释放底层句柄
                if (gen != _generation)
                {
                    if (opHandle.IsValid()) Addressables.Release(opHandle);
                    tcs.TrySetResult(null);
                    return null;
                }
                // 创建业务层用的 Handle
                var resHandle = new ResourceHandle<T>(key, asset, opHandle);

                // 写入缓存
                _cacheDict[key] = new CacheItem
                {
                    Handle = resHandle,
                    RefCount = 1
                };

                tcs.TrySetResult(asset);// 唤醒并发等待者
                return resHandle;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Resource] 加载异常 {key}: {ex.Message}");
                tcs.TrySetResult(null);
                return null;
            }
            finally
            {
                if (_loadingTasks.TryGetValue(key, out var cur) && cur == tcs)
                    _loadingTasks.Remove(key);
            }
        }

        /// <summary>
        /// 释放资源（由 ResourceHandle.Dispose 触发）
        /// </summary>
        internal static void Release(IResourceHandle handle)
        {
            string key = handle.Key;
            if (!_cacheDict.TryGetValue(key, out var cacheItem)) return;

            cacheItem.RefCount--;

            if (cacheItem.RefCount > 0)
            {
                Debug.Log($"[Resource] 释放一次: {key} | 剩余引用: {cacheItem.RefCount}");
                return;
            }

            _cacheDict.Remove(key);
            // 用缓存里的原始 Handle 释放（持有真实 opHandle），而不是调用方的包装 Handle
            cacheItem.Handle.ReleaseAddressableHandle();
            Debug.Log($"[Resource] 引用归零，已彻底卸载: {key}");
        }

        /// <summary>
        /// 场景切换清空
        /// </summary>
        public static void ClearAll()
        {
            _generation++;   // 让所有在途加载结果作废

            // 1. 真正释放已缓存资源
            foreach (var item in _cacheDict.Values)
                item.Handle.ReleaseAddressableHandle();
            _cacheDict.Clear();

            // 2. 先清空再唤醒，避免续体在遍历时修改字典
            var pendingList = new List<TaskCompletionSource<object>>(_loadingTasks.Values);
            _loadingTasks.Clear();
            foreach (var tcs in pendingList)
                tcs.TrySetResult(null);
        }
    }
}