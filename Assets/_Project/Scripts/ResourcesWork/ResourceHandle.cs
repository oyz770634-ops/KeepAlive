using System;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace ResourcesWork
{
    /// <summary>
    /// 这个 ResourceHandle<T> : IDisposable 非常非常专业。这叫 RAII（资源获取即初始化）模式。
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class ResourceHandle<T> : IDisposable, IResourceHandle
    {
        public T Asset { get; private set; }
        public string Key { get; private set; }
        private AsyncOperationHandle<T> _handle;
        private bool _isDisposed;

        public ResourceHandle(string key, T asset, AsyncOperationHandle<T> handle)
        {
            Key = key;
            _handle = handle;
            Asset = asset;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            // 释放底层引用计数
            ResourceManager.Release(this);
        }
        // 提供内部方法，仅供 Manager 在计数归零时调用
        public void ReleaseAddressableHandle()
        {
            if (_handle.IsValid())
            {
                Addressables.Release(_handle);
            }
        }
    }
}
