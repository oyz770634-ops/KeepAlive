using Network.Transport;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace Network.Transport
{
    /// <summary>
    /// 算退避时间
    /// </summary>
    public class ReconnectPolicy
    {
        private readonly ChannelConfig _config;

        public ReconnectPolicy(ChannelConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// 第 attempt 次重连的等待时间（秒）
        /// </summary>
        /// <param name="attempt"></param>
        /// <returns></returns>
        public float GetDelay(int attempt)
        {
            // 指数退避：base * 2^(attempt-1)，设上限
            float delay = _config.BaseRetryDelaySec * Mathf.Pow(2, attempt - 1);
            delay = Mathf.Min(delay, _config.MaxRetryDelaySec);

            // 随机抖动 0.5 ~ 1.5，打散重连峰值
            delay *= Random.Range(0.5f, 1.5f);
            return delay;
        }

        /// <summary>
        /// 判断哪些错误不该重连，并返回是否重连
        /// </summary>
        /// <param name="error"></param>
        /// <returns></returns>
        public bool ShouldRetry(ChannelError error)
        {
            switch (error)
            {
                case ChannelError.Aborted:          // 主动取消
                case ChannelError.ObjectDisposed:   // 对象已释放（通常是主动关闭）
                    return false;
                default:
                    return true;
            }
        }
    }
}
