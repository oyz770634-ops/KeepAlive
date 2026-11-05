using System.Threading.Tasks;
using UnityEngine;
using KeepAlive.Framework.FSM;

namespace KeepAlive.Framework
{
    public class GameFlowManager : MonoBehaviour
    {
        public static GameFlowManager Instance { get; private set; }

        private StateMachine _fsm;
        private bool _started;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            _fsm = new StateMachine();
        }

        private void Update() => _fsm?.Update();

        // ★ 新增：只读转发
        public IState CurrentState => _fsm?.CurrentState;
        public bool IsTransitioning => _fsm?.IsTransitioning ?? false;
        /// <summary>启动游戏流程，通常由入口脚本在 Start 里调用一次</summary>
        public async Task StartWithAsync(IState initialState)
        {
            if (_started)
            {
                KeepAlive.Core.Logger.Warning("FSM", "StartWithAsync 重复调用，忽略");
                return;
            }

            if (initialState == null)   // ★ 提前校验，不消耗 _started
            {
                KeepAlive.Core.Logger.Error("FSM", "初始状态为 null，状态机未启动");
                return;                 // ← 不置 _started，允许后续修正后重试
            }

            _started = true;
            await _fsm.ChangeStateAsync(initialState);
        }

        /// <summary>异步切换状态（推荐）</summary>
        public Task ChangeStateAsync(IState newState) => _fsm.ChangeStateAsync(newState);

        /// <summary>取消当前切换</summary>
        public void CancelTransition() => _fsm.CancelTransition();

        /// <summary>
        /// [已废弃] 同步版本，仅用于兼容旧调用方。
        /// 无法感知切换何时完成、是否失败——请改用 ChangeStateAsync。
        /// </summary>
        [System.Obsolete("请使用 ChangeStateAsync，同步版本无法感知异步完成与失败")]
        public void ChangeState(IState newState) => _ = _fsm.ChangeStateAsync(newState);
    }
}