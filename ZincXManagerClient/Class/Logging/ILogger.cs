namespace ZincXManagerClient.Class.Logging;

public interface ILogger
{
    void Log(LogLevel level, string text);
    void Dispose();
}