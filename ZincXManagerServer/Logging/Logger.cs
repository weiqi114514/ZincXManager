using System;
using ZincXManagerServer.FileIO;
using static System.Net.WebRequestMethods;

namespace ZincXManagerServer.Logging;

public static class Logger
{
    private static readonly object _lock = new();
    private static readonly ZFile _logFile = new();

    public static void Init(string fileName)
    {
        _logFile.FileOperate(fileName, "rwNFC");
    }

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

    public static void Log(LogLevel level, string message)
    {
        var line = $"[{GetName(level)}] [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

        lock (_lock)
        {
            Console.WriteLine(line);
            _logFile.WriteFileTxt(line, true);
        }
    }
}