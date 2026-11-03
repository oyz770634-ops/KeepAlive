namespace KeepAlive.Framework.Pool
{
    public interface IPoolable
    {
        /// <summary>
        /// 首次被 Instantiate 创建时调用一次（适合做一次性初始化）。
        /// </summary>
        void OnCreate();

        /// <summary>
        /// 从池中取出时调用（重置状态、激活、恢复物理）。
        /// </summary>
        void OnSpawn();

        /// <summary>
        /// 放回池中时调用（停止协程、停物理、清状态）。
        /// </summary>
        void OnDespawn();
    }
}