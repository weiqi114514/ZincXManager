using System;
using ZincXManagerServer.FileIO;

namespace ZincXManagerServer.Logging;

public static class Logger
{
    // 保证多线程（Task.Run）下写文件不会乱序
    private static readonly object _lock = new();

    // 获取日志等级名称（对应 C++ 的 getName）
    public static string GetName(LogLevel level) => level switch
    {
        LogLevel.Fatal => "Fatal",
        LogLevel.Error => "Error",
        LogLevel.Warning => "Warning",
        LogLevel.Info => "Info",
        LogLevel.Debug => "Debug",
        LogLevel.Trace => "Trace",
        _ => "Unknown"
    };

    // 写日志（对应 C++ 的 logging::log）
    public static void Log(LogLevel level, string message)
    {
        // 拼装格式：[等级] [时间] 消息
        var line = $"[{GetName(level)}] [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

        lock (_lock)
        {
            // 输出到控制台
            Console.WriteLine(line);

            // 写入日志文件（用全局对象 logF，对应 C++ 的 zFile::logF）
            ZFile.logF.WriteFileTxt(line, true);
        }
    }
}