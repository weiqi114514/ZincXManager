using System;                              // DateTime / Exception / TimeSpan
using System.Collections.Concurrent;       // ConcurrentDictionary
using System.Threading;                    // SemaphoreSlim / CancellationToken / CancellationTokenSource
using System.Threading.Tasks;              // Task
using ZincXManagerShared.Logging;          // Logger / LogLevel
using ZincXManagerShared.Network;          // RconClient / RconAuthException / RconTimeoutException

namespace ZincXManagerServer.Zxms;

/// <summary>
/// 每台服一条 RCON 长连接：懒连、断了下次发送时自动重连、同一条连接上的命令串行。
///
/// <para>只服务控制台模式为 1 的服；桥接模式的服走 ZXMM 专用通道，不经过这里。</para>
/// <para>一条 RCON 连接同一时刻只能跑一问一答，所以每台服一把 <see cref="SemaphoreSlim"/>，
/// 并发进来的命令排队而不是把回包读串。</para>
/// </summary>
public static class RconHub
{
    /// <summary>建连超时：连不上要尽快回话，不能把控制台卡在这里。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>失败后的静默窗口：对端根本没开 RCON 时，别每条命令都去敲一遍。</summary>
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 判在线的容忍窗口：超过这么久没成功往返过就不再报在线。
    /// 对端进程被杀后 <c>TcpClient.Connected</c> 仍旧是 true，只能靠这个时间差兜住。
    /// </summary>
    private static readonly TimeSpan OnlineFreshness = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<int, RconClient> _clients = new();       //每服一条连接
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> _gates = new();      //同一条连接上的命令串行
    private static readonly ConcurrentDictionary<int, string> _signatures = new();        //连接是用哪套地址+密码建的
    private static readonly ConcurrentDictionary<int, DateTime> _lastFailureAt = new();   //静默窗口的起点
    private static readonly ConcurrentDictionary<int, string> _lastFailureReason = new(); //静默窗口里回的原因

    /// <summary>
    /// 在这台服的 RCON 上执行一条命令；连不上不抛，返回 <c>(false, 原因)</c>。
    /// </summary>
    /// <param name="entry">目标服的连接项（用它的 RCON 主机、端口、密码）。</param>
    /// <param name="command">要执行的 MC 命令，不带前导斜杠。</param>
    /// <param name="ct">调用方的取消令牌。</param>
    public static async Task<(bool Ok, string Result)> SendAsync(ZxmmEntry entry, string command, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(entry.Id, _ => new SemaphoreSlim(1, 1));   //按编号复用,别每次新建
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsCoolingDown(entry, out var cooling))
            {
                return (false, cooling);
            }

            RconClient? client;
            string error;
            try
            {
                (client, error) = await EnsureConnectedAsync(entry, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 连接阶段被调用方取消:与命令阶段保持同一口径,别把异常抛给上层
                return (false, "已取消");
            }

            if (client is null)
            {
                return (false, error);
            }

            try
            {
                var output = await client.SendAsync(command, ct).ConfigureAwait(false);
                return (true, output);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return (false, "已取消");
            }
            catch (RconTimeoutException)
            {
                // 超时说明这条命令的回包随时会晚到,不清掉就会和下一条命令串包
                await DropAsync(entry.Id).ConfigureAwait(false);
                Logger.Log(LogLevel.Warning, $"[RCON] {entry.Id} {entry.Name} 命令超时 → {entry.RconAddress}，已丢弃连接准备重连");
                return (false, "RCON 命令超时");
            }
            catch (OperationCanceledException)
            {
                // 超时说明这条命令的回包随时会晚到,不清掉就会和下一条命令串包
                await DropAsync(entry.Id).ConfigureAwait(false);
                Logger.Log(LogLevel.Warning, $"[RCON] {entry.Id} {entry.Name} 命令超时 → {entry.RconAddress}，已丢弃连接准备重连");
                return (false, "RCON 命令超时");
            }
            catch (TimeoutException)
            {
                await DropAsync(entry.Id).ConfigureAwait(false);
                Logger.Log(LogLevel.Warning, $"[RCON] {entry.Id} {entry.Name} 命令超时 → {entry.RconAddress}，已丢弃连接准备重连");
                return (false, "RCON 命令超时");
            }
            catch (RconAuthException)
            {
                await DropAsync(entry.Id).ConfigureAwait(false);
                Logger.Log(LogLevel.Warning, $"[RCON] {entry.Id} {entry.Name} 会话失效 → {entry.RconAddress}，已丢弃连接准备重连");
                return (false, "RCON 密码不对");
            }
            catch (Exception ex)
            {
                await DropAsync(entry.Id).ConfigureAwait(false);
                Logger.Log(LogLevel.Warning, $"[RCON] {entry.Id} {entry.Name} 命令失败 → {entry.RconAddress} {ex.Message}");
                return (false, $"RCON 命令失败:{ex.Message}");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 这台服的 RCON 现在通不通。这是保守判定，不是探活：不发命令（不发会污染命令日志、也会占串行锁），
    /// 只要求「有会话 + socket 没坏 + 最近 30 秒内成功往返过」，刚连上还没发过命令的会话也算新鲜。
    /// </summary>
    /// <param name="serverId">服务器编号。</param>
    public static bool IsOnline(int serverId)
    {
        if (!_clients.TryGetValue(serverId, out var client))
        {
            return false;
        }

        // Connected 只是上一次 I/O 的残留状态，对端进程被杀它照样是 true，必须再叠一层时间窗口
        return client.Connected && DateTime.UtcNow - client.LastOkAt <= OnlineFreshness;
    }

    /// <summary>配置变过（RCON 地址 / 密码）就抬掉旧连接，下次发送时按新配置重连。</summary>
    /// <param name="serverId">服务器编号。</param>
    public static void Reset(int serverId)
    {
        ClearCooldown(serverId);
        _signatures.TryRemove(serverId, out _);

        if (_clients.TryRemove(serverId, out var client))
        {
            _ = DisposeQuietlyAsync(serverId, client);   //同步方法里不能等,关连接交给后台
        }
    }

    /// <summary>拿一条可用连接：已有且配置没变就直接用，否则按当前配置重连。</summary>
    private static async Task<(RconClient? Client, string Error)> EnsureConnectedAsync(ZxmmEntry entry, CancellationToken ct)
    {
        var signature = $"{entry.RconAddress}|{entry.RconPassword}";
        if (_clients.TryGetValue(entry.Id, out var existing) && existing.Connected &&
            _signatures.TryGetValue(entry.Id, out var opened) && opened == signature)
        {
            return (existing, "");
        }

        await DropAsync(entry.Id).ConfigureAwait(false);   //旧连接要么死了要么配置变了,不能留着占套接字

        var client = new RconClient(ResolveHost(entry), entry.RconPort, entry.RconPassword);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);

            _clients[entry.Id] = client;
            _signatures[entry.Id] = signature;
            ClearCooldown(entry.Id);
            return (client, "");
        }
        catch (RconAuthException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            // 密码不对和"连不上"是两件事:运维要照着原因去改 rcon.password,别混在超时里
            RecordFailure(entry, "RCON 密码不对");
            Logger.Log(LogLevel.Warning, $"[RCON] {entry.Id} {entry.Name} 认证失败 → {entry.RconAddress} RCON 密码不对，已丢弃连接");
            return (null, "RCON 密码不对");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            RecordFailure(entry, "连接超时");
            Logger.Log(LogLevel.Warning, $"[RCON] {entry.Id} {entry.Name} 连不上 → {entry.RconAddress} {ConnectTimeout.TotalSeconds:0} 秒内没连上");
            return (null, $"RCON 连不上 {entry.RconAddress}:连接超时");
        }
        catch (Exception ex)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            RecordFailure(entry, ex.Message);
            Logger.Log(LogLevel.Warning, $"[RCON] {entry.Id} {entry.Name} 连不上 → {entry.RconAddress} {ex.Message}");
            return (null, $"RCON 连不上 {entry.RconAddress}:{ex.Message}");
        }

        // 配置里 RCON 主机留空 = 与 ZXMM 同一台机器
        static string ResolveHost(ZxmmEntry item)
            => string.IsNullOrWhiteSpace(item.RconHost) ? item.Host : item.RconHost.Trim();
    }

    /// <summary>静默窗口内直接回上一次的失败原因，不再去连。</summary>
    private static bool IsCoolingDown(ZxmmEntry entry, out string message)
    {
        message = "";
        if (!_lastFailureAt.TryGetValue(entry.Id, out var at) || DateTime.UtcNow - at >= RetryCooldown)
        {
            return false;
        }

        var reason = _lastFailureReason.TryGetValue(entry.Id, out var text) ? text : "刚失败过";
        // 明确写出来:运维看日志要能分清"连了但失败"和"压根没去连"
        message = $"RCON 连不上 {entry.RconAddress}:{reason}(刚失败过,静默窗口内没重试)";
        return true;
    }

    private static void RecordFailure(ZxmmEntry entry, string reason)
    {
        _lastFailureAt[entry.Id] = DateTime.UtcNow;
        _lastFailureReason[entry.Id] = reason;
    }

    private static void ClearCooldown(int serverId)
    {
        _lastFailureAt.TryRemove(serverId, out _);
        _lastFailureReason.TryRemove(serverId, out _);
    }

    private static async Task DropAsync(int serverId)
    {
        _signatures.TryRemove(serverId, out _);
        if (_clients.TryRemove(serverId, out var client))
        {
            await DisposeQuietlyAsync(serverId, client).ConfigureAwait(false);
        }
    }

    private static async Task DisposeQuietlyAsync(int serverId, RconClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 连接早就断了,关不掉不算问题,留个 Debug 免得以后找不到线索
            Logger.Log(LogLevel.Debug, $"[RCON] 关闭 {serverId} 的连接失败 → {ex.Message}");
        }
    }
}
