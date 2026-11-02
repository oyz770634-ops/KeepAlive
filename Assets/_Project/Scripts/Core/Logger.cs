using System;
using System.Collections.Generic;
using UnityEngine;

namespace KeepAlive.Core
{
    /// <summary>
    /// 全局日志系统：支持分级、带时间戳、模块前缀、富文本颜色，以及单模块开关
    /// </summary>
    public static class Logger
    {
        // 全局总开关（打包发布正式版时，可将 Info 关掉）
        public static bool EnableInfo = true;
        public static bool EnableWarning = true;
        public static bool EnableError = true;

        // 针对特定高频模块（如 "Network"）的独立开关字典
        private static readonly Dictionary<string, bool> _moduleSwitches = new Dictionary<string, bool>();

        /// <summary>
        /// 单独开启或关闭某个模块的 Info 日志（不影响其他模块）
        /// </summary>
        /// <param name="module">模块名，如 "Network"</param>
        /// <param name="enable">是否开启</param>
        public static void SetModuleLog(string module, bool enable)
        {
            _moduleSwitches[module] = enable;
        }

        /// <summary>
        /// 普通信息（白色）
        /// </summary>
        public static void Info(string module, string message)
        {
            if (!EnableInfo) return;

            // 检查该模块是否被单独关闭了
            if (_moduleSwitches.TryGetValue(module, out bool enable) && !enable) return;

            Debug.Log(Format(module, message, "#FFFFFF")); // 默认白色
        }

        /// <summary>
        /// 警告信息（黄色）
        /// </summary>
        public static void Warning(string module, string message)
        {
            if (!EnableWarning) return;
            Debug.LogWarning(Format(module, message, "#FFFF00")); // 黄色
        }

        /// <summary>
        /// 错误信息（红色）
        /// </summary>
        public static void Error(string module, string message)
        {
            if (!EnableError) return;
            Debug.LogError(Format(module, message, "#FF0000")); // 红色
        }

        /// <summary>
        /// 专门用于打印异常（自动包含报错堆栈）
        /// </summary>
        public static void Exception(string module, Exception ex, string context = "")
        {
            if (!EnableError) return;
            string msg = string.IsNullOrEmpty(context) ? ex.Message : $"{context} | {ex.Message}";
            Debug.LogError(Format(module, $"{msg}\n{ex.StackTrace}", "#FF0000"));
        }

        /// <summary>
        /// 统一格式化输出内容（带时间戳和颜色）
        /// </summary>
        private static string Format(string module, string message, string colorHex)
        {
            // 使用 Unity 支持的富文本格式，给模块名加上颜色，使其在 Console 中非常醒目
            string moduleColor = "#00BFFF"; // 浅蓝色模块名
            string time = DateTime.Now.ToString("HH:mm:ss.fff");

            // 格式：[模块名] [时间] 内容
            return $"<color={moduleColor}>[{module}]</color> <color=#888888>[{time}]</color> <color={colorHex}>{message}</color>";
        }
    }
}
