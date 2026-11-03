using KeepAlive.Core;

namespace KeepAlive.Framework.Scene
{
    /// <summary>
    /// 场景开始加载事件
    /// </summary>
    public struct SceneLoadStartEvent : IEvent
    {
        public string SceneKey;
    }

    /// <summary>
    /// 场景加载进度事件（0.0 到 1.0）
    /// </summary>
    public struct SceneLoadProgressEvent : IEvent
    {
        public float Progress;
    }

    /// <summary>
    /// 场景加载完成事件
    /// </summary>
    public struct SceneLoadCompleteEvent : IEvent
    {
        public string SceneKey;
    }
    /// <summary>
    /// 场景加载失败（加载或激活阶段均可能触发）。
    /// UI 层监听此事件展示"加载失败"弹窗；FSM 监听此事件做状态回退。
    /// </summary>
    public struct SceneLoadFailedEvent : IEvent
    {
        public string SceneKey;
        public string ErrorMessage;
    }

}