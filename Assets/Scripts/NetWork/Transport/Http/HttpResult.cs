using UnityEngine;

namespace Network.Transport
{
    public class HttpResult
    {
        public bool IsSuccess;          // 是否请求成功且业务无异常
        public long StatusCode;         // HTTP 状态码 (200, 404, 500)
        public string ErrorMsg;         // 错误信息
        public string RawText;          // 原始返回的 JSON 文本

        public static HttpResult Success(long code, string raw) => new HttpResult
        {
            IsSuccess = true,
            StatusCode = code,
            RawText = raw
        };

        public static HttpResult Fail(long code, string error) => new HttpResult
        {
            IsSuccess = false,
            StatusCode = code,
            ErrorMsg = error
        };
    }
}