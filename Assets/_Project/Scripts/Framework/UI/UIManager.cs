using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using KeepAlive.Core;
using ResourcesWork;

namespace KeepAlive.Framework.UI
{
    public class UIManager : MonoBehaviour
    {
        public static UIManager Instance { get; private set; }

        /// <summary>
        /// ★ 合并 _uiCache + _uiHandles + 可见状态
        /// </summary>
        private class UIData
        {
            public UIBase UI;
            public IResourceHandle Handle;
            public bool IsVisible;
        }

        /// <summary>
        /// 隐藏 UI 的最大保留数量，超过则 LRU 淘汰最旧的
        /// </summary>
        private const int MaxHiddenUICount = 5;

        private readonly Dictionary<string, UIData> _uidata = new();
        private readonly Dictionary<string, TaskCompletionSource<UIBase>> _loadingTasks = new();
        private readonly HashSet<string> _pendingClose = new();       // 加载中被要求关闭
        private readonly Stack<string> _uiStack = new();
        private readonly Dictionary<UILayer, Transform> _layerRoots = new();
        private readonly Queue<string> _hiddenOrder = new();          // LRU 队列

        // ── 初始化 ─────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            EnsureEventSystem();
            InitLayerRoots();
        }

        private void EnsureEventSystem()
        {
            // EventSystem.current 会自动处理激活状态的对象
            // 如果 current 为空，再兜底全场景找一次激活的 EventSystem
            if (EventSystem.current == null && FindAnyObjectByType<EventSystem>() == null)
            {
                // 若项目用新 Input System，请换用 InputSystemUIInputModule
                new GameObject("[EventSystem]", typeof(EventSystem), typeof(StandaloneInputModule));
                KeepAlive.Core.Logger.Info("UI", "自动创建 EventSystem（项目中没有找到）");
            }
        }

        private void InitLayerRoots()
        {
            foreach (UILayer layer in Enum.GetValues(typeof(UILayer)))
            {
                var go = new GameObject($"Canvas_{layer}",
                    typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                go.transform.SetParent(transform);

                var canvas = go.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = (int)layer * 100;

                _layerRoots[layer] = go.transform;
            }
        }

        /// <summary>
        /// 打开
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="uiKey"></param>
        /// <param name="layer"></param>
        /// <returns></returns>

        public async Task<UIBase> OpenUIAsync<T>(string uiKey, UILayer layer = UILayer.Normal)
            where T : UIBase
        {
            // 用户又点了打开，撤销之前的关闭意图
            _pendingClose.Remove(uiKey);

            // 1. 缓存命中
            if (_uidata.TryGetValue(uiKey, out var existing))
            {
                ShowUI(existing);
                return existing.UI;
            }

            // 2. 并发拦截
            if (_loadingTasks.TryGetValue(uiKey, out var pending))
            {
                try
                {
                    await pending.Task;
                }
                catch (Exception e)
                {
                    KeepAlive.Core.Logger.Error("UI", $"等待加载失败 {uiKey}: {e.Message}");
                    return null;
                }

                // 唤醒后重走缓存逻辑（此时缓存已就绪）
                if (_uidata.TryGetValue(uiKey, out var loaded))
                {
                    ShowUI(loaded);
                    return loaded.UI;
                }
                return null;  // 加载成功但未进缓存（如 pendingClose 触发）
            }

            // 3. 全新加载
            var tcs = new TaskCompletionSource<UIBase>();
            _loadingTasks[uiKey] = tcs;

            try
            {
                var resHandle = await ResourceManager.LoadAssetAsync<GameObject>(uiKey);
                if (resHandle == null || resHandle.Asset == null)
                {
                    resHandle?.Dispose();  // ★ 补 Dispose
                    var err = $"加载界面预制体失败: {uiKey}";
                    KeepAlive.Core.Logger.Error("UI", err);
                    tcs.SetException(new Exception(err));
                    return null;
                }

                // ★ 加载中被关闭 → 销毁并释放
                if (_pendingClose.Remove(uiKey))
                {
                    resHandle.Dispose();
                    var cancelErr = $"加载完成时已被取消: {uiKey}";
                    KeepAlive.Core.Logger.Info("UI", cancelErr);
                    tcs.SetException(new Exception(cancelErr));
                    return null;
                }

                var go = Instantiate(resHandle.Asset, _layerRoots[layer]);
                var ui = go.GetComponent<T>();

                if (ui == null)
                {
                    var typeErr = $"预制体上缺少脚本 {typeof(T).Name}: {uiKey}";
                    KeepAlive.Core.Logger.Error("UI", typeErr);
                    Destroy(go);
                    resHandle.Dispose();
                    tcs.SetException(new Exception(typeErr));
                    return null;
                }

                ui.Init(uiKey, layer);
                ui.OnInit();

                var data = new UIData { UI = ui, Handle = resHandle, IsVisible = true };
                _uidata[uiKey] = data;

                ui.OnShow();
                PushToStack(uiKey);

                tcs.SetResult(ui);
                KeepAlive.Core.Logger.Info("UI", $"打开界面成功: {uiKey}");
                return ui;
            }
            catch (Exception e)
            {
                KeepAlive.Core.Logger.Exception("UI", e, $"打开界面异常 {uiKey}");
                tcs.TrySetException(e);
                return null;
            }
            finally
            {
                _loadingTasks.Remove(uiKey);
            }
        }

        /// <summary>
        /// 关闭
        /// </summary>
        /// <param name="uiKey"></param>
        /// <param name="destroy"></param>

        public void CloseUI(string uiKey, bool destroy = false)
        {
            // 1. 已加载 → 正常关闭
            if (_uidata.TryGetValue(uiKey, out var data))
            {
                data.UI?.OnHide();
                data.IsVisible = false;
                RemoveFromStack(uiKey);

                if (destroy)
                {
                    DestroyUIData(data);
                    _uidata.Remove(uiKey);
                }
                else
                {
                    _hiddenOrder.Enqueue(uiKey);
                    TrimHiddenUI();   // ★ 超阈值淘汰最旧的隐藏 UI
                }
                return;
            }

            // 2. 加载中 → 标记延迟关闭
            if (_loadingTasks.ContainsKey(uiKey))
            {
                _pendingClose.Add(uiKey);
                KeepAlive.Core.Logger.Info("UI", $"加载中，标记为延迟关闭: {uiKey}");
            }
        }

        /// <summary>
        /// 关闭所有 UI（切场景、断线、回登录等）。
        /// ★ SceneLoader.LoadSceneAsync 切场景时必须调用此方法。
        /// </summary>
        public void CloseAll(bool destroy = true)
        {
            // 加载中的也标记为 pendingClose
            foreach (var key in _loadingTasks.Keys)
                _pendingClose.Add(key);

            foreach (var data in _uidata.Values)
            {
                if (data.UI != null)
                {
                    data.UI.OnHide();
                    data.IsVisible = false;
                    if (destroy)
                    {
                        data.UI.OnDestroyUI();
                        Destroy(data.UI.gameObject);
                    }
                }
                if (destroy)
                {
                    data.Handle?.Dispose();
                    data.Handle = null;
                }
            }

            if (destroy)
            {
                _uidata.Clear();
                _hiddenOrder.Clear();
            }
            _uiStack.Clear();

            KeepAlive.Core.Logger.Info("UI", $"CloseAll (destroy={destroy})");
        }

        /// <summary>
        /// 返回
        /// </summary>

        public void Back()
        {
            if (_uiStack.Count == 0) return;

            string topKey = _uiStack.Pop();
            CloseUI(topKey, destroy: false);

            if (_uiStack.Count > 0)
            {
                string newTop = _uiStack.Peek();
                if (_uidata.TryGetValue(newTop, out var data) && data.UI != null)
                {
                    // ★ 补齐 SetActive(true)
                    data.UI.gameObject.SetActive(true);
                    data.UI.OnShow();
                    data.IsVisible = true;
                }
            }
        }

        // ── 内部辅助 ───────────────────────────────────────────

        private void ShowUI(UIData data)
        {
            if (data.UI == null) return;
            data.UI.gameObject.SetActive(true);
            data.UI.OnShow();
            data.IsVisible = true;
            PushToStack(data.UI.UIKey);
            KeepAlive.Core.Logger.Info("UI", $"显示界面: {data.UI.UIKey}");
        }

        private void DestroyUIData(UIData data)
        {
            if (data.UI != null)
            {
                data.UI.OnDestroyUI();
                Destroy(data.UI.gameObject);
            }
            data.Handle?.Dispose();
            data.Handle = null;
        }

        private void TrimHiddenUI()
        {
            while (_hiddenOrder.Count > MaxHiddenUICount)
            {
                string evictKey = _hiddenOrder.Dequeue();
                if (_uidata.TryGetValue(evictKey, out var data) && !data.IsVisible && data.UI != null)
                {
                    DestroyUIData(data);
                    _uidata.Remove(evictKey);
                    KeepAlive.Core.Logger.Info("UI", $"LRU 淘汰隐藏界面: {evictKey}");
                }
            }
        }

        private void PushToStack(string uiKey)
        {
            // 栈顶就是目标 → 不动
            if (_uiStack.Count > 0 && _uiStack.Peek() == uiKey) return;

            // 栈里已有但不在栈顶 → 移除旧位置，push 到栈顶
            if (_uiStack.Contains(uiKey)) RemoveFromStack(uiKey);
            _uiStack.Push(uiKey);
        }

        private void RemoveFromStack(string uiKey)
        {
            if (!_uiStack.Contains(uiKey)) return;

            var temp = new List<string>();
            while (_uiStack.Count > 0)
            {
                var k = _uiStack.Pop();
                if (k != uiKey) temp.Add(k);
            }
            for (int i = temp.Count - 1; i >= 0; i--)
                _uiStack.Push(temp[i]);
        }
    }
}