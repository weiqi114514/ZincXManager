using System;  // 基础类型 / 常用静态方法
using System.Threading.Tasks;  // async / await 异步
using ZincXManagerShared.Account;  // 共享库
using System.Linq;  // LINQ 查询
using ZincXManagerShared.Command;  // 共享库
using ZincXManagerShared.Logging;  // 共享库
using ZincXManagerShared.History;  // megback 查询
using ZincXManagerShared.Login;  // 绑定关系(手动 bind / unbind 用)
using ZincXManagerShared.Network;  // 共享库

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

    /// <summary>显示名与官方账号名的联动状态说明(空 = 不显示这一行)。</summary>
    public Func<string>? LinkHint { get; init; }

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
        Func<int> backgetCountProvider,
        Func<string, string> osMegBack,
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
        Func<WhitelistStore> whitelistProvider,
        Func<string> serverAccountNameProvider,
        Action enterConfig,
        Func<Zxmm.ZxmmApi?>? zxmmProvider = null)
    {
        // 记住本端的服务端账号名:用户账号不能和它重名(和 config 里的 osuser 同步)
        _serverAccountName = serverAccountNameProvider();

        // 账号表(给 megback p 按名字查 ID 用,也用来把玩家 ID 翻成名字)
        var accounts = accountProvider;

        // ---- ZXMM 专用通道(连不上时靠它手动重连) ----
        cmd.Register("zxmm connect", _ => CmdZxmmConnect(zxmmProvider?.Invoke()),
            "手动重连 ZXMM 专用通道(自动重连 5 次失败后用它)",
            "zxmm connect");

        cmd.Register("zxmm status", _ => CmdZxmmStatus(zxmmProvider?.Invoke()),
            "查看 ZXMM 专用通道状态",
            "zxmm status");

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

        // ---- 白名单(通联标 = MC 玩家名 / ZXM 账号名 / ZXM 账号 ID) ----
        cmd.Register("wl add", args => CmdWhitelistAdd(whitelistProvider(), args),
            "添加白名单:wl add <通联标(MC玩家名|ZXM账号名|ZXM账号ID)>",
            "wl add XDH1234");

        cmd.Register("wl rm", args => CmdWhitelistRemove(whitelistProvider(), args),
            "删除白名单:wl rm <通联标>",
            "wl rm XDH1234");

        cmd.Register("wl on", _ => CmdWhitelistSwitch(whitelistProvider(), true),
            "开启白名单(进服先查白名单,不在就黄字踢出)",
            "wl on");

        cmd.Register("wl off", _ => CmdWhitelistSwitch(whitelistProvider(), false),
            "关闭白名单",
            "wl off");

        cmd.Register("wl", args => CmdWhitelistQuery(whitelistProvider(), args),
            "查看白名单:wl 显示全部;wl <通联标> 查这一条",
            "wl XDH1234");

        // ---- 账号绑定(手动给某个 MC 玩家绑上 ZXM 账号,或解掉) ----
        cmd.Register("bind list", args => CmdBindList(accountProvider(), args),
            "列出全部绑定;带参数时按账号ID / 账号名 / 玩家名查一条",
            "bind list [账号ID|账号名|玩家名]");

        cmd.Register("bind", args => CmdBind(accountProvider(), args),
            "手动绑定:先写账号(名称或 ID),再写 MC 玩家名",
            "bind <账号名或ID> <玩家名>");

        cmd.Register("unbind", args => CmdUnbind(accountProvider(), args),
            "解绑:MC玩家名 / ZXM账号名 / ZXM账号ID 都能用,解绑后该玩家再进服要重新走确认",
            "unbind weiqi_wq");

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

        cmd.Register("msg", args =>
        {
            if (args.Length == 0)
            {
                Logger.Log(LogLevel.Warning, "[消息] 用法:" + NoticeUsage);
                Logger.Log(LogLevel.Warning, "[消息] 用法:" + PushUsage);
                return;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "notice":
                    CmdNotice(localNotice, args[1..]);
                    break;
                case "push":
                    CmdPush(localPush, args[1..]);
                    break;
                case "backget":
                {
                    // 尾部带 -os 就查官方端的记录(必须是本服账号)
                    var query = args[1..];   //查询关键字
                    var wantOs = query.Length > 0 &&
                                 string.Equals(query[^1], "-os", StringComparison.OrdinalIgnoreCase);
                    if (wantOs)
                    {
                        query = query[..^1];
                        var text = osMegBack(string.Join(' ', query));
                        foreach (var line in text.Split('\n'))
                        {
                            Logger.Log(LogLevel.Info, "[消息] " + line);
                        }

                        break;
                    }

                    // 查本服玩家返回记录(Data/MegBack.txt)
                    MegBackCommand.Run(query,
                        id => TitleOf(id),
                        name => accounts().Find(name)?.Id ?? 0,
                        id => accounts().NameOf(id),
                        backgetCountProvider());
                    break;
                }
                default:
                    Logger.Log(LogLevel.Warning, $"[消息] 不认识的子命令 {args[0]};可用:notice / push");
                    break;
            }
        }, "消息:msg notice <种类> <标题> <正文> [主动作] [次动作] [选项] / msg push <目标> <种类> <标题> <正文> [主动作] [次动作] [选项]",
            "msg notice choice 是否同意 新协议要点见附件 是 否    /    " + MegBackCommand.Usage);



        cmd.Register("srv show", _ => CmdSrvShow(serverAccountNameProvider()),
            "查看本端接入官方服务端用的账号名",
            "srv show");

        cmd.Register("config", _ => enterConfig(),
            "进入配置模式(表格:键名/中文名/作用/类型/当前值;exitc 退出)",
            "config");
    }

    /// <summary>控制台命令的首词 —— 用来判断"这行输入其实是命令,不是账号名/密码"。</summary>
    private static readonly string[] CommandWords =
    {
        "help", "echo", "exit", "info", "infopc", "msgdays", "srvmsgdays", "name",
        "msg", "osuser", "srv", "user", "ban", "file", "net", "config", "exitc",
    };

    /// <summary>这行输入看起来像控制台命令吗(像的话就不该被当成账号名 / 密码收下)。</summary>
    internal static bool LooksLikeConsoleCommand(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var first = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();   //拆分结果
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


    /// <summary>手动重连 ZXMM 通道</summary>
    private static void CmdZxmmConnect(Zxmm.ZxmmApi? api)
    {
        if (api is null)
        {
            Logger.Log(LogLevel.Warning, "[ZXMM] 还没初始化(启动流程没走到),等启动完再试");
            return;
        }

        api.Reconnect();
        Logger.Log(LogLevel.Info, "[ZXMM] 已手动触发重连,5 秒一轮,最多 5 次");
    }

    /// <summary>看 ZXMM 通道状态</summary>
    private static void CmdZxmmStatus(Zxmm.ZxmmApi? api)
    {
        if (api is null)
        {
            Logger.Log(LogLevel.Info, "[ZXMM] 未初始化");
            return;
        }

        var state = api.Connected ? "已连接" : api.GaveUp ? "已放弃自动重连(用 zxmm connect)" : "未连接,自动重连中";
        Logger.Log(LogLevel.Info, $"[ZXMM] 专用通道:{state}");
    }

    /// <summary>列绑定:不带参数全列,带参数按 ID/账号名/玩家名 查一条</summary>
    private static void CmdBindList(AccountStore accounts, string[] args)
    {
        var query = args.Length > 0 ? args[0].Trim() : "";
        if (query.Length == 0)
        {
            var binds = LoginStore.AllBinds();   //全部绑定
            if (binds.Count == 0)
            {
                Logger.Log(LogLevel.Info, "[绑定] 无绑定信息(玩家进服会被踢并拿到绑定码)");
                return;
            }

            Logger.Log(LogLevel.Info, $"[绑定] 共 {binds.Count} 条:");
            foreach (var pair in binds.OrderBy(p => p.Key))
            {
                var name = accounts.FindById(pair.Key)?.Name ?? "?";   //账号名
                Logger.Log(LogLevel.Info, $"  玩家 {pair.Value}  ↔  账号 {name}(ID {pair.Key})");
            }

            return;
        }

        // 单条查询:账号名 → 账号 ID → 玩家名
        var accountId = accounts.Find(query)?.Id ?? 0;   //账号ID
        if (accountId <= 0 && int.TryParse(query, out var byId))   //byId 当ID解析
        {
            accountId = accounts.FindById(byId)?.Id ?? 0;
        }

        if (accountId <= 0)
        {
            accountId = LoginStore.AccountOf(query);
        }

        if (accountId <= 0)
        {
            Logger.Log(LogLevel.Info, $"[绑定] 无绑定信息:{query} 既不是账号名/ID,也不是已绑定的玩家名");
            return;
        }

        var accountName = accounts.FindById(accountId)?.Name ?? "?";   //账号名
        var playerName = LoginStore.PlayerOf(accountId);                 //绑定的玩家名

        if (playerName.Length == 0)
        {
            Logger.Log(LogLevel.Info, $"[绑定] 无绑定信息:账号 {accountName}(ID {accountId}) 没有绑定任何玩家");
            return;
        }

        Logger.Log(LogLevel.Info, $"[绑定] 账号 {accountName}(ID {accountId})  ↔  玩家 {playerName}");
    }

    /// <summary>绑定:先账号(名或 ID),再玩家名</summary>
    private static void CmdBind(AccountStore accounts, string[] args)
    {
        if (args.Length < 2)
        {
            Logger.Log(LogLevel.Warning, "[绑定] 用法:bind <账号名或ID> <玩家名>    例:bind localplayer weiqi_wq");
            return;
        }

        var key = args[0].Trim();
        var player = args[1].Trim();   //玩家名

        // 账号名或 ID 都行:先按名字找,找不到再当 ID
        var account = accounts.Find(key);
        if (account is null && int.TryParse(key, out var byId))
        {
            account = accounts.FindById(byId);
        }

        if (account is null)
        {
            Logger.Log(LogLevel.Warning, $"[绑定] 没有账号 {key}(名称或 ID 都行,可用 user list 查)");
            return;
        }

        // 一个玩家只能绑一个账号
        var existing = LoginStore.AccountOf(player);   //绑定的账号ID
        if (existing > 0 && existing != account.Id)
        {
            var other = accounts.FindById(existing)?.Name ?? "?";
            Logger.Log(LogLevel.Warning, $"[绑定] 玩家 {player} 已经绑在账号 {other} 上,先 unbind {player} 再绑");
            return;
        }

        if (LoginStore.Bind(account.Id, player, out var error))
        {
            LoginStore.Log($"[手动绑定] 玩家 {player} ↔ 账号 {account.Name}(ID {account.Id})");
            Logger.Log(LogLevel.Info, $"[绑定] 成功:{player} ↔ {account.Name}(ID {account.Id})");
        }
        else
        {
            Logger.Log(LogLevel.Warning, "[绑定] 失败:" + error);
        }
    }

    /// <summary>
    /// 解绑:MC 玩家名 / ZXM 账号名 / ZXM 账号 ID 都能当参数。
    /// 先按玩家名精确找(输入的是玩家名就解这个玩家),找不到再按账号名、账号 ID。
    /// </summary>
    private static void CmdUnbind(AccountStore accounts, string[] args)
    {
        if (args.Length < 1)
        {
            Logger.Log(LogLevel.Warning, "[绑定] 用法:unbind <MC玩家名|ZXM账号名|ZXM账号ID>    例:unbind weiqi_wq / unbind testuser / unbind 5");
            return;
        }

        var key = args[0].Trim();

        // ① 玩家名(MC 账号名称):绑定表里这个玩家归哪个账号
        var accountId = LoginStore.AccountOf(key);

        // ② ZXM 账号名(ZM 账号名称)
        if (accountId <= 0)
        {
            accountId = accounts.Find(key)?.Id ?? 0;
        }

        // ③ ZXM 账号 ID
        if (accountId <= 0 && int.TryParse(key, out var byId))
        {
            accountId = accounts.FindById(byId)?.Id ?? 0;
        }

        if (accountId <= 0)
        {
            Logger.Log(LogLevel.Warning,
                $"[绑定] 找不到 {key}:既不是已绑定的 MC 玩家名,也不是 ZXM 账号名 / 账号 ID(可用 bind list 查)");
            return;
        }

        var accountName = accounts.FindById(accountId)?.Name ?? "?";   //账号名

        if (!LoginStore.Unbind(accountId, out var player))
        {
            Logger.Log(LogLevel.Warning, $"[绑定] 账号 {accountName}(ID {accountId}) 没有绑定记录");
            return;
        }

        // 解绑必须立刻生效:持久登录表里的"已信任"记录一起删掉
        LoginStore.RemovePersistentLogin(player);
        LoginStore.Log($"[手动解绑] 玩家 {player} ↔ 账号 {accountName}(ID {accountId})");

        Logger.Log(LogLevel.Info, $"[绑定] 已解绑:{player} ↔ {accountName}(ID {accountId})");
        Logger.Log(LogLevel.Info, $"[绑定] 该玩家再进服会重新走确认流程");
    }

    /// <summary>加白名单条目(通联标:MC 玩家名 / ZXM 账号名 / ZXM 账号 ID)。</summary>
    private static void CmdWhitelistAdd(WhitelistStore whitelist, string[] args)
    {
        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Logger.Log(LogLevel.Warning, "[白名单] 用法:wl add <MC玩家名|ZXM账号名|ZXM账号ID>");
            return;
        }

        var key = args[0].Trim();
        if (whitelist.Add(key, out var error))
        {
            Logger.Log(LogLevel.Info, $"[白名单] 已添加:{key}(当前 {whitelist.All.Length} 条)");
        }
        else
        {
            Logger.Log(LogLevel.Warning, "[白名单] 添加失败:" + error);
        }
    }

    /// <summary>删白名单条目。</summary>
    private static void CmdWhitelistRemove(WhitelistStore whitelist, string[] args)
    {
        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Logger.Log(LogLevel.Warning, "[白名单] 用法:wl rm <MC玩家名|ZXM账号名|ZXM账号ID>");
            return;
        }

        var key = args[0].Trim();
        if (whitelist.Remove(key))
        {
            Logger.Log(LogLevel.Info, $"[白名单] 已删除:{key}(当前 {whitelist.All.Length} 条)");
        }
        else
        {
            Logger.Log(LogLevel.Warning, $"[白名单] 没有这条:{key}");
        }
    }

    /// <summary>开 / 关白名单。</summary>
    private static void CmdWhitelistSwitch(WhitelistStore whitelist, bool on)
    {
        whitelist.SetEnabled(on);
        Logger.Log(LogLevel.Info, on
            ? $"[白名单] 已开启(进服会先查白名单,共 {whitelist.All.Length} 条)"
            : "[白名单] 已关闭(不再检查)");
    }

    /// <summary>查看白名单:不带参数显示开关与全部条目;带通联标时只查这一条。</summary>
    private static void CmdWhitelistQuery(WhitelistStore whitelist, string[] args)
    {
        var list = whitelist.All;

        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            var key = args[0].Trim();
            var hit = whitelist.Contains(key);
            Logger.Log(LogLevel.Info,
                $"[白名单] 查询 {key}:{(hit ? "在白名单里" : "不在白名单里")}" +
                $"(开关:{(whitelist.Enabled ? "开启" : "关闭")},总共 {list.Length} 条)");
            return;
        }

        Logger.Log(LogLevel.Info, $"[白名单] 状态:{(whitelist.Enabled ? "开启" : "关闭")},共 {list.Length} 条");

        if (list.Length == 0)
        {
            Logger.Log(LogLevel.Info, "[白名单] 空(用 wl add <通联标> 添加)");
            return;
        }

        foreach (var item in list)
        {
            Logger.Log(LogLevel.Info, "  " + item);
        }
    }

    /// <summary>注册封禁命令(ban add / user / del / list)</summary>
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

    /// <summary>ban del:删掉一条封禁(按类型 + 值)。</summary>
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
/// <summary>ban list:列出当前所有封禁及剩余时间。</summary>

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

    /// <summary>多行信息输出:先空一行再打印,免得第一行贴着命令行/提示符导致"错位"。</summary>
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
        if (!TryReadCardArgs(args, NoticeUsage, out var card))
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
        if (!TryReadCardArgs(args[1..], PushUsage, out var card))
        {
            return;
        }

        var sent = push(target, card);
        Logger.Log(LogLevel.Info, sent > 0
            ? $"[消息] 已推给 {target}({card.Kind} 卡片),本服 {sent} 个客户端收到"
            : $"[消息] {target} 不在本服在线,已忽略");
    }

    /// <summary>公告卡片的写法(帮助与报错都用它)。</summary>

    /// <summary>按消息号取卡片标题(用于 megback 列表;取不到返回空)。</summary>
    private static string TitleOf(int messageId)
    {
        var entry = MessageArchive.Find(messageId);
        return entry is not null && MessageCard.TryParse(entry.Card, out var card) ? card.Title : "";
    }

    private const string NoticeUsage = "msg notice <种类> <标题> <正文> [主动作] [次动作] [选项]";

    /// <summary>定向推送的写法。</summary>
    private const string PushUsage = "msg push <目标> <种类> <标题> <正文> [主动作] [次动作] [选项]";

    /// <summary>把「种类 标题 正文 [主动作] [次动作] [选项]」读成一张卡片(校验在共享库里)。</summary>
    private static bool TryReadCardArgs(string[] args, string usage, out CardInput card)
    {
        card = new CardInput();

        if (!MessageCard.TryReadCardArgs(args, out card, out var error))
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
        var link = identity.LinkHint?.Invoke() ?? "";

        LogBlock(
            $"[身份] 名称       {identity.Name()}" + (link.Length > 0 ? $"({link})" : "") + "\n" +
            $"[身份] 账号       {identity.User()}\n" +
            $"[身份] 服务端码   {identity.ServerCode()}\n" +
            $"[身份] 机器码     {ZincXManagerShared.Account.MachineCode.Value}\n" +
            $"[身份] 官方服务端 {target} ({state})");
    }
}