using System;
using System.Net.Sockets;

namespace Network.Transport
{
    public enum ChannelError
    {
        /// <summary>
        /// 无异常
        /// </summary>
        None = 0,
        /// <summary>
        /// 对端正常关闭（FIN）
        /// </summary>
        NormalClose,            
        /// <summary>
        /// 连接被重置（RST）
        /// </summary>
        ConnectionReset,        
        /// <summary>
        /// 连接被拒绝（端口未开） 
        /// </summary>
        ConnectionRefused,      
        /// <summary>
        /// 超时
        /// </summary>
        TimedOut,               
        /// <summary>
        /// 网络不可达
        /// </summary>
        NetworkUnreachable,     
        /// <summary>
       /// 主机不可达
       /// </summary>
        HostUnreachable,       
        /// <summary>
                                /// 本地主动中止
                                /// </summary>
        Aborted,               
        /// <summary>
        /// 对象已释放
        /// </summary>
        ObjectDisposed,         
        /// <summary>
        /// 未知错误
        /// </summary>
        Unknown                 
    }

    /// <summary>
    /// 用于将系统提供的报错映射为自定义报错
    /// </summary>
    public static class ChannelErrorMapper
    {
        public static ChannelError FromSocketError(SocketError error)
        {
            switch (error)
            {
                case SocketError.ConnectionReset:
                    return ChannelError.ConnectionReset;
                case SocketError.ConnectionRefused:
                    return ChannelError.ConnectionRefused;
                case SocketError.TimedOut:
                    return ChannelError.TimedOut;
                case SocketError.NetworkUnreachable:
                case SocketError.NetworkDown:
                    return ChannelError.NetworkUnreachable;
                case SocketError.HostUnreachable:
                case SocketError.HostDown:
                    return ChannelError.HostUnreachable;
                case SocketError.OperationAborted:
                case SocketError.Interrupted:
                    return ChannelError.Aborted;
                case SocketError.NotConnected:
                case SocketError.NotSocket:
                case SocketError.Shutdown:
                    return ChannelError.ObjectDisposed;
                default:
                    return ChannelError.Unknown;
            }
        }

        public static ChannelError FromException(Exception ex)
        {
            if (ex is SocketException se)
                return FromSocketError(se.SocketErrorCode);
            if (ex is OperationCanceledException)
                return ChannelError.Aborted;
            if (ex is ObjectDisposedException)
                return ChannelError.ObjectDisposed;
            if (ex is System.IO.IOException && ex.InnerException is SocketException inner)
                return FromSocketError(inner.SocketErrorCode);
            return ChannelError.Unknown;
        }
    }
}