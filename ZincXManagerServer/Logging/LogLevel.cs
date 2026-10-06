namespace ZincXManagerServer.Logging;

// 日志等级（对应 C++ 的 enum class LogL : uint8_t）
public enum LogLevel : byte
{
    Debug,      // 调试
    Info,      // 信息
    Trace,      // 跟踪
    Warning,    // 警告
    Error,      // 错误
    Fatal       // 致命
}//