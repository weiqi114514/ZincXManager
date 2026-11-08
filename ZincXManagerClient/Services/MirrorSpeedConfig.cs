using System;  // TimeSpan
using ZincXManagerShared.Logging;  // Logger / LogLevel
using ZincXManagerShared.Update;  // MirrorSpeedPolicy:缺省阈值

namespace ZincXManagerClient.Services;

/// <summary>
/// 镜像测速换源的配置(都在 config.ini 的 update.speed.* 下):
/// 开关 + 速度下限 + 首次判定等待 + 复检间隔 + 连接超时。缺键自动写默认值。
///
/// <para>开关关掉时退回旧行为:按顺序试,连不上或非 2xx 才换下一个,不按速度判。</para>
/// </summary>
public static class MirrorSpeedConfig
{
    /// <summary>配置键:是否启用测速换源(1 开 / 0 关)</summary>
    private const string KeyEnabled = "update.speed.enabled";

    /// <summary>配置键:速度下限,单位 KB/s</summary>
    private const string KeyMinKb = "update.speed.minkb";

    /// <summary>配置键:开始下载后多久做第一次判定,单位秒</summary>
    private const string KeyFirstSeconds = "update.speed.firstseconds";

    /// <summary>配置键:第一次判定之后每隔多久复检,单位秒</summary>
    private const string KeyIntervalSeconds = "update.speed.intervalseconds";

    /// <summary>配置键:多久拿不到响应头就算连不上,单位秒</summary>
    private const string KeyConnectSeconds = "update.speed.connectseconds";

    /// <summary>缺省:是否启用(1 开 / 0 关)。</summary>
    private const int DefaultEnabled = 1;

    /// <summary>缺省:速度下限(KB/s),取共享策略里的 500 KB/s。</summary>
    private const int DefaultMinKb = (int)(MirrorSpeedPolicy.MinBytesPerSecond / 1024);

    /// <summary>缺省:首次判定等待(秒)。</summary>
    private static readonly int DefaultFirstSeconds = (int)MirrorSpeedPolicy.FirstCheckDelay.TotalSeconds;

    /// <summary>缺省:复检间隔(秒)。</summary>
    private static readonly int DefaultIntervalSeconds = (int)MirrorSpeedPolicy.CheckInterval.TotalSeconds;

    /// <summary>缺省:连接超时(秒)。</summary>
    private static readonly int DefaultConnectSeconds = (int)MirrorSpeedPolicy.ConnectTimeout.TotalSeconds;

    /// <summary>是否启用测速换源。</summary>
    public static bool Enabled => ReadInt(KeyEnabled, DefaultEnabled) != 0;

    /// <summary>速度下限(B/s),至少 1 KB/s。</summary>
    public static long MinBytesPerSecond =>
        Math.Max(1, ReadInt(KeyMinKb, DefaultMinKb)) * 1024L;

    /// <summary>开始下载后多久做第一次判定,至少 1 秒。</summary>
    public static TimeSpan FirstCheckDelay =>
        TimeSpan.FromSeconds(Math.Max(1, ReadInt(KeyFirstSeconds, DefaultFirstSeconds)));

    /// <summary>复检间隔,至少 1 秒。</summary>
    public static TimeSpan CheckInterval =>
        TimeSpan.FromSeconds(Math.Max(1, ReadInt(KeyIntervalSeconds, DefaultIntervalSeconds)));

    /// <summary>连接超时,至少 3 秒(太短会把正常的镜像误判成连不上)。</summary>
    public static TimeSpan ConnectTimeout =>
        TimeSpan.FromSeconds(Math.Max(3, ReadInt(KeyConnectSeconds, DefaultConnectSeconds)));

    /// <summary>
    /// 确保五个键都在:缺的写缺省值,已经有了的值一个字不动(配置迁移与设置页初始化用)。
    /// <para>缺省值从这里统一取,迁移代码不必另抄一份数字。</para>
    /// </summary>
    public static void EnsureDefaults()
    {
        _ = ReadInt(KeyEnabled, DefaultEnabled);
        _ = ReadInt(KeyMinKb, DefaultMinKb);
        _ = ReadInt(KeyFirstSeconds, DefaultFirstSeconds);
        _ = ReadInt(KeyIntervalSeconds, DefaultIntervalSeconds);
        _ = ReadInt(KeyConnectSeconds, DefaultConnectSeconds);
    }

    /// <summary>读一个整数键;缺失或写坏了就用缺省值并记一条日志。</summary>
    private static int ReadInt(string key, int defaultValue)
    {
        var raw = ClientConfig.Get(key, defaultValue.ToString()).Trim();
        if (int.TryParse(raw, out var value) && value >= 0)
        {
            return value;
        }

        Logger.Log(LogLevel.Warning, $"[更新] {key} 的值「{raw}」不是有效整数,按默认值 {defaultValue} 处理");
        return defaultValue;
    }

    /// <summary>当前策略的一句话摘要(日志 / 排查用)。</summary>
    public static string Describe() =>
        Enabled
            ? $"测速换源开:下限 {MinBytesPerSecond / 1024} KB/s,{FirstCheckDelay.TotalSeconds:F0} 秒判定," +
              $"每 {CheckInterval.TotalSeconds:F0} 秒复检,连接超时 {ConnectTimeout.TotalSeconds:F0} 秒"
            : "测速换源关:按顺序试,只在失败时换下一个";
}
