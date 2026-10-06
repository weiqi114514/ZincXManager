namespace ZincXManagerShared.Logging;

/// <summary>
/// 日志等级。
/// 数值即严重程度,数值越大越严重,低于当前门槛的日志不输出。
/// 顺序必须保持 Debug &lt; Trace &lt; Info &lt; Warning &lt; Error &lt; Fatal,
/// 与命令 help 中列出的顺序一致(旧版把 Info 排在 Trace 前面,导致"门槛设为 Trace 时 Info 反而不输出")。
/// </summary>
public enum LogLevel : byte
{
    Debug = 0,      // 调试
    Trace = 1,      // 跟踪
    Info = 2,       // 信息
    Warning = 3,    // 警告
    Error = 4,      // 错误
    Fatal = 5       // 致命
}

/// <summary>日志等级的名称与解析。</summary>
public static class LogLevelExtensions
{
    /// <summary>等级名(首字母大写,与日志文件中的写法一致)。</summary>
    public static string GetName(this LogLevel level) => level switch
    {
        LogLevel.Debug => "Debug",
        LogLevel.Trace => "Trace",
        LogLevel.Info => "Info",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Fatal => "Fatal",
        _ => "Unknown"
    };

    /// <summary>解析等级名(忽略大小写),只接受已定义的等级。</summary>
    public static bool TryParse(string? text, out LogLevel level)
    {
        level = LogLevel.Info;
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (Enum.TryParse(text, ignoreCase: true, out LogLevel parsed)
            && Enum.IsDefined(typeof(LogLevel), parsed))
        {
            level = parsed;
            return true;
        }
        return false;
    }
}
