using System;  // 基础类型 / 常用静态方法
using System.Collections.ObjectModel;  // 可观察集合(界面绑定用)
using ZincXManagerClient.Models;  // LogEntry:日志面板里的一行
using ZincXManagerShared.Logging;  // 共享库

namespace ZincXManagerClient.Logging;

/// <summary>
/// 进程级实时日志缓冲:从启动那一刻起把 <see cref="Logger"/> 的输出攒在内存里,
/// 主界面的「日志」页直接绑这个集合,边写日志边刷新,不再去读日志文件。
///
/// <para>为什么放在进程级:主界面是登录之后才创建的,如果只有它自己注册 sink,
/// 启动、连接、登录这些日志一条都看不到。这里在 <c>Program.OpenLog</c> 之后立刻挂上,
/// 谁先开窗都能看到完整的一段。</para>
/// </summary>
public static class LiveLog
{
    private static readonly object _lock = new();

    // 是否已经挂过 sink:挂多次会重复一条日志
    private static bool _started;

    /// <summary>实时日志行(界面直接绑定,进程启动以来的全部,不截断)。</summary>
    public static ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>挂上共享库日志(重复调用只挂一次)。</summary>
    public static void EnsureStarted()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
        }

        Logger.AddSink(new UiLogSink(Append));
    }

    /// <summary>清空面板(不影响日志文件)。</summary>
    public static void Clear() => Entries.Clear();

    /// <summary>追加一行(由 <see cref="UiLogSink"/> 在 UI 线程调用)。</summary>
    private static void Append(LogLevel level, string message)
        => Entries.Add(new LogEntry(DateTime.Now, level.GetName(), message));
}
