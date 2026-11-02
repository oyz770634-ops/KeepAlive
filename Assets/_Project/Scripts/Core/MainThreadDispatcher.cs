using System;
using System.Collections.Concurrent;
using UnityEngine;
namespace KeepAlive.Core
{
    /// <summary>
    /// 主要功能就是将多线程执行结束的任务放入主线程中调用
    /// </summary>
    public class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private static bool _isQuitting;

        public static MainThreadDispatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("[MainThreadDispatcher]");
                    _instance = go.AddComponent<MainThreadDispatcher>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }
        /// <summary>
        /// 对外提供的压栈方法
        /// </summary>
        /// <param name="action"></param>
        public static void Enqueue(Action action)
        {
            if (action == null) return;
            if (_isQuitting) return;
            _queue.Enqueue(action);
        }
        /// <summary>
        /// 每帧处理回调函数
        /// </summary>
        private void Update()
        {
            while (_queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[MainThreadDispatcher] 回调执行异常：{e}");
                }
            }
        }

        private void OnApplicationQuit()
        {
            _isQuitting = true;
            while (_queue.TryDequeue(out _)) { }
        }
    }
}