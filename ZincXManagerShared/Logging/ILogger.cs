namespace ZincXManagerShared.Logging;

/// <summary>
/// 日志输出目标抽象。
/// 宿主(例如 ZXMC 的界面)可以实现它,然后通过 <see cref="Logger.AddSink"/> 注册,
/// 把日志同时显示到界面上。
/// </summary>
public interface ILogger
{
    /// <summary>写一条日志。实现方需自行保证线程安全。</summary>
    void Log(LogLevel level, string message);
}
