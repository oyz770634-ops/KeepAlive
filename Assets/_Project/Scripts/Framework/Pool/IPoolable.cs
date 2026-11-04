namespace KeepAlive.Framework.Pool
{
    /// <summary>
    /// 它规定的是「一个对象如果想让池子在正确的时机通知我，就必须实现这三个方法」
    /// 注意这是可选协议，不是强制约束——ObjectPool<T> 只要求 T : Component，实现 IPoolable 是加分项。
    /// 这和通知例子里 INotifier 是强制的（不实现就抛异常）形成对照：强协议保证正确性，可选协议提供扩展点。
    /// </summary>
    public interface IPoolable
    {
        /// <summary>
        /// 首次被 Instantiate 创建时调用一次。适合做一次性初始化。
        /// 可以在这里订阅"永久事件"（对象一生都需要收的事件），
        /// 并在 IPoolableDestroy.OnDestroy 或 MonoBehaviour.OnDestroy 中退订。
        /// </summary>
        void OnCreate();

        /// <summary>
        /// 从池中取出时调用。适合重置状态、激活、恢复物理。
        /// 可以在这里订阅"临时事件"（只在这一轮取用期间需要收的事件），
        /// 并在 OnDespawn 中退订。
        /// </summary>
        void OnSpawn();

        /// <summary>
        /// 放回池中时调用。适合停协程、停物理、清临时状态。
        /// 必须退掉 OnSpawn 中订阅的临时事件。
        /// </summary>
        void OnDespawn();
    }
}