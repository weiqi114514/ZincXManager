using System;  // TimeSpan / 基础类型
using System.Collections.Generic;  // IReadOnlyList
using System.Diagnostics;  // Stopwatch:测速窗口
using System.IO;  // File.Create
using System.Net.Http;  // HttpClient:下载
using System.Threading;  // CancellationToken
using System.Threading.Tasks;  // Task

namespace ZincXManagerShared.Update;

/// <summary>一次下载的结局:成没成、最终用的地址、实测速度(B/s)。</summary>
/// <param name="Ok">是否完整下完</param>
/// <param name="Url">实际用的地址</param>
/// <param name="BytesPerSecond">实测速度(失败时是失败前那一段的平均速度)</param>
public sealed record MirrorDownloadOutcome(bool Ok, string Url, double BytesPerSecond);

/// <summary>
/// 带测速的镜像下载:按候选顺序试,开始 <c>firstCheckDelay</c> 后速度仍低于
/// <c>minBytesPerSecond</c> 就换下一个;连不上也换下一个;
/// 全部达不到阈值时,回头用实测最快的那份把整份下完。
///
/// <para>放 Shared 是为了能被回归测试直接驱动:测速换源靠的是「等一会儿再看速度」,
/// 阈值和时间窗口都能传进来,测试才不用真等 10 秒。</para>
/// </summary>
public static class MirrorDownloader
{
    /// <summary>复用连接(下载大文件,别每次握手)。</summary>
    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// 按候选顺序下载到 <paramref name="destination"/>。
    /// </summary>
    /// <param name="candidates">候选地址(镜像在前、官方最后)</param>
    /// <param name="destination">落盘路径(每次换源都重写这一份)</param>
    /// <param name="minBytesPerSecond">速度下限(B/s)</param>
    /// <param name="firstCheckDelay">开始后多久做第一次速度判定</param>
    /// <param name="checkInterval">之后每隔多久再判一次</param>
    /// <param name="connectTimeout">多久没拿到响应头就算连不上</param>
    /// <param name="coverSpeedLimit">是否按速度换源(false = 只在连不上 / 下载失败时换下一个)</param>
    /// <param name="onProgress">进度(0-100)</param>
    /// <param name="log">日志回调(可空)</param>
    /// <param name="ct">取消令牌</param>
    public static async Task<MirrorDownloadOutcome> DownloadAsync(
        IReadOnlyList<string> candidates,
        string destination,
        long minBytesPerSecond,
        TimeSpan firstCheckDelay,
        TimeSpan checkInterval,
        TimeSpan connectTimeout,
        bool coverSpeedLimit = true,
        IProgress<int>? onProgress = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var tried = new List<MirrorDownloadOutcome>();

        foreach (var candidate in candidates)
        {
            var attempt = await TryAsync(candidate, destination, minBytesPerSecond, firstCheckDelay, checkInterval,
                connectTimeout, onProgress, log, enforceSpeed: coverSpeedLimit, ct);
            if (attempt.Ok)
            {
                onProgress?.Report(100);
                return attempt;
            }

            tried.Add(attempt);
        }

        // 没开测速就只有这一轮:按顺序试完就结束,不再回头
        if (!coverSpeedLimit)
        {
            log?.Invoke("[更新] 所有下载地址都失败");
            return new MirrorDownloadOutcome(false, "", 0);
        }

        // 全都没达标:按实测速度从高到低再完整下一遍,不再中途换源
        tried.Sort((a, b) => b.BytesPerSecond.CompareTo(a.BytesPerSecond));
        if (tried.Count > 0)
        {
            log?.Invoke($"[更新] 没有一个地址达到 {minBytesPerSecond / 1024} KB/s,改下实测最快的 " +
                        $"{tried[0].Url}({tried[0].BytesPerSecond / 1024:F0} KB/s)");
        }

        foreach (var fallback in tried)
        {
            var attempt = await TryAsync(fallback.Url, destination, minBytesPerSecond, firstCheckDelay, checkInterval,
                connectTimeout, onProgress, log, enforceSpeed: false, ct);
            if (attempt.Ok)
            {
                onProgress?.Report(100);
                return attempt;
            }
        }

        log?.Invoke("[更新] 所有下载地址都失败");
        return new MirrorDownloadOutcome(false, "", 0);
    }

    /// <summary>试一个地址;异常/太慢/非 2xx 都折成失败的结局。</summary>
    private static async Task<MirrorDownloadOutcome> TryAsync(
        string url,
        string destination,
        long minBytesPerSecond,
        TimeSpan firstCheckDelay,
        TimeSpan checkInterval,
        TimeSpan connectTimeout,
        IProgress<int>? onProgress,
        Action<string>? log,
        bool enforceSpeed,
        CancellationToken ct)
    {
        try
        {
            log?.Invoke("[更新] 下载地址: " + url);
            return await DownloadCoreAsync(url, destination, minBytesPerSecond, firstCheckDelay, checkInterval,
                connectTimeout, onProgress, enforceSpeed, ct);
        }
        catch (SlowMirrorException ex)
        {
            log?.Invoke($"[更新] 这个镜像太慢({ex.BytesPerSecond / 1024:F0} KB/s,低于 {minBytesPerSecond / 1024} KB/s),换下一个: {url}");
            return new MirrorDownloadOutcome(false, url, ex.BytesPerSecond);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log?.Invoke($"[更新] 镜像连不上(超过 {connectTimeout.TotalSeconds:F0} 秒没响应),换下一个: {url}");
            return new MirrorDownloadOutcome(false, url, 0);
        }
        catch (Exception ex)
        {
            log?.Invoke("[更新] 下载异常: " + ex.Message);
            return new MirrorDownloadOutcome(false, url, 0);
        }
    }

    /// <summary>下载主体:边写盘边测速。</summary>
    private static async Task<MirrorDownloadOutcome> DownloadCoreAsync(
        string url,
        string destination,
        long minBytesPerSecond,
        TimeSpan firstCheckDelay,
        TimeSpan checkInterval,
        TimeSpan connectTimeout,
        IProgress<int>? onProgress,
        bool enforceSpeed,
        CancellationToken ct)
    {
        // 只给「拿到响应头」这一段限时:连不上、半天不回就来换源。
        // 拿到头之后必须撤掉定时器,否则大文件读满 15 秒会被它连坐取消。
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(connectTimeout);

        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, connectCts.Token);
        connectCts.CancelAfter(Timeout.InfiniteTimeSpan);

        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"下载返回 {(int)resp.StatusCode}");
        }

        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destination);

        var buffer = new byte[81920];
        var clock = Stopwatch.StartNew();
        long read = 0;
        long windowBytes = 0;
        var windowStart = TimeSpan.Zero;
        var speed = 0.0;
        int n;

        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            windowBytes += n;
            if (total > 0)
            {
                onProgress?.Report((int)(read * 100 / total));
            }

            var now = clock.Elapsed;

            // 第一次判定看「开始到现在」的平均速度,之后每 checkInterval 看一个窗口
            var due = windowStart == TimeSpan.Zero
                ? now >= firstCheckDelay
                : now - windowStart >= checkInterval;
            if (!due)
            {
                continue;
            }

            var span = now - windowStart;
            speed = windowBytes / Math.Max(span.TotalSeconds, 0.001);
            if (enforceSpeed && speed < minBytesPerSecond)
            {
                throw new SlowMirrorException(speed);
            }

            windowStart = now;
            windowBytes = 0;
        }

        return new MirrorDownloadOutcome(true, url, speed);
    }

    /// <summary>建 HttpClient(带 UA,不然 GitHub 会 403)。</summary>
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZincXManagerClient/1.0");
        return client;
    }

    /// <summary>速度不达标:内部信号,只用来让这一次尝试提前结束。</summary>
    private sealed class SlowMirrorException(double bytesPerSecond) : Exception
    {
        /// <summary>实测速度(B/s)。</summary>
        public double BytesPerSecond { get; } = bytesPerSecond;
    }
}
