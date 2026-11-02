using System;

namespace ResourcesWork
{
    /// <summary>
    /// 非泛型的资源句柄接口，用于管理底层释放
    /// </summary>
    public interface IResourceHandle : IDisposable
    {
        string Key { get; }
        void ReleaseAddressableHandle();
    }
}
