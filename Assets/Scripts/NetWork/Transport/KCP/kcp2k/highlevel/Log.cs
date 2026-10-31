// A simple logger class that uses Console.WriteLine by default.
// Can also do Logger.LogMethod = Debug.Log for Unity etc.
// (this way we don't have to depend on UnityEngine)
using System;

namespace kcp2k
{
    /// <summary>
    /// 这个 Log 类极其精简，只有短短 5 行代码，但它体现了底层网络库极高的自我修养（设计模式）。
    ///一句话总结：它定义了一个“日志插槽”，允许你在不同的平台（Unity、控制台服务器）上，把 kcp2k 内部的日志无缝接入到你自己的日志系统中。
    /// </summary>
    public static class Log
    {
        /// <summary>
        /// public static Action<string>这里用了 C# 的委托（Action）。Action<string> 表示“一个接收字符串参数、没有返回值的方法”。你可以把它理解为一个函数指针或者日志回调接口。
        /// = Console.WriteLine这是默认实现。如果没人管它，它就使用 .NET 标准库的控制台输出。
        /// </summary>
        public static Action<string> Info    = Console.WriteLine;
        public static Action<string> Warning = Console.WriteLine;
        /// <summary>
        /// Console.Error.WriteLine：错误日志默认输出到标准错误流（在控制台中通常是红色的）。
        /// </summary>
        public static Action<string> Error   = Console.Error.WriteLine;
    }
}
