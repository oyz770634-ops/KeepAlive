using System.Threading;
using System.Threading.Tasks;

namespace KeepAlive.Framework.FSM
{
    public interface IState
    {
        /// <summary>
        /// 进入状态（异步：加载场景、打开 UI、加载资源等）
        /// </summary>
        Task OnEnterAsync(CancellationToken ct = default);


        /// <summary>
        /// 每帧更新（保持同步，高频调用异步无收益）
        /// </summary>
        void OnUpdate();

        /// <summary>
        /// 退出状态（异步：关闭 UI、释放资源、保存数据等）
        /// </summary>
        Task OnExitAsync(CancellationToken ct = default);
    }
}
