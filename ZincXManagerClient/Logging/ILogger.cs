namespace ZincXManagerClient.Logging;

public interface ILogger
{
    void Log(LogLevel level, string text);
    void Dispose();
}