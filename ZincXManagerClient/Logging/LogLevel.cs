namespace ZincXManagerClient.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
    Fatal
}

public static class LogLevelExtensions
{
    public static string GetName(this LogLevel level)
    {
        return level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO",
            LogLevel.Warning => "WARNING",
            LogLevel.Error => "ERROR",
            LogLevel.Fatal => "FATAL",
            _ => "UNKNOWN"
        };
    }
}