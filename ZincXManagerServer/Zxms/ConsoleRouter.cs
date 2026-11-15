using System;                              // Func / Exception
using System.Globalization;                // CultureInfo
using System.Threading;                    // CancellationToken
using System.Threading.Tasks;              // Task
using ZincXManagerShared.Logging;          // CommandLog / Logger / LogLevel

namespace ZincXManagerServer.Zxms;

/// <summary>
/// ZXMS 内部的控制台入口：所有"把一条命令送到某台服"的调用都从这里走。
///
/// <para>它按该服的控制台模式选路（0 = ZXMM 桥接，1 = 直连 RCON），并统一记命令日志；
/// 以后别的地方要发命令，也只调这里，不要再自己判断走哪条路。</para>
/// </summary>
public static class ConsoleRouter
{
    /// <summary>写 RCON 三要素的目标文件。</summary>
    private const string ConfigFile = "server.properties";

    private static Func<int, ZxmmEntry?>? _entryOf;   //接线方给的"按编号取连接项"
    private static Func<int, Zxmm.ZxmmApi?>? _zxmmOf; //接线方给的"按编号取 ZXMM 连接"

    /// <summary>
    /// 接线：由宿主给出两个查询函数，其它模块都从本类发命令。
    /// </summary>
    /// <param name="entryOf">按服务器编号取连接项，没有这台服时返回 null。</param>
    /// <param name="zxmmOf">按服务器编号取 ZXMM 长连接，该服没连 ZXMM 时返回 null。</param>
    public static void Attach(Func<int, ZxmmEntry?> entryOf, Func<int, Zxmm.ZxmmApi?> zxmmOf)
    {
        _entryOf = entryOf;
        _zxmmOf = zxmmOf;
    }

    /// <summary>
    /// 发一条命令给指定服：按该服的控制台模式选路，无论成败都写命令日志。
    /// </summary>
    /// <param name="serverId">目标服务器编号。</param>
    /// <param name="command">要执行的 MC 命令，不带前导斜杠。</param>
    /// <param name="operatorName">操作人名称（记进命令日志）。</param>
    /// <param name="accountId">操作人账号编号，0 表示没有账号（运维控制台）。</param>
    /// <param name="ct">调用方的取消令牌。</param>
    public static async Task<(bool Ok, string Result)> SendAsync(
        int serverId, string command, string operatorName, int accountId, CancellationToken ct = default)
    {
        var entry = _entryOf?.Invoke(serverId);
        if (entry is null)
        {
            CommandLog.Write(operatorName, accountId, serverId, "", command, false, "没有这个服务器");
            return (false, "没有这个服务器");
        }

        var (ok, result) = await DispatchAsync(entry, command, ct).ConfigureAwait(false);
        CommandLog.Write(operatorName, accountId, serverId, entry.Name, command, ok, result);
        return (ok, result);
    }

    /// <summary>桥接模式：这台服的 ZXMM 通道通不通。</summary>
    /// <param name="serverId">服务器编号。</param>
    public static bool IsBridgeOnline(int serverId)
        => _zxmmOf?.Invoke(serverId) is { Connected: true };

    /// <summary>
    /// 把 RCON 三要素写进该服 MC 的 server.properties（经 ZXMM 的 config 方法），改完要重启 MC 服才生效。
    /// </summary>
    /// <param name="serverId">目标服务器编号。</param>
    /// <param name="host">ZXMS 之后用来连这台服 RCON 的主机（只进日志，server.properties 里没有这一项）。</param>
    /// <param name="port">RCON 端口，写进 rcon.port。</param>
    /// <param name="password">RCON 密码，写进 rcon.password；日志里只说「已设置」。</param>
    /// <param name="ct">调用方的取消令牌。</param>
    public static async Task<(bool Ok, string Result)> WriteRconConfigAsync(
        int serverId, string host, int port, string password, CancellationToken ct = default)
    {
        var entry = _entryOf?.Invoke(serverId);
        if (entry is null)
        {
            return (false, "没有这个服务器");
        }

        var api = _zxmmOf?.Invoke(serverId);
        if (api is null || !api.Connected)
        {
            return (false, "ZXMM 通道没连上");
        }

        // 先看一眼现状再覆盖:本来就已经配好的服被改写,日志里得留下"改之前是什么样"
        var (configured, detail) = await ProbeRconConfigAsync(serverId, ct).ConfigureAwait(false);
        Logger.Log(LogLevel.Info,
            $"[控制台] 写 {entry.Id} {entry.Name} 的 {ConfigFile} 前,RCON 现状:{(configured ? "已配" : "未配")} {detail}");

        var (ok, reason) = await SetAsync(api, "enable-rcon", "true", ct).ConfigureAwait(false);
        if (!ok)
        {
            return Fail(entry, "enable-rcon", reason);
        }

        (ok, reason) = await SetAsync(api, "rcon.port", port.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        if (!ok)
        {
            return Fail(entry, "rcon.port", reason);
        }

        (ok, reason) = await SetAsync(api, "rcon.password", password, ct).ConfigureAwait(false);
        if (!ok)
        {
            return Fail(entry, "rcon.password", reason);
        }

        Logger.Log(LogLevel.Info,
            $"[控制台] 已写入 {entry.Id} {entry.Name} 的 {ConfigFile}:enable-rcon=true rcon.port={port} rcon.password=已设置" +
            $"(ZXMS 之后按 {host}:{port} 连,重启 MC 服生效)");

        return (true, $"已写入 {entry.Name} 的 server.properties(enable-rcon=true rcon.port={port}) —— 重启 MC 服才生效");
    }

    /// <summary>
    /// 该服的 RCON 三要素在 server.properties 里是不是还是默认值（enable-rcon 不存在/为 false、rcon.password 空）。
    ///
    /// <para>ZXMM 目前只提供 config 的写（顺带回改之前的值），没有读接口，所以现在读不到；
    /// 按"读不到就当没配"处理：<c>Configured=false</c>，原因写在 Detail 里。</para>
    /// </summary>
    /// <param name="serverId">目标服务器编号。</param>
    /// <param name="ct">调用方的取消令牌（探测暂无 IO，保留参数是为了将来接上读取）。</param>
    public static Task<(bool Configured, string Detail)> ProbeRconConfigAsync(int serverId, CancellationToken ct = default)
    {
        var entry = _entryOf?.Invoke(serverId);
        if (entry is null)
        {
            return Task.FromResult<(bool, string)>((false, "没有这个服务器"));
        }

        return Task.FromResult<(bool, string)>((false, "无法读取,按未配置处理"));
    }

    /// <summary>按控制台模式把命令分到 RCON 或 ZXMM 桥接，不记日志（记日志在调用方统一做）。</summary>
    private static async Task<(bool Ok, string Result)> DispatchAsync(ZxmmEntry entry, string command, CancellationToken ct)
    {
        if (entry.ConsoleMode == ZxmmRegistry.ConsoleModeRcon)
        {
            return await RconHub.SendAsync(entry, command, ct).ConfigureAwait(false);
        }

        var api = _zxmmOf?.Invoke(entry.Id);
        if (api is null || !api.Connected)
        {
            return (false, "ZXMM 通道没连上");
        }

        // CallAsync 已经把 res|err 变成 Ok=false、把原因放在 Result 里,这里原样透传
        return await api.ConsoleAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>写一个 server.properties 键；成功时 ZXMM 回的是改之前的值，所以只留失败原因。</summary>
    private static async Task<(bool Ok, string Reason)> SetAsync(
        Zxmm.ZxmmApi api, string key, string value, CancellationToken ct)
    {
        var (ok, result) = await api.ConfigSetAsync(ConfigFile, key, value, ct).ConfigureAwait(false);
        return (ok, ok ? "" : result);
    }

    private static (bool Ok, string Result) Fail(ZxmmEntry entry, string key, string reason)
    {
        Logger.Log(LogLevel.Warning, $"[控制台] 写 {entry.Id} {entry.Name} 的 {ConfigFile} 失败 → 键 {key}:{reason}");
        return (false, $"写 {key} 失败:{reason}");
    }
}
