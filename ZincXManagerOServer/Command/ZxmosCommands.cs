using System;
using ZincXManagerShared.Account;
using ZincXManagerShared.Command;
using ZincXManagerShared.Logging;
using ZincXManagerShared.Network;

namespace ZincXManagerOServer.Command;

/// <summary>
/// <c>srv ban</c> / <c>srv unban</c> / <c>srv blacklist</c> 的落地动作。
///
/// <para>这三条要动账号表、封禁表和连接表,所以实现在 <c>Program</c> 里;
/// 命令层只负责命令行的形状与帮助文案,通过这个钩子转过去。</para>
/// </summary>
public sealed class SrvBlacklistHooks
{
    /// <summary>拉黑一个 ZXMS:归档它名下账号 + 按"账号名 + 它的 IP"封 + 断开在线连接;返回归档的账号数(没有该账号返回 -1)。</summary>
    public required Func<string, TimeSpan?, string, int> Ban { get; init; }

    /// <summary>解封一个 ZXMS,并从归档恢复它名下账号;返回恢复的账号数。</summary>
    public required Func<string, int> Unban { get; init; }

    /// <summary>列出已归档(被拉黑)的 ZXMS,每项一行文案。</summary>
    public required Func<IReadOnlyList<string>> List { get; init; }
}

/// <summary>
/// ZXMOS 专属控制台命令 —— 和 ZXMS 那套对称,只在本端启动时挂上去。
///
/// <para><b>账号体系(和 ZXMS 不同):</b>等级是 Player / Admin / System。
/// Player = 玩家账号(以后玩家直连官方服务端用),Admin / System = 子服务端(ZXMS)接入账号。</para>
///
/// <para><b>挂载方式(必须在 <c>CommandHandler.Start()</c> 之前):</b></para>
/// <code>
/// var cmd = new CommandHandler();
///
/// cmd.RegisterZxmosCommands(
///     listenProvider:       () => "0.0.0.0:" + port,
///     registryProvider:     () => _server?.Registry,
///     accountProvider:      () => Users,
///     serverAccountProvider:() => Servers,
///     banProvider:          () => Bans,
///     srvBlacklist:         new SrvBlacklistHooks { Ban = SrvBan, Unban = SrvUnban, List = SrvBlacklistList });
///
/// cmd.Start();
/// </code>
/// </summary>
public static class ZxmosCommands
{
    /// <summary>挂载 ZXMOS 专属命令。</summary>
    public static void RegisterZxmosCommands(
        this CommandHandler cmd,
        Func<string> listenProvider,
        Func<ClientRegistry?> registryProvider,
        Func<AccountStore> accountProvider,
        Func<AccountStore> serverAccountProvider,
        Func<BanStore> banProvider,
        SrvBlacklistHooks srvBlacklist)
    {
        // ---- 封禁:账号 / IP / 机器码,永久或定时 ----
        var bans = banProvider();

        cmd.Register("ban add", args =>
        {
            if (args.Length < 2)
            {
                Logger.Log(LogLevel.Warning, "[封禁] 用法:ban add <user|ip|machine> <值> [perm|30d|12h] [原因]");
                return;
            }

            if (!BanStore.TryParseDuration(args.Length > 2 ? args[2] : "perm", out var span, out var err))
            {
                Logger.Log(LogLevel.Warning, "[封禁] " + err);
                return;
            }

            var why = args.Length > 3 ? string.Join(' ', args[3..]) : "未填写";
            Logger.Log(LogLevel.Info, bans.Add(args[0], args[1], span, why, out var e)
                ? $"[封禁] 已封 {args[0]}={args[1]} 原因:{why}"
                : "[封禁] 失败:" + e);
        }, "封禁:ban add <user|ip|machine> <值> [perm|30d|12h|30m] [原因]", "ban add ip 1.2.3.4 7d 刷屏");

        cmd.Register("ban user", args => CmdBanUser(accountProvider(), banProvider(), args),
            "封账号(可连坐它的 IP / 机器码):ban user <名称或ID> [时长] [原因] [--ip] [--machine]",
            "ban user tester 30d 开挂 --ip --machine");

        cmd.Register("ban del", args =>
        {
            if (args.Length < 2)
            {
                Logger.Log(LogLevel.Warning, "[封禁] 用法:ban del <user|ip|machine> <值>");
                return;
            }

            Logger.Log(LogLevel.Info, bans.Remove(args[0], args[1])
                ? $"[封禁] 已解封 {args[0]}={args[1]}" : "[封禁] 没有这条封禁记录");
        }, "解封:ban del <user|ip|machine> <值>", "ban del ip 1.2.3.4");

        cmd.Register("ban list", _ =>
        {
            var all = bans.All;
            if (all.Count == 0)
            {
                Logger.Log(LogLevel.Info, "[封禁] 列表为空");
                return;
            }

            foreach (var b in all)
            {
                Logger.Log(LogLevel.Info, $"[封禁] {b.Kind,-8} {b.Value,-20} 剩余 {b.Remain,-12} 原因:{b.Reason}");
            }
        }, "查看封禁列表", "ban list");
        cmd.Register("net status", _ => CmdNetStatus(listenProvider(), registryProvider()),
            "查看监听情况与接入数量(按身份分组)",
            "net status");

        cmd.Register("net clients", _ => CmdClients(registryProvider()),
            "查看连接登记表(身份 / 模式 / 地址 / 在线时长 / 收包数)",
            "net clients");

        cmd.Register("user list", _ => CmdUserList(accountProvider()),
            "列出全部账号(玩家账号 + ZXMS 接入账号)",
            "user list");

        cmd.Register("user reg", args => CmdUserReg(accountProvider(), args),
            "新建账号:<名称> <密码> <等级> [IP];等级 = Player / Admin / System",
            "user reg ZXMS-1 123456 Admin");

        cmd.Register("user del", args => CmdUserDel(accountProvider(), args),
            "删除账号:<名称或ID>",
            "user del ZXMS-1");

        cmd.Register("user rep", args => CmdUserRep(accountProvider(), args),
            "改密码:<名称或ID> <新密码>",
            "user rep ZXMS-1 654321");

        cmd.Register("user rel", args => CmdUserRel(accountProvider(), args),
            "改权限等级:<名称或ID> <新等级>",
            "user rel 3 System");

        // ---- 服务端账号(哪个 ZXMS 接入本端;和用户账号是两个东西) ----
        cmd.Register("srv list", _ => CmdSrvList(serverAccountProvider()),
            "列出接入本端的 ZXMS 服务端账号(名称 / 等级 / IP)",
            "srv list");

        cmd.Register("srv reg", args => CmdSrvReg(serverAccountProvider(), args),
            "新建服务端账号:<名称> <密码> [IP](服务端账号没有等级分类)",
            "srv reg ZXMS-1 123456 127.0.0.1");

        cmd.Register("srv del", args => CmdUserDel(serverAccountProvider(), args),
            "删除服务端账号:<名称或ID>",
            "srv del ZXMS-1");

        cmd.Register("srv rep", args => CmdUserRep(serverAccountProvider(), args),
            "改服务端账号密码:<名称或ID> <新密码>",
            "srv rep ZXMS-1 654321");

        // ---- 黑名单(整台 ZXMS):名下账号先归档,再按"账号名 + 它的 IP"封,最后断开在线连接 ----
        cmd.Register("srv ban", args =>
        {
            if (args.Length < 1)
            {
                Logger.Log(LogLevel.Warning, "[黑名单] 用法:srv ban <名称或ID> [perm|30d|12h] [原因]");
                return;
            }

            // 第二段能解析成时长就算时长,否则整段当原因(和 ban 命令一致的手感)
            TimeSpan? duration = null;
            string reason;

            if (args.Length > 1 && BanStore.TryParseDuration(args[1], out var parsed, out _))
            {
                duration = parsed;
                reason = args.Length > 2 ? string.Join(' ', args[2..]) : "未填写";
            }
            else
            {
                reason = args.Length > 1 ? string.Join(' ', args[1..]) : "未填写";
            }

            var archived = srvBlacklist.Ban(args[0], duration, reason);
            if (archived < 0)
            {
                // Program 已经报过"没有这个服务端账号"
                return;
            }

            Logger.Log(LogLevel.Info,
                $"[黑名单] 已拉黑 {args[0]}(归档 {archived} 个名下账号,{(duration is null ? "永久" : "限时")},原因:{reason})");
        }, "拉黑一台 ZXMS:名下账号归档到 blacklist/zxms/<名称>/,按账号名+它的 IP 封,并断开在线连接",
            "srv ban ZXMS-1 30d 长期刷屏");

        cmd.Register("srv unban", args =>
        {
            if (args.Length < 1)
            {
                Logger.Log(LogLevel.Warning, "[黑名单] 用法:srv unban <名称或ID>");
                return;
            }

            Logger.Log(LogLevel.Info, $"[黑名单] {args[0]} 已解封(从归档恢复 {srvBlacklist.Unban(args[0])} 个账号)");
        }, "解封一台 ZXMS,并从 blacklist/zxms/<名称>/ 把名下账号读回来",
            "srv unban ZXMS-1");

        cmd.Register("srv blacklist", _ =>
        {
            var lines = srvBlacklist.List();
            if (lines.Count == 0)
            {
                Logger.Log(LogLevel.Info, "[黑名单] 还没有归档(blacklist/zxms/ 为空)");
                return;
            }

            Logger.Log(LogLevel.Info, $"[黑名单] 已归档 {lines.Count} 台 ZXMS(blacklist/zxms/):");

            foreach (var line in lines)
            {
                Logger.Log(LogLevel.Info, "[黑名单] " + line);
            }
        }, "列出已归档(被拉黑)的 ZXMS 及拉黑信息",
            "srv blacklist");

    }

    /// <summary>ban user &lt;名称或ID&gt; [perm|30d] [原因] [--ip] [--machine]</summary>
    private static void CmdBanUser(AccountStore store, BanStore bans, string[] args)
    {
        if (args.Length < 1)
        {
            Logger.Log(LogLevel.Warning, "[封禁] 用法:ban user <名称或ID> [perm|30d] [原因] [--ip] [--machine]");
            return;
        }

        var account = store.FindByKey(args[0]);
        if (account is null)
        {
            Logger.Log(LogLevel.Warning, "[封禁] 没有这个账号:" + args[0]);
            return;
        }

        var flags = args.Where(a => a.StartsWith("--")).ToArray();
        var plain = args.Where(a => !a.StartsWith("--")).ToArray();
        var durationText = plain.Length > 1 ? plain[1] : "perm";

        if (!BanStore.TryParseDuration(durationText, out var duration, out var err))
        {
            Logger.Log(LogLevel.Warning, "[封禁] " + err);
            return;
        }

        var reason = plain.Length > 2 ? string.Join(' ', plain[2..]) : "未填写";
        bans.Add("user", account.Name, duration, reason, out _);
        Logger.Log(LogLevel.Info, $"[封禁] 已封账号 {account.Name}");

        if (flags.Contains("--ip") && account.LastIp.Length > 0)
        {
            bans.Add("ip", account.LastIp, duration, reason, out _);
            Logger.Log(LogLevel.Info, $"[封禁] 连坐 IP {account.LastIp}");
        }

        if (flags.Contains("--machine") && account.Machine.Length > 0)
        {
            bans.Add("machine", account.Machine, duration, reason, out _);
            Logger.Log(LogLevel.Info, $"[封禁] 连坐机器码 {account.Machine}");
        }
    }

    /// <summary>srv list:接入本端的 ZXMS 服务端账号</summary>
    private static void CmdSrvList(AccountStore store)
    {
        var all = store.All;
        if (all.Count == 0)
        {
            Logger.Log(LogLevel.Info, $"[服务端账号] 还没有({store.Path});ZXMS 首次接入时会自动注册,也可以 srv reg 手建");
            return;
        }

        foreach (var a in all)
        {
            Logger.Log(LogLevel.Info, $"[服务端账号] ID={a.Id,-3} {a.Name,-16} IP={(a.Ip.Length > 0 ? a.Ip : "-")}");
        }

        Logger.Log(LogLevel.Info, $"[服务端账号] 共 {all.Count} 个({store.Path})");
    }

    /// <summary>srv reg &lt;名称&gt; &lt;密码&gt; &lt;等级&gt; [IP]</summary>
    private static void CmdSrvReg(AccountStore store, string[] args)
    {
        if (args.Length < 2)
        {
            Logger.Log(LogLevel.Warning, "[服务端账号] 用法:srv reg <名称> <密码> [IP]");
            return;
        }

        var ip = args.Length > 2 ? args[2] : "";
        if (store.Add(args[0], PasswordHash.Hash(args[0], args[1]), Protocol.ServerAccountLevel, ip, out var account, out var error))
        {
            Logger.Log(LogLevel.Info, $"[服务端账号] 已新建 {account!.Name}({account.Level}) ID={account.Id}");
        }
        else
        {
            Logger.Log(LogLevel.Warning, "[服务端账号] 新建失败:" + error);
        }
    }

    /// <summary>net status:监听地址 + 按身份统计的接入数量</summary>
    private static void CmdNetStatus(string listen, ClientRegistry? registry)
    {
        var zxms = registry?.CountOf("ZXMS") ?? 0;
        var zxmc = registry?.CountOf("ZXMC") ?? 0;
        var unknown = (registry?.Count ?? 0) - zxms - zxmc;

        Logger.Log(LogLevel.Info,
            $"[网络] 监听    {listen}\n" +
            $"[网络] ZXMS    {zxms} 个(子服务端)\n" +
            $"[网络] ZXMC    {zxmc} 个(客户端)\n" +
            $"[网络] 未识别  {unknown} 个(还没握手)");
    }

    /// <summary>net clients:打印连接登记表(表格格式由共享库统一生成)</summary>
    private static void CmdClients(ClientRegistry? registry)
    {
        if (registry is null || registry.Count == 0)
        {
            Logger.Log(LogLevel.Info, "[网络] 当前没有客户端接入");
            return;
        }

        Logger.Log(LogLevel.Info, registry.ToTable("[网络] "));
    }

    /// <summary>user list:账号一览</summary>
    private static void CmdUserList(AccountStore store)
    {
        var all = store.All;
        if (all.Count == 0)
        {
            Logger.Log(LogLevel.Info, $"[账号] 还没有账号({store.Path});用 user reg 新建");
            return;
        }

        Logger.Log(LogLevel.Info, $"[账号] 共 {all.Count} 个({store.Path},等级:{store.LevelList})");

        foreach (var account in all)
        {
            var kind = account.Level == store.RegisterLevel ? "玩家" : "ZXMS";
            var ip = account.Ip.Length > 0 ? account.Ip : "-";
            Logger.Log(LogLevel.Info, $"[账号] ID={account.Id,-3} {account.Name,-16} {account.Level,-7} {kind,-5} IP={ip}");
        }
    }

    /// <summary>user reg &lt;名称&gt; &lt;密码&gt; &lt;等级&gt; [IP]</summary>
    private static void CmdUserReg(AccountStore store, string[] args)
    {
        if (args.Length < 3)
        {
            Logger.Log(LogLevel.Warning, "[账号] 用法:user reg <名称> <密码> <等级> [IP];等级 = " + store.LevelList);
            return;
        }

        var ip = args.Length > 3 ? args[3] : "";

        if (store.Add(args[0], PasswordHash.Hash(args[0], args[1]), args[2], ip, out var account, out var error))
        {
            Logger.Log(LogLevel.Info, $"[账号] 已新建 {account!.Name}({account.Level}) ID={account.Id}");
        }
        else
        {
            Logger.Log(LogLevel.Warning, "[账号] 新建失败:" + error);
        }
    }

    /// <summary>user del &lt;名称或ID&gt;</summary>
    private static void CmdUserDel(AccountStore store, string[] args)
    {
        if (args.Length < 1)
        {
            Logger.Log(LogLevel.Warning, "[账号] 用法:user del <名称或ID>");
            return;
        }

        Logger.Log(LogLevel.Info, store.Remove(args[0]) ? "[账号] 已删除 " + args[0] : "[账号] 没有这个账号:" + args[0]);
    }

    /// <summary>user rep &lt;名称或ID&gt; &lt;新密码&gt;</summary>
    private static void CmdUserRep(AccountStore store, string[] args)
    {
        if (args.Length < 2)
        {
            Logger.Log(LogLevel.Warning, "[账号] 用法:user rep <名称或ID> <新密码>");
            return;
        }

        var target = store.FindByKey(args[0]);
        if (target is null)
        {
            Logger.Log(LogLevel.Warning, "[账号] 没有这个账号:" + args[0]);
            return;
        }

        Logger.Log(LogLevel.Info, store.SetPassword(args[0], PasswordHash.Hash(target.Name, args[1]), out var error)
            ? $"[账号] {target.Name} 的密码已更新"
            : "[账号] 改密码失败:" + error);
    }

    /// <summary>user rel &lt;名称或ID&gt; &lt;新等级&gt;</summary>
    private static void CmdUserRel(AccountStore store, string[] args)
    {
        if (args.Length < 2)
        {
            Logger.Log(LogLevel.Warning, "[账号] 用法:user rel <名称或ID> <新等级>;等级 = " + store.LevelList);
            return;
        }

        Logger.Log(LogLevel.Info, store.SetLevel(args[0], args[1], out var error)
            ? $"[账号] {args[0]} 的权限等级已改为 {args[1]}"
            : "[账号] 改等级失败:" + error);
    }
}
