using System;
using System.Collections.Generic;
using System.IO;
using ZincXManagerShared.FileIO;

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
    /// 按端类型前缀生成默认日志文件名,例如 <c>DefaultLogFileName("ZXMS")</c> → <c>ZXMS2026-10-06_14-10-50.log</c>。
    /// </summary>
    public static string DefaultLogFileName(string prefix, string? directory = null)
    {
        var name = $"{prefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log";
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }

    /// <summary>按端类型前缀打开全局日志文件(ZXMC / ZXMS / ZXMOS 三个端统一走这里)。</summary>
    public static bool OpenDefaultLogFile(string prefix, out string fileName, string? directory = null)
    {
        fileName = DefaultLogFileName(prefix, directory);
        return ZFile.logF.FileOperate(fileName, "rwNFC");
    }

    /// <summary>解析等级名。</summary>
    public static bool TryParseLevel(string? text, out LogLevel level) => LogLevelExtensions.TryParse(text, out level);

    /// <summary>写一条日志。</summary>
    public static void Log(LogLevel level, string message)
    {
        ILogger[] sinks;
        lock (_lock)
        {
            if (level < _minLevel) return;
            sinks = _sinks.Count == 0 ? Array.Empty<ILogger>() : _sinks.ToArray();
        }

        var line = $"[{level.GetName()}] [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

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
}
