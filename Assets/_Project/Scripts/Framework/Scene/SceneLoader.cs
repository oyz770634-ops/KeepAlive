using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using KeepAlive.Core;
using KeepAlive.Framework.Audio;
using KeepAlive.Framework.Pool;
using KeepAlive.Framework.UI;
using ResourcesWork;

namespace KeepAlive.Framework.Scene
{
    public class SceneLoader : MonoBehaviour
    {
        public static SceneLoader Instance { get; private set; }

        /// <summary>激活阶段的超时（秒）</summary>
        [SerializeField] private float _activateTimeout = 10f;

        /// <summary>进度广播的阈值（变化小于此值不广播）</summary>
        private const float ProgressThreshold = 0.01f;

        private bool _isLoading;

        /// <summary>
        /// 已加载的 Additive 场景（key -> handle）。切主场景时统一卸载。
        /// </summary>
        private readonly Dictionary<string, AsyncOperationHandle<SceneInstance>> _additiveScenes = new();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            // 兜底：卸载所有 Additive（防止 SceneLoader 被意外销毁时泄漏）
            foreach (var handle in _additiveScenes.Values)
            {
                if (handle.IsValid()) Addressables.UnloadSceneAsync(handle);
            }
            _additiveScenes.Clear();
        }

        // ── 主场景加载 ────────────────────────────────────────

        /// <summary>
        /// 异步加载主场景（Single 模式）。
        /// 返回 true 表示加载成功；false 表示失败（已广播 SceneLoadFailedEvent）。
        /// ★ 事务性语义：失败时旧场景的 UI/音频/资源/池化对象全部保持原状。
        /// </summary>
        public async Task<bool> LoadSceneAsync(string sceneKey)
        {
            if (string.IsNullOrEmpty(sceneKey))
            {
                KeepAlive.Core.Logger.Error("Scene", "sceneKey 为空");
                return false;
            }

            if (_isLoading)
            {
                KeepAlive.Core.Logger.Warning("Scene", $"正在加载中，拒绝新请求: {sceneKey}");
                return false;
            }

            _isLoading = true;
            KeepAlive.Core.Logger.Info("Scene", $"准备加载场景: {sceneKey}");
            EventBus.Publish(new SceneLoadStartEvent { SceneKey = sceneKey });

            try
            {
                // ① 发起加载（activateOnLoad: false → 旧场景和资源保持不动）
                var handle = Addressables.LoadSceneAsync(sceneKey, LoadSceneMode.Single, activateOnLoad: false);
                await handle.Task;

                if (handle.Status != AsyncOperationStatus.Succeeded)
                {
                    var err = handle.OperationException?.Message ?? "未知错误";
                    Fail(sceneKey, $"场景加载失败: {err}");
                    if (handle.IsValid()) Addressables.Release(handle);
                    return false;
                }

                // ② 加载完成，广播 100%（激活期间 UI 不会"卡"在 99%）
                EventBus.Publish(new SceneLoadProgressEvent { Progress = 1.0f });

                // ③ 先卸载 Additive（它们持有的旧场景资源即将失效）
                await UnloadAllAdditiveAsync();

                // ④ 清理各模块（此时旧场景仍在，能正确 Destroy 其中的对象）
                CleanupModules();

                // ⑤ 激活新场景（Unity 会卸载旧主场景）
                if (!await ActivateWithTimeoutAsync(handle, sceneKey))
                {
                    if (handle.IsValid()) Addressables.Release(handle);
                    return false;
                }

                KeepAlive.Core.Logger.Info("Scene", $"场景加载成功: {sceneKey}");
                EventBus.Publish(new SceneLoadCompleteEvent { SceneKey = sceneKey });
                return true;
            }
            catch (Exception e)
            {
                KeepAlive.Core.Logger.Exception("Scene", e, $"加载场景 {sceneKey} 异常");
                EventBus.Publish(new SceneLoadFailedEvent
                {
                    SceneKey = sceneKey,
                    ErrorMessage = e.Message
                });
                return false;
            }
            finally
            {
                _isLoading = false;
            }
        }

        // ── Additive 场景 ─────────────────────────────────────

        /// <summary>
        /// 叠加加载场景。失败时抛异常（调用方必须用 try-catch 或 await 捕获）。
        /// 成功的句柄会被 SceneLoader 内部登记，切主场景时统一卸载。
        /// </summary>
        public async Task<AsyncOperationHandle<SceneInstance>> LoadSceneAdditiveAsync(string sceneKey)
        {
            if (string.IsNullOrEmpty(sceneKey))
                throw new ArgumentException("sceneKey 为空", nameof(sceneKey));

            if (_additiveScenes.ContainsKey(sceneKey))
            {
                KeepAlive.Core.Logger.Warning("Scene", $"Additive 已加载，直接复用: {sceneKey}");
                return _additiveScenes[sceneKey];
            }

            KeepAlive.Core.Logger.Info("Scene", $"叠加加载场景: {sceneKey}");
            var handle = Addressables.LoadSceneAsync(sceneKey, LoadSceneMode.Additive, activateOnLoad: true);
            await handle.Task;

            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                var err = handle.OperationException?.Message ?? "未知错误";
                KeepAlive.Core.Logger.Error("Scene", $"Additive 加载失败: {sceneKey} | {err}");
                if (handle.IsValid()) Addressables.Release(handle);
                throw new Exception($"Additive 加载失败: {sceneKey} | {err}");
            }

            _additiveScenes[sceneKey] = handle;
            return handle;
        }

        /// <summary>
        /// 卸载 Additive 场景。返回 true 表示已卸载（或本来就未加载）；
        /// false 表示句柄无效或卸载失败。
        /// </summary>
        public async Task<bool> UnloadSceneAdditiveAsync(string sceneKey)
        {
            if (!_additiveScenes.TryGetValue(sceneKey, out var handle))
            {
                KeepAlive.Core.Logger.Warning("Scene", $"未注册的 Additive 场景: {sceneKey}");
                return false;
            }

            _additiveScenes.Remove(sceneKey);

            if (!handle.IsValid())
            {
                KeepAlive.Core.Logger.Warning("Scene", $"Additive 句柄已失效（可能已被释放）: {sceneKey}");
                return false;
            }

            try
            {
                await Addressables.UnloadSceneAsync(handle).Task;
                KeepAlive.Core.Logger.Info("Scene", $"Additive 已卸载: {sceneKey}");
                return true;
            }
            catch (Exception e)
            {
                KeepAlive.Core.Logger.Exception("Scene", e, $"卸载 Additive 失败: {sceneKey}");
                return false;
            }
        }

        /// <summary>卸载全部 Additive 场景（切主场景时自动调用）。</summary>
        public async Task UnloadAllAdditiveAsync()
        {
            if (_additiveScenes.Count == 0) return;

            var keys = new List<string>(_additiveScenes.Keys);
            foreach (var key in keys)
                await UnloadSceneAdditiveAsync(key);
        }

        // ── 内部辅助 ─────────────────────────────────────────

        /// <summary>
        /// 显式清理所有模块。顺序固定且有明确原因：
        ///   1) 先 CloseAll/ClearAudio（Dispose 句柄正常生效，RefCount 正常减）
        ///   2) 再 ResourceManager.ClearAll（兜底剩余孤儿句柄）
        /// </summary>
        private void CleanupModules()
        {
            UIManager.Instance?.CloseAll(destroy: true);
            AudioManager.Instance?.ClearAudio();
            PoolManager.Instance?.ClearAll();
            ResourceManager.ClearAll();
            // 说明：EventBus 不在这里清——业务可能需要跨场景的全局监听（如断线重连）。
            // 如果项目确实需要清空，可以在业务层显式调用 EventBus.Clear()。
        }

        /// <summary>激活场景，带超时保护。</summary>
        private async Task<bool> ActivateWithTimeoutAsync(AsyncOperationHandle<SceneInstance> handle, string sceneKey)
        {
            var activateTask = handle.Result.ActivateAsync().AsTask();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(_activateTimeout));
            var completed = await Task.WhenAny(activateTask, timeoutTask);

            if (completed == timeoutTask)
            {
                KeepAlive.Core.Logger.Error("Scene", $"场景激活超时({_activateTimeout}s): {sceneKey}");
                EventBus.Publish(new SceneLoadFailedEvent
                {
                    SceneKey = sceneKey,
                    ErrorMessage = $"激活超时({_activateTimeout}s)"
                });
                return false;
            }

            // 让 activateTask 的异常能正常抛出到外层 catch
            await activateTask;
            return true;
        }

        private void Fail(string sceneKey, string message)
        {
            KeepAlive.Core.Logger.Error("Scene", message);
            EventBus.Publish(new SceneLoadFailedEvent
            {
                SceneKey = sceneKey,
                ErrorMessage = message
            });
        }
    }
}

/// <summary>
/// 扩展方法
/// </summary>
public static class AsyncOperationExtensions
{
    public static Task AsTask(this AsyncOperation asyncOp)
    {
        var tcs = new TaskCompletionSource<bool>();
        asyncOp.completed += _ => tcs.TrySetResult(true);
        return tcs.Task;
    }
}