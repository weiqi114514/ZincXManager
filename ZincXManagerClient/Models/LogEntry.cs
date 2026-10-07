using System;

namespace ZincXManagerClient.Models;

/// <summary>界面日志面板里的一行。</summary>
public sealed class LogEntry
{
    public LogEntry(DateTime time, string level, string message)
    {
        Time = time;
        Level = level;
        Message = message;
    }

    /// <summary>发生时间。</summary>
    public DateTime Time { get; }

    /// <summary>日志等级名(Debug/Info/Warning/Error/Fatal)。</summary>
    public string Level { get; }

    /// <summary>日志正文。</summary>
    public string Message { get; }

    /// <summary>界面上直接绑这一行文本。</summary>
    public string Display => $"{Time:HH:mm:ss} [{Level}] {Message}";
}
