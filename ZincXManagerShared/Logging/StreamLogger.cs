using System;
using System.IO;
using System.Text;

namespace ZincXManagerShared.Logging;

/// <summary>
/// 独立的文件日志写入器:每个实例写自己的文件,不使用全局的 <see cref="Logger"/> 与 ZFile.logF。
/// 适合"一个对象一份日志"或不想污染全局日志文件的场景。
/// </summary>
public sealed class StreamLogger : ILogger, IDisposable
{
    private readonly object _lock = new();
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private LogLevel _minLevel;
    private bool _disposed;

    /// <summary>创建(或追加打开)日志文件。</summary>
    /// <param name="filePath">日志文件路径,目录不存在会自动创建。</param>
    /// <param name="minLevel">该实例自己的等级门槛。</param>
    public StreamLogger(string filePath, LogLevel minLevel = LogLevel.Debug)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("日志文件路径不能为空", nameof(filePath));

        _minLevel = minLevel;

        var full = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // FileShare.ReadWrite:其它进程(例如日志查看器)可以边写边读
        _stream = new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>当前日志文件全路径。</summary>
    public string FilePath => _stream.Name;

    /// <summary>设置该实例的等级门槛。</summary>
    public void SetLevel(LogLevel level)
    {
        lock (_lock) _minLevel = level;
    }

    /// <summary>写一条日志。</summary>
    public void Log(LogLevel level, string message)
    {
        lock (_lock)
        {
            if (_disposed || level < _minLevel) return;
            _writer.WriteLine($"[{level.GetName()}] [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
        }
    }

    /// <summary>关闭文件。</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
            _stream.Dispose();
        }
    }
}
