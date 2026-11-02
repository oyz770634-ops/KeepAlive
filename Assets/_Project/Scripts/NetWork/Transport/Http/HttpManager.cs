using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Network.Transport
{
    // 隐藏的协程宿主，用于在非 MonoBehaviour 类中启动协程
    internal class HttpCoroutineRunner : MonoBehaviour { }

    public static class HttpManager
    {
        private static HttpCoroutineRunner _runner;

        // 全局 BaseUrl 和 Token
        public static string BaseUrl = "http://127.0.0.1:8080";
        public static string Token = "";

        private static HttpCoroutineRunner Runner
        {
            get
            {
                if (_runner == null)
                {
                    var go = new GameObject("[HttpManager Runner]");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _runner = go.AddComponent<HttpCoroutineRunner>();
                }
                return _runner;
            }
        }

        // ==================== 对业务层暴露的泛型接口 ====================

        /// <summary>
        /// GET 请求，自动反序列化
        /// </summary>
        public static Coroutine Get<T>(string path, Action<T> onSuccess, Action<HttpResult> onFail = null, int timeout = 10)
        {
            string fullUrl = CombineUrl(path);
            return Runner.StartCoroutine(SendRequest<object, T>(fullUrl, UnityWebRequest.kHttpVerbGET, null, onSuccess, onFail, timeout));
        }

        /// <summary>
        /// POST 请求，自动序列化 Body 并反序列化 Response
        /// </summary>
        public static Coroutine Post<TRequest, TResponse>(string path, TRequest requestData, Action<TResponse> onSuccess, Action<HttpResult> onFail = null, int timeout = 10)
        {
            string fullUrl = CombineUrl(path);
            return Runner.StartCoroutine(SendRequest<TRequest, TResponse>(fullUrl, UnityWebRequest.kHttpVerbPOST, requestData, onSuccess, onFail, timeout));
        }

        /// <summary>
        /// POST 请求（不需要处理返回结果）
        /// </summary>
        public static Coroutine Post<TRequest>(string path, TRequest requestData, Action<HttpResult> onComplete = null, int timeout = 10)
        {
            string fullUrl = CombineUrl(path);
            return Runner.StartCoroutine(SendRequest<TRequest, object>(fullUrl, UnityWebRequest.kHttpVerbPOST, requestData, null, onComplete, timeout));
        }

        // ==================== 核心底层逻辑 ====================

        private static IEnumerator SendRequest<TRequest, TResponse>(
            string url,
            string method,
            TRequest requestData,
            Action<TResponse> onSuccess,
            Action<HttpResult> onFail,
            int timeout)
        {
            using (UnityWebRequest req = new UnityWebRequest(url, method))
            {
                req.timeout = timeout;
                req.downloadHandler = new DownloadHandlerBuffer();

                // 1. 注入全局 Header (比如 Token)
                if (!string.IsNullOrEmpty(Token))
                {
                    req.SetRequestHeader("Authorization", "Bearer " + Token);
                }
                req.SetRequestHeader("Content-Type", "application/json");

                // 2. 处理 POST Body
                if (method == UnityWebRequest.kHttpVerbPOST && requestData != null)
                {
                    string jsonBody = JsonUtility.ToJson(requestData);
                    byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                    req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                }

                // 3. 发送请求并等待
                yield return req.SendWebRequest();

                // 4. 结果统一拦截
                if (req.result == UnityWebRequest.Result.Success)
                {
                    string rawText = req.downloadHandler.text;

                    // 如果业务层不需要反序列化 (TResponse 是 object)
                    if (typeof(TResponse) == typeof(object))
                    {
                        onSuccess?.Invoke(default);
                        onFail?.Invoke(HttpResult.Success(req.responseCode, rawText));
                        yield break;
                    }

                    // 尝试解析 JSON
                    try
                    {
                        TResponse response = JsonUtility.FromJson<TResponse>(rawText);
                        onSuccess?.Invoke(response);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[HttpManager] JSON 解析失败: {e.Message}, Raw: {rawText}");
                        onFail?.Invoke(HttpResult.Fail(req.responseCode, "数据解析异常"));
                    }
                }
                else if (req.result == UnityWebRequest.Result.ProtocolError)
                {
                    // 4xx, 5xx 错误
                    Debug.LogWarning($"[HttpManager] HTTP错误 {req.responseCode}: {req.error}");
                    onFail?.Invoke(HttpResult.Fail(req.responseCode, req.error));
                }
                else if (req.result == UnityWebRequest.Result.ConnectionError)
                {
                    // 断网、超时
                    Debug.LogWarning($"[HttpManager] 网络错误: {req.error}");
                    onFail?.Invoke(HttpResult.Fail(0, "网络连接失败"));
                }
                else
                {
                    onFail?.Invoke(HttpResult.Fail(req.responseCode, req.error));
                }
            }
        }

        private static string CombineUrl(string path)
        {
            if (path.StartsWith("http")) return path;
            return BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        }
    }
}