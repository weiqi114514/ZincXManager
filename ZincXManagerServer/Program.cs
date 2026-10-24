using System;  // 基础类型 / 常用静态方法
using System.Linq;  // LINQ 查询
using System.Collections.Concurrent;  // 并发集合(多线程共享)
using System.Net;  // IP 地址与网络端点
using System.Text;  // 文本编码与 StringBuilder
using System.Threading;  // 线程 / 取消 / 定时
using System.Threading.Tasks;  // async / await 异步
using ZincXManagerShared.Account;  // 共享库
using ZincXManagerShared.Command;  // 共享库
using ZincXManagerShared.FileIO;  // 共享库
using ZincXManagerShared.History;  // 补发游标
using ZincXManagerShared.Logging;  // 共享库
using ZincXManagerShared.Network;  // 共享库
using ZincXManagerShared.Security;  // 共享库
using ZincXManagerShared.Verify;  // 共享库
using ZincXManagerServer.Command;  // 子服自身

// 与 BCL 的 TcpClient 同名,起个别名
using TcpClient = ZincXManagerShared.Network.TcpClient;

using ZincXManagerServer.Login;  // 登录/绑定存储与归属地

namespace ZincXManagerServer;

internal static class Program
{
    // 配置键名:ozip/ozport = 官方服务端地址端口,czport = 客户端接入端口
    // 缺省值:正式环境一般不用,只在配置缺失且拿不到输入时兜底
    /// <summary>本端名称(config.ini 的 name):客户端连进来时发给它。</summary>
    private const string KeyName = "name";

    /// <summary>名称没配时的兜底值。</summary>
    private const string DefaultName = "锌能源服务器";

    /// <summary>显示名是否跟随官方账号名(config.ini 的 linkname:1=一致(默认),0=分开)。</summary>
    private const string KeyLinkName = "linkname";

    /// <summary>默认与官方账号名一致 —— 玩家看到的服务端名称就是本端在 ZXMOS 上的账号名。</summary>
    private const string DefaultLinkName = "1";

    /// <summary>官方服务端名称(收到 201 后写进 config.ini 的 ozname)。</summary>
    private const string KeyOsName = "ozname";

    /// <summary>本服消息保留天数(config.ini 的 msgdays,默认 7):下发给客户端,决定客户端本地留多久。</summary>
    private const string KeyMsgDays = "msgdays";

    /// <summary>服务器广场在客户端显示的名字(config.ini 的 squarename,默认"服务器广场")。</summary>
    private const string KeySquareName = "squarename";

    /// <summary>消息保留天数默认值(天)。</summary>
    private const int DefaultMsgDays = 7;

    /// <summary>当前消息保留天数(启动时从 config 读,`msgdays` 命令可改)。</summary>
    private static int _msgDays = DefaultMsgDays;

    /// <summary>本服自己的消息存档保留天数(config.ini 的 srvmsgdays,默认 7):补发时给客户端看多久的本服历史。</summary>
    private const string KeySrvMsgDays = "srvmsgdays";

    // 配置键:玩家返回查询(msg backget)默认返回条数
    private const string KeyBackget = "backgetcount";

    // 默认返回条数
    private const int DefaultBackgetCount = 50;

    /// <summary>本服存档保留天数默认值(天)。</summary>
    private const int DefaultSrvMsgDays = 7;

    /// <summary>当前本服存档保留天数(启动时从 config 读,srvmsgdays 命令可改)。</summary>
    private static int _srvMsgDays = DefaultSrvMsgDays;

    // 玩家返回查询默认条数(config 的 backgetcount)
    private static int _backgetCount = DefaultBackgetCount;

    // 登录确认:正在处理数组(请求ID → 这一条)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, PendingLogin> PendingLogins = new();

    // 绑定码 → (玩家名, 生成时间)
    private static readonly Dictionary<string, (string Player, DateTime At)> BindCodes = new();

    // 登录请求序号
    private static int _loginSeq;

    // -os 查询:等官方端回包的待决表(包 id → 结果)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<string>> MegBackWaits = new();

    // -os 查询的包 id 序号
    private static int _megBackSeq;

    /// <summary>官方验证第二层的开关(config.ini 的 officialcheck,默认 1 = 开):对端不是官方服务端就断开。</summary>
    private const string KeyOfficialCheck = "officialcheck";

    private static bool _officialCheck = true;

    /// <summary>客户端接入端口(czport),Main 里读出来挂这里,报文方法要用。</summary>
    private static string _czport = DefaultCzPort;

    /// <summary>本实例的服务端码(config.ini 的 servercode):32 位十六进制,首次生成后不变。
    /// 同一台机器可以跑多个 ZXMS,靠它区分"是哪一个实例"(机器码只用来识别设备 / 防绕过)。</summary>
    private const string KeyServerCode = "servercode";

    /// <summary>要不要连官方服务端(ZXMOS):<b>1 = 连接(默认)</b>,0 = 不连接。</summary>
    private const string KeyConnectOs = "connectOS";

    /// <summary>connectOS 的默认值(1 = 启动时连官方端)。</summary>
    private const string DefaultConnectOs = "1";

    /// <summary>配置格式版本。2 起 connectOS 是「1=连接、0=不连接」(以前正好相反)。</summary>
    private const string KeyConfigVer = "configver";

    private const string ConfigVerNow = "2";
    private const string KeyOzip = "ozip";
    private const string KeyOzport = "ozport";
    // config 键:子服端口。
    private const string KeyCzport = "czport";
    // 官方服务端默认端口:25000(客户端默认也是 zxm.zincms.top:25000)
    private const string DefaultOsPort = "25000";
    private const string DefaultCzPort = "25566";

    // 协议常量统一放在共享库 Protocol 里(三端共用一份),这里只是别名,阅读方便
    private const ushort SOnline = Protocol.SOnline;
    private const ushort SBack = Protocol.SBack;
    private const ushort ServerIdent = Protocol.ServerIdent;
    private const ushort Conline = Protocol.Conline;
    private const ushort CBack = Protocol.CBack;

    /// <summary>连向 ZXMOS 的客户端;存字段,不然会被回收(中转要用它)。</summary>
    private static TcpClient? _os;

    /// <summary>本端 config.ini(命令层改配置时用;启动时赋值)。</summary>
    private static ZFile? _config;

    /// <summary>监听 ZXMC / ZXMM 的服务端;登记表在它的 Registry 上,中转时也靠它推消息。</summary>
    private static TcpServer? _server;

    /// <summary>账号表(users.ini):注册 / 登录都查它;控制台 account 命令也用它。</summary>
    private static readonly AccountStore Accounts = new("User/Data");

    /// <summary>封禁表:账号 / IP / 机器码,永久或定时。</summary>
    private static readonly BanStore Bans = new("bans.json");

    static void OpenLog()
    {
        if (!Logger.OpenDefaultLogFile("ZXMS", out var logFile))
        {
            Console.Error.WriteLine("[Fatal]打开日志文件失败: " + logFile);
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// 本端显示给玩家的名称。<b>linkname=1(默认)时直接复用官方账号名</b>(和 ZXMOS 上那个账号一个字不差),
    /// linkname=0 时用 config.ini 的 name,两者互不干扰。
    /// </summary>
    static string ResolveName(ZFile config)
    {
        var account = config.MapGet(KeyOsUser).Trim();
        var own = config.MapGet(KeyName).Trim();

        if (config.MapGet(KeyLinkName).Trim() != "0" && account.Length > 0)
        {
            return account;
        }

        return own.Length > 0 ? own : DefaultName;
    }

    /// <summary>
    /// 解析 connectOS。<b>1 = 默认连接 ZXMOS,0 = 不连接。</b>
    ///
    /// <para>老配置(没有 configver)里 0 表示"连接"、1 表示"不连" —— 直接按新语义读会让老部署突然不连了,
    /// 所以碰到老配置先把值翻过来、写上 configver=2,只迁移一次。</para>
    /// </summary>
    static string ResolveConnectOS(ZFile config)
    {
        var raw = config.MapGet(KeyConnectOs).Trim();
        var legacy = config.MapGet(KeyConfigVer).Trim().Length == 0;

        if (legacy && raw is "0" or "1")
        {
            var flipped = raw == "0" ? "1" : "0";
            config.WriteFileMap(KeyConnectOs, flipped);
            config.WriteFileMap(KeyConfigVer, ConfigVerNow);
            Logger.Log(LogLevel.Info,
                $"[CONFIG]{KeyConnectOs} 已迁移到新语义:{raw} → {flipped}(1=连接 ZXMOS,0=不连接)");
            raw = flipped;
        }

        var value = AskIfMissing(config, KeyConnectOs,
            "[CONFIG]输入1默认连接官方服务端(ZXMOS),输入0不连接",
            v => v is "0" or "1",
            fallback: "1");

        if (config.MapGet(KeyConfigVer).Trim().Length == 0)
        {
            config.WriteFileMap(KeyConfigVer, ConfigVerNow);
        }

        return value;
    }

    /// <summary>
    /// 配置检查:ini 里这个键没有"有效值"就在控制台问一次,问到合法值后写回 ini。
    /// 直接回车(空输入)或输入流已结束(管道/重定向)时用 fallback 兜底,不会死循环。
    /// </summary>
    static string AskIfMissing(ZFile file, string key, string prompt, Func<string, bool> valid, string fallback)
    {
        var value = file.MapGet(key).Trim();
        if (valid(value))
        {
            return value;
        }

        while (true)
        {
            Logger.Log(LogLevel.Info, prompt);

            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                input = fallback;
            }

            if (valid(input))
            {
                file.WriteFileMap(key, input);
                Logger.Log(LogLevel.Info, $"[CONFIG]{key} 已设置为 {input}");
                return input;
            }

            Logger.Log(LogLevel.Warning, $"[CONFIG]{key} 输入不合法:{input}");
        }
    }

    /// <summary>
    /// 回复握手确认(收到 300 回 301)。
    /// 成功/失败都记日志 —— 否则服务端这边只有"上线",看不出确认包有没有发出去。
    /// </summary>
    static async Task ReplyHandshakeAsync(ServerboundPacket packet, ushort ackType, string ackName, string peer, string text)
    {
        try
        {
            await packet.WriteAsync(new Packet(packet.Id, ackType, Encoding.UTF8.GetBytes(text)));
            Logger.Log(LogLevel.Info, $"[{peer}] 已回复握手确认({ackName}) → {packet.Connection.RemoteEndPoint},握手完成");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, $"[{peer}] 回复握手确认失败: {ex.Message}");
        }
    }

    /// <summary>版本号(程序集 InformationalVersion,形如 a0.3.1)。</summary>
    private static string AppVersion { get; } =
        ((System.Reflection.AssemblyInformationalVersionAttribute?)System.Attribute.GetCustomAttribute(
            typeof(Program).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute)))
            ?.InformationalVersion?.Split('+')[0] ?? "1.0.0";

    /// <summary>
    /// 处理一条来自 ZXMC / ZXMM 的报文:按 Type 分派。
    /// </summary>
    /// <param name="packet">收到的报文包(带连接上下文)。</param>
    /// <summary>
    /// 替命令查官方端的玩家返回记录(msg backget ... -os):发 508,等 509 回包。
    /// </summary>
    /// <param name="args">查询参数(命令里 -os 之前那部分)。</param>
    /// <returns>官方端渲染好的文本;连不上/超时返回原因。</returns>
    private static async Task<string> QueryOsMegBackAsync(string args)
    {
        var os = _os;
        if (os is null || !os.IsConnected)
        {
            return "官方服务端未连接,查不了 -os";
        }

        var id = Interlocked.Increment(ref _megBackSeq);
        var wait = new TaskCompletionSource<string>();
        MegBackWaits[id] = wait;

        try
        {
            await os.WriteAsync(new Packet((ulong)id, Protocol.MegBackReq, Encoding.UTF8.GetBytes(args)));
            var done = await Task.WhenAny(wait.Task, Task.Delay(6000));
            return done == wait.Task ? await wait.Task : "等官方服务端回包超时";
        }
        catch (Exception ex)
        {
            return "转发查询失败: " + ex.Message;
        }
        finally
        {
            MegBackWaits.TryRemove(id, out _);
        }
    }

    /// <summary>一条等待客户端确认的登录请求。</summary>
    private sealed record PendingLogin(string Player, string Ip, int AccountId, Guid OriginConnId, DateTime At);

    /// <summary>给指定账号的 ZXMC 连接发一条包(不在线返回 false)。</summary>
    private static async Task<bool> SendToAccountAsync(int accountId, ushort type, string payload)
    {
        var server = _server;
        var account = Accounts.FindById(accountId);
        if (server is null || account is null)
        {
            return false;
        }

        foreach (var conn in server.Connections)
        {
            var info = server.Registry.Find(conn.Id);
            if (info is null || !string.Equals(info.User, account.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                await conn.SendAsync(type, payload, 0, "ZXMC");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[登录] 发给 " + account.Name + " 失败: " + ex.Message);
            }
        }

        return false;
    }

    /// <summary>生成六位绑定码(去掉容易看错的 0/O/1/I)。</summary>
    private static string NewBindCode()
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        var code = new char[6];
        for (var i = 0; i < code.Length; i++)
        {
            code[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        }

        return new string(code);
    }

    private static async Task HandlePacketAsync(ServerboundPacket packet)
    {
        // 别名:让方法体保持原来的写法
        var server = _server!;       // 监听中的服务端
        var config = _config!;       // config.ini 句柄
        var czport = _czport;        // 客户端接入端口(答复里要回给客户端)

        switch (packet.Type)
        {
            // ---- ZXMC(客户端)上线:负载形如 "ZXMC:SAOS"(客户端把自己的连接模式报上来) ----
            case Conline:
            {
                var text = Encoding.UTF8.GetString(packet.Data).Trim();
                var parts = text.Split(':', 2);
                var mode = parts.Length > 1 ? parts[1] : "-";

                _server.Registry.SetRole(packet.Connection.Id, "ZXMC", mode);
                Logger.Log(LogLevel.Info, $"[ZXMC] 上线(握手): {text} 模式={mode}");
                ClientLog.Online("ZXMC", mode, packet.Connection.RemoteEndPoint?.ToString() ?? "?");

                await ReplyHandshakeAsync(packet, CBack, "301", "ZXMC", "ZXMS ok");
                break;
            }

            // ---- 会话加密:ZXMC 发 ECDH 公钥,回自己的公钥,之后报文全部加密 ----
            case Protocol.SessionHello:
            {
                var handshake = new SessionHandshake();
                await packet.Connection.SendAsync(Protocol.SessionHelloAck, handshake.PublicKey, packet.Id);
                packet.Connection.Crypto = handshake.Complete(packet.Data, isServer: true);
                Logger.Log(LogLevel.Info, $"[会话] 与 ZXMC 建立加密通道 ← {packet.Connection.RemoteEndPoint}");
                break;
            }

            // ---- 账号:登录(负载 "名称|密码密文|等级") ----
            case Protocol.AccountLogin:
            {
                // 未协商加密的登录一律拒绝:登录凭据不能走明文链路(否则抓包就能重放)
                if (packet.Connection.Crypto is null)
                {
                    Logger.Log(LogLevel.Warning, $"[安全] 拒绝明文登录 ← {packet.Connection.RemoteEndPoint}(客户端过旧,需更新)");
                    await packet.Connection.SendAsync(Protocol.AccountLoginAck,
                        "err|客户端版本过旧(缺少加密会话),请更新到 a0.3.1 或更高版本", packet.Id);
                    break;
                }

                var parts = Encoding.UTF8.GetString(packet.Data).Split('|');
                var user = parts.Length > 0 ? parts[0].Trim() : "";
                var passwordHash = parts.Length > 1 ? parts[1] : "";
                var level = parts.Length > 2 ? parts[2].Trim() : Protocol.RegisterLevel;
                var side = DecideAccountSide(level, parts.Length > 3 ? parts[3] : "");
                var machine = parts.Length > 4 ? parts[4].Trim() : "";
                // 第 6/7 段是挑战-响应(loginNonce / proof),用于本地账号增强校验
                var loginNonce = parts.Length > 6 ? parts[6].Trim() : "";
                var loginProof = parts.Length > 7 ? parts[7].Trim() : "";
                var fromIp = packet.Connection.RemoteEndPoint?.Address.ToString() ?? "";

                if (Bans.Check(user, fromIp, machine, out var ban))
                {
                    Logger.Log(LogLevel.Warning, $"[封禁] 拒绝登录 {user}({ban!.Kind}={ban.Value},剩余 {ban.Remain})");
                    await packet.SendAsync(Protocol.AccountLoginAck, $"err|已被封禁:{ban.Reason}({ban.Remain})", packet.Id);
                    break;
                }

                // 该在官方服务端校验的(玩家账号 / ZXMOS 管理账号):优先中转过去
                if (side == "zxmos")
                {
                    // 登录也打 relay 标记:官方服务端才知道这是"替玩家 / 管理账号登录",
                // 而不是 ZXMS 自己接入账号的登录(否则会去查服务端账号表 → 账号不存在)
                if (await TryRelayAccountAsync(packet, Protocol.AccountLogin, "|relay"))
                    {
                        break;
                    }

                    if (level != Protocol.RegisterLevel)
                    {
                        Logger.Log(LogLevel.Warning, "[账号] 官方服务端未连接,无法登录 ZXMOS 管理账号");
                        await packet.SendAsync(Protocol.AccountLoginAck,
                            "err|官方服务端未连接,无法登录 ZXMOS 管理账号", packet.Id);
                        break;
                    }

                    Logger.Log(LogLevel.Warning, "[账号] 官方服务端未连接,玩家账号退回本端处理");
                }

                var ok = loginNonce.Length > 0 && loginProof.Length > 0
                    ? Accounts.TryLoginProof(user, loginNonce, loginProof, level, out var account, out var error)
                    : Accounts.TryLogin(user, passwordHash, level, out account, out error);

                if (ok)
                {
                    _server.Registry.SetAccount(packet.Connection.Id, account!.Name, account.Level);
                    Accounts.Touch(account.Name, fromIp, machine);
                    Logger.Log(LogLevel.Info, $"[账号] 登录成功:{account.Name}({account.Level}) ← {packet.Connection.RemoteEndPoint}");
                    ClientLog.Write(account.Level, $"登录 账号={account.Name} 地址={packet.Connection.RemoteEndPoint}");

                    await packet.SendAsync(Protocol.AccountLoginAck,
                        $"ok|{account.Name}|{account.Id}|{account.Level}", packet.Id);
                }
                else
                {
                    Logger.Log(LogLevel.Warning, $"[账号] 登录失败:{user} —— {error}");
                    await packet.SendAsync(Protocol.AccountLoginAck, "err|" + error, packet.Id);
                }

                break;
            }

            // ---- 账号:注册(负载 "名称|密码密文";等级固定 Player) ----
            case Protocol.AccountRegister:
            {
                var parts = Encoding.UTF8.GetString(packet.Data).Split('|');
                var user = parts.Length > 0 ? parts[0].Trim() : "";
                var passwordHash = parts.Length > 1 ? parts[1] : "";
                // 第 3 段是客户端机器码(老客户端没这段,当空处理)
                var regMachine = parts.Length > 2 ? parts[2].Trim() : "";

                var regIp = packet.Connection.RemoteEndPoint?.Address.ToString() ?? "";
                if (Bans.Check(user, regIp, regMachine, out var regBan))
                {
                    Logger.Log(LogLevel.Warning, $"[封禁] 拒绝注册 {user}({regBan!.Kind}={regBan.Value})");
                    await packet.SendAsync(Protocol.AccountRegisterAck, $"err|已被封禁:{regBan.Reason}({regBan.Remain})", packet.Id);
                    break;
                }

                // 玩家账号优先在官方服务端注册(SAOS 下也必须经 ZXMS 中转,不能绕过);
                // 带 "|relay" 让官方服务端知道这是替玩家注册,不是 ZXMS 自己注册服务端账号
                if (await TryRelayAccountAsync(packet, Protocol.AccountRegister, "|relay"))
                {
                    break;
                }

                Logger.Log(LogLevel.Info, "[账号] 官方服务端未连接,玩家账号在本端注册");

                if (Accounts.TryRegister(user, passwordHash, out var account, out var error))
                {
                    // 注册成功也要登记账号 + 记下 IP/机器码:否则这条连接在拉黑时踢不掉,也少一条连坐
                    _server.Registry.SetAccount(packet.Connection.Id, account!.Name, account.Level);
                    Accounts.Touch(account.Name, regIp, regMachine);
                    Logger.Log(LogLevel.Info, $"[账号] 注册成功:{account.Name}({account.Level}) ← {packet.Connection.RemoteEndPoint}");
                    ClientLog.Write(account.Level, $"注册 账号={account.Name} 地址={packet.Connection.RemoteEndPoint}");

                    await packet.SendAsync(Protocol.AccountRegisterAck,
                        $"ok|{account.Name}|{account.Id}|{account.Level}", packet.Id);
                }
                else
                {
                    Logger.Log(LogLevel.Warning, $"[账号] 注册失败:{user} —— {error}");
                    await packet.SendAsync(Protocol.AccountRegisterAck, "err|" + error, packet.Id);
                }

                break;
            }

            // ---- 头像:取当前账号的头像(负载空) ----
            case Protocol.AccountAvatarReq:
            {
                var info = _server.Registry.Find(packet.Connection.Id);
                var avatar = AvatarStore.Load(info?.User ?? "");

                // 有头像就把图片字节原样发回去;没有就发空负载
                await packet.Connection.SendAsync(Protocol.AccountAvatarAck, avatar, packet.Id, info?.Level ?? "-");
                Logger.Log(LogLevel.Info,
                    $"[账号] {info?.User ?? "未登录"} 请求头像 → {(avatar.Length > 0 ? avatar.Length + " 字节" : "没有头像")}");
                break;
            }

            // ---- 头像:设置当前账号的头像(负载 = 图片字节) ----
            case Protocol.AccountAvatarSet:
            {
                var info = _server.Registry.Find(packet.Connection.Id);

                if (info is null || info.User.Length == 0)
                {
                    await packet.SendAsync(Protocol.AccountAvatarAck, "err|请先登录", packet.Id);
                    break;
                }

                var ok = AvatarStore.Save(info.User, packet.Data, out var error);
                await packet.SendAsync(Protocol.AccountAvatarAck, ok ? "ok" : "err|" + error, packet.Id);
                Logger.Log(LogLevel.Info, ok
                    ? $"[账号] {info.User} 更新头像({packet.Data.Length} 字节)"
                    : $"[账号] {info.User} 更新头像失败:{error}");
                break;
            }

            // ---- 卡片动作(502):客户端点了公告 / 推送卡片上的按钮 ----
            case Protocol.CardAction:
            {
                var text = Encoding.UTF8.GetString(packet.Data);
                var who = _server?.Registry.Find(packet.Connection.Id)?.User ?? "?";

                if (!MessageCard.TryParseAction(text, out var talk))
                {
                    Logger.Log(LogLevel.Warning, "[消息] 卡片动作负载格式不对,已忽略:" + text);
                    await packet.SendAsync(Protocol.CardActionAck, "err|动作格式不对", packet.Id);
                    break;
                }

                // 卡片是官方服务端发的(来源 ZXMOS-…):转交过去,让官方端知道是谁点的
                var forwarded = talk.CardSource.StartsWith("ZXMOS-", StringComparison.OrdinalIgnoreCase) &&
                                await TryForwardCardActionAsync(who, text);

                // 玩家返回单独记一份:Data/MegBack.txt(一行一条)
                var originId = MessageCard.TryParse(talk.Card, out var origin) ? origin.Id : 0;
                var originEntry = originId > 0 ? MessageArchive.Find(originId) : null;
                var originTarget = originEntry is null || originEntry.Target.Trim().Length == 0
                    ? (talk.CardSource.Length > 0 ? talk.CardSource : "notice")
                    : originEntry.Target;
                CardResponses.Append(DateTime.Now, originId, originTarget,
                    Accounts.Find(who)?.Id ?? 0, MessageCard.ActionText(text));

                Logger.Log(LogLevel.Info,
                    $"[消息] {who} 在卡片「{talk.CardTitle}」上点了「{MessageCard.ActionText(text)}」" +
                    (forwarded ? "(已转交官方服务端)" : "(本服卡片)"));

                await packet.SendAsync(Protocol.CardActionAck,
                    forwarded ? "ok|已转交官方服务端" : "ok|本服已收到你的选择", packet.Id);
                break;
            }

            // ---- 补发请求(505):客户端登录后要一次历史消息 → 转给官方服务端,回包再按包 Id 转回这条连接 ----
            case Protocol.MessageHistoryReq:
            {
                var who = _server?.Registry.Find(packet.Connection.Id)?.User ?? "?";
                var intent = Encoding.UTF8.GetString(packet.Data).Trim();
                if (intent.Length == 0)
                {
                    intent = "client";
                }

                var forClient = !string.Equals(intent, "self", StringComparison.OrdinalIgnoreCase);

                // ① 先把本服自己的存档给出去:官方端掉线 / OS 模式(只连子服)时也能补到本服消息。
                //    官方端连着时跳过"从官方端转下来的那些"(官方那边本来就会给,免得发两遍、506 数字虚高)
                var osLive = _os is { IsConnected: true };
                var localSent = await SendLocalHistoryAsync(packet, who, forClient, osLive);

                // ② 官方端连着就再要一批;收尾时把"官方条数 + 本服条数"合成一条 506 回给客户端
                var os = _os;
                if (os is null || !os.IsConnected)
                {
                    Logger.Log(LogLevel.Info,
                        $"[消息] {who} 来要补发:官方服务端未连接,只补本服 {localSent} 条");
                    await packet.SendAsync(Protocol.MessageHistoryDone, localSent.ToString(), packet.Id);
                    break;
                }

                var relayId = Interlocked.Increment(ref _relayId);
                HistoryRelay[relayId] = (packet.Connection.Id, packet.Id, localSent);

                try
                {
                    await os.WriteAsync(new Packet(relayId, Protocol.MessageHistoryReq,
                        Encoding.UTF8.GetBytes(intent)));
                    Logger.Log(LogLevel.Info,
                        $"[消息] 已替 {who} 向官方服务端要补发(relay={relayId}),本服已先给 {localSent} 条");
                }
                catch (Exception ex)
                {
                    HistoryRelay.TryRemove(relayId, out _);
                    Logger.Log(LogLevel.Error, "[消息] 转发补发请求失败: " + ex.Message);
                    await packet.SendAsync(Protocol.MessageHistoryDone, localSent.ToString(), packet.Id);
                }

                break;
            }

            // ---- 官方端回包(509):-os 查询结果,唤醒等待的命令 ----
            case Protocol.MegBackAck:
            {
                var ackText = Encoding.UTF8.GetString(packet.Data);
                if (MegBackWaits.TryRemove((int)packet.Id, out var pending))
                {
                    pending.TrySetResult(ackText);
                }
                break;
            }

            // ---- 登录请求(620,来自 ZXMM):有绑定就问客户端,没绑定就发绑定码 ----
            case Protocol.LoginRequest:
            {
                var loginText = Encoding.UTF8.GetString(packet.Data).Trim();
                var loginParts = loginText.Split('|');
                var player = loginParts.Length > 0 ? loginParts[0].Trim() : "";
                var ip = loginParts.Length > 1 ? loginParts[1].Trim() : "";

                if (player.Length == 0)
                {
                    await packet.SendAsync(Protocol.LoginResult, "err|玩家名为空", packet.Id);
                    break;
                }

                var boundAccount = LoginStore.AccountOf(player);
                if (boundAccount <= 0)
                {
                    // 没绑定:发一个绑定码,让玩家去 ZXMC 的绑定功能里绑
                    var code = NewBindCode();
                    lock (BindCodes)
                    {
                        BindCodes[code] = (player, DateTime.Now);
                    }

                    LoginStore.Log($"[未绑定] 玩家 {player}({ip}) 拿到绑定码 {code}");
                    await packet.SendAsync(Protocol.LoginResult, $"nobind|{code}|{player}", packet.Id);
                    break;
                }

                var requestId = Interlocked.Increment(ref _loginSeq);
                PendingLogins[requestId] = new PendingLogin(player, ip, boundAccount, packet.Connection.Id, DateTime.Now);
                LoginStore.Log($"[登录请求] {player}({ip}) → 账号 {boundAccount},请求 {requestId}");

                var geo = GeoLookup.Lookup(ip);
                if (!await SendToAccountAsync(boundAccount, Protocol.LoginAsk,
                        $"{requestId}|{player}|{ip}|{geo}"))
                {
                    PendingLogins.TryRemove(requestId, out _);
                    await packet.SendAsync(Protocol.LoginResult, $"err|绑定的账号不在线|{player}", packet.Id);
                }
                break;
            }

            // ---- 客户端回答(622):同意 / 拒绝 ----
            case Protocol.LoginAnswer:
            {
                var answerText = Encoding.UTF8.GetString(packet.Data).Trim();
                var answerParts = answerText.Split('|');
                if (answerParts.Length < 2 || !int.TryParse(answerParts[0], out var requestId) ||
                    !PendingLogins.TryRemove(requestId, out var pending))
                {
                    break;
                }

                var agree = string.Equals(answerParts[1], "yes", StringComparison.OrdinalIgnoreCase);
                var answerInfo = _server?.Registry.Find(packet.Connection.Id);
                var answerIp = answerInfo?.Address ?? "?";

                LoginStore.Log(agree
                    ? $"[登录通过] 玩家 {pending.Player},账号 {pending.AccountId},发起 IP {pending.Ip},同意 IP {answerIp}"
                    : $"[登录拒绝] 玩家 {pending.Player},账号 {pending.AccountId},发起 IP {pending.Ip},拒绝 IP {answerIp}");

                var origin = _server?.Connections.FirstOrDefault(c => c.Id == pending.OriginConnId);
                if (origin is not null)
                {
                    var accountName = Accounts.FindById(pending.AccountId)?.Name ?? "";
                    await origin.SendAsync(Protocol.LoginResult,
                        $"{(agree ? "ok" : "no")}|{pending.Player}|{accountName}|{pending.AccountId}", 0, "ZXMM");
                }

                if (agree)
                {
                    LoginStore.AddPersistentLogin(pending.Player, pending.Ip);
                }
                break;
            }

            // ---- 绑定码转交(625):ZXMM 把玩家手里的码转给服务端 ----
            case Protocol.BindNotice:
            {
                var noticeText = Encoding.UTF8.GetString(packet.Data).Trim();
                var noticeParts = noticeText.Split('|');
                if (noticeParts.Length >= 2 && noticeParts[0].Trim().Length > 0)
                {
                    lock (BindCodes)
                    {
                        BindCodes[noticeParts[0].Trim().ToUpperInvariant()] = (noticeParts[1].Trim(), DateTime.Now);
                    }
                }
                break;
            }

            // ---- 绑定(626):客户端用绑定码绑定 ----
            case Protocol.BindApply:
            {
                var code = Encoding.UTF8.GetString(packet.Data).Trim().ToUpperInvariant();
                var bindWho = _server?.Registry.Find(packet.Connection.Id)?.User ?? "";
                var bindAccount = Accounts.Find(bindWho)?.Id ?? 0;
                if (bindAccount <= 0)
                {
                    await packet.SendAsync(Protocol.BindAck, "err|请先登录", packet.Id);
                    break;
                }

                var bindPlayer = "";
                lock (BindCodes)
                {
                    if (BindCodes.TryGetValue(code, out var entry) && (DateTime.Now - entry.At).TotalMinutes <= 30)
                    {
                        bindPlayer = entry.Player;
                        BindCodes.Remove(code);
                    }
                }

                if (bindPlayer.Length == 0)
                {
                    await packet.SendAsync(Protocol.BindAck, "err|绑定码无效或已过期", packet.Id);
                    break;
                }

                await packet.SendAsync(Protocol.BindAck,
                    LoginStore.Bind(bindAccount, bindPlayer, out var bindError) ? $"ok|{bindPlayer}" : "err|" + bindError,
                    packet.Id);
                break;
            }

            // ---- 解绑(628) ----
            case Protocol.Unbind:
            {
                var unbindWho = _server?.Registry.Find(packet.Connection.Id)?.User ?? "";
                var unbindAccount = Accounts.Find(unbindWho)?.Id ?? 0;
                if (unbindAccount > 0 && LoginStore.Unbind(unbindAccount, out var removed))
                {
                    await packet.SendAsync(Protocol.BindAck, $"ok|{removed}", packet.Id);
                }
                else
                {
                    await packet.SendAsync(Protocol.BindAck, "err|没有绑定记录", packet.Id);
                }
                break;
            }

            // ---- 查绑定(629,给 ZXMM 用):回列表,一行一条 玩家名=账号名 ----
            case Protocol.BindQuery:
            {
                var lines = LoginStore.AllBinds()
                    .Select(pair => $"{pair.Value}={Accounts.FindById(pair.Key)?.Name ?? "?"}");
                await packet.SendAsync(Protocol.BindList, string.Join("\n", lines), packet.Id);
                break;
            }

            // ---- 客户端回执(507):这几条它已经收下了,记进已发清单 ----
            case Protocol.MessageAck:
            {
                var ackWho = _server?.Registry.Find(packet.Connection.Id)?.User ?? "";
                var ackId = Accounts.Find(ackWho)?.Id ?? 0;
                var ackText = Encoding.UTF8.GetString(packet.Data).Trim();
                var ackIds = ackText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => int.TryParse(t, out var v) ? v : 0).Where(v => v > 0).ToArray();
                if (ackId > 0 && ackIds.Length > 0)
                {
                    SentMessages.Add(ackId, ackIds);
                    Logger.Log(LogLevel.Info, $"[消息] {ackWho} 回执:已收 {ackIds.Length} 条");
                }

                // 官方服那边也存了这批消息:回执要原样转过去,
                // 不然它没有"已发"记录,客户端每次重启它都会再补一遍
                var ackOs = _os;
                if (ackOs is { IsConnected: true } && ackIds.Length > 0)
                {
                    try
                    {
                        await ackOs.WriteAsync(new Packet(0, Protocol.MessageAck, packet.Data));
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(LogLevel.Warning, "[消息] 转发回执给官方服失败: " + ex.Message);
                    }
                }
                break;
            }

            // ---- 服务端消息:公告(500)/ 定向推送(501)只有服务端能发,客户端发来一律忽略 ----
            case Protocol.Notice:
            case Protocol.Push:
                Logger.Log(LogLevel.Warning,
                    $"[ZXMS] 客户端试图发服务端消息 type={packet.Type},已忽略 ← {packet.Connection.RemoteEndPoint}");
                break;

            // 其它类型先记日志(ZXMM 模组端的类型还没定,定了在这里加 case)
            default:
                Logger.Log(LogLevel.Warning, $"[网络] 未处理的包 type={packet.Type} len={packet.Data.Length}");
                break;
        }
    }

    /// <summary>打启动 banner:ASCII 艺术字 + 版本 + 官网地址。</summary>
    private static void PrintBanner()
    {
        Console.WriteLine(" _____   _           _  __ __  ___                                     _____    \r\n/__  /  (_)___  ____| |/ //  |/  /___ _____  ____ _____ ____  _____   / ___/    \r\n  / /  / / __ \\/ ___/   // /|_/ / __ `/ __ \\/ __ `/ __ `/ _ \\/ ___/   \\__ \\     \r\n / /__/ / / / / /__/   |/ /  / / /_/ / / / / /_/ / /_/ /  __/ /      ___/ /     \r\n/____/_/_/ /_/\\___/_/|_/_/  /_/\\__,_/_/ /_/\\__,_/\\__, /\\___/_/      /____/      \r\n                                                /____/                          ");
        Console.WriteLine("欢迎使用ZincXManager子服务端");
        Console.WriteLine("当前版本为 ZXMS-" + AppVersion);
        Console.WriteLine("ZincXManager官网 https://weiqi114514.github.io/ZincXManager/");
        Console.WriteLine("旧站(带端口) http://zxm.zincms.top:10/");
    }

    /// <summary>
    /// 读 config.ini 并把该问的问了:名称、服务端码、是否连官方、端口、消息保留天数。
    /// 缺的项会当场交互问一次然后写回文件,下次启动就不问了。
    /// </summary>
    /// <returns>(config 句柄, 本端名称, 是否连官方 "1"/"0")。</returns>
    private static (ZFile Config, string ServerName, string ConnectOs) LoadConfig()
    {
        // 设置文件
        var config = new ZFile();
        config.FileOperate("config.ini", "rwNFC");

        // 本端名称:客户端连进来时发给它(客户端不用自己填服务端名称)
        // 没有就当场问 —— 以前是静默写个默认名,结果只能事后用 name 命令改
        var serverName = AskIfMissing(config, KeyName,
            $"[CONFIG]输入本服务端的显示名称(下发给客户端,直接回车用默认:{DefaultName})",
            v => v.Trim().Length >= 1 && v.Trim().Length <= 32,
            fallback: DefaultName);

        // 显示名与官方账号名是否联动(默认联动:玩家看到的名字 = 本端在 ZXMOS 的账号名)
        var linkName = config.MapGet(KeyLinkName).Trim();
        if (linkName.Length == 0)
        {
            config.WriteFileMap(KeyLinkName, DefaultLinkName);
            linkName = DefaultLinkName;
            Logger.Log(LogLevel.Info, $"[CONFIG]{KeyLinkName} 未配置,已写入默认值 {DefaultLinkName}(1=与官方账号名一致)");
        }

        // 服务端码:本实例的身份证。缺失或格式不对就生成一个 32 位十六进制并写回
        var serverCode = config.MapGet(KeyServerCode).Trim().ToLowerInvariant();
        if (serverCode.Length != 32 || serverCode.Any(c => !Uri.IsHexDigit(c)))
        {
            serverCode = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            config.WriteFileMap(KeyServerCode, serverCode);
            Logger.Log(LogLevel.Info, $"[CONFIG]已生成服务端码:{serverCode}");

            // 已经配过 osuser 而且**以前连上过官方服**(config 里有 ozname)= 这是个老部署:
            // 这时缺 servercode 说明 config.ini 被整体换过,官方服记的还是老码 → 接上去会被判"身份不一致"。
            // (刚发下去、还没连过的干净 config 不提醒 —— 那种情况本来就要新生成一个码)
            var knownUser = config.MapGet(KeyOsUser).Trim();
            if (knownUser.Length > 0 && config.MapGet(KeyOsName).Trim().Length > 0)
            {
                Logger.Log(LogLevel.Warning,
                    $"[CONFIG]注意:本端已经配过服务端账号 {knownUser} 而且以前连上过官方服务端,但 config.ini 里没有 servercode —— " +
                    "现在生成的是一个新码,官方服务端记的还是当初那个,接入会被判「身份不一致」。\n" +
                    "         处理:① 把 servercode 改回当初登记的那个(config.ini 备份里找);" +
                    "② 或者在官方服务端那边清掉这个服务端账号,本端重连时会自动重新注册");
            }
        }


        // 要不要连官方服务端(1 = 连接 ZXMOS,0 = 不连接)
        var connectOS = ResolveConnectOS(config);

        // 消息保留天数(下发给客户端):config 里的 msgdays,默认 7,合法范围 1-365
        _config = config;
        _msgDays = int.TryParse(config.MapGet(KeyMsgDays).Trim(), out var keepDays) && keepDays is >= 1 and <= 365
            ? keepDays
            : DefaultMsgDays;
        if (config.MapGet(KeyMsgDays).Trim().Length == 0)
        {
            config.WriteFileMap(KeyMsgDays, _msgDays.ToString());
            Logger.Log(LogLevel.Info, $"[CONFIG]{KeyMsgDays} 未配置,已写入默认值 {_msgDays}(消息保留天数)");
        }

        // 本服自己的消息存档保留天数(config 的 srvmsgdays,默认 7,合法范围 1-365)
        _srvMsgDays = int.TryParse(config.MapGet(KeySrvMsgDays).Trim(), out var srvDays) && srvDays is >= 1 and <= 365
            ? srvDays
            : DefaultSrvMsgDays;
        if (config.MapGet(KeySrvMsgDays).Trim().Length == 0)
        {
            config.WriteFileMap(KeySrvMsgDays, _srvMsgDays.ToString());
            Logger.Log(LogLevel.Info,
                $"[CONFIG]{KeySrvMsgDays} 未配置,已写入默认值 {_srvMsgDays}(本服消息存档保留天数)");
        }

        // 玩家返回查询默认条数(msg backget 不带参数时返回多少条)
        _backgetCount = int.TryParse(config.MapGet(KeyBackget).Trim(), out var backget) && backget > 0
            ? Math.Clamp(backget, 1, 1000)
            : DefaultBackgetCount;
        if (config.MapGet(KeyBackget).Trim().Length == 0)
        {
            config.WriteFileMap(KeyBackget, _backgetCount.ToString());
            Logger.Log(LogLevel.Info, $"[CONFIG]{KeyBackget} 未配置,已写入默认值 {_backgetCount}(玩家返回查询条数)");
        }

        MessageArchive.SetKeepDays(_srvMsgDays);
        MessageArchive.Load();

        // 官方验证第二层:config.ini 的 officialcheck,默认 1(开)= 对端不是官方服务端就断开
        _officialCheck = config.MapGet(KeyOfficialCheck).Trim() != "0";
        Logger.Log(LogLevel.Info,
            $"[官方验证] 对端校验:{(_officialCheck ? "开(验不过就断开)" : "关(只警告,继续连接)")}(config 里 {KeyOfficialCheck}=0 可关)");

        // 客户端接入端口:ZXMC / ZXMM 连这里
        var czport = AskIfMissing(config, KeyCzport,
            "[CONFIG]输入客户端接入端口czport(ZXMC/ZXMM 连这里,1-65535)",
            value => ushort.TryParse(value, out var n) && n > 0,
            fallback: DefaultCzPort);

        // 端口落到静态字段:监听和报文方法都要用(报文里要把端口回给客户端)
        _czport = czport;

        return (config, serverName, connectOS);
    }

    /// <summary>子服务端入口:banner → 读配置 → 起监听 → 挂连接事件 → 连官方 → 起命令线程。</summary>
    static async Task Main(string[] args)
    {
        // 主线程起名 Main:日志里才会显示 [Program/Main] 这种标签
        Thread.CurrentThread.Name ??= "Main";
        // 先打 banner 和版本,再干活
        PrintBanner();
        Console.ForegroundColor = ConsoleColor.Green;
        OpenLog();

        // 读 config.ini:名称 / 服务端码 / 是否连官方 / 端口 / 消息天数,缺项会问一次并写回
        var (config, serverName, connectOS) = LoadConfig();

        // ================= 启动监听:ZXMC / ZXMM 都连这个端口 =================
        _server = new TcpServer(IPAddress.Any, ushort.Parse(_czport));

        // 有人接进来:先中立记录身份未知,再主动报上"我是 ZXMS",让客户端按模式校验
        _server.Connected += async conn =>
        {
            Logger.Log(LogLevel.Info, $"[网络] 连接接入 {conn.RemoteEndPoint}(等待握手识别身份)");

            try
            {
                // 名称每次接入现读一次:name 命令改的是 config,改了之后新接入的客户端立刻拿到新名称
                var liveName = ResolveName(config);
                if (liveName.Length == 0)
                {
                    liveName = serverName;
                }

                await conn.WriteAsync(new Packet(0, ServerIdent, Encoding.UTF8.GetBytes("ZXMS")));
                await conn.WriteAsync(new Packet(0, Protocol.ServerName, Encoding.UTF8.GetBytes(liveName)));
                var squareName = config.MapGet(KeySquareName).Trim();
                if (squareName.Length == 0)
                {
                    squareName = "服务器广场";
                }

                await conn.WriteAsync(new Packet(0, Protocol.ServerConfig,
                    Encoding.UTF8.GetBytes($"msgdays={_msgDays}\nsquarename={squareName}")));
                Logger.Log(LogLevel.Info,
                    $"[ZXMS] 已发送身份包(200):ZXMS,名称(201):{liveName},消息保留(202):{_msgDays} 天");
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[ZXMS] 发送身份包失败: " + ex.Message);
            }
        };

        // 断开:登记表里还留着这条数据(身份 / 模式 / 在线时长),先取出来再记审计
        _server.Disconnected += conn =>
        {
            var info = _server?.Registry.Find(conn.Id);
            var role = info?.Role ?? "未识别";
            var mode = info?.Mode ?? "-";

            Logger.Log(LogLevel.Info, $"[{role}] 断开 {conn.RemoteEndPoint}");
            ClientLog.Offline(role, mode, info?.Address ?? conn.RemoteEndPoint?.ToString() ?? "?",
                info?.Online ?? TimeSpan.Zero, info?.Packets ?? 0);
        };

        // 报文分派:上线 / 加密握手 / 登录 / 注册 / 头像 / 卡片动作 / 补发 / 公告
        _server.AddHandler(HandlePacketAsync);

        try
        {
            _server.Open();
            Logger.Log(LogLevel.Info, $"[ZXMS] 已开始监听 0.0.0.0:{_czport}(czport,ZXMC/ZXMM 从这里接入)");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Fatal, "[ZXMS] 监听失败: " + ex.Message);
            Environment.Exit(1);
        }

        // ================= 按配置连接官方服务端 =================
        // connectOS:1 = 连接 ZXMOS(默认),0 = 不连接
        if (connectOS == "1")
        {
            await ConnectOServerAsync(config);
        }

        // 登录确认超时:五分钟没人点就回 ZXMM timeout,并提醒客户端
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                foreach (var pair in PendingLogins.ToArray())
                {
                    if ((DateTime.Now - pair.Value.At).TotalMinutes < 5 || !PendingLogins.TryRemove(pair.Key, out var late))
                    {
                        continue;
                    }

                    LoginStore.Log($"[登录超时] 玩家 {late.Player},请求 {pair.Key},超过 5 分钟未确认");
                    var origin = _server?.Connections.FirstOrDefault(c => c.Id == late.OriginConnId);
                    if (origin is not null)
                    {
                        try { await origin.SendAsync(Protocol.LoginResult, $"timeout|{late.Player}", 0, "ZXMM"); }
                        catch { }
                    }

                    await SendToAccountAsync(late.AccountId, Protocol.LoginTimeout, $"{pair.Key}|{late.Player}");
                }
            }
        });

        var cmd = new CommandHandler();//命令线程

        // 本端专属命令(共享类库里没有),必须在 Start() 之前挂上
        cmd.RegisterZxmsCommands(
            osProvider: () => _os,
            osTargetProvider: () => config.MapGet(KeyOzip) + ":" + config.MapGet(KeyOzport),
            osNameProvider: () => config.MapGet(KeyOsName),
            identity: new ZxmsIdentity
            {
                Name = () => ResolveName(config),
                LinkHint = () => config.MapGet(KeyLinkName).Trim() != "0" ? "与官方账号名一致" : "独立设置",
                User = () => config.MapGet(KeyOsUser).Trim(),
                ServerCode = () => config.MapGet(KeyServerCode).Trim(),
                SetName = value =>
                {
                    config.WriteFileMap(KeyName, value);

                    if (config.MapGet(KeyLinkName).Trim() != "0")
                    {
                        Logger.Log(LogLevel.Warning,
                            $"[配置] 已写入 name={value},但 {KeyLinkName}=1(与官方账号名联动),玩家看到的仍是账号名 " +
                            $"{config.MapGet(KeyOsUser).Trim()};要单独命名请先 {KeyLinkName} 0(改完重启)");
                        return;
                    }

                    Logger.Log(LogLevel.Info, $"[配置] 本端名称已改为 {value}");
                },
            },
            backgetCountProvider: () => _backgetCount,
            osMegBack: argsText => QueryOsMegBackAsync(argsText).GetAwaiter().GetResult(),
            clientCountProvider: () => _server?.Connections.Count ?? 0,
            registryProvider: () => _server?.Registry,
            accountProvider: () => Accounts,
            osReconnect: () => ConnectOServerAsync(config),
            osRename: (newName, newPassword) => RenameOsAccountAsync(config, newName, newPassword),
            // 名称现读:name 命令改完,之后发的消息来源标签立刻跟着变
            setMsgDays: SetMsgDaysLocal,
            setSrvMsgDays: SetSrvMsgDaysLocal,
            localNotice: card => NoticeLocal(ResolveName(config), card),
            localPush: (target, card) => PushLocal(ResolveName(config), target, card),
            banProvider: () => Bans,
            serverAccountNameProvider: () => config.MapGet(KeyOsUser).Trim(),
            enterConfig: Command.ConfigMode.Enter);

        // 配置模式:进入后每一行都交给它,不再当命令分发
        cmd.LineInterceptor = line => Command.ConfigMode.Handle(line);
        cmd.IsConfigMode = () => Command.ConfigMode.Active;

        cmd.Start();
        Logger.Log(LogLevel.Info, "启动成功");
        await Task.Delay(Timeout.Infinite);
    }

    /// <summary>
    /// 连接官方服务端(ZXMOS):先关掉旧连接 → 读配置 → 建连接 → 报上线 → 校验对方身份。
    /// 启动时调用一次,"net reconnect" 命令也调它。
    /// </summary>
    /// <summary>本 ZXMS 在 ZXMOS 上注册的账号名(config.ini 的 osuser)。</summary>
    private const string KeyOsUser = "osuser";

    /// <summary>该账号的密码密文(config.ini 的 oshash;明文不落盘)。</summary>
    private const string KeyOsHash = "oshash";

    /// <summary>该账号的等级(config.ini 的 oslevel;ZXMOS 上子服务端账号是 Admin / System)。</summary>
    private const string KeyOsLevel = "oslevel";

    /// <summary>
    /// 本端自己发给 ZXMOS 的账号请求:包 Id → 等回执的等待者(601 登录 / 603 注册 / 612 改名)。
    /// 按包 Id 存字典,登录与改名各等各的,不会互相把等待者顶掉。
    /// </summary>
    private static readonly ConcurrentDictionary<ulong, TaskCompletionSource<string>> OsAcks = new();

    /// <summary>本端自己发的包 Id 计数(从 10 万起,和"替客户端中转"的 relay Id 段分开)。</summary>
    private static ulong _osOwnPacketId = 100_000;

    /// <summary>
    /// 用 ZXMS 账号登录 ZXMOS:没注册过就顺手注册(ZXMOS 给子服务端账号 Admin 级,并记下本机 IP)。
    /// 账号 / 密码没配过时在这里问一次,密码算成密文写回 config.ini。
    /// </summary>
    private static async Task LoginOServerAsync(TcpClient client, ZFile config)
    {
        var user = AskOsUser(config);

        // 密文没有就不问明文存明文,而是问一次、当场算成密文存下来
        var hash = config.MapGet(KeyOsHash).Trim();
        if (!PasswordHash.IsHash(hash))
        {
            if (hash.Length > 0)
            {
                Logger.Log(LogLevel.Warning,
                    $"[CONFIG]{KeyOsHash} 格式不对(应是 64 位十六进制,当前 {hash.Length} 个字符),请重新输入密码");
            }

            hash = AskPasswordHash(config, user);
        }

        // 服务端账号没有等级分类,用占位符
        var level = Protocol.ServerAccountLevel;

        // 带上本机机器码:被拉黑的子服务端换个 config(改名 / 换 IP)也解不开
        var machine = MachineCode.Value;
        var serverCode = config.MapGet(KeyServerCode).Trim().ToLowerInvariant();
        Logger.Log(LogLevel.Info, $"[ZXMOS] 本机机器码:{machine}(设备)服务端码:{serverCode}(实例)");

        // 载荷:名称|密文|等级|端|机器码|服务端码
        var reply = await SendAccountAsync(client, Protocol.AccountLogin, $"{user}|{hash}|{level}|zxms|{machine}|{serverCode}");

        if (reply.StartsWith("err|") && reply.Contains("不存在"))
        {
            Logger.Log(LogLevel.Info, $"[ZXMOS] 账号 {user} 还没注册过,正在注册…");
            reply = await SendAccountAsync(client, Protocol.AccountRegister, $"{user}|{hash}|{machine}|{serverCode}");
        }

        if (reply.StartsWith("ok|"))
        {
            Logger.Log(LogLevel.Info, $"[ZXMOS] 账号认证通过:{reply[3..]}");

            // 第二层:向官方服务端要一张官方证明(登录成功才要,免得谁都能来收集许可证)
            await CheckOfficialAsync(client, config);
        }
        else
        {
            Logger.Log(LogLevel.Error, "[ZXMOS] 账号认证失败:" + (reply.StartsWith("err|") ? reply[4..] : reply));
        }
    }

    /// <summary>
    /// 官方验证第二层:要证明 + 校验。<b>ZXMS 只连官方服务端</b>(验不过就断开)。
    /// 运行期放行口只有两个:运维用 config.ini 的 <c>officialcheck=0</c>;
    /// 自动化测试用环境变量 <c>ZXM_ALLOW_UNOFFICIAL=1</c>(见 <see cref="OfficialGuard.DevBypass"/>)。
    /// 编译期已经没有"不验的构建"了。
    /// </summary>
    private static async Task CheckOfficialAsync(TcpClient client, ZFile config)
    {
        var verdict = await AskOfficialProofAsync(client);

        if (verdict.Ok)
        {
            Logger.Log(LogLevel.Info,
                $"[官方验证] 对端是官方服务端:{verdict.Name}(序列号 {verdict.Serial}," +
                $"{(verdict.Expires == DateTime.MinValue ? "永久" : $"有效期到 {verdict.Expires:yyyy-MM-dd}")})");
            return;
        }

        Logger.Log(LogLevel.Warning, $"[官方验证] 对端不是官方服务端({verdict.Code}):{verdict.Detail}");

        if (!_officialCheck)
        {
            Logger.Log(LogLevel.Warning, "[官方验证] 本端配置了 officialcheck=0,已放行(继续连接)");
            return;
        }

        if (OfficialGuard.DevBypass)
        {
            Logger.Log(LogLevel.Warning, "[官方验证] 环境变量 ZXM_ALLOW_UNOFFICIAL=1(测试/联调专用),只警告不断开");
            return;
        }

        Logger.Log(LogLevel.Error, "[官方验证] 子服务端只连官方服务端 —— 断开这条连接");
        await client.CloseAsync();
        _os = null;
    }

    /// <summary>发一个 nonce 等对方的证明(521,超时 5 秒)。</summary>
    private static async Task<OfficialVerdict> AskOfficialProofAsync(TcpClient client)
    {
        var nonce = OfficialHandshake.NewNonce();
        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 本端同时只连一台官方服务端(重连会把旧连接关掉),所以这里不用为并发加锁
        _officialNonce = nonce;
        _officialWait = waiter;

        try
        {
            await client.WriteAsync(new Packet(0, Protocol.OfficialChallenge, Encoding.UTF8.GetBytes(nonce)));
        }
        catch (Exception ex)
        {
            _officialWait = null;
            return new OfficialVerdict { Ok = false, Code = "send-failed", Detail = ex.Message };
        }

        var finished = await Task.WhenAny(waiter.Task, Task.Delay(5000));
        _officialWait = null;

        if (finished != waiter.Task)
        {
            return new OfficialVerdict { Ok = false, Code = "timeout", Detail = "对方没有回应官方证明请求(5 秒超时)" };
        }

        return OfficialHandshake.Verify(waiter.Task.Result, nonce);
    }

    /// <summary>把"替客户端中转"的登录 / 注册成功回执(ok|名称|ID|等级)登记到那条客户端连接上。</summary>
    private static void RegisterRelayedAccount(Guid connectionId, string reply)
    {
        if (!reply.StartsWith("ok|"))
        {
            return;
        }

        var fields = reply.Split('|');
        var name = fields.Length > 1 ? fields[1].Trim() : "";
        var level = fields.Length > 3 ? fields[3].Trim() : Protocol.RegisterLevel;

        if (name.Length == 0)
        {
            return;
        }

        _server?.Registry.SetAccount(connectionId, name, level);
        Logger.Log(LogLevel.Info, $"[账号] 本服在线名单已登记中转账号:{name}({level})");
    }

    /// <summary>发账号包并等回执(超时 5 秒)。每个请求一个包 Id,回执不会被别的请求认领走。</summary>
    private static async Task<string> SendAccountAsync(TcpClient client, ushort type, string payload)
    {
        var packetId = Interlocked.Increment(ref _osOwnPacketId);
        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        OsAcks[packetId] = waiter;

        try
        {
            await client.WriteAsync(new Packet(packetId, type, Encoding.UTF8.GetBytes(payload)));
        }
        catch (Exception ex)
        {
            OsAcks.TryRemove(packetId, out _);
            return "err|" + ex.Message;
        }

        var finished = await Task.WhenAny(waiter.Task, Task.Delay(5000));
        OsAcks.TryRemove(packetId, out _);

        return finished == waiter.Task ? waiter.Task.Result : "err|ZXMOS 没有回执(超时)";
    }

    /// <summary>
    /// 改本端在官方服务端的账号名(顺带改密码):发 611 → 等 612 → 成功才写本地 config。
    /// 顺序不能反:先写本地的话,服务器一旦拒绝,config 里就留了一个对不上的账号,下次接入就认证不过去。
    /// </summary>
    private static async Task<string> RenameOsAccountAsync(ZFile config, string newName, string newPassword)
    {
        var os = _os;
        if (os is null || !os.IsConnected)
        {
            return "err|官方服务端未连接,请先 net reconnect";
        }

        var oldName = config.MapGet(KeyOsUser).Trim();
        if (oldName.Length == 0)
        {
            return "err|本端还没有服务端账号(连上官方服务端会自动注册)";
        }

        // 新密码当场算成密文(盐用新账号名):明文不出本机,config 里也只有密文
        var newHash = PasswordHash.Hash(newName, newPassword);

        // 载荷:旧名|新名|新密文|机器码|服务端码
        var reply = await SendAccountAsync(os, Protocol.AccountRename,
            $"{oldName}|{newName}|{newHash}|{MachineCode.Value}|{config.MapGet(KeyServerCode).Trim().ToLowerInvariant()}");

        if (!reply.StartsWith("ok|"))
        {
            return reply;
        }

        config.WriteFileMap(KeyOsUser, newName);
        config.WriteFileMap(KeyOsHash, newHash);
        Logger.Log(LogLevel.Info, $"[CONFIG]osuser 已改为 {newName}(oshash 同步更新,只存密文)");
        return "ok|" + newName;
    }

    /// <summary>
    /// 问本端在 ZXMOS 上的账号名。**控制台命令不算数**:以前有人在提问里输了 `srv show`,
    /// 结果官方端账号就叫 "srv show" —— 这里直接拒掉并重问(带空格的名字也不收)。
    /// </summary>
    private static string AskOsUser(ZFile config)
    {
        var current = config.MapGet(KeyOsUser).Trim();
        if (current.Length >= 2)
        {
            return current;
        }

        Logger.Log(LogLevel.Info,
            $"[CONFIG]输入本 ZXMS 在官方服务端注册的账号名(2-32 字符、不要带空格、不是控制台命令;回车用 {Dns.GetHostName()})");

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input))
            {
                input = Dns.GetHostName();
            }

            if (Command.ZxmsCommands.LooksLikeConsoleCommand(input))
            {
                Logger.Log(LogLevel.Warning,
                    $"[CONFIG]「{input}」看起来是控制台命令,不是账号名 —— 这里要的是账号名(例如 ZXMS-Main);请重新输入");
                continue;
            }

            if (input.Length < 2 || input.Length > 32 || input.Contains(' '))
            {
                Logger.Log(LogLevel.Warning, "[CONFIG]账号名要 2-32 个字符且不能带空格,请重新输入");
                continue;
            }

            config.WriteFileMap(KeyOsUser, input);
            Logger.Log(LogLevel.Info, $"[CONFIG]osuser 已设置为 {input}");
            return input;
        }

        var fallback = Dns.GetHostName();
        config.WriteFileMap(KeyOsUser, fallback);
        Logger.Log(LogLevel.Warning, $"[CONFIG]多次输入不合法,已用主机名 {fallback} 当账号名");
        return fallback;
    }

    /// <summary>问一次密码并算成密文写回 config.ini(明文只在内存里过一下)。</summary>
    private static string AskPasswordHash(ZFile config, string user)
    {
        string? input = null;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            Logger.Log(LogLevel.Info, $"[CONFIG]输入账号 {user} 的密码(只保存密文,至少 4 位;这里要密码,不是命令)");
            input = Console.ReadLine();

            if (string.IsNullOrEmpty(input))
            {
                break;
            }

            input = input.Trim();

            if (Command.ZxmsCommands.LooksLikeConsoleCommand(input))
            {
                Logger.Log(LogLevel.Warning,
                    $"[CONFIG]「{input}」看起来是控制台命令,不是密码 —— 请重新输入密码");
                input = null;
                continue;
            }

            if (input.Equals(user, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log(LogLevel.Warning, "[CONFIG]密码不能和账号名一样,请重新输入");
                input = null;
                continue;
            }

            if (input.Length < 4)
            {
                Logger.Log(LogLevel.Warning, "[CONFIG]密码至少 4 位,请重新输入");
                input = null;
                continue;
            }

            break;
        }

        if (string.IsNullOrEmpty(input))
        {
            // 管道 / 重定向 / 非交互启动:随机生成一个,打在日志里让管理员能看到
            input = Guid.NewGuid().ToString("N")[..16];
            Logger.Log(LogLevel.Warning, "[CONFIG]没有读到合法密码,已随机生成一个(请改用 osuser 修改): " + input);
        }

        var hash = PasswordHash.Hash(user, input);
        config.WriteFileMap(KeyOsHash, hash);
        Logger.Log(LogLevel.Info, "[CONFIG]oshash 已保存(密文)");
        return hash;
    }

    /// <summary>
    /// 转发到 ZXMOS 的账号请求:ZXMOS 回执的包 Id → (哪个 ZXMC 连接, 它原来的包 Id)。
    /// ZXMS 只是中转,ZXMC 完全不用知道账号到底在哪边校验的。
    /// </summary>
    private static readonly ConcurrentDictionary<ulong, (Guid Connection, ulong PacketId)> AccountRelay = new();

    /// <summary>
    /// 转给 ZXMOS 的补发请求:包 Id → (哪条 ZXMC 连接要的, 它原来的包 Id, 本服先给了几条)。
    /// ZXMOS 的 504 一条条按这个 Id 找到人转过去;506 到了把"官方条数 + 本服条数"合成一条收尾回给客户端。
    /// </summary>
    private static readonly ConcurrentDictionary<ulong, (Guid Connection, ulong PacketId, int LocalSent)> HistoryRelay = new();

    /// <summary>
    /// 官方验证第二层:等官方服务端回证明(521)的等待者 + 这次发出去的 nonce。
    /// 本端同时只连一台官方服务端,所以两个字段够用。
    /// </summary>
    private static TaskCompletionSource<string>? _officialWait;

    /// <summary>和 ZXMOS 协商密钥时等 381 用。</summary>
    private static TaskCompletionSource<byte[]>? _osSessionWait;

    private static string _officialNonce = "";

    private static ulong _relayId = 1000;

    /// <summary>
    /// 这个账号请求该在哪个端处理:
    ///   客户端明确指定了端(zxms / zxmos)就听它的;
    ///   没指定:玩家账号(Player)官方优先 → zxmos;管理账号按 ZXMS 处理。
    /// 载荷第 4 个字段就是"端"("名称|密文|等级|端")。
    /// </summary>
    private static string DecideAccountSide(string level, string side)
    {
        side = side.Trim().ToLowerInvariant();
        if (side is "zxms" or "zxmos")
        {
            return side;
        }

        return level == Protocol.RegisterLevel ? "zxmos" : "zxms";
    }

    /// <summary>把账号包转给 ZXMOS;没连上就返回 false(调用方决定退回本地还是报错)。</summary>
    private static async Task<bool> TryRelayAccountAsync(ServerboundPacket packet, ushort osType, string tail = "")
    {
        var os = _os;
        if (os is null || !os.IsConnected)
        {
            return false;
        }

        var relayId = Interlocked.Increment(ref _relayId);
        AccountRelay[relayId] = (packet.Connection.Id, packet.Id);

        try
        {
            var payload = tail.Length == 0 ? packet.Data : [.. packet.Data, .. Encoding.UTF8.GetBytes(tail)];
            await os.WriteAsync(new Packet(relayId, osType, payload));
            Logger.Log(LogLevel.Info, $"[ZXMOS] 已转发账号请求 type={osType}(relay={relayId})给官方服务端");
            return true;
        }
        catch (Exception ex)
        {
            AccountRelay.TryRemove(relayId, out _);
            Logger.Log(LogLevel.Error, "[ZXMOS] 转发账号请求失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>补发包的日志摘要:504 是一张卡片,506 只有一个条数。</summary>
    private static string Brief(ushort type, string text)
        => type == Protocol.MessageHistoryDone ? $"{text.Trim()} 条" : MessageCard.Summary(text);

    /// <summary>从卡片里取来源标签(解析不出来就是空串)。</summary>
    private static string CardSource(string card)
    {
        MessageCard.TryParse(card, out var fields);
        return fields.Source;
    }

    /// <summary>
    /// 把本服自己的存档逐条(504)回给这条连接,不在这里收尾;返回条数。
    /// <paramref name="forClient"/> = 这次是替玩家要的:点名给本端自己的(<c>@server:</c>)消息就不给玩家;
    /// 点名给本端名下客户端的(<c>@clients:</c>)给;别的玩家的定向推送只给他本人。
    /// </summary>
    private static async Task<int> SendLocalHistoryAsync(ServerboundPacket packet, string who, bool forClient, bool skipOfficial)
    {
        var sent = 0;

        // 这个用户已经收过的不再补发(客户端回执 507 之后才记进已发清单)
        var userId = Accounts.Find(who)?.Id ?? 0;

        foreach (var entry in MessageArchive.Recent())
        {
            if (userId > 0 && SentMessages.Contains(userId, entry.Id))
            {
                continue;
            }

            // 官方端连着时,官方转下来的那批由官方端自己补,不再从这里发一遍
            if (skipOfficial && entry.Source.StartsWith("ZXMOS-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = entry.Target;

            if (target.StartsWith("@server:", StringComparison.OrdinalIgnoreCase))
            {
                if (forClient)
                {
                    continue;
                }
            }
            else if (target.StartsWith("@clients:", StringComparison.OrdinalIgnoreCase))
            {
                // 点名给本端名下所有客户端的:谁都该收到
            }
            else if (target.Length > 0 && !target.Equals("@all", StringComparison.OrdinalIgnoreCase)
                     && !target.Equals(who, StringComparison.OrdinalIgnoreCase))
            {
                // 别的玩家的定向推送
                continue;
            }

            try
            {
                await packet.SendAsync(Protocol.MessageHistory, entry.Card, packet.Id);
                sent++;
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[消息] 本服补发失败: " + ex.Message);
                break;
            }
        }

        return sent;
    }

    /// <summary>
    /// 把服务端消息发(转)给本服的 ZXMC 客户端:给了 <paramref name="target"/> 就按登录账号过滤
    /// (不区分大小写);只发给已握手的 ZXMC,ZXMM / 未识别连接不投。返回投递条数。
    /// </summary>
    private static async Task<int> SendToLocalClientsAsync(ushort type, string payload, string? target)
    {
        var server = _server;
        if (server is null)
        {
            return 0;
        }

        var sent = 0;
        foreach (var conn in server.Connections)
        {
            var info = server.Registry.Find(conn.Id);
            if (info?.Role != "ZXMC")
            {
                continue;
            }

            if (target is { Length: > 0 } && !string.Equals(info.User, target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                await conn.SendAsync(type, payload, 0, "ZXMC");
                sent++;
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, $"[消息] 发给 {info.Address} 失败: {ex.Message}");
            }
        }

        return sent;
    }

    /// <summary>
    /// 把玩家的卡片动作转交给官方服务端(负载前面加一段账号,官方端才知道是谁点的)。
    /// 官方服务端不回执 —— 这条链路的回执只到"已转交",玩家那边已经拿到 ZXMS 的回话了。
    /// </summary>
    private static async Task<bool> TryForwardCardActionAsync(string account, string actionPayload)
    {
        var os = _os;
        if (os is null || !os.IsConnected)
        {
            return false;
        }

        try
        {
            var payload = account + "|" + actionPayload;
            await os.WriteAsync(new Packet(0, Protocol.CardAction, Encoding.UTF8.GetBytes(payload)));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[消息] 转交卡片动作失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>控制台 msgdays:改本服的消息保留天数(写 config + 立刻用 202 广播给在线客户端)。</summary>
    private static int SetMsgDaysLocal(int days)
    {
        days = Math.Clamp(days, 1, 365);
        _msgDays = days;
        _config?.WriteFileMap(KeyMsgDays, days.ToString());

        // 在线的客户端立刻知道新值(它们据此决定本地留多久)
        return SendToLocalClientsAsync(Protocol.ServerConfig, $"msgdays={days}", null).GetAwaiter().GetResult();
    }

    /// <summary>配置模式里显示用的当前值(只读项也一样显示)。</summary>
    internal static string ConfigValue(string key)
    {
        var config = _config;
        if (config is null)
        {
            return "";
        }

        return key.ToLowerInvariant() switch
        {
            "name" => ResolveName(config),
            "linkname" => config.MapGet(KeyLinkName).Trim() is { Length: > 0 } link ? link : DefaultLinkName,
            "connectos" => config.MapGet(KeyConnectOs).Trim() is { Length: > 0 } conn ? conn : DefaultConnectOs,
            "czport" => config.MapGet(KeyCzport).Trim(),
            "ozip" => config.MapGet(KeyOzip).Trim(),
            "ozport" => config.MapGet(KeyOzport).Trim(),
            "osuser" => config.MapGet(KeyOsUser).Trim(),
            "msgdays" => _msgDays.ToString(),
            "srvmsgdays" => _srvMsgDays.ToString(),
            "officialcheck" => _officialCheck ? "1" : "0",
            "ozname" => config.MapGet(KeyOsName).Trim(),
            "servercode" => config.MapGet(KeyServerCode).Trim(),
            "oshash" => config.MapGet(KeyOsHash).Trim(),
            "configver" => config.MapGet(KeyConfigVer).Trim(),
            _ => "",
        };
    }

    /// <summary>
    /// 配置模式里改一个键:校验 → 写 config.ini → 能立刻生效的立刻生效。返回空串表示成功。
    /// </summary>
    internal static string SetConfigItem(string key, string value)
    {
        var config = _config;
        if (config is null)
        {
            return "配置还没加载完";
        }

        value = value.Trim();

        switch (key.ToLowerInvariant())
        {
            case "name":
                if (value.Length < 1 || value.Length > 32)
                {
                    return "名称要 1-32 个字符";
                }

                config.WriteFileMap(KeyName, value);
                if (config.MapGet(KeyLinkName).Trim() != "0")
                {
                    return "";
                }

                Logger.Log(LogLevel.Info, "[配置] 名称已改,新接入的客户端立刻用新名称");
                return "";

            case "linkname":
                if (value is not ("0" or "1"))
                {
                    return "只能填 1(与账号名一致)或 0(分开)";
                }

                config.WriteFileMap(KeyLinkName, value);
                return "";

            case "connectos":
                if (value is not ("0" or "1"))
                {
                    return "只能填 1(连接)或 0(不连接)";
                }

                config.WriteFileMap(KeyConnectOs, value);
                Logger.Log(LogLevel.Info, "[配置] 是否连接官方端在下次启动时生效(现在可用 net reconnect 手动连)");
                return "";

            case "czport":
                if (!ushort.TryParse(value, out var cz) || cz == 0)
                {
                    return "端口范围 1-65535";
                }

                config.WriteFileMap(KeyCzport, value);
                Logger.Log(LogLevel.Warning, "[配置] 客户端端口重启后生效");
                return "";

            case "ozip":
                if (!HostText.IsHost(value))
                {
                    return "要填 IP 或域名";
                }

                config.WriteFileMap(KeyOzip, value);
                Logger.Log(LogLevel.Info, "[配置] 官方端地址下次连接时生效(可用 net reconnect)");
                return "";

            case "ozport":
                if (!ushort.TryParse(value, out var oz) || oz == 0)
                {
                    return "端口范围 1-65535";
                }

                config.WriteFileMap(KeyOzport, value);
                return "";

            case "osuser":
                if (value.Length < 2 || value.Length > 32 || value.Contains(' '))
                {
                    return "账号名要 2-32 个字符且不能带空格";
                }

                if (Command.ZxmsCommands.LooksLikeConsoleCommand(value))
                {
                    return "这看起来是控制台命令,不是账号名";
                }

                config.WriteFileMap(KeyOsUser, value);
                Logger.Log(LogLevel.Warning,
                    "[配置] 只改了账号名;密码还是旧的,而且官方端可能不认 —— 正常改名请用 osuser <新名> <新密码>");
                return "";

            case "oshash":
                if (!PasswordHash.IsHash(value))
                {
                    return "要 64 位十六进制密文";
                }

                config.WriteFileMap(KeyOsHash, value);
                Logger.Log(LogLevel.Info, "[配置] 官方账号密文已写入,下次连接 ZXMOS 时生效");
                return "";

            case "squarename":
                if (value.Length < 1 || value.Length > 24)
                {
                    return "广场名称要 1-24 个字符";
                }

                config.WriteFileMap(KeySquareName, value);
                Logger.Log(LogLevel.Info, "[配置] 服务器广场名称已改为 " + value + "(新接入的客户端立刻生效)");
                return "";

            case "msgdays":
                if (!int.TryParse(value, out var days) || days is < 1 or > 365)
                {
                    return "天数范围 1-365";
                }

                SetMsgDaysLocal(days);
                return "";

            case "srvmsgdays":
                if (!int.TryParse(value, out var srvDays) || srvDays is < 1 or > 365)
                {
                    return "天数范围 1-365";
                }

                SetSrvMsgDaysLocal(srvDays);
                return "";

            case "officialcheck":
                if (value is not ("0" or "1"))
                {
                    return "只能填 1(验不过就断开)或 0(只警告)";
                }

                _officialCheck = value == "1";
                config.WriteFileMap(KeyOfficialCheck, value);
                return "";

            default:
                return $"没有这个配置项:{key}(不能新增)";
        }
    }

    /// <summary>控制台 srvmsgdays:改本服自己存档的保留天数(写 config + 立刻清一次过期)。返回剩余条数。</summary>
    private static int SetSrvMsgDaysLocal(int days)
    {
        days = Math.Clamp(days, 1, 365);
        _srvMsgDays = days;
        _config?.WriteFileMap(KeySrvMsgDays, days.ToString());
        MessageArchive.SetKeepDays(days);
        MessageArchive.Prune();
        return MessageArchive.Count;
    }

    /// <summary>
    /// 控制台 notice:给本服所有客户端发一条公告;返回投递条数。来源打成 ZXMS-&lt;本服名称&gt;。
    /// 先入库拿到本服消息号,再发<b>同一张</b>卡 —— 这样在线收到的和以后补发的能对上(去重靠来源 + 号)。
    /// </summary>
    private static int NoticeLocal(string serverName, CardInput card)
    {
        var (_, text) = MessageArchive.Add(id => BuildCard(card, serverName, id),
            MessageCard.SourceTag("ZXMS", serverName));
        return SendToLocalClientsAsync(Protocol.Notice, text, null).GetAwaiter().GetResult();
    }

    /// <summary>控制台 push:只给本服的某个账号定向推送;返回投递条数。来源同上。</summary>
    private static int PushLocal(string serverName, string target, CardInput card)
    {
        var (_, text) = MessageArchive.Add(id => BuildCard(card, serverName, id),
            MessageCard.SourceTag("ZXMS", serverName), target);
        return SendToLocalClientsAsync(Protocol.Push, MessageCard.BuildPush(target, text), target)
            .GetAwaiter().GetResult();
    }

    /// <summary>把命令读出来的卡片拼成线格式,来源统一带本服名称(ZXMS-&lt;名称&gt;)。</summary>
    private static string BuildCard(CardInput card, string serverName, int id = 0)
        => MessageCard.Build(card.Kind, card.Title, card.Body, MessageCard.SourceTag("ZXMS", serverName),
            card.Primary, card.Secondary, card.Options, id);

    private static async Task ConnectOServerAsync(ZFile config)
    {
        // 重连时旧对象不能复用,先关掉
        if (_os is not null)
        {
            await _os.CloseAsync();
            _os = null;
        }

        // 连官方端时还能改一次本端名称:只有"还是默认名"时才问,免得每次重连都打扰
        var nameNow = ResolveName(config);
        if (nameNow.Length == 0 || nameNow == DefaultName)
        {
            Logger.Log(LogLevel.Info,
                $"[CONFIG]本服务端显示名称当前是「{(nameNow.Length == 0 ? DefaultName : nameNow)}」" +
                "(会下发给客户端);回车沿用,或直接输入新名称:");
            var nameInput = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(nameInput) && nameInput.Length <= 32)
            {
                config.WriteFileMap(KeyName, nameInput);
                Logger.Log(LogLevel.Info, $"[CONFIG]{KeyName} 已设置为 {nameInput}(新接入的客户端立刻用新名称)");
            }
        }

        // 官方服务端(ZXMOS)的地址 / 端口:没有或不合法就问
        // 地址允许写域名(官方端对外就是 zxm.zincms.top),不限于 IP
        var ozip = AskIfMissing(config, KeyOzip,
            "[CONFIG]输入官方服务端地址ozip(IP 或域名,例如 zxm.zincms.top)",
            value => HostText.IsHost(value),
            fallback: "127.0.0.1");

        var ozport = AskIfMissing(config, KeyOzport,
            "[CONFIG]输入官方服务端端口ozport(1-65535)",
            value => ushort.TryParse(value, out var n) && n > 0,
            fallback: DefaultOsPort);

        var osPort = ushort.Parse(ozport);

        // 传主机名进去:是 IP 就直接连,是域名就连的时候做 DNS(见 TcpClient)
        var client = new TcpClient(ozip, osPort);

        client.AddHandler(async packet =>

        {
            switch (packet.Type)
            {
                // ZXMOS 自报身份:必须真的是官方服务端
                case ServerIdent:
                {
                    var role = Encoding.UTF8.GetString(packet.Data).Trim();
                    if (role == "ZXMOS")
                    {
                        Logger.Log(LogLevel.Info, "[ZXMOS] 身份校验通过:ZXMOS");
                    }
                    else
                    {
                        Logger.Log(LogLevel.Error, $"[ZXMOS] 身份校验不通过:对方自报 {role},不是官方服务端");
                    }

                    break;
                }

                // 官方服务端自报名称(201):记下来给 net status 显示,也写进 config
                case Protocol.ServerName:
                {
                    var osName = Encoding.UTF8.GetString(packet.Data).Trim();
                    config.WriteFileMap(KeyOsName, osName);
                    Logger.Log(LogLevel.Info, $"[ZXMOS] 服务端名称:{osName}");
                    break;
                }

                // 官方验证第二层:对方的证明(521)—— 交给正在等的那次校验
                case Protocol.OfficialProof:
                    Logger.Log(LogLevel.Info, "[官方验证] 收到对方的证明");
                    _officialWait?.TrySetResult(Encoding.UTF8.GetString(packet.Data));
                    break;

                // ZXMOS 确认握手
                // 会话加密:ZXMOS 回的公钥(381)
                case Protocol.SessionHelloAck:
                    _osSessionWait?.TrySetResult(packet.Data);
                    break;

                case SBack:
                    Logger.Log(LogLevel.Info, "[ZXMOS] 握手成功: " + Encoding.UTF8.GetString(packet.Data));
                    break;

                // ZXMOS 移交过来的用户账号(拉黑前移交)
                case Protocol.AccountMigrate:
                {
                    var lines = Encoding.UTF8.GetString(packet.Data).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var ok = 0;
                    foreach (var line in lines)
                    {
                        var f = line.Trim().Split('|');
                        if (f.Length >= 2 && Accounts.Add(f[0].Trim(), f[1].Trim(),
                                f.Length > 2 && Accounts.IsLevel(f[2].Trim()) ? f[2].Trim() : Protocol.RegisterLevel, out _, out _))
                        {
                            ok++;
                        }
                    }

                    Logger.Log(LogLevel.Info, $"[账号] 官方服务端移交 {lines.Length} 个账号,新建 {ok} 个");
                    break;
                }

                // ZXMOS 的账号回执(601 登录 / 603 注册 / 612 改名)
                case Protocol.AccountLoginAck:
                case Protocol.AccountRegisterAck:
                case Protocol.AccountRenameAck:
                {
                    var text = Encoding.UTF8.GetString(packet.Data).Trim();

                    // ① 是替某个 ZXMC 转发的 → 用它原来的包 Id 转回去
                    if (AccountRelay.TryRemove(packet.Id, out var pending))
                    {
                        var target = _server?.Connections.FirstOrDefault(c => c.Id == pending.Connection);
                        if (target is not null)
                        {
                            await target.WriteAsync(new Packet(pending.PacketId, packet.Type, packet.Data));
                            Logger.Log(LogLevel.Info, $"[ZXMOS] 账号回执已转回客户端:{text}");

                            // 顺带把账号登记到这条客户端连接上:不然本服的在线名单里查不到玩家
                            // (net clients 显示 "-",按账号定向推送也匹配不到人)
                            RegisterRelayedAccount(pending.Connection, text);
                        }
                        else
                        {
                            Logger.Log(LogLevel.Warning, "[ZXMOS] 要转的客户端已断开,回执丢弃:" + text);
                        }

                        break;
                    }

                    // ② 是本端自己发的账号请求(601 登录 / 603 注册 / 612 改名)的回执
                    if (OsAcks.TryRemove(packet.Id, out var own))
                    {
                        Logger.Log(LogLevel.Info, "[ZXMOS] 账号回执: " + text);
                        own.TrySetResult(text);
                        break;
                    }

                    // ③ 谁的都不是(已经超时 / 对方版本对不上):记一条日志,别静默吞掉
                    Logger.Log(LogLevel.Warning, $"[ZXMOS] 收到没人认领的账号回执(包 Id={packet.Id}):{text}");
                    break;
                }

                // 官方服务端补发的历史消息(504 一条 / 506 收尾):按包 Id 找到是哪条客户端连接要的,原样转过去
                case Protocol.MessageHistory:
                case Protocol.MessageHistoryDone:
                {
                    var text = Encoding.UTF8.GetString(packet.Data);

                    if (!HistoryRelay.TryGetValue(packet.Id, out var pending))
                    {
                        Logger.Log(LogLevel.Warning,
                            $"[消息] 收到没人认领的补发包 type={packet.Type}(包 Id={packet.Id}):{Brief(packet.Type, text)}");
                        break;
                    }

                    var target = _server?.Connections.FirstOrDefault(c => c.Id == pending.Connection);
                    if (target is null)
                    {
                        // 要补发的客户端已经断开:这条请求作废,别再挂着(收尾包到了就清记录)
                        if (packet.Type == Protocol.MessageHistoryDone)
                        {
                            HistoryRelay.TryRemove(packet.Id, out _);
                        }

                        Logger.Log(LogLevel.Warning, "[消息] 要补发的客户端已断开,补发包丢弃:" + Brief(packet.Type, text));
                        break;
                    }

                    if (packet.Type == Protocol.MessageHistoryDone)
                    {
                        // 本服那批已经发过了,收尾只回一条:官方条数 + 本服条数
                        HistoryRelay.TryRemove(packet.Id, out _);
                        var remote = int.TryParse(text.Trim(), out var count) ? count : 0;
                        var total = pending.LocalSent + remote;
                        await target.WriteAsync(new Packet(pending.PacketId, Protocol.MessageHistoryDone,
                            Encoding.UTF8.GetBytes(total.ToString())));
                        Logger.Log(LogLevel.Info,
                            $"[消息] 官方服务端补发结束:官方 {remote} 条 + 本服 {pending.LocalSent} 条 = 合计 {total} 条");
                    }
                    else
                    {
                        await target.WriteAsync(new Packet(pending.PacketId, packet.Type, packet.Data));
                        Logger.Log(LogLevel.Info, "[消息] 补发转给客户端:" + MessageCard.Summary(text));
                    }

                    break;
                }

                // 官方服务端下发的公告(500):原样转给本服所有 ZXMC 客户端
                case Protocol.Notice:
                {
                    var card = Encoding.UTF8.GetString(packet.Data);
                    MessageArchive.Add(card, CardSource(card));
                    var sent = await SendToLocalClientsAsync(Protocol.Notice, card, null);
                    Logger.Log(LogLevel.Info,
                        $"[消息] 收到官方服务端公告,已转给本服 {sent} 个客户端 {MessageCard.Summary(card)}");
                    break;
                }

                // 官方服务端下发的定向推送(501):按目标分流 —— 点名叫本端的自己收、@clients: 转给名下全部客户端、
                // 其它按玩家账号过滤(不在本服就丢弃)
                case Protocol.Push:
                {
                    var text = Encoding.UTF8.GetString(packet.Data);

                    if (!MessageCard.TryParsePush(text, out var target, out var card))
                    {
                        Logger.Log(LogLevel.Warning, "[消息] 定向推送的负载格式不对,已忽略:" + text);
                        break;
                    }

                    var selfName = config.MapGet(KeyOsUser).Trim();

                    // ① 官方服务端点名给本端(子服自己):控制台醒目打印,不转给客户端
                    if (target.StartsWith("@server:", StringComparison.OrdinalIgnoreCase))
                    {
                        var who = target[8..].Trim();
                        if (who.Length > 0 && !string.Equals(who, selfName, StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Log(LogLevel.Info, $"[消息] 这条是给子服 {who} 的,不是本端({selfName}),已忽略");
                            break;
                        }

                        MessageArchive.Add(card, CardSource(card), "@server:" + selfName);

                        MessageCard.TryParse(card, out var parsed);   // 解析不出来就是空卡片,照样打印
                        Logger.Log(LogLevel.Info,
                            "┌─ 官方服务端给本端的消息 ────────────────\n" +
                            $"│ {parsed.Title}\n" +
                            $"│ {parsed.Body}\n" +
                            $"└─ 来源 {parsed.Source}   {DateTime.Now:MM-dd HH:mm}");
                        break;
                    }

                    // ② 给本端名下所有客户端
                    if (target.StartsWith("@clients:", StringComparison.OrdinalIgnoreCase))
                    {
                        var who = target[9..].Trim();
                        if (who.Length > 0 && !string.Equals(who, selfName, StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Log(LogLevel.Info, $"[消息] 这是发给子服 {who} 名下客户端的,不是本端({selfName}),已忽略");
                            break;
                        }

                        MessageArchive.Add(card, CardSource(card), "@clients:" + selfName);

                        var all = await SendToLocalClientsAsync(Protocol.Push, text, null);
                        Logger.Log(LogLevel.Info, all > 0
                            ? $"[消息] 官方服务端给本端名下所有客户端的消息,已投给 {all} 个客户端 {MessageCard.Summary(card)}"
                            : $"[消息] 官方服务端给本端名下所有客户端的消息,但本端没有客户端在线 {MessageCard.Summary(card)}");
                        break;
                    }

                    // ③ 全体(@all):本服所有客户端都收
                    if (target.Equals("@all", StringComparison.OrdinalIgnoreCase))
                    {
                        MessageArchive.Add(card, CardSource(card), "@all");

                        var all = await SendToLocalClientsAsync(Protocol.Push, text, null);
                        Logger.Log(LogLevel.Info, all > 0
                            ? $"[消息] 官方服务端的全体推送,已投给本服 {all} 个客户端 {MessageCard.Summary(card)}"
                            : $"[消息] 官方服务端的全体推送,但本服没有客户端在线 {MessageCard.Summary(card)}");
                        break;
                    }

                    // ④ 普通玩法:按玩家账号过滤
                    MessageArchive.Add(card, CardSource(card), target);

                    var sent = await SendToLocalClientsAsync(Protocol.Push, text, target);
                    Logger.Log(LogLevel.Info, sent > 0
                        ? $"[消息] 定向推送 → {target}:已投给本服 {sent} 个客户端 {MessageCard.Summary(card)}"
                        : $"[消息] 定向推送 → {target}:本服没有这个玩家,已忽略");
                    break;
                }

                default:
                    Logger.Log(LogLevel.Info, $"[ZXMOS] 收到 type={packet.Type}: {Encoding.UTF8.GetString(packet.Data)}");
                    break;
            }

        });

        try
        {
            await client.ConnectAsync();
            _os = client;

            // 先和 ZXMOS 协商会话密钥:官方端会拒绝明文链路(包括本端自己的账号登录)
            try
            {
                var hs = new ZincXManagerShared.Security.SessionHandshake();
                _osSessionWait = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                await client.WriteAsync(new Packet(0, Protocol.SessionHello, hs.PublicKey));

                var done = await Task.WhenAny(_osSessionWait.Task, Task.Delay(5000));
                if (done == _osSessionWait.Task)
                {
                    client.Crypto = hs.Complete(_osSessionWait.Task.Result, isServer: false);
                    Logger.Log(LogLevel.Info, "[ZXMOS] 已建立加密会话(ECDH P-256 + AES-256-GCM)");
                }
                else
                {
                    Logger.Log(LogLevel.Warning, "[ZXMOS] 加密协商超时,官方端可能拒绝明文登录");
                }
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[ZXMOS] 加密协商失败: " + ex.Message);
            }
            finally
            {
                _osSessionWait = null;
            }

            await client.WriteAsync(new Packet(1, SOnline, Encoding.UTF8.GetBytes("ZXMS online")));
            Logger.Log(LogLevel.Info, $"[ZXMOS] 已连接 {ozip}:{osPort}");

            // 接入 ZXMOS 必须有 ZXMS 账号:登录(没有则注册)
            await LoginOServerAsync(client, config);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[ZXMOS] 连接失败: " + ex.Message);
            await client.CloseAsync();
        }
    }
}
