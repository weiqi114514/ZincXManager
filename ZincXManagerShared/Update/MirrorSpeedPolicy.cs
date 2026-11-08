using System;
using System.Collections.Generic;

namespace ZincXManagerShared.Update;

/// <summary>
/// 下载镜像的测速换源策略:开始下载 10 秒后速度仍低于 500 KB/s 就换下一个镜像;
/// 全部镜像都达不到时,回头用实测速度最高的那个把整份下完。
///
/// <para>放在 Shared 而不是客户端里,理由与 <see cref="VersionRules"/> 相同:这份阈值是
/// 更新逻辑的一部分,回归测试要能直接断言,各端各抄一份迟早对不上。</para>
/// </summary>
public static class MirrorSpeedPolicy
{
    /// <summary>合格速度下限:500 KB/s(按 1024 进制的 KB 算)。</summary>
    public const long MinBytesPerSecond = 500 * 1024;

    /// <summary>开始下载后多久做第一次判定。</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(10);

    /// <summary>第一次判定之后,每隔多久再看一次(半路掉速也要能发现)。</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(3);

    /// <summary>连不上就算换源的判据:超过这个时间还没拿到响应头。</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>这个窗口里的平均速度是否低于下限。</summary>
    /// <param name="bytes">窗口内下载到的字节数</param>
    /// <param name="window">窗口时长</param>
    public static bool BelowThreshold(long bytes, TimeSpan window) =>
        window > TimeSpan.Zero && bytes / window.TotalSeconds < MinBytesPerSecond;

    /// <summary>是否该换源:至少要等过 <see cref="FirstCheckDelay"/>,再看窗口速度。</summary>
    /// <param name="elapsed">从开始下载到现在的时长</param>
    /// <param name="bytesInWindow">当前窗口内下载到的字节数</param>
    /// <param name="window">当前窗口时长</param>
    public static bool ShouldSwitchAway(TimeSpan elapsed, long bytesInWindow, TimeSpan window) =>
        elapsed >= FirstCheckDelay && BelowThreshold(bytesInWindow, window);

    /// <summary>在若干实测速度里挑最快的那个,返回下标;全为 0 时返回 -1。</summary>
    /// <param name="bytesPerSecond">各候选源的实测速度(B/s)</param>
    public static int PickFastest(IReadOnlyList<double> bytesPerSecond)
    {
        var best = -1;
        var bestSpeed = 0.0;
        for (var i = 0; i < bytesPerSecond.Count; i++)
        {
            if (bytesPerSecond[i] > bestSpeed)
            {
                bestSpeed = bytesPerSecond[i];
                best = i;
            }
        }

        return best;
    }
}
