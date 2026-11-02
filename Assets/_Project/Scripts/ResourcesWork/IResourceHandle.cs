using System;

namespace ResourcesWork
{
    /// <summary>
    /// 非泛型的资源句柄接口。
    /// 只暴露只读信息与 Dispose。
    /// 底层释放能力已从接口移除，改由内部基类 ResourceHandleBase 的 internal 方法提供，
    /// 从 API 层面杜绝"调用方绕过 Manager 直接释放底层"的误用。
    /// </summary>
    public interface IResourceHandle : IDisposable
    {
        string Key { get; }
    }
    
}
