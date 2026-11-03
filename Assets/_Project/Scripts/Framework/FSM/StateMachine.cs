using System;
using System.Threading;
using System.Threading.Tasks;
using KeepAlive.Core;

namespace KeepAlive.Framework.FSM
{
    public class StateMachine
    {
        public IState CurrentState { get; private set; }
        public bool IsTransitioning { get; private set; }

        private IState _pendingState;
        private CancellationTokenSource _transitionCts;

        /// <summary>
        /// 切换状态（异步）
        /// - 同状态忽略
        /// - 切换中：挂起最新请求，当前完成后接续执行
        /// - 异常不中断：通过 EventBus 广播失败事件
        /// </summary>
        public async Task ChangeStateAsync(IState newState)
        {
            if (newState == null) return;

            // 1. 非切换中 + 同状态 → 直接忽略
            if (!IsTransitioning && IsSameState(CurrentState, newState))
                return;

            // 2. 切换中 → 只保留最新请求，覆盖旧的挂起请求
            if (IsTransitioning)
            {
                _pendingState = newState;
                Logger.Info("FSM", $"切换中，挂起请求: {newState.GetType().Name}");
                return;
            }

            // 3. 执行切换
            await DoTransitionAsync(newState);

            // 4. 处理挂起的请求（循环直到没有新的挂起）
            while (_pendingState != null)
            {
                var next = _pendingState;
                _pendingState = null;
                if (IsSameState(CurrentState, next)) continue;
                await DoTransitionAsync(next);
            }
        }

        private async Task DoTransitionAsync(IState newState)
        {
            IsTransitioning = true;
            _transitionCts = new CancellationTokenSource();
            var ct = _transitionCts.Token;
            var oldState = CurrentState;

            try
            {
                // ① 退出旧状态
                if (oldState != null)
                {
                    Logger.Info("FSM", $"退出状态: {oldState.GetType().Name}");
                    try
                    {
                        await oldState.OnExitAsync(ct);
                    }
                    catch (OperationCanceledException) { Logger.Info("FSM", "OnExit 被取消"); }
                    catch (Exception e)
                    {
                        Logger.Exception("FSM", e, $"退出 {oldState.GetType().Name} 异常");
                        EventBus.Publish(new StateTransitionFailedEvent
                        {
                            State = oldState.GetType(),
                            ErrorMessage = e.Message
                        });
                        // 旧状态已半退出 → 继续切换更安全
                    }
                }

                // ② 切换引用
                CurrentState = newState;

                // ③ 进入新状态
                Logger.Info("FSM", $"进入状态: {newState.GetType().Name}");
                try
                {
                    await newState.OnEnterAsync(ct);
                    EventBus.Publish(new StateChangedEvent
                    {
                        FromState = oldState?.GetType(),
                        ToState = newState.GetType()
                    });
                }
                catch (OperationCanceledException) { Logger.Info("FSM", "OnEnter 被取消"); }
                catch (Exception e)
                {
                    Logger.Exception("FSM", e, $"进入 {newState.GetType().Name} 异常");
                    EventBus.Publish(new StateTransitionFailedEvent
                    {
                        State = newState.GetType(),
                        ErrorMessage = e.Message
                    });
                    // CurrentState 保持为新状态 → 业务通过订阅失败事件自行决定是否回退
                }
            }
            finally
            {
                IsTransitioning = false;
                _transitionCts?.Dispose();
                _transitionCts = null;
            }
        }

        /// <summary>每帧驱动（切换中跳过，异常不外抛）</summary>
        public void Update()
        {
            if (IsTransitioning || CurrentState == null) return;
            try
            {
                CurrentState.OnUpdate();
            }
            catch (Exception e)
            {
                Logger.Exception("FSM", e, $"OnUpdate {CurrentState.GetType().Name} 异常");
            }
        }

        /// <summary>取消当前切换（OnEnter/OnExit 内响应 ct 即可中断）</summary>
        public void CancelTransition() => _transitionCts?.Cancel();

        private static bool IsSameState(IState a, IState b)
        {
            if (a == null || b == null) return a == b;
            return a.GetType() == b.GetType();
        }
    }
}