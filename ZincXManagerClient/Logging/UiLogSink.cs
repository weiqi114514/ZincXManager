using System;  // Action<LogLevel, string>:接收方自己决定怎么落地
using Avalonia.Threading;  // UI 线程调度
using ZincXManagerShared.Logging;  // 共享库

namespace ZincXManagerClient.Logging;

/// <summary>
/// 日志输出目标:把共享库 <see cref="Logger"/> 的日志实时丢给界面。
///
/// <para>作用:共享库(网络、文件、命令)里所有 Logger.Log 都会经过这里,
/// 所以界面能实时看到"收到包 / 连接失败 / 配置读取"这些日志,不用另写一套。</para>
///
/// <para>注意:Logger 可能在网络线程上回调,而 Avalonia 只能在 UI 线程改界面,
/// 所以这里统一用 <see cref="Dispatcher.UIThread"/> 切回去。</para>
/// </summary>
public sealed class UiLogSink : ILogger
{
    /// <summary>日志落到哪儿(由使用方决定写进哪个集合)。</summary>
    private readonly Action<LogLevel, string> _append;

    /// <summary>绑定接收方:每来一条日志就调一次 <paramref name="append"/>。</summary>
    public UiLogSink(Action<LogLevel, string> append)
    {
        _append = append;
    }

    /// <summary>Logger 每次输出都会调用这里(可能不在 UI 线程)。</summary>
    public void Log(LogLevel level, string message)
    {
        Dispatcher.UIThread.Post(() => _append(level, message));
    }
}
