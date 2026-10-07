using System;
using System.Threading.Tasks;
using ZincXManagerShared.Account;
using System.Linq;
using ZincXManagerShared.Account;
using ZincXManagerShared.Command;
using ZincXManagerShared.Logging;
using ZincXManagerShared.Network;

// 与 BCL 的 TcpClient 同名,起个别名
using TcpClient = ZincXManagerShared.Network.TcpClient;

namespace ZincXManagerServer.Command;

/// <summary>
/// ZXMS 专属控制台命令 —— 共享类库里没有这些,只在本端启动时挂上去。
///
/// <para><b>挂载方式(必须在 <c>CommandHandler.Start()</c> 之前):</b></para>
/// <code>
/// var cmd = new CommandHandler();
///
/// cmd.RegisterZxmsCommands(
///     osProvider:          () => _os,
///     osTargetProvider:    () => config.MapGet("ozip") + ":" + config.MapGet("ozport"),
///     clientCountProvider: () => _server?.Connections.Count ?? 0,
///     registryProvider:    () => _server?.Registry,
///     accountProvider:     () => Accounts,
///     osReconnect:         () => ConnectOServerAsync(config));
///
/// cmd.Start();
/// </code>
/// </summary>
public static class ZxmsCommands
{
    /// <summary>本端服务端账号名(用户账号不能和它重名);由挂载时注入。</summary>
    private static string _serverAccountName = "";
    /// <summary>挂载 ZXMS 专属命令。</summary>
    public static void RegisterZxmsCommands(
        this CommandHandler cmd,
        Func<TcpClient?> osProvider,
        Func<string> osTargetProvider,
        Func<string> osNameProvider,
        Func<int> clientCountProvider,
        Func<ClientRegistry?> registryProvider,
        Func<AccountStore> accountProvider,
        Func<Task> osReconnect,
        Func<BanStore> banProvider,
        Func<string> serverAccountNameProvider,
        Action<string, string> serverAccountRegister)
    {
        cmd.Register("net status", _ => CmdNetStatus(osProvider(), osTargetProvider(), osNameProvider(), clientCountProvider()),
            "查看 ZXMOS 连接与客户端接入情况",
            "net status");

        cmd.Register("net clients", _ => CmdClients(registryProvider()),
            "查看连接登记表(身份 / 账号 / 等级 / 地址 / 在线 / 收包)",
            "net clients");

        cmd.Register("net reconnect", args =>
            {
                // 故意不 await:重连可能要重试好几秒,不能把命令线程卡住
                // 注意:lambda 参数不能命名成 _ ,否则这里的 _ 会被当成参数而不是丢弃符
                _ = ReconnectAsync(osReconnect);
            },
            "断开并重新连接官方服务端",
            "net reconnect");

        // ---- 账号管理(和 ZXMOS 同结构:user 是命令类型) ----
        cmd.Register("user list", _ => CmdUserList(accountProvider()),
            "列出所有账号(名称 / 等级 / ID)",
            "user list");

        cmd.Register("user reg", args => CmdUserReg(accountProvider(), args),
            "新建账号:<名称> <密码> <等级>",
            "user reg <名称> <密码> <等级>   等级 = Player / LAdmin / HAdmin / System");

        cmd.Register("user del", args => CmdUserDel(accountProvider(), args),
            "删除账号:<名称或ID>",
            "user del <名称或ID>");

        cmd.Register("user rep", args => CmdUserRep(accountProvider(), args),
            "改密码:<名称或ID> <新密码>",
            "user rep <名称或ID> <新密码>");

        cmd.Register("user rel", args => CmdUserRel(accountProvider(), args),
            "改权限等级:<名称或ID> <新等级>",
            "user rel <名称或ID> <新等级>");

        RegisterBanCommands(cmd, accountProvider, banProvider);

        // ---- 本端的服务端账号(用于接入官方服务端;和用户账号是两个东西) ----
        cmd.Register("srv show", _ => CmdSrvShow(serverAccountNameProvider()),
            "查看本端接入官方服务端用的账号名",
            "srv show");

        cmd.Register("srv reg", args => CmdSrvReg(serverAccountRegister, args),
            "在本端注册/修改服务端账号(名称 密码):会写进 config.ini,并立刻向官方服务端注册",
            "srv reg ZXMS-1 123456");
    }

    /// <summary>ban add &lt;user|ip|machine&gt; &lt;值&gt; [perm|30d|12h] [原因]</summary>
    private static void CmdBanAdd(BanStore bans, string[] args)
    {
        if (args.Length < 2)
        {
            Logger.Log(LogLevel.Warning, "[封禁] 用法:ban add <user|ip|machine> <值> [perm|30d|12h] [原因]");
            return;
        }

        if (!BanStore.TryParseDuration(args.Length > 2 ? args[2] : "perm", out var duration, out var err))
        {
            Logger.Log(LogLevel.Warning, "[封禁] " + err);
            return;
        }

        var reason = args.Length > 3 ? string.Join(' ', args[3..]) : "未填写";
        Logger.Log(LogLevel.Info, bans.Add(args[0], args[1], duration, reason, out var error)
            ? $"[封禁] 已封 {args[0]}={args[1]}({(duration is null ? "永久" : "限时")})"
            : "[封禁] 失败:" + error);
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

    /// <summary>srv show:本端服务端账号</summary>
    private static void CmdSrvShow(string name)
        => Logger.Log(LogLevel.Info, name.Length > 0
            ? $"[服务端账号] 本端接入官方服务端用的账号:{name}"
            : "[服务端账号] 还没注册;用 srv reg <名称> <密码> 在本端注册(连上官方服务端时会自动同步过去)");

    /// <summary>srv reg &lt;名称&gt; &lt;密码&gt;</summary>
    private static void CmdSrvReg(Action<string, string> register, string[] args)
    {
        if (args.Length < 2)
        {
            Logger.Log(LogLevel.Warning, "[服务端账号] 用法:srv reg <名称> <密码>");
            return;
        }

        register(args[0], args[1]);
    }

    /// <summary>ban del &lt;类型&gt; &lt;值&gt; / ban list / ban add / ban user</summary>
    private static void RegisterBanCommands(CommandHandler cmd, Func<AccountStore> accountProvider, Func<BanStore> banProvider)
    {
        cmd.Register("ban add", args => CmdBanAdd(banProvider(), args),
            "封禁:ban add <user|ip|machine> <值> [perm|30d|12h|30m] [原因]",
            "ban add ip 1.2.3.4 7d 刷屏");

        cmd.Register("ban user", args => CmdBanUser(accountProvider(), banProvider(), args),
            "封账号(可连坐它的 IP / 机器码):ban user <名称或ID> [时长] [原因] [--ip] [--machine]",
            "ban user tester 30d 开挂 --ip --machine");

        cmd.Register("ban del", args => CmdBanDel(banProvider(), args),
            "解封:ban del <user|ip|machine> <值>",
            "ban del ip 1.2.3.4");

        cmd.Register("ban list", _ => CmdBanList(banProvider()),
            "查看封禁列表",
            "ban list");
    }

    private static void CmdBanDel(BanStore bans, string[] args)
    {
        if (args.Length < 2)
        {
            Logger.Log(LogLevel.Warning, "[封禁] 用法:ban del <user|ip|machine> <值>");
            return;
        }

        Logger.Log(LogLevel.Info, bans.Remove(args[0], args[1])
            ? $"[封禁] 已解封 {args[0]}={args[1]}"
            : "[封禁] 没有这条封禁记录");
    }

    private static void CmdBanList(BanStore bans)
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
    }

    /// <summary>net status:打印当前网络状态(连接状态、目标地址与名称、接入的客户端数)</summary>
    private static void CmdNetStatus(TcpClient? os, string target, string osName, int clientCount)
    {
        var osState = os is null
            ? "未连接"
            : os.IsConnected ? "已连接" : "已断开(对象还在,可 net reconnect)";

        // 名称是官方服务端在连接时下发的(201),没收到就只显示地址
        var name = osName.Length > 0 ? $"(名称:{osName})" : "(名称:未收到)";

        Logger.Log(LogLevel.Info,
            $"[网络] ZXMOS  {osState}\n" +
            $"[网络] 目标    {target} {name}\n" +
            $"[网络] 已接入客户端 {clientCount} 个");
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

    /// <summary>user list</summary>
    private static void CmdUserList(AccountStore store)
    {
        var all = store.All;
        if (all.Count == 0)
        {
            Logger.Log(LogLevel.Info, $"[账号] 还没有账号({store.Path});用 account add 新建");
            return;
        }

        foreach (var a in all)
        {
            var ip = a.Ip.Length > 0 ? "  IP=" + a.Ip : "";
            Logger.Log(LogLevel.Info, $"[账号] ID={a.Id,-3} {a.Name,-14} {a.Level,-7}{ip}");
        }

        Logger.Log(LogLevel.Info, $"[账号] 共 {all.Count} 个({store.Path},等级:{store.LevelList})");
    }

    /// <summary>user reg &lt;名称&gt; &lt;密码&gt; &lt;等级&gt;</summary>
    private static void CmdUserReg(AccountStore store, string[] args)
    {
        if (args.Length < 3)
        {
            Logger.Log(LogLevel.Warning, "[账号] 用法:user reg <名称> <密码> <等级>;等级 = " + store.LevelList);
            return;
        }

        // 用户账号不能和服务端账号重名(任何类型、任何账号都不能重名)
        if (string.Equals(args[0], _serverAccountName, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Log(LogLevel.Warning, "[账号] 这个名字是本端的服务端账号,不能再当用户账号");
            return;
        }

        // 控制台输入的是明文,这里先算成密文再存(文件里不落明文)
        if (store.Add(args[0], PasswordHash.Hash(args[0], args[1]), args[2], out var account, out var error))
        {
            Logger.Log(LogLevel.Info, $"[账号] 已创建 {account!.Name}({account.Level}) ID={account.Id}");
            return;
        }

        Logger.Log(LogLevel.Warning, "[账号] 创建失败:" + error);
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

    /// <summary>后台执行重连,异常在这里兜住(否则 async void / 丢弃的任务会静默失败)</summary>
    private static async Task ReconnectAsync(Func<Task> osReconnect)
    {
        try
        {
            Logger.Log(LogLevel.Info, "[网络] 正在重连 ZXMOS…");
            await osReconnect();
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[网络] 重连出错: " + ex.Message);
        }
    }
}
