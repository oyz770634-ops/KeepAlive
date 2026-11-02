using System;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace ResourcesWork
{
    /// <summary>
    /// 句柄抽象基类。
    ///
    /// 在接口 IResourceHandle 的基础上，额外提供 Manager 需要的内部能力：
    ///   · Generation          —— 创建时的场景世代，用于跨世代失效校验
    ///   · Owner               —— 此句柄归属的 RealHandle（RealHandle 自己返回自己）
    ///   · ReleaseAddressableHandle —— 仅供 Manager 在引用归零时调用
    ///
    /// 这三个能力都是 internal：
    ///   · 外部程序集无法实现（也因此无法继承本类）
    ///   · 外部程序集无法调用（封死误调入口）
    /// </summary>
    public abstract class ResourceHandleBase : IResourceHandle,IDisposable
    {
        public abstract string Key { get; }
        public abstract void Dispose();

        /// <summary>创建此句柄时的场景世代。Manager 用它拒绝上一世代的旧句柄。</summary>
        internal abstract int Generation { get; }

        /// <summary>返回此句柄归属的 RealHandle；RealHandle 自身返回自己。</summary>
        internal abstract ResourceHandleBase Owner { get; }

        /// <summary>仅供 Manager 在引用归零时调用，释放底层 Addressables 句柄。</summary>
        internal abstract void ReleaseAddressableHandle();
    }

    /// <summary>
    /// 泛型资源句柄。有两种形态：
    ///
    ///   【RealHandle / Owner】
    ///     · 内部持有 AsyncOperationHandle&lt;T&gt;
    ///     · 只存在于 _cacheDict 中，永不直接暴露给调用方
    ///
    ///   【View】
    ///     · 每次 LoadAssetAsync 返回给调用方的包装
    ///     · 持有 Asset + Key + 对 Owner 的引用，自身不持有底层句柄
    ///     · Dispose 时由 Manager 通过 (Generation, Owner) 双重校验身份
    ///
    /// 这个对称结构消除了原设计中的两处隐患：
    ///   1) 首次加载返回缓存项本身 -> 调用方可绕过 Manager 释放底层
    ///   2) 缓存命中返回包装、首次返回真身 -> 两条路径能力不对称
    /// 现在两条路径永远都返回 View，能力一致。
    /// </summary>
    public class ResourceHandle<T> : ResourceHandleBase
    {
        public T Asset { get; private set; }
        public override string Key { get; }

        private readonly AsyncOperationHandle<T> _handle;
        private readonly ResourceHandleBase _owner;   // null => 自己是 RealHandle
        private readonly int _generation;
        private bool _isDisposed;

        /// <summary>RealHandle 专用构造：内部持有真实底层句柄。仅 Manager 使用。</summary>
        internal ResourceHandle(string key, T asset, AsyncOperationHandle<T> handle, int generation)
        {
            Key = key;
            Asset = asset;
            _handle = handle;
            _owner = null;
            _generation = generation;
        }

        /// <summary>View 专用构造：包装一个 Owner，自身不持有底层句柄。</summary>
        private ResourceHandle(string key, T asset, ResourceHandleBase owner, int generation)
        {
            Key = key;
            Asset = asset;
            _owner = owner;
            _generation = generation;
            _handle = default;
        }

        internal override int Generation => _generation;

        /// <summary>
        /// Owner 是 View 内部的一个指针，指回"创建我的那个 RealHandle"。
        ///作用：当 View 请求释放时，Manager 用它来判断"这个 View 到底是不是当前缓存项那个 RealHandle 派生的"。
        /// </summary>
        internal override ResourceHandleBase Owner => _owner ?? this;

        /// <summary>为当前 RealHandle 创建一个对外 View。</summary>
        internal ResourceHandle<T> CreateView()
            => new ResourceHandle<T>(Key, Asset, this, _generation);

        public override void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            // 释放后清空 Asset，让调用方在误用时拿到 null，而不是悬空的 Unity 对象引用
            Asset = default;

            ResourceManager.Release(this);
        }

        internal override void ReleaseAddressableHandle()
        {
            // 只有 RealHandle 会走到这里（View 的 _handle 是 default，IsValid() 返回 false）
            if (_handle.IsValid())
                Addressables.Release(_handle);
        }
    }
}