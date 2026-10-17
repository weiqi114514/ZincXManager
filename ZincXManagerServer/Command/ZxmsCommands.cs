using System;
using System.Threading.Tasks;
using ZincXManagerShared.Account;
using System.Linq;
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
/// <summary>本端身份(命令层只通过它读/写这几个值,不直接碰 config)。</summary>
public sealed class ZxmsIdentity
{
    /// <summary>本端显示名(config 的 name,会下发给客户端)。</summary>
    public required Func<string> Name { get; init; }

    /// <summary>接入官方服务端用的账号名(config 的 osuser)。</summary>
    public required Func<string> User { get; init; }

    /// <summary>本实例的服务端码(32 位十六进制)。</summary>
    public required Func<string> ServerCode { get; init; }

    /// <summary>改显示名(写 config)。</summary>
    public required Action<string> SetName { get; init; }
}

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
        ZxmsIdentity identity,
        Func<int> clientCountProvider,
        Func<ClientRegistry?> registryProvider,
        Func<AccountStore> accountProvider,
        Func<Task> osReconnect,
        Func<string, string, Task<string>> osRename,
        Func<int, int> setMsgDays,
        Func<int, int> setSrvMsgDays,
        Func<CardInput, int> localNotice,
        Func<string, CardInput, int> localPush,
        Func<BanStore> banProvider,
        Func<string> serverAccountNameProvider)
    {
        // 记住本端的服务端账号名:用户账号不能和它重名(和 config 里的 osuser 同步)
        _serverAccountName = serverAccountNameProvider();

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
        cmd.Register("info", _ => CmdInfo(identity, osProvider(), osTargetProvider()),
            "查看本端身份:名称 / 账号 / 服务端码 / 机器码 / 连接状态",
            "info");

        cmd.Register("name", args => CmdName(identity, args),
            "改本端显示名(会下发给客户端):name <新名称>",
            "name 锌能源子服");

        cmd.Register("osuser", args => CmdOsUser(identity, osProvider(), osRename, args),
            "改本端在官方服务端的账号名与密码(需已连上官方服务端;名字写成一样的就只改密码)",
            "osuser <新名> <新密码>");

        cmd.Register("msgdays", args =>
        {
            if (args.Length < 1 || !int.TryParse(args[0].Trim(), out var days))
            {
                Logger.Log(LogLevel.Warning, "[消息] 用法:msgdays <天数 1-365>(下发给客户端,决定它们本地留多久)");
                return;
            }

            var sent = setMsgDays(days);
            Logger.Log(LogLevel.Info, $"[消息] 本服消息保留天数已设为 {Math.Clamp(days, 1, 365)} 天,已通知 {sent} 个在线客户端");
        }, "设置本服消息保留天数(下发给客户端):msgdays <天数>", "msgdays 14");

        cmd.Register("srvmsgdays", args =>
        {
            if (args.Length < 1 || !int.TryParse(args[0].Trim(), out var days))
            {
                Logger.Log(LogLevel.Warning,
                    "[消息] 用法:srvmsgdays <天数 1-365>(本服自己存档的保留天数,补发历史消息用)");
                return;
            }

            var left = setSrvMsgDays(days);
            Logger.Log(LogLevel.Info,
                $"[消息] 本服消息存档保留天数已设为 {Math.Clamp(days, 1, 365)} 天,现存 {left} 条");
        }, "设置本服自己存档的保留天数(影响补发的历史消息):srvmsgdays <天数>", "srvmsgdays 14");

        cmd.Register("notice", args => CmdNotice(localNotice, args),
            "给本服所有客户端发公告:notice <标题>|<正文>[|种类|主动作|次动作|选项];种类 = text / update / choice / input / select",
            "notice 是否同意|新协议要点见附件|choice|是|否");

        cmd.Register("push", args => CmdPush(localPush, args),
            "给本服某个玩家定向推送(只发本服):push <账号> <标题>|<正文>[|种类|主动作|次动作|选项]",
            "push 猫娘 版本选择|请选一个版本|select|||1.21.1,1.21,1.20.6");

        cmd.Register("srv show", _ => CmdSrvShow(serverAccountNameProvider()),
            "查看本端接入官方服务端用的账号名",
            "srv show");
    }

    /// <summary>控制台命令的首词 —— 用来判断"这行输入其实是命令,不是账号名/密码"。</summary>
    private static readonly string[] CommandWords =
    {
        "help", "echo", "exit", "info", "infopc", "msgdays", "srvmsgdays", "name",
        "notice", "push", "osuser", "srv", "user", "ban", "file", "net", "config", "exitc",
    };

    /// <summary>这行输入看起来像控制台命令吗(像的话就不该被当成账号名 / 密码收下)。</summary>
    internal static bool LooksLikeConsoleCommand(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var first = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        return Array.IndexOf(CommandWords, first) >= 0;
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
    {
        if (name.Length == 0)
        {
            Logger.Log(LogLevel.Info,
                "[服务端账号] 还没注册;连上官方服务端时会自动注册,之后可以用 osuser <新名> <新密码> 改名或改密码");
            return;
        }

        Logger.Log(LogLevel.Info, $"[服务端账号] 本端接入官方服务端用的账号:{name}");

        // 以前有人在"账号名"提问里输了命令,结果账号就叫 srv show —— 这里顺手提示怎么改
        if (LooksLikeConsoleCommand(name) || name.Contains(' '))
        {
            Logger.Log(LogLevel.Warning,
                "[服务端账号] 这个名字像是误输入(像命令 / 带空格);要改:osuser <新名> <新密码>(会先问官方端同意)");
        }
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

        LogBlock(
            $"[网络] ZXMOS  {osState}\n" +
            $"[网络] 目标    {target} {name}\n" +
            $"[网络] 已接入客户端 {clientCount} 个");
    }

    /// <summary>
    /// 多行信息输出:先空一行再打印,免得第一行贴着命令行/提示符导致"错位"。
    /// </summary>
    private static void LogBlock(string text) => Logger.Log(LogLevel.Info, Environment.NewLine + text);

    /// <summary>net clients:打印连接登记表(表格格式由共享库统一生成)</summary>
    private static void CmdClients(ClientRegistry? registry)
    {
        if (registry is null || registry.Count == 0)
        {
            Logger.Log(LogLevel.Info, "[网络] 当前没有客户端接入");
            return;
        }

        LogBlock(registry.ToTable("[网络] "));
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
    /// <summary>notice &lt;标题&gt;|&lt;正文&gt;[|种类|主动作|次动作|选项]:给本服所有客户端发公告。</summary>
    private static void CmdNotice(Func<CardInput, int> notice, string[] args)
    {
        if (!TryReadCard(args, NoticeUsage, out var card))
        {
            return;
        }

        var sent = notice(card);
        Logger.Log(LogLevel.Info, sent > 0
            ? $"[消息] 公告({card.Kind})已发出,本服 {sent} 个客户端收到"
            : $"[消息] 公告({card.Kind})已发出,但本服现在没有客户端在线");
    }

    /// <summary>push &lt;账号&gt; &lt;标题&gt;|&lt;正文&gt;[|种类|…]:只投本服。</summary>
    private static void CmdPush(Func<string, CardInput, int> push, string[] args)
    {
        if (args.Length < 2)
        {
            Logger.Log(LogLevel.Warning, "[消息] 用法:" + PushUsage);
            return;
        }

        var target = args[0].Trim();
        if (!TryReadCard(args[1..], PushUsage, out var card))
        {
            return;
        }

        var sent = push(target, card);
        Logger.Log(LogLevel.Info, sent > 0
            ? $"[消息] 已推给 {target}({card.Kind} 卡片),本服 {sent} 个客户端收到"
            : $"[消息] {target} 不在本服在线,已忽略");
    }

    /// <summary>公告卡片的写法(帮助与报错都用它)。</summary>
    private const string NoticeUsage = "notice <标题>|<正文>[|种类|主动作|次动作|选项]";

    /// <summary>定向推送的写法。</summary>
    private const string PushUsage = "push <账号> <标题>|<正文>[|种类|主动作|次动作|选项]";

    /// <summary>把命令参数读成一张卡片(格式/长度/按钮文案都在共享库里校验)。</summary>
    private static bool TryReadCard(string[] args, string usage, out CardInput card)
    {
        card = new CardInput();

        if (args.Length < 1)
        {
            Logger.Log(LogLevel.Warning, "[消息] 用法:" + usage);
            return false;
        }

        if (!MessageCard.TryReadCard(string.Join(' ', args), out card, out var error))
        {
            Logger.Log(LogLevel.Warning, $"[消息] {error};用法:{usage}");
            return false;
        }

        return true;
    }

    /// <summary>osuser &lt;新名&gt; &lt;新密码&gt;:改本端在官方服务端的账号名(密码一起改)。</summary>
    private static void CmdOsUser(ZxmsIdentity identity, TcpClient? os, Func<string, string, Task<string>> rename, string[] args)
    {
        if (args.Length < 2)
        {
            Logger.Log(LogLevel.Warning, "[身份] 用法:osuser <新名> <新密码>");
            return;
        }

        var oldName = identity.User();
        if (oldName.Length == 0)
        {
            Logger.Log(LogLevel.Warning, "[身份] 本端还没有服务端账号:先 net reconnect 连上官方服务端,会自动注册");
            return;
        }

        if (os is null || !os.IsConnected)
        {
            Logger.Log(LogLevel.Warning, "[身份] 官方服务端未连接,改不了账号(先 net reconnect)");
            return;
        }

        var newName = args[0];

        // 命令线程就在这里等回执:改名一次一问,最长 5 秒
        var reply = rename(newName, args[1]).GetAwaiter().GetResult();

        if (reply.StartsWith("ok|"))
        {
            Logger.Log(LogLevel.Info, $"[身份] 服务端账号已改名:{oldName} → {newName}(密码已更新)");
            return;
        }

        Logger.Log(LogLevel.Warning, "[身份] 改名失败:" + (reply.StartsWith("err|") ? reply[4..] : reply));
    }

    /// <summary>name &lt;新名称&gt;:改本端显示名(重连后下发给客户端)。</summary>
    private static void CmdName(ZxmsIdentity identity, string[] args)
    {
        var name = string.Join(' ', args).Trim();
        if (name.Length == 0)
        {
            Logger.Log(LogLevel.Warning, "[身份] 用法:name <新名称>");
            return;
        }

        identity.SetName(name);
        Logger.Log(LogLevel.Info, $"[身份] 本端名称已改为 {name}(新接入的客户端会收到新名称)");
    }

    /// <summary>info:本端身份一览(名称 / 账号 / 服务端码 / 机器码 / 连接状态)。</summary>
    private static void CmdInfo(ZxmsIdentity identity, TcpClient? os, string target)
    {
        var state = os is null ? "未连接" : os.IsConnected ? "已连接" : "已断开(可 net reconnect)";
        LogBlock(
            $"[身份] 名称       {identity.Name()}\n" +
            $"[身份] 账号       {identity.User()}\n" +
            $"[身份] 服务端码   {identity.ServerCode()}\n" +
            $"[身份] 机器码     {ZincXManagerShared.Account.MachineCode.Value}\n" +
            $"[身份] 官方服务端 {target} ({state})");
    }
}