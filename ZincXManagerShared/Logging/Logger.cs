using System;  // 基础类型 / 常用静态方法
using System.Collections.Generic;  // 泛型集合
using System.IO;  // 文件与目录读写
using System.Runtime.CompilerServices;  // CallerFilePath:拿到调用方所在的类
using System.Threading;  // 线程名/编号,日志里标出是哪条线程
using ZincXManagerShared.FileIO;  // 共享库

namespace ZincXManagerShared.Logging;

/// <summary>
/// 全局静态日志器(ZXMC / ZXMS / ZXMOS 共用)。
/// 输出目标:
///   1. 控制台(Console.WriteLine,可用 <see cref="EchoToConsole"/> 关闭,适配 GUI 宿主);
///   2. 全局日志文件 <see cref="ZFile.logF"/>(由宿主在启动时 FileOperate 打开);
///   3. 任意数量的额外输出目标(见 <see cref="AddSink"/>)。
/// 日志等级门槛:低于 <see cref="SetLevel"/> 设置等级的日志不输出。
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static readonly List<ILogger> _sinks = new();

    // 日志等级门槛:低于此等级不输出(默认 Debug = 全部输出)
    private static LogLevel _minLevel = LogLevel.Debug;

    // 防止 sink 内部再次调用 Logger.Log 造成无限递归
    [ThreadStatic] private static bool _inSink;

    /// <summary>是否同时输出到控制台。GUI 宿主可以设为 false。</summary>
    public static bool EchoToConsole { get; set; } = true;

    /// <summary>设置日志等级门槛。</summary>
    public static void SetLevel(LogLevel level)
    {
        lock (_lock) _minLevel = level;
    }

    /// <summary>获取当前日志等级门槛。</summary>
    public static LogLevel GetLevel()
    {
        lock (_lock) return _minLevel;
    }

    /// <summary>注册一个额外输出目标(重复注册同一实例会被忽略)。</summary>
    public static void AddSink(ILogger sink)
    {
        if (sink == null) return;
        lock (_lock)
        {
            if (!_sinks.Contains(sink)) _sinks.Add(sink);
        }
    }

    /// <summary>注销输出目标。</summary>
    public static void RemoveSink(ILogger sink)
    {
        if (sink == null) return;
        lock (_lock) _sinks.Remove(sink);
    }

    /// <summary>取等级名。</summary>
    public static string GetName(LogLevel level) => level.GetName();

    /// <summary>
    /// 日志目录。为 null 时依次取:环境变量 <c>ZXM_LOG_DIR</c> → 可执行文件目录下的 <c>logs</c> 子目录。
    /// 相对路径按可执行文件所在目录解析;目录的创建由 ZFile 打开日志文件时完成。
    /// </summary>
    public static string? LogDirectory { get; set; }

    /// <summary>
    /// 解析日志目录路径(只解析、不创建,创建交给 ZFile)。
    /// 优先级:参数 <paramref name="directory"/> &gt; <see cref="LogDirectory"/> &gt; 环境变量 <c>ZXM_LOG_DIR</c> &gt; 可执行文件目录下的 <c>logs</c>。
    /// </summary>
    public static string ResolveLogDirectory(string? directory = null)
    {
        var dir = directory;
        if (string.IsNullOrWhiteSpace(dir)) dir = LogDirectory;
        if (string.IsNullOrWhiteSpace(dir)) dir = Environment.GetEnvironmentVariable("ZXM_LOG_DIR");
        if (string.IsNullOrWhiteSpace(dir)) dir = "logs";

        return Path.IsPathRooted(dir) ? dir : Path.Combine(AppContext.BaseDirectory, dir);
    }

    /// <summary>
    /// 按端类型前缀生成日志文件名(已含目录),例如 <c>DefaultLogFileName("ZXMS")</c> →
    /// <c>D:\ZXM\logs\ZXMS2026-10-06_14-10-50.log</c>。
    /// </summary>
    public static string DefaultLogFileName(string prefix, string? directory = null)
        => Path.Combine(ResolveLogDirectory(directory), $"{prefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");

    /// <summary>按端类型前缀打开全局日志文件;父目录不存在时由 ZFile 的 rwNFC 自动创建。</summary>
    public static bool OpenDefaultLogFile(string prefix, out string fileName, string? directory = null)
    {
        fileName = DefaultLogFileName(prefix, directory);
        return ZFile.logF.FileOperate(fileName, "rwNFC");
    }

    /// <summary>解析等级名。</summary>
    public static bool TryParseLevel(string? text, out LogLevel level) => LogLevelExtensions.TryParse(text, out level);

    /// <summary>写一条日志。调用方不用传任何额外参数 —— 类名与线程标签在这里自动取。</summary>
    public static void Log(LogLevel level, string message,
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0)
    {
        ILogger[] sinks;
        lock (_lock)
        {
            if (level < _minLevel) return;
            sinks = _sinks.Count == 0 ? Array.Empty<ILogger>() : _sinks.ToArray();
        }

        // 形如 [Info] [2026-10-24 14:30:00] [Program/Main] 消息
        var line = $"[{level.GetName()}] [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{ClassOf(callerFile)}/{ThreadOf()}] {message}";

        // 锁外做实际 I/O:Logger 与 ZFile 各自持锁,错开可以彻底避免互锁
        if (EchoToConsole) Console.WriteLine(line);
        ZFile.logF.WriteFileTxt(line, true);

        if (sinks.Length == 0 || _inSink) return;

        _inSink = true;
        try
        {
            foreach (var sink in sinks)
            {
                try
                {
                    sink.Log(level, message);
                }
                catch
                {
                    // 单个输出目标出错不影响其它目标,也不影响主流程
                }
            }
        }
        finally
        {
            _inSink = false;
        }
    }

    /// <summary>调用方文件名 → 类名(Program.cs → Program;App.axaml.cs → App)。</summary>
    private static string ClassOf(string callerFile)
    {
        if (string.IsNullOrEmpty(callerFile)) return "?";
        var name = Path.GetFileNameWithoutExtension(callerFile);
        if (name.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
        return string.IsNullOrEmpty(name) ? "?" : name;
    }

    /// <summary>当前线程标签:起过名字用名字(主线程统一叫 Main),没名字用 T+线程号。</summary>
    private static string ThreadOf()
    {
        var thread = Thread.CurrentThread;
        var name = thread.Name;
        return string.IsNullOrEmpty(name) ? "T" + thread.ManagedThreadId : name;
    }
}
