namespace ZincXManagerClient.Class.Logging;

using System;
using System.IO;
using System.Text;

public class Logger : IDisposable, ILogger
{
    private readonly object _lock = new object();
    private readonly FileStream _fileStream;
    private readonly StreamWriter _writer;

    public Logger(string filePath = "log.log")
    {
        _fileStream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(_fileStream, Encoding.UTF8, 1024, leaveOpen: true);
    }

    public void Log(LogLevel level, string text)
    {
        string time = DateTime.Now.ToString("HH:mm:ss");
        string line = $"[{level.GetName()}] [{time}] {text}";

        lock (_lock)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _fileStream?.Dispose();
    }
}