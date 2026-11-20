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
using ZincXManagerShared.Update;  // 版本号比较(强制更新)
using ZincXManagerShared.Verify;  // 共享库
using ZincXManagerServer.Command;  // 子服自身

// 与 BCL 的 TcpClient 同名,起个别名
using TcpClient = ZincXManagerShared.Network.TcpClient;

using ZincXManagerServer.Login;  // 归属地查询
using ZincXManager.Server.Zxms;  // 多服数据分离:服务器登记 / 分区分层
using ZincXManagerShared.Login;  // 绑定表 / 持久登录表 / 登录日志

namespace ZincXManagerServer;

internal static partial class Program
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

    // 绑定二次确认:待确认的绑定(确认令牌 → 这一条),用户点了确定(631)才写绑定表
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingBind> PendingBinds = new();

    /// <summary>绑定确认令牌有效期(分钟):超过就作废,客户端要重新输入绑定码。</summary>
    private const int BindConfirmMinutes = 2;

    // 绑定码 → (玩家名, 生成时间)
    private static readonly Dictionary<string, (string Player, DateTime At)> BindCodes = new();

    /// <summary>登录数组(玩家名 → 到期时间):确认通过的玩家默认 5 分钟内直接放行。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> LoginArray = new();

    /// <summary>登录数组有效期(分钟)。</summary>
    private const int LoginArrayMinutes = 5;

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

    /// <summary>ZXMM 执行接口地址,启动时从 config 读。</summary>
    private static string _zxmmApiAddress = DefaultZxmmApi;

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

    /// <summary>ZXMM 执行接口地址(zxmmapi):ZXMS 连过去下发任务,格式 host:port。</summary>
    private const string KeyZxmmApi = "zxmmapi";

    /// <summary>ZXMM 执行接口默认地址。</summary>
    private const string DefaultZxmmApi = "127.0.0.1:21200";

    /// <summary>ZXMM 共享令牌(zxmmtoken):与模组 config 里的 api.token 一致才校验,空 = 不校验。</summary>
    private const string KeyZxmmToken = "zxmmtoken";

    /// <summary>ZXMM 共享令牌的值,启动时从 config 读。</summary>
    private static string _zxmmToken = "";

    /// <summary>ZXMM 执行接口(懒创建)。</summary>
    private static Zxmm.ZxmmApi? _zxmmApi;

    /// <summary>登记表里其它服务器的 ZXMM 专用通道(服务器编号 → 客户端);默认服那台仍旧走 <see cref="_zxmmApi"/>。</summary>
    private static readonly Dictionary<int, Zxmm.ZxmmApi> _zxmmApis = new();
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

    /// <summary>官方端自动重连锁:0 = 空闲,1 = 正在重连(避免和手动 net reconnect 撞车)。</summary>
    private static int _osAutoReconnecting;

    /// <summary>本端 config.ini(命令层改配置时用;启动时赋值)。</summary>
    private static ZFile? _config;

    /// <summary>每连接发包速率上限(config 的 packetrate,默认 30 个/秒,0 = 不限)。</summary>
    private const string KeyPacketRate = "packetrate";

    /// <summary>发包速率上限的默认值:正常客户端一秒顶多几个包,30 已经放宽了。</summary>
    private const int DefaultPacketRate = 30;

    /// <summary>登录/注册失败次数上限(config 的 loginrate,默认 5 分钟内 10 次每个 IP,0 = 不限)。</summary>
    private const string KeyLoginRate = "loginrate";

    /// <summary>登录失败窗口长度(秒)。</summary>
    private const int LoginRateWindowSeconds = 300;

    /// <summary>登录失败次数上限的默认值。</summary>
    private const int DefaultLoginRate = 10;

    /// <summary>强制更新开关(config 的 forceupdate,默认 0 = 关)。开了以后版本低于门槛的 ZXMC 登录被拒、在线的被踢。</summary>
    private const string KeyForceUpdate = "forceupdate";

    /// <summary>强制更新的最低版本(config 的 minversion,形如 <c>b0.3.2a</c>)。 留空 = 用本端自己的版本号(<see cref="AppVersion"/>)。</summary>
    private const string KeyMinVersion = "minversion";

    /// <summary>强制更新开关当前值(启动时读配置,配置模式里改立刻生效)。</summary>
    private static bool _forceUpdate;

    /// <summary>绑定上限(config 的 bindmax):一个 ZXM 账号最多能绑几个 MC 玩家。</summary>
    private const string KeyBindMax = "bindmax";

    /// <summary>绑定上限的默认值:一个账号一个玩家,和以前的规矩一样。</summary>
    private const int DefaultBindMax = 1;

    /// <summary>绑定上限当前值(config 里改立刻生效,0 = 不限)。</summary>
    private static int _bindMax = DefaultBindMax;

    /// <summary>当前绑定上限(控制台 bind 命令要按同一条规矩判断)。</summary>
    internal static int BindMax => _bindMax;

    /// <summary>多服数据分离开关(config 的 data-separation):1 = 数据按 MC 服分目录存,0 = 全部共享。</summary>
    private const string KeyDataSeparation = "data-separation";

    /// <summary>要按服分离的数据键号(config 的 data-separation-list),逗号分隔,键号见 countdataseparationkey.ini。</summary>
    private const string KeyDataSeparationList = "data-separation-list";

    /// <summary>免确认保持天数(config 的 persistdays):同玩家同 IP 在这几天内进服不用再确认。</summary>
    private const string KeyPersistDays = "persistdays";

    /// <summary>免确认保持天数的默认值:30 天,逾期要重新确认一次。</summary>
    private const int DefaultPersistDays = 30;

    /// <summary>每次都要确认(config 的 confirmalways):1 = 不看持久登录表,谁都弹窗。</summary>
    private const string KeyConfirmAlways = "confirmalways";

    /// <summary>机器码必填(config 的 machine.require,默认 1 = 开):
    /// 开 = 客户端取不到主板序列号(机器码为空串)就拒绝登录/注册;
    /// 关 = 允许登录,但不把机器码写进账号,机器码封禁对这类账号无效。</summary>
    private const string KeyMachineRequire = "machine.require";

    /// <summary>机器码必填的默认值:严格 —— 拿不到主板序列号不许登录。</summary>
    private const bool DefaultMachineRequire = true;

    /// <summary>机器码必填当前值(启动时读配置,配置模式里改立刻生效)。</summary>
    private static bool _machineRequire = DefaultMachineRequire;

    /// <summary>免确认保持天数当前值(0 = 不限期)。</summary>
    private static int _persistDays = DefaultPersistDays;

    /// <summary>是不是每次登录都要确认(打开时持久登录表等于失效,并且启动/开启时会被清空)。</summary>
    private static bool _confirmAlways;



    /// <summary>强制更新的门槛版本;空串表示"跟本端版本一致"。</summary>
    private static string _minVersion = "";

    /// <summary>按连接限速:一个连接狂刷包只耗自己的额度,不拖慢别人。</summary>
    private static RateLimiter _packetLimiter = new(DefaultPacketRate);

    /// <summary>按 IP 数登录/注册失败次数:挡住爆破,又不误伤共用同一个外网 IP 的正常玩家。</summary>
    private static FailureThrottle _loginLimiter = new(DefaultLoginRate, LoginRateWindowSeconds);

    /// <summary>被丢弃的包数(超过限速的那些),给控制台 info 看。</summary>
    private static long _droppedPackets;

    /// <summary>限速告警的节流:同一连接不要每丢一个包就打一行日志。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> RateWarnedAt = new();

    /// <summary>监听 ZXMC / ZXMM 的服务端;登记表在它的 Registry 上,中转时也靠它推消息。</summary>
    private static TcpServer? _server;

    /// <summary>客户端最近一次切换到的 MC 服务器编号(连接 → 编号),后续按服取数据时用它;连接断开就清掉。</summary>
    private static readonly Dictionary<TcpConnection, int> _clientServer = new();

    /// <summary>保护 <see cref="_clientServer"/>:收包在分派线程,断开回调在收发线程,两个都会动它。</summary>
    private static readonly object _clientServerLock = new();

    /// <summary>正在持续接收控制台输出的连接(连接 → 服务器编号);断开时清掉,一条连接同时只收一台。</summary>
    private static readonly Dictionary<TcpConnection, int> _consoleTaps = new();

    /// <summary>保护 <see cref="_consoleTaps"/>:分派线程增删,ZXMM 收包线程推送时取快照。</summary>
    private static readonly object _consoleTapLock = new();

    /// <summary>每台服务器当前的持续输出开关(服务器编号 → 模组是否已在推);只在计数从 0 变正、或从正变 0 时翻转,免得同一台重复通知。</summary>
    private static readonly Dictionary<int, bool> _tapState = new();

    /// <summary>账号表(users.ini):注册 / 登录都查它;控制台 account 命令也用它。</summary>
    private static readonly AccountStore Accounts = new("User/Data");

    /// <summary>封禁表:账号 / IP / 机器码,永久或定时。</summary>
    private static readonly BanStore Bans = new("bans.json");

    /// <summary>白名单表:通联标(MC 玩家名 / ZXM 账号名 / ZXM 账号 ID),进服时先查它。</summary>
    private static readonly WhitelistStore Whitelist = new("Data/Whitelist.txt");

    static void OpenLog()
    {
        if (!Logger.OpenDefaultLogFile("ZXMS", out var logFile, "logs/MLog"))
        {
            Console.Error.WriteLine("[Fatal]打开日志文件失败: " + logFile);
            Environment.Exit(1);
        }

        // 两类日志各有各的去处:客户端连接日志(CClient)按时间分文件,控制台命令日志(Cmd)按时间分文件
        ClientLog.Initialize("logs/CClient");
        CommandLog.Initialize("logs/Cmd");

        WarnOldLogsInRoot();
    }

    /// <summary>
    /// 提示 logs 根目录里改动前留下的旧日志。只提示不自动搬:自动搬要把可能正在写的文件挪走,风险高。
    /// 只数顶层 *.log,MLog / CClient / Cmd 子目录里的不算。
    /// </summary>
    static void WarnOldLogsInRoot()
    {
        try
        {
            var root = Logger.ResolveLogDirectory();   //日志根目录,与 MLog 同一锚点
            if (!Directory.Exists(root))
            {
                return;
            }

            var count = Directory.GetFiles(root, "*.log").Length;   //顶层旧日志文件数
            if (count > 0)
            {
                Logger.Log(LogLevel.Info, $"[日志] logs 根目录下还有 {count} 个旧日志文件(改动前生成的),不影响使用;要归档可手动移到 logs/MLog");
            }
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[日志] 统计 logs 根目录的旧日志失败: " + ex.Message);
        }
    }

    /// <summary>本端显示给玩家的名称。<b>linkname=1(默认)时直接复用官方账号名</b>(和 ZXMOS 上那个账号一个字不差), linkname=0 时用 config.ini 的 name,两者互不干扰。</summary>
    static string ResolveName(ZFile config)
    {
        var account = config.MapGet(KeyOsUser).Trim();   //配置值
        var own = config.MapGet(KeyName).Trim();   //配置值

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
        var raw = config.MapGet(KeyConnectOs).Trim();   //配置值
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

    /// <summary>配置检查:ini 里这个键没有"有效值"就在控制台问一次,问到合法值后写回 ini。 直接回车(空输入)或输入流已结束(管道/重定向)时用 fallback 兜底,不会死循环。</summary>
    static string AskIfMissing(ZFile file, string key, string prompt, Func<string, bool> valid, string fallback, bool interactive = true)
    {
        var value = file.MapGet(key).Trim();
        if (valid(value))
        {
            return value;
        }

        // 自动重连路径不读控制台(后台线程读输入会吃掉服主的命令),缺失就用兜底值写回配置
        if (!interactive)
        {
            file.WriteFileMap(key, fallback);
            Logger.Log(LogLevel.Warning, $"[CONFIG]{key} 缺失,自动重连用兜底值 {fallback}");
            return fallback;
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

    /// <summary>回复握手确认(收到 300 回 301)。 成功/失败都记日志 —— 否则服务端这边只有"上线",看不出确认包有没有发出去。</summary>
    static async Task ReplyHandshakeAsync(ServerboundPacket packet, ushort ackType, string ackName, string peer, string text)
    {
        try
        {
            await packet.WriteAsync(new Packet(packet.Id, ackType, Encoding.UTF8.GetBytes(text)));
            Logger.Log(LogLevel.Info, $"[{peer}] 已回复握手确认({ackName}) → {Where(packet.Connection)},握手完成");
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

    /// <summary>处理一条来自 ZXMC / ZXMM 的报文:按 Type 分派。</summary>
    /// <param name="packet">收到的报文包(带连接上下文)。</param>
    /// <summary>替命令查官方端的玩家返回记录(msg backget ... -os):发 508,等 509 回包。</summary>
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
    /// <summary>一条等待客户端确认的登录请求。KickedAtJoining = 连接阶段就把人踢走了,超时只清记录。</summary>
    private sealed record PendingLogin(string Player, string Ip, int AccountId, Guid OriginConnId, DateTime At,
        bool KickedAtJoining);

    /// <summary>一条等待账号主人二次确认的绑定(绑定码已校验通过,还没写绑定表)。</summary>
    private sealed record PendingBind(int AccountId, string Player, string Code, DateTime At);

    /// <summary>给指定账号的 ZXMC 连接发一条包(不在线返回 false)。</summary>
    private static async Task<bool> SendToAccountAsync(int accountId, ushort type, string payload)
    {
        var server = _server;
        var account = Accounts.FindById(accountId);   //账号
        if (server is null || account is null)
        {
            return false;
        }

        foreach (var conn in server.Connections)
        {
            if (conn.IsClosed)
            {
                continue;   //已经在关了,发过去只会抛 disposed
            }

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

    /// <summary>
    /// 拼 621 的负载:<c>请求ID|玩家名|连接IP|归属地[|上报的局域网IP]</c>。
    ///
    /// <para>前四段是老格式,老客户端只读前四段,所以第五段<b>只在真有上报值时才追加</b> —— 不会让它错位。</para>
    /// <para>归属地由调用处固定用<b>连接 IP</b> 查:上报值是本机的局域网地址,拿它查归属地只会得到「内网」。</para>
    /// </summary>
    private static string LoginAskPayload(int requestId, string player, string ip, string geo, string reportedIp)
        => reportedIp.Length > 0
            ? $"{requestId}|{player}|{ip}|{geo}|{reportedIp}"
            : $"{requestId}|{player}|{ip}|{geo}";

    /// <summary>
    /// 把一段客户端来的文本截到 <paramref name="max"/> 个字符以内,超了就在末尾标上原长度。
    ///
    /// <para>只给日志用:未登录连接也能发包、单包负载上限 8 MB,原样往日志里写会被刷成大文件。</para>
    /// </summary>
    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + $"…(共 {text.Length} 字符)";

    /// <summary>
    /// 玩家进服时这个账号没有上报过局域网地址(或已过期),就向它的客户端要一次(633)。
    ///
    /// <para><b>不等回复</b>:玩家进服不能卡在客户端回包上。这一趟的 621 用的仍是现有缓存(没有就不带第五段),
    /// 客户端补报上来的值从<b>下一次</b>进服开始生效 —— 不回头改这次的 621。</para>
    /// <para>账号的 ZXMC 不在线就静默跳过:它下次登录成功时会主动上报(632)。</para>
    /// </summary>
    private static void AskReportedIpIfStale(int accountId, string accountName, string player)
    {
        if (accountId <= 0 || accountName.Length == 0 || ReportedClientIp.Get(accountName).Length > 0)
        {
            return;
        }

        _ = AskReportedIpAsync(accountId, accountName, player);
    }

    /// <summary>真正去发 633(异常只记日志,不能让它冒到进服流程里)。</summary>
    private static async Task AskReportedIpAsync(int accountId, string accountName, string player)
    {
        try
        {
            if (await SendToAccountAsync(accountId, Protocol.ClientIpAsk, ""))
            {
                Logger.Log(LogLevel.Info, $"[局域网地址] 玩家 {player} 进服时没有可用的上报地址,已向 {accountName} 的客户端索要一次");
            }
            else
            {
                Logger.Log(LogLevel.Info, $"[局域网地址] 玩家 {player} 进服时没有可用的上报地址,{accountName} 的客户端不在线,跳过索要");
            }
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[局域网地址] 索要上报地址失败: " + ex.Message);
        }
    }

    /// <summary>让 ZXMM 踢人(ZXMS 只发配,动作由它在主线程执行)</summary>
    private static async Task<bool> KickViaZxmmAsync(string player, string reason)
    {
        if (string.IsNullOrWhiteSpace(player))
        {
            return false;
        }

        try
        {
            _zxmmApi ??= CreateZxmmApi();

            // ZXMM 回 err 就说明没真踢成(人不在线 / 主线程忙),别一律当成功
            var (ok, detail) = await _zxmmApi.KickAsync(player, reason);
            Logger.Log(ok ? LogLevel.Info : LogLevel.Warning,
                $"[ZXMM] 下发踢人任务 {player} → {detail}");
            return ok;
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[ZXMM] 下发踢人任务失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 日志里描述一条连接:IP 后面补上已登录的账号(「[账号#ID/名称]」),没登录就只给 IP。
    /// 账号信息来自 ClientRegistry(登录 / 注册成功时写入),断线后随连接一起消失。
    /// </summary>
    private static string Where(TcpConnection connection)
    {
        var address = connection.RemoteEndPoint?.ToString() ?? "?";
        var info = _server?.Registry.Find(connection.Id);
        return info is null || info.User.Length == 0
            ? address
            : $"{address} [账号#{info.AccountId}/{info.User}]";
    }

    /// <summary>
    /// 这条连接当前选的服务器编号(没选过/没登记 = 0,表示走共享区)。
    /// 切服是客户端主动发的 672,记在 _clientServer 里,断开时清掉。
    /// </summary>
    private static int ServerOf(TcpConnection connection)
    {
        int serverId;   //连接当前选的服务器编号
        lock (_clientServerLock)
        {
            if (!_clientServer.TryGetValue(connection, out serverId))
            {
                return 0;
            }
        }

        // 没开 bind 分离时所有账号都该落在共享区:这时还返回编号,调用方就会去读一个根本不该存在的分区
        return DataSeparation.IsSeparated("bind") ? serverId : 0;
    }

    /// <summary>
    /// 能不能用服务器控制台:只有 HAdmin 和 System。Player / LAdmin 不够。
    /// </summary>
    /// <param name="connection">发请求的连接。</param>
    private static bool CanUseConsole(TcpConnection connection)
    {
        var info = _server?.Registry.Find(connection.Id);   //这条连接的登录信息
        return info is not null && (info.Level == "HAdmin" || info.Level == "System");
    }

    /// <summary>
    /// 把某台服的一行控制台输出推给正在监听它的客户端。
    /// </summary>
    /// <param name="serverId">服务器编号。</param>
    /// <param name="text">这一行输出。</param>
    private static void PushConsoleOutput(int serverId, string text)
    {
        // 推送是网络 IO:不能压在 ZXMM 收包线程上,也不能让一条慢连接拖住别的连接
        _ = Task.Run(async () =>
        {
            try
            {
                var targets = new List<TcpConnection>();   //正在收这台服的连接快照(持锁期间不写包)
                lock (_consoleTapLock)
                {
                    foreach (var pair in _consoleTaps)
                    {
                        if (pair.Value == serverId)
                        {
                            targets.Add(pair.Key);
                        }
                    }
                }

                if (targets.Count == 0)
                {
                    return;
                }

                Logger.Log(LogLevel.Debug,
                    $"[控制台] 推 {text.Length} 字的输出给 {targets.Count} 条连接({serverId} 号)");

                var payload = Encoding.UTF8.GetBytes($"{serverId}|{text}");
                foreach (var conn in targets)
                {
                    try
                    {
                        await conn.WriteAsync(new Packet(0, Protocol.ConsoleOutput, payload));
                        Logger.Log(LogLevel.Debug, $"[控制台] 已把这一行发给连接 {conn.Id.ToString("N")[..8]}");
                    }
                    catch (Exception ex)
                    {
                        // 断了的连接会在 Disconnected 里清掉,这里只留个痕迹
                        Logger.Log(LogLevel.Warning, $"[控制台] 推送 {serverId} 的输出失败 → {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error, $"[控制台] 推送 {serverId} 的控制台输出异常 → {ex}");
            }
        });
    }

    /// <summary>
    /// 重算某台服务器还要不要持续输出,并把结果同步给模组。
    ///
    /// <para>客户端勾选 / 取消 / 换服务器 / 断开都走这里:订阅连接数从 0 变正才让模组开始推,落到 0 才让它停。
    /// 光记 <see cref="_consoleTaps"/> 不通知模组的话,客户端只会收到回执、永远收不到真正的输出。</para>
    /// </summary>
    /// <param name="serverId">服务器编号。</param>
    private static void RefreshTap(int serverId)
    {
        bool want;   //这台服现在到底该不该让模组推
        lock (_consoleTapLock)
        {
            var count = 0;
            foreach (var pair in _consoleTaps)
            {
                if (pair.Value == serverId)
                {
                    count++;
                }
            }

            want = count > 0;
            if (_tapState.TryGetValue(serverId, out var current) && current == want)
            {
                return;
            }

            _tapState[serverId] = want;
        }

        var api = ZxmmApiOf(serverId);
        if (api is null)
        {
            // 桥接没连上时只留痕:等它重连后台再勾一次即可,不要在这里阻塞分派线程
            Logger.Log(LogLevel.Warning, $"[控制台] {serverId} 号服务器没有 ZXMM 通道,持续输出开关发不出去");
            return;
        }

        _ = SendTapAsync(api, serverId, want);
    }

    /// <summary>把持续输出开关发给模组;失败只记日志,不往调用方抛。</summary>
    /// <param name="api">该服的 ZXMM 通道。</param>
    /// <param name="serverId">服务器编号(只用于日志)。</param>
    /// <param name="on">true=开始推,false=停止推。</param>
    private static async Task SendTapAsync(Zxmm.ZxmmApi api, int serverId, bool on)
    {
        try
        {
            var ok = await api.ConsoleTapAsync(on).ConfigureAwait(false);
            Logger.Log(ok ? LogLevel.Info : LogLevel.Warning, ok
                ? $"[控制台] 已通知 {serverId} 号服务器{(on ? "开始" : "停止")}推送控制台输出"
                : $"[控制台] {serverId} 号服务器{(on ? "开始" : "停止")}推送控制台输出失败(模组没答 ok)");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[控制台] 通知 {serverId} 号服务器开关持续输出时出错 → {ex.Message}");
        }
    }

    /// <summary>按 zxmmapi=host:port 建接口客户端(支持 [::1]:21200)</summary>
    private static Zxmm.ZxmmApi CreateZxmmApi()
    {
        var (host, port) = SplitZxmmApiAddress(_zxmmApiAddress);
        var api = new Zxmm.ZxmmApi(host, port, _zxmmToken);   //ZXMM 长连接(连不连由调用方 Start)
        // 持续输出按登记表编号路由:客户端订阅的是登记表那台,模组自报的是 MC 服自己的 id,两者对不上
        api.ConsoleOutput += line =>
        {
            if (Servers.Default is { } entry)
            {
                PushConsoleOutput(entry.Id, line);
            }
        };
        return api;
    }

    /// <summary>把 zxmmapi 的 host:port 拆开(支持 [::1]:21200);拆不出来时按 127.0.0.1:21200 兜底。</summary>
    private static (string Host, int Port) SplitZxmmApiAddress(string address)
    {
        var host = "127.0.0.1";
        var port = 21200;

        var text = address.Trim();

        // IPv6 字面量必须带方括号,端口在方括号外:[::1]:21200
        if (text.StartsWith('['))
        {
            var end = text.IndexOf(']');
            if (end > 1)
            {
                host = text[1..end];
                var tail = text[(end + 1)..];
                if (tail.StartsWith(':') && int.TryParse(tail[1..], out var v6Port) && v6Port is > 0 and <= 65535)
                {
                    port = v6Port;
                }
            }
        }
        else
        {
            var colon = text.LastIndexOf(':');
            if (colon > 0 && int.TryParse(text[(colon + 1)..], out var parsedPort) && parsedPort is > 0 and <= 65535)
            {
                host = text[..colon].Trim();
                port = parsedPort;
            }
            else if (text.Length > 0)
            {
                host = text;
            }
        }

        return (host, port);
    }

    /// <summary>
    /// 按服务器编号取那条 ZXMM 长连接:单独建过连的查 _zxmmApis,没单独建的(通常是登记表第一台)走主通道。
    /// </summary>
    /// <param name="serverId">服务器编号。</param>
    /// <returns>该服的连接;这个编号没连过 ZXMM 时返回 null。</returns>
    private static Zxmm.ZxmmApi? ZxmmApiOf(int serverId)
    {
        if (_zxmmApis.TryGetValue(serverId, out var api))
        {
            return api;
        }

        return Servers.Default?.Id == serverId ? _zxmmApi : null;
    }

    /// <summary>
    /// 按登记表给每台服务器连一条 ZXMM 专用通道;建不起来只记 Warn,不影响启动。
    ///
    /// <para>主机一律取 zxmmapi 配的 host 部分(同一台机器上的多个 MC 服只差端口),端口取登记表的 Port。
    /// 同一端口只连一条 —— 模组接受多连接,连两条会让同一个进服事件上报两次。
    /// 本期只建连备查:踢人 / 查询 / 广播仍旧走 <see cref="_zxmmApi"/>,不按服务器路由。</para>
    /// </summary>
    private static void ConnectAllZxmm()
    {
        if (Servers.All.Count == 0)
        {
            Logger.Log(LogLevel.Info, "[ZXMM] 登记表里没有服务器 → 只保留默认专用通道");
            return;
        }

        var (host, defaultPort) = SplitZxmmApiAddress(_zxmmApiAddress);
        var connectedPorts = new HashSet<int> { defaultPort };   //已经连过的端口,默认通道占一个

        foreach (var entry in Servers.All)
        {
            // 每台可以自己写主机(模组不一定和 ZXMS 同机);没写就用 zxmmapi 配的那个
            var entryHost = entry.Host.Length > 0 ? entry.Host : host;
            if (!connectedPorts.Add(entry.Port))
            {
                Logger.Log(LogLevel.Info,
                    $"[ZXMM] 服务器 {entry.Id}({entry.Name}) 的端口 {entry.Port} 已经有连接 → 不重复建连");
                continue;
            }

            try
            {
                // 登记项自己带令牌就用它的,没填则跟全局 zxmmtoken
                var api = new Zxmm.ZxmmApi(entryHost, entry.Port, entry.Token.Length > 0 ? entry.Token : _zxmmToken);
                api.ServerIdentified += (id, name) =>
                    Logger.Log(LogLevel.Info, $"[ZXMM] 服务器 {entry.Id}({entry.Name}) 报上身份 {id}({name})");
                // 这台服的持续输出推给按编号订阅的客户端(foreach 变量每轮新建,捕获安全)
                api.ConsoleOutput += line => PushConsoleOutput(entry.Id, line);
                api.Start();
                _zxmmApis[entry.Id] = api;
                _zxmmApi ??= api;   //默认接口已经建好时不会覆盖,只有兜底路径才由第一台顶上
                Logger.Log(LogLevel.Info,
                    $"[ZXMM] 服务器 {entry.Id}({entry.Name}) 专用通道 → {entryHost}:{entry.Port}(断了自动重连)");
            }
            catch (Exception ex)
            {
                // 一台建不起来不能让整个启动失败
                Logger.Log(LogLevel.Warning, $"[ZXMM] 服务器 {entry.Id}({entry.Name}) 专用通道建不起来 → {ex.Message}");
            }
        }

        Logger.Log(LogLevel.Info, $"[ZXMM] 多服专用通道就绪 {_zxmmApis.Count} 条(默认通道另算)");
    }

    /// <summary>
    /// configuration 阶段快判:能直接放行就什么都不做;不能就下发踢人,理由显示在客户端的连接界面。
    ///
    /// <para>已绑定的玩家不再原地等 621:发完登录请求就把人踢走,账号主人点「同意」后重进,
    /// 命中登录数组直接放行 —— 也就是「在 ZXMC 点完确认再进服务器」。</para>
    /// </summary>
    private static async Task HandlePlayerJoiningAsync(string player, string ip)
    {
        if (string.IsNullOrWhiteSpace(player))
        {
            return;
        }

        ip = GeoLookup.Normalize(ip);
        if (ip.Length == 0)
        {
            ip = "unknown";
        }

        // ① 白名单(开启时):按通联标比对(玩家名 / 绑定账号名 / 绑定账号ID),未命中直接黄字踢出
        if (Whitelist.Enabled)
        {
            var wlBound = LoginStore.AccountOf(player);   //这个玩家绑的账号ID(没绑定就是 0)
            var wlAccount = wlBound > 0
                ? (Accounts.FindById(wlBound) ?? await ResolveAccountAsync(player))
                : await ResolveAccountAsync(player);   //本地没有就按账号路由去 ZXMOS

            var hit = Whitelist.Contains(player)
                      || (wlAccount is not null
                          && (Whitelist.Contains(wlAccount.Name) || Whitelist.Contains(wlAccount.Id.ToString())));

            if (!hit)
            {
                Logger.Log(LogLevel.Warning, $"[白名单] 拒绝玩家 {player}({ip}):不在白名单");
                LoginStore.Log($"[白名单] 拒绝玩家 {player}({ip}):不在白名单");
                await KickViaZxmmAsync(player, "§e你未在白名单中");
                return;
            }

            LoginStore.Log($"[白名单] 玩家 {player}({ip}) 通过白名单");
        }

        // ② 封禁:玩家名和它绑定的账号名(含官方账号)任一被封都拦 —— 必须排在登录数组/持久表前面,
        //    否则在数组里的玩家会先被放行,封禁只能等 play 阶段才生效
        var boundAccount = LoginStore.AccountOf(player);   //绑定的账号ID
        var boundAccountEntity = boundAccount > 0
            ? Accounts.FindById(boundAccount) ?? await ResolveAccountAsync(player)
            : await ResolveAccountAsync(player);   //本地没有就按账号路由去 ZXMOS
        var boundName = boundAccountEntity?.Name ?? "";
        if (Bans.Check(player, ip, "", out var ban) ||
            (boundName.Length > 0 && Bans.Check(boundName, ip, "", out ban)))
        {
            Logger.Log(LogLevel.Warning, $"[配置阶段] 拒绝被封禁的玩家 {player}({ban!.Kind}={ban.Value})");
            LoginStore.Log($"[配置阶段] 拒绝被封禁的玩家 {player}({ip}):{ban.Kind}={ban.Value}");
            await KickViaZxmmAsync(player, $"已被封禁:{ban.Reason}({ban.Remain})");
            return;
        }

        // ③ 登录数组 / 持久登录表(同玩家 + 同 IP)→ 放行,交给 play 阶段
        if (InLoginArray(player))
        {
            LoginStore.Log($"[配置阶段] 玩家 {player}({ip}) 在登录数组里,放行");
            return;
        }

        if (!_confirmAlways && LoginStore.HasPersistentLogin(player, ip, _persistDays))
        {
            LoginStore.Log($"[配置阶段] 玩家 {player}({ip}) 在持久登录表里(同 IP),放行");
            return;
        }

        // ③ 没绑定 → 生成绑定码,踢去 ZXMC 绑定
        if (boundAccount <= 0)
        {
            var code = NewBindCode();   //绑定码
            lock (BindCodes)
            {
                BindCodes[code] = (player, DateTime.Now);
            }

            LoginStore.Log($"[未绑定] 玩家 {player}({ip}) 拿到绑定码 {code}(连接阶段)");
            await KickViaZxmmAsync(player,
                "请先绑定账号。绑定码：" + code + " —— 在 ZXMC 的「小功能组 → 绑定/解绑」里输入这 6 位");
            return;
        }

        // ④ 已绑定但这次没确认过:621 发给账号主人,然后把玩家踢走,点完同意重进即可
        var requestId = Interlocked.Increment(ref _loginSeq);   //请求ID
        PendingLogins[requestId] = new PendingLogin(player, ip, boundAccount, Guid.Empty, DateTime.Now,
            KickedAtJoining: true);
        LoginStore.Log($"[登录请求] {player}({ip}) → 账号 {boundAccount},请求 {requestId}(连接阶段)");

        var geo = await GeoLookup.LookupAsync(ip);   //归属地
        // 归属地固定用连接 IP 查:上报的是玩家本机的局域网地址,拿它查只会得到「内网」
        AskReportedIpIfStale(boundAccount, boundName, player);
        var reportedIp = ReportedClientIp.Get(boundName);   //上报的局域网地址(可能为空)
        if (!await SendToAccountAsync(boundAccount, Protocol.LoginAsk,
                LoginAskPayload(requestId, player, ip, geo, reportedIp)))
        {
            PendingLogins.TryRemove(requestId, out _);
            LoginStore.Log($"[登录失败] 玩家 {player}: 绑定的 ZXMC 账号不在线");
            await KickViaZxmmAsync(player, "绑定的 ZXMC 账号不在线，无法确认登录，请稍后再试");
            return;
        }

        await KickViaZxmmAsync(player, "登录请求已发送到 ZXMC，点「同意」后重新进入服务器");
    }

    /// <summary>玩家进服:全部流程都在这里判(登录数组→持久表→绑定→弹窗)</summary>
    private static async Task HandlePlayerJoinedAsync(string player, string ip)
    {
        if (string.IsNullOrWhiteSpace(player))
        {
            return;
        }

        // 老版本模组报的是 /1.2.3.4:5678,先压成纯 IP:归属地、持久登录表、日志都用它
        ip = GeoLookup.Normalize(ip);
        if (ip.Length == 0)
        {
            ip = "unknown";
        }
        // ① 封禁优先:玩家名或它绑定的账号名被封都拦,登录数组/持久表不能绕过封禁
        var boundAccount = LoginStore.AccountOf(player);   //绑定的账号ID
        var boundName = boundAccount > 0 ? Accounts.FindById(boundAccount)?.Name ?? "" : "";
        if (Bans.Check(player, ip, "", out var joinedBan) ||
            (boundName.Length > 0 && Bans.Check(boundName, ip, "", out joinedBan)))
        {
            Logger.Log(LogLevel.Warning, $"[进服] 拒绝被封禁的玩家 {player}({joinedBan!.Kind}={joinedBan.Value})");
            await KickViaZxmmAsync(player, $"已被封禁:{joinedBan.Reason}({joinedBan.Remain})");
            return;
        }

        // ② 登录数组(5 分钟)里 → 放行,什么都不做
        if (InLoginArray(player))
        {
            LoginStore.Log($"[登录数组] 玩家 {player}({ip}) 在登录数组里,放行");
            return;
        }

        // ③ 持久登录表 → 记进登录数组,放行
        if (!_confirmAlways && LoginStore.HasPersistentLogin(player, ip, _persistDays))
        {
            AddToLoginArray(player);
            LoginStore.Log($"[持久登录] 玩家 {player}({ip}) 在持久登录表里,放行");
            return;
        }

        // ④ 没绑定 → 生成绑定码,直接通过执行接口把他踢去绑定
        if (boundAccount <= 0)
        {
            var code = NewBindCode();   //绑定码
            lock (BindCodes)
            {
                BindCodes[code] = (player, DateTime.Now);
            }

            LoginStore.Log($"[未绑定] 玩家 {player}({ip}) 拿到绑定码 {code}");
            await KickViaZxmmAsync(player,
                "请先绑定账号。绑定码：" + code + " —— 在 ZXMC 的「小功能组 → 绑定/解绑」里输入这 6 位");
            return;
        }

        // ④ 有绑定 → 弹窗问账号主人,等 622
        // 同一个玩家短时间内重复上报(客户端重连 / 事件补报):只留一条待确认,别让账号主人连点两次
        foreach (var pair in PendingLogins)
        {
            if (string.Equals(pair.Value.Player, player, StringComparison.OrdinalIgnoreCase) &&
                (DateTime.Now - pair.Value.At).TotalMinutes < 5)
            {
                Logger.Log(LogLevel.Info, $"[ZXMM] 忽略重复上报: {player} 已挂着请求 {pair.Key}");
                LoginStore.Log($"[登录请求] 玩家 {player} 已有待确认的请求(请求 {pair.Key}),忽略这次重复上报");
                return;
            }
        }

        var requestId = Interlocked.Increment(ref _loginSeq);   //请求ID
        PendingLogins[requestId] = new PendingLogin(player, ip, boundAccount, Guid.Empty, DateTime.Now,
            KickedAtJoining: false);
        LoginStore.Log($"[登录请求] {player}({ip}) → 账号 {boundAccount},请求 {requestId}");

        // 归属地是展示用的:最多等 1.2 秒,超时按"未知"发,不拖住登录弹窗
        var geo = await GeoLookup.LookupAsync(ip);   //归属地
        // 归属地固定用连接 IP 查:上报的是玩家本机的局域网地址,拿它查只会得到「内网」
        AskReportedIpIfStale(boundAccount, boundName, player);
        var reportedIp = ReportedClientIp.Get(boundName);   //上报的局域网地址(可能为空)
        if (!await SendToAccountAsync(boundAccount, Protocol.LoginAsk,
                LoginAskPayload(requestId, player, ip, geo, reportedIp)))
        {
            PendingLogins.TryRemove(requestId, out _);
            LoginStore.Log($"[登录失败] 玩家 {player}: 绑定的账号不在线");
            await KickViaZxmmAsync(player, "绑定的 ZXMC 账号不在线，无法确认登录，请稍后再试");
        }
    }

    /// <summary>玩家在不在登录数组里(过期的顺手清掉)。</summary>
    private static bool InLoginArray(string player)
    {
        if (!LoginArray.TryGetValue(player, out var expires))
        {
            return false;
        }

        if (expires < DateTime.Now)
        {
            LoginArray.TryRemove(player, out _);
            return false;
        }

        return true;
    }

    /// <summary>把玩家放进登录数组(默认 5 分钟)。</summary>
    private static void AddToLoginArray(string player)
    {
        LoginArray[player] = DateTime.Now.AddMinutes(LoginArrayMinutes);
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

    /// <summary>限速告警节流:同一个 key 一分钟最多打一行,否则刷屏比攻击本身更烦人。</summary>
    /// <param name="key">限速维度(连接 id / IP)。</param>
    /// <param name="message">要打的日志。</param>
    private static void WarnRateLimited(string key, string message)
    {
        var now = Environment.TickCount64;
        var last = RateWarnedAt.GetOrAdd(key, 0);
        if (now - last < 60_000)
        {
            return;
        }

        if (RateWarnedAt.TryUpdate(key, now, last))
        {
            Logger.Log(LogLevel.Warning, message);
        }
    }

    private static async Task HandlePacketAsync(ServerboundPacket packet)
    {
        // 别名:让方法体保持原来的写法
        var server = _server!;       // 监听中的服务端
        var config = _config!;       // config.ini 句柄
        var czport = _czport;        // 客户端接入端口(答复里要回给客户端)

        // 发包限速:一条连接狂刷时只丢它自己的包,别让它把 dispatcher 队列塞满
        if (!_packetLimiter.TryTake(packet.Connection.Id.ToString()))
        {
            Interlocked.Increment(ref _droppedPackets);
            WarnRateLimited(packet.Connection.Id.ToString(),
                $"[限速] {Where(packet.Connection)} 发包太快,已丢弃 type={packet.Type}");
            return;
        }

        switch (packet.Type)
        {
            // ---- ZXMC(客户端)上线:负载形如 "ZXMC:SAOS"(客户端把自己的连接模式报上来) ----
            case Conline:
            {
                var text = Encoding.UTF8.GetString(packet.Data).Trim();   //包内容
                var parts = text.Split(':', 2);   //拆分结果
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
                Logger.Log(LogLevel.Info, $"[会话] 与 ZXMC 建立加密通道 ← {Where(packet.Connection)}");
                break;
            }

            // ---- 账号:登录(负载 "名称|密码密文|等级") ----
            case Protocol.AccountLogin:
            {
                // 未协商加密的登录一律拒绝:登录凭据不能走明文链路(否则抓包就能重放)
                if (packet.Connection.Crypto is null)
                {
                    Logger.Log(LogLevel.Warning, $"[安全] 拒绝明文登录 ← {Where(packet.Connection)}(客户端过旧,需更新)");
                    await packet.Connection.SendAsync(Protocol.AccountLoginAck,
                        "err|客户端版本过旧(缺少加密会话),请更新到 a0.3.1 或更高版本", packet.Id);
                    break;
                }

                var parts = Encoding.UTF8.GetString(packet.Data).Split('|');   //包内容
                var user = parts.Length > 0 ? parts[0].Trim() : "";
                var passwordHash = parts.Length > 1 ? parts[1] : "";
                var level = parts.Length > 2 ? parts[2].Trim() : Protocol.RegisterLevel;
                var side = DecideAccountSide(level, parts.Length > 3 ? parts[3] : "");
                var machine = parts.Length > 4 ? parts[4].Trim() : "";
                // 第 6/7 段是挑战-响应(loginNonce / proof),用于本地账号增强校验
                var loginNonce = parts.Length > 6 ? parts[6].Trim() : "";
                var loginProof = parts.Length > 7 ? parts[7].Trim() : "";
                // 第 9 段是客户端版本(b0.3.2a);老客户端没有这一段,当"版本未知"
                var loginVersion = parts.Length > 8 ? parts[8].Trim() : "";
                var fromIp = packet.Connection.RemoteEndPoint?.Address.ToString() ?? "";

                _server.Registry.SetVersion(packet.Connection.Id, loginVersion);

                // 强制更新:版本不够直接回 err,不耗限速额度(旧客户端会一直重连,别把整个 IP 的额度用光)
                if (RejectOutdatedClient(loginVersion, protocol: "登录", user) is { } outdatedLogin)
                {
                    await packet.SendAsync(Protocol.AccountLoginAck, "err|" + outdatedLogin, packet.Id);
                    break;
                }

                // 登录只数失败:正常玩家一次就过、额度根本不动,爆破的人每试一次都在扣
                if (_loginLimiter.IsBlocked(fromIp))
                {
                    WarnRateLimited("login:" + fromIp,
                        $"[限速] {fromIp} 登录失败次数过多({user}),已拒绝");
                    await packet.SendAsync(Protocol.AccountLoginAck,
                        "err|失败次数过多,请稍后再试", packet.Id);
                    break;
                }

                // 机器码为空 = 客户端取不到主板序列号(见 MachineCode.ValueForLogin)。
                // 严格模式直接拒:这种客户端算不出能区分机器的值,放进来等于没有机器码封禁;
                // 宽松模式放行,但绝不把空串写进账号、也不拿空串去匹配机器码封禁(Bans.Check 对空串不命中)。
                if (machine.Length == 0 && _machineRequire)
                {
                    Logger.Log(LogLevel.Warning,
                        $"[机器码] 拒绝登录 {user}:客户端取不到主板序列号({KeyMachineRequire}=1) ← {Where(packet.Connection)}");
                    await packet.SendAsync(Protocol.AccountLoginAck,
                        "err|取不到主板序列号,本服要求绑定机器码;请确认主板序列号可读后重试,或联系服主关闭机器码必填", packet.Id);
                    break;
                }

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
                    ? Accounts.TryLoginProof(user, passwordHash, loginNonce, loginProof, level, out var account, out var error)
                    : Accounts.TryLogin(user, passwordHash, level, out account, out error);

                if (ok)
                {
                    _loginLimiter.RecordSuccess(fromIp);
                    _server.Registry.SetAccount(packet.Connection.Id, account!.Name, account.Level, account.Id);
                    Accounts.Touch(account.Name, fromIp, machine);
                    Logger.Log(LogLevel.Info, $"[账号] 登录成功:{account.Name}({account.Level}) ← {Where(packet.Connection)}");
                    ClientLog.Write(account.Level, $"登录 账号={account.Name} 地址={packet.Connection.RemoteEndPoint}");

                    await packet.SendAsync(Protocol.AccountLoginAck,
                        $"ok|{account.Name}|{account.Id}|{account.Level}|{LocalPlayerId(account, config).Full}", packet.Id);
                    await OnPlayerSignedInAsync(account, config);
                    await SendChatHelloAsync(packet.Connection, account, config);
                }
                else
                {
                    _loginLimiter.RecordFailure(fromIp);
                    // 只有「本地确实没有这个账号」才提官方端:那种情况本来就该去官方端校验,掉线才是真原因;
                    // 密码错误 / 等级不符本地就能判定,拼上官方端会让玩家以为必须连官方端才能登录
                    // 文案与 AccountStore.TryLogin / TryLoginProof 里「账号不存在」那一支逐字一致
                    const string missingAccount = "账号不存在";
                    var reason = side == "zxmos" && _os is not { IsConnected: true } && error == missingAccount
                        ? error + "(官方服务端未连接,暂时无法校验官方账号,请稍后再试)"
                        : error;

                    Logger.Log(LogLevel.Warning, $"[账号] 登录失败:{user} —— {reason}");
                    await packet.SendAsync(Protocol.AccountLoginAck, "err|" + reason, packet.Id);
                }

                break;
            }

            // ---- 账号:注册(负载 "名称|密码密文";等级固定 Player) ----
            case Protocol.AccountRegister:
            {
                var parts = Encoding.UTF8.GetString(packet.Data).Split('|');   //包内容
                var user = parts.Length > 0 ? parts[0].Trim() : "";
                var passwordHash = parts.Length > 1 ? parts[1] : "";
                // 第 3 段是客户端机器码(老客户端没这段,当空处理)
                var regMachine = parts.Length > 2 ? parts[2].Trim() : "";
                // 注册负载最后一段也是客户端版本(名称|密文|机器码|nonce|proof|版本)
                var regVersion = parts.Length > 5 ? parts[5].Trim() : "";

                var regIp = packet.Connection.RemoteEndPoint?.Address.ToString() ?? "";

                _server.Registry.SetVersion(packet.Connection.Id, regVersion);

                // 强制更新对注册同样生效:旧客户端注册进来也一样用不了
                if (RejectOutdatedClient(regVersion, protocol: "注册", user) is { } outdatedReg)
                {
                    await packet.SendAsync(Protocol.AccountRegisterAck, "err|" + outdatedReg, packet.Id);
                    break;
                }

                // 注册和登录共用同一份失败计数:否则可以拿注册接口反过来试口令 / 刷账号
                if (_loginLimiter.IsBlocked(regIp))
                {
                    WarnRateLimited("login:" + regIp,
                        $"[限速] {regIp} 失败次数过多(注册 {user}),已拒绝");
                    await packet.SendAsync(Protocol.AccountRegisterAck,
                        "err|失败次数过多,请稍后再试", packet.Id);
                    break;
                }

                // 注册这条分支的机器码规则与登录一致:空串 + 严格模式 → 拒绝,不写账号
                if (regMachine.Length == 0 && _machineRequire)
                {
                    Logger.Log(LogLevel.Warning,
                        $"[机器码] 拒绝注册 {user}:客户端取不到主板序列号({KeyMachineRequire}=1) ← {Where(packet.Connection)}");
                    await packet.SendAsync(Protocol.AccountRegisterAck,
                        "err|取不到主板序列号,本服要求绑定机器码;请确认主板序列号可读后重试,或联系服主关闭机器码必填", packet.Id);
                    break;
                }

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
                    _loginLimiter.RecordSuccess(regIp);
                    // 注册成功也要登记账号 + 记下 IP/机器码:否则这条连接在拉黑时踢不掉,也少一条连坐
                    _server.Registry.SetAccount(packet.Connection.Id, account!.Name, account.Level, account.Id);
                    Accounts.Touch(account.Name, regIp, regMachine);
                    Logger.Log(LogLevel.Info, $"[账号] 注册成功:{account.Name}({account.Level}) ← {Where(packet.Connection)}");
                    ClientLog.Write(account.Level, $"注册 账号={account.Name} 地址={packet.Connection.RemoteEndPoint}");

                    await packet.SendAsync(Protocol.AccountRegisterAck,
                        $"ok|{account.Name}|{account.Id}|{account.Level}|{LocalPlayerId(account, config).Full}", packet.Id);
                }
                else
                {
                    _loginLimiter.RecordFailure(regIp);
                    Logger.Log(LogLevel.Warning, $"[账号] 注册失败:{user} —— {error}");
                    await packet.SendAsync(Protocol.AccountRegisterAck, "err|" + error, packet.Id);
                }

                break;
            }

            // ---- 局域网地址上报(632):ZXMC 报上它自己网卡的 IPv4,只用于展示与日志 ----
            case Protocol.ClientIpReport:
            {
                var reportText = Encoding.UTF8.GetString(packet.Data).Trim();   //包内容
                var reporter = server.Registry.Find(packet.Connection.Id);   //这条连接登记信息

                // 写进日志的文本先截断:这个包未登录也能发,负载上限 8 MB,原样落日志就是个日志炸弹
                var reportForLog = Shorten(reportText, 64);

                // 没登录就不知道是谁报的:丢弃(账号名是这条缓存的键,没有键就存不了)
                if (reporter is null || reporter.User.Length == 0)
                {
                    Logger.Log(LogLevel.Warning, $"[局域网地址] 未登录的连接上报了地址 {reportForLog},已忽略 ← {Where(packet.Connection)}");
                    break;
                }

                if (ReportedClientIp.Remember(reporter.User, reportText, out var savedIp))
                {
                    Logger.Log(LogLevel.Info, $"[局域网地址] {reporter.User} 上报本机局域网地址 {savedIp}(连接地址仍是 {reporter.Ip},安全判断只用连接地址)");
                }
                else
                {
                    Logger.Log(LogLevel.Warning, $"[局域网地址] {reporter.User} 上报的地址不可用,已忽略:{reportForLog}");
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
                var text = Encoding.UTF8.GetString(packet.Data);   //包内容
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
                    (await ResolveAccountAsync(who))?.Id ?? 0, MessageCard.ActionText(text));

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
                var intent = Encoding.UTF8.GetString(packet.Data).Trim();   //包内容
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
                var ackText = Encoding.UTF8.GetString(packet.Data);   //包内容
                if (MegBackWaits.TryRemove((int)packet.Id, out var pending))
                {
                    pending.TrySetResult(ackText);
                }
                break;
            }

            // ---- 620 已废:现在 ZXMM 走专用长连接推 evt|joined(见 HandlePlayerJoinedAsync) ----
            case Protocol.LoginRequest:
                break;

            // ---- 客户端回答(622):同意 / 拒绝 ----
            case Protocol.LoginAnswer:
            {
                var answerText = Encoding.UTF8.GetString(packet.Data).Trim();   //回答负载
                var answerParts = answerText.Split('|');   //拆分结果
                if (answerParts.Length < 2 || !int.TryParse(answerParts[0], out var requestId) ||
                    !PendingLogins.TryGetValue(requestId, out var pending))
                {
                    break;
                }

                var agree = string.Equals(answerParts[1], "yes", StringComparison.OrdinalIgnoreCase);
                var answerInfo = _server?.Registry.Find(packet.Connection.Id);
                var answerIp = answerInfo?.Address ?? "?";

                // 只有被问的那个账号能回答:否则任何一个登录着的 ZXMC 都能替别人点"同意",
                // 白白给别的玩家写一条持久登录(以后免确认进服)。越权的回答不能把请求吃掉。
                var answerAccount = (await ResolveAccountAsync(answerInfo?.User ?? ""))?.Id ?? 0;
                if (answerAccount != pending.AccountId)
                {
                    Logger.Log(LogLevel.Warning,
                        $"[安全] 越权回答登录请求 请求 {requestId}(属于账号 {pending.AccountId}) ← 账号 {answerAccount}({answerInfo?.User ?? "?"})");
                    LoginStore.Log($"[登录拒绝] 请求 {requestId} 被非绑定账号(账号 {answerAccount})回答，已忽略");
                    break;
                }

                // 认领这条请求(并发下只让一个人处理)
                if (!PendingLogins.TryRemove(requestId, out pending))
                {
                    break;
                }

                LoginStore.Log(agree
                    ? $"[登录通过] 玩家 {pending.Player},账号 {pending.AccountId},发起 IP {pending.Ip},同意 IP {answerIp}"
                    : $"[登录拒绝] 玩家 {pending.Player},账号 {pending.AccountId},发起 IP {pending.Ip},拒绝 IP {answerIp}");

                // ZXMM 的上报是短连接，早就关了；这里只做决策，结果不再回给它

                if (agree)
                {
                    // 流程:允许 → 进登录数组(5 分钟) + 写持久登录表,之后直接放行
                    AddToLoginArray(pending.Player);
                    LoginStore.AddPersistentLogin(pending.Player, pending.Ip);
                }
                else
                {
                    // 流程:拒绝 → 由 ZXMS 通过执行接口让 ZXMM 去踢
                    // 不 await:踢人要等 ZXMM 回执(最长 10 秒),等在这儿会把整条连接的报文处理堵住
                    _ = KickViaZxmmAsync(pending.Player, "账号主人拒绝了这次登录");
                }
                break;
            }

            // ---- 绑定码转交(625):ZXMM 把玩家手里的码转给服务端 ----
            case Protocol.BindNotice:
            {
                var noticeText = Encoding.UTF8.GetString(packet.Data).Trim();   //包内容
                var noticeParts = noticeText.Split('|');   //拆分结果
                if (noticeParts.Length >= 2 && noticeParts[0].Trim().Length > 0)
                {
                    lock (BindCodes)
                    {
                        BindCodes[noticeParts[0].Trim().ToUpperInvariant()] = (noticeParts[1].Trim(), DateTime.Now);
                    }
                }
                break;
            }

            // ---- 绑定(626):客户端提交绑定码 —— 只校验并生成确认令牌,真正写绑定要等 631 ----
            case Protocol.BindApply:
            {
                var code = Encoding.UTF8.GetString(packet.Data).Trim().ToUpperInvariant();   //包内容
                var bindWho = _server?.Registry.Find(packet.Connection.Id)?.User ?? "";
                var bindAccount = (await ResolveAccountAsync(bindWho))?.Id ?? 0;
                if (bindAccount <= 0)
                {
                    await packet.SendAsync(Protocol.BindAck, "err|请先登录(这条连接没有登录态,断线重连后请重新登录一次)", packet.Id);
                    break;
                }

                var bindPlayer = "";
                lock (BindCodes)
                {
                    if (BindCodes.TryGetValue(code, out var entry) && (DateTime.Now - entry.At).TotalMinutes <= 30)
                    {
                        bindPlayer = entry.Player;
                    }
                }

                if (bindPlayer.Length == 0)
                {
                    await packet.SendAsync(Protocol.BindAck, "err|绑定码无效或已过期", packet.Id);
                    break;
                }

                // 绑定表按这条连接当前选的服务器取:客户端切到哪台,就只认那台的分区(没开分离时 Scope 返回空串)
                var bindScope = DataSeparation.Scope("bind", ServerOf(packet.Connection));

                // 绑定码只是"玩家本人的凭证":码可能被别人看到或转发,
                // 所以真正落盘前还要账号主人在 ZXMC 上亲眼确认一次(631)
                var alreadyOwner = LoginStore.AccountOf(bindPlayer, bindScope);
                if (alreadyOwner > 0 && alreadyOwner != bindAccount)
                {
                    await packet.SendAsync(Protocol.BindAck, $"err|这个玩家已经被账号 {alreadyOwner} 绑定了", packet.Id);
                    break;
                }

                // 上限在提交码这一步就先挡:让用户当场看到"先解绑一个",而不是输了码点了确认才失败
                var bindLimit = LoginStore.CheckBindLimit(bindAccount, bindPlayer, _bindMax, bindScope);
                if (bindLimit.Length > 0)
                {
                    await packet.SendAsync(Protocol.BindAck, "err|" + bindLimit, packet.Id);
                    break;
                }

                var token = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();   //确认令牌
                PendingBinds[token] = new PendingBind(bindAccount, bindPlayer, code, DateTime.Now);
                await packet.SendAsync(Protocol.BindAck, $"pending|{token}|{bindPlayer}", packet.Id);
                Logger.Log(LogLevel.Info,
                    $"[绑定] 账号 {bindWho}(ID {bindAccount}) 提交绑定码,待确认玩家 {bindPlayer} 令牌 {token}");
                break;
            }

            // ---- 绑定二次确认(631):用户点了"确定绑定"才真正写绑定表 ----
            case Protocol.BindConfirm:
            {
                var token = Encoding.UTF8.GetString(packet.Data).Trim().ToUpperInvariant();   //包内容
                var confirmWho = _server?.Registry.Find(packet.Connection.Id)?.User ?? "";
                var confirmAccount = (await ResolveAccountAsync(confirmWho))?.Id ?? 0;
                if (confirmAccount <= 0)
                {
                    await packet.SendAsync(Protocol.BindAck, "err|请先登录(这条连接没有登录态,断线重连后请重新登录一次)", packet.Id);
                    break;
                }

                // 令牌只给发起账号用,别让另一条连接拿着它把别人的码绑到自己名下
                if (!PendingBinds.TryRemove(token, out var pending) ||
                    pending.AccountId != confirmAccount ||
                    (DateTime.Now - pending.At).TotalMinutes > BindConfirmMinutes)
                {
                    await packet.SendAsync(Protocol.BindAck, "err|确认已过期,请重新输入绑定码", packet.Id);
                    break;
                }

                // 这个账号名下的旧玩家不做任何处理:上限拦着不许顶掉旧的,
                // 真要让他失去免确认只能走 628 解绑(解绑会清登录数组、持久登录表并踢人)
                if (!LoginStore.Bind(confirmAccount, pending.Player, _bindMax, out var bindError,
                        DataSeparation.Scope("bind", ServerOf(packet.Connection))))
                {
                    await packet.SendAsync(Protocol.BindAck, "err|" + bindError, packet.Id);
                    break;
                }

                // 码用过就作废(一次性):只在真正绑成之后才删,取消确认的码还能重试
                lock (BindCodes)
                {
                    if (BindCodes.TryGetValue(pending.Code, out var used) &&
                        string.Equals(used.Player, pending.Player, StringComparison.OrdinalIgnoreCase))
                    {
                        BindCodes.Remove(pending.Code);
                    }
                }

                LoginStore.Log($"[绑定] 账号 {confirmAccount} 确认绑定玩家 {pending.Player}(令牌 {token})");
                await packet.SendAsync(Protocol.BindAck, $"ok|{pending.Player}", packet.Id);

                // 多绑之后不再有"改绑顶掉旧玩家":旧玩家仍在这个账号名下,他的免登录与在线状态都不能动
                // (要让他失去免确认,只能走 628 解绑)

                break;
            }

            // ---- 解绑(628):负载是玩家名(空 = 这个账号名下全部解掉,兼容老客户端) ----
            case Protocol.Unbind:
            {
                var unbindWho = _server?.Registry.Find(packet.Connection.Id)?.User ?? "";
                var unbindAccount = (await ResolveAccountAsync(unbindWho))?.Id ?? 0;
                var unbindTarget = Encoding.UTF8.GetString(packet.Data).Trim();   //要解掉哪个玩家
                if (unbindAccount <= 0)
                {
                    await packet.SendAsync(Protocol.BindAck, "err|请先登录(这条连接没有登录态,断线重连后请重新登录一次)", packet.Id);
                    break;
                }

                // 只解自己名下的:负载里的玩家名必须真在这个账号名下,不能拿别人的名字来解绑
                var unbindScope = DataSeparation.Scope("bind", ServerOf(packet.Connection));   //解绑只动当前选中那台服的分区
                List<string> removed;
                if (unbindTarget.Length == 0)
                {
                    removed = LoginStore.UnbindAll(unbindAccount, unbindScope);
                }
                else
                {
                    removed = LoginStore.Unbind(unbindAccount, unbindTarget, out var one, unbindScope) ? [one] : [];
                }

                if (removed.Count == 0)
                {
                    await packet.SendAsync(Protocol.BindAck,
                        unbindTarget.Length == 0 ? "err|没有绑定记录" : $"err|这个账号没有绑定玩家 {unbindTarget}", packet.Id);
                    break;
                }

                // 解绑要立刻失效:登录数组、持久登录表都清掉,再通过执行接口把在线的人踢掉
                foreach (var player in removed)
                {
                    LoginArray.TryRemove(player, out _);
                    LoginStore.RemovePersistentLogin(player);
                }

                await packet.SendAsync(Protocol.BindAck, $"ok|{string.Join(",", removed)}", packet.Id);

                // 踢人不 await(同上),否则回执会被这条踢人拖到 10 秒后
                foreach (var player in removed)
                {
                    _ = KickViaZxmmAsync(player, "账号已解绑，请重新绑定后再进入");
                }

                break;
            }

            // ---- 查绑定(629):ZXMM 查全部;ZXMC 只查自己绑了哪些 ----
            case Protocol.BindQuery:
            {
                var queryInfo = _server?.Registry.Find(packet.Connection.Id);

                if (queryInfo?.Role == "ZXMC")
                {
                    // 客户端来查:只回自己名下的玩家,一行一个(解绑页按行做列表)
                    var selfId = (await ResolveAccountAsync(queryInfo.User))?.Id ?? 0;
                    var selfPlayers = selfId > 0
                        ? LoginStore.PlayersOf(selfId, DataSeparation.Scope("bind", ServerOf(packet.Connection)))   //只回这台服分区里的玩家
                        : [];

                    if (selfPlayers.Count > 0)
                    {
                        var selfLines = selfPlayers.Select(p => $"{p}={queryInfo.User}");
                        await packet.SendAsync(Protocol.BindList, string.Join("\n", selfLines), packet.Id);
                        break;
                    }

                    // 还没绑定:只说"没绑",不带任何别人的玩家名/绑定码 —— 绑定码是玩家的私密凭据,
                    // 服务端在未绑定时并不知道这个账号对应哪个玩家,推"最近进服那个"就是跨玩家泄漏
                    await packet.SendAsync(Protocol.BindList, "nobind||", packet.Id);
                    break;
                }

                var lines = LoginStore.AllBinds()
                    .Select(pair => $"{pair.Value}={Accounts.FindById(pair.Key)?.Name ?? "?"}");
                await packet.SendAsync(Protocol.BindList, string.Join("\n", lines), packet.Id);
                break;
            }

            // ---- 客户端回执(507):这几条它已经收下了,记进已发清单 ----
            case Protocol.MessageAck:
            {
                var ackWho = _server?.Registry.Find(packet.Connection.Id)?.User ?? "";
                var ackId = (await ResolveAccountAsync(ackWho))?.Id ?? 0;
                var ackText = Encoding.UTF8.GetString(packet.Data).Trim();   //包内容
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
                    $"[ZXMS] 客户端试图发服务端消息 type={packet.Type},已忽略 ← {Where(packet.Connection)}");
                break;

            // ---- 好友:每个动作一行,真正的判断在 Friends 目录里的处理器 ----
            case Protocol.FriendMsg:
                await HandleFriendMessageAsync(packet, config);
                break;

            case Protocol.FriendMsgHistory:
                await HandleFriendHistoryQueryAsync(packet, config);
                break;

            case Protocol.FriendReadMark:
                await HandleFriendReadMarkAsync(packet, config);
                break;

            case Protocol.FriendAvatarReq:
                await HandleFriendAvatarAsync(packet, config);
                break;

            case Protocol.FriendListQuery:
                await HandleFriendListQueryAsync(packet, config);
                break;

            case Protocol.FriendRequest:
                await HandleFriendRequestAsync(packet, config);
                break;

            case Protocol.FriendRequestsQuery:
                await HandleFriendRequestsQueryAsync(packet, config);
                break;

            case Protocol.FriendAnswer:
                await HandleFriendAnswerAsync(packet, config);
                break;

            case Protocol.FriendRemove:
                await HandleFriendRemoveAsync(packet, config);
                break;

            case Protocol.FriendSearch:
                await HandleFriendSearchAsync(packet, config);
                break;

            case Protocol.FriendRemark:
                await HandleFriendRemarkAsync(packet, config);
                break;

            // ---- 多服数据分离:客户端要服务器列表 / 切换当前服务器 ----
            case Protocol.ServerListReq:
            {
                var lines = new List<string>();   //每行一台:编号|名称|端口
                foreach (var entry in Servers.All)
                {
                    lines.Add($"{entry.Id}|{entry.Name}|{entry.Port}");
                }

                // 0 台时回空负载,客户端按空列表处理
                var listText = string.Join('\n', lines);
                await packet.WriteAsync(new Packet(packet.Id, Protocol.ServerList, Encoding.UTF8.GetBytes(listText)));
                Logger.Log(LogLevel.Info,
                    $"[数据分离] 客户端 {Where(packet.Connection)} 要服务器列表 → 回 {lines.Count} 台");
                break;
            }

            case Protocol.ServerSwitchReq:
            {
                var targetText = Encoding.UTF8.GetString(packet.Data).Trim();   //目标服务器编号(十进制)
                if (!int.TryParse(targetText, out var targetId) || !Servers.TryGet(targetId, out var target))
                {
                    var err = Encoding.UTF8.GetBytes("err|没有这个服务器");
                    await packet.WriteAsync(new Packet(packet.Id, Protocol.ServerSwitchAck, err));
                    Logger.Log(LogLevel.Warning,
                        $"[数据分离] 客户端 {Where(packet.Connection)} 切到服务器「{targetText}」→ 没有这个服务器");
                    break;
                }

                lock (_clientServerLock)
                {
                    _clientServer[packet.Connection] = target.Id;
                }

                var okText = Encoding.UTF8.GetBytes($"ok|{target.Id}");
                await packet.WriteAsync(new Packet(packet.Id, Protocol.ServerSwitchAck, okText));
                Logger.Log(LogLevel.Info,
                    $"[数据分离] 客户端 {Where(packet.Connection)} 切到服务器 {target.Id}({target.Name}) → ok");
                break;
            }

            // ---- 控制台:列服务器 / 执行一条命令 / 开关持续输出 ----
            case Protocol.ConsoleServersReq:
            {
                var lines = new List<string>();   //每行一台:编号|名称|控制台模式|桥接在线|RCON在线
                foreach (var entry in Servers.All)
                {
                    var bridge = Zxms.ConsoleRouter.IsBridgeOnline(entry.Id) ? 1 : 0;   //ZXMM 通道通不通
                    var rcon = entry.ConsoleMode == Zxms.ZxmmRegistry.ConsoleModeRcon && Zxms.RconHub.IsOnline(entry.Id) ? 1 : 0;   //RCON 会话在不在
                    lines.Add($"{entry.Id}|{entry.Name}|{entry.ConsoleMode}|{bridge}|{rcon}");
                }

                // 0 台时回空负载,客户端按空列表处理
                var listText = string.Join('\n', lines);
                await packet.WriteAsync(new Packet(packet.Id, Protocol.ConsoleServers, Encoding.UTF8.GetBytes(listText)));
                Logger.Log(LogLevel.Info,
                    $"[控制台] 客户端 {Where(packet.Connection)} 要控制台服务器列表 → 回 {lines.Count} 台");
                break;
            }

            case Protocol.ConsoleCommand:
            {
                // 先卡权限:没权限的连接连表都不查、命令也不执行
                if (!CanUseConsole(packet.Connection))
                {
                    var denied = Encoding.UTF8.GetBytes("err|没有这个服务器控制台的权限");
                    await packet.WriteAsync(new Packet(packet.Id, Protocol.ConsoleResult, denied));
                    Logger.Log(LogLevel.Warning,
                        $"[控制台] 客户端 {Where(packet.Connection)} 想执行控制台命令 → 不是 HAdmin/System,已拒绝");
                    break;
                }

                var text = Encoding.UTF8.GetString(packet.Data);   //负载:服务器编号|命令
                var bar = text.IndexOf('|');   //命令里可能还有竖线,只切第一根
                if (bar <= 0 || !int.TryParse(text[..bar].Trim(), out var serverId))
                {
                    var bad = Encoding.UTF8.GetBytes("err|参数不对");
                    await packet.WriteAsync(new Packet(packet.Id, Protocol.ConsoleResult, bad));
                    break;
                }

                var command = text[(bar + 1)..].Trim();   //要执行的 MC 命令(不带前导斜杠)
                if (command.Length == 0)
                {
                    var empty = Encoding.UTF8.GetBytes("err|命令是空的");
                    await packet.WriteAsync(new Packet(packet.Id, Protocol.ConsoleResult, empty));
                    break;
                }

                // 命令日志要记是谁发的:登录信息随连接走,没登录取不到就按「控制台」记
                var info = _server?.Registry.Find(packet.Connection.Id);
                var operatorName = info?.User ?? "";   //操作人名称
                if (operatorName.Length == 0)
                {
                    operatorName = "控制台";
                }

                var accountId = info?.AccountId ?? 0;   //操作人账号编号,0 = 没有账号
                var (ok, result) = await Zxms.ConsoleRouter.SendAsync(serverId, command, operatorName, accountId);
                var reply = Encoding.UTF8.GetBytes(ok ? $"ok|{result}" : $"err|{result}");
                await packet.WriteAsync(new Packet(packet.Id, Protocol.ConsoleResult, reply));
                break;
            }

            case Protocol.ConsoleTap:
            {
                if (!CanUseConsole(packet.Connection))
                {
                    var denied = Encoding.UTF8.GetBytes("err|没有这个服务器控制台的权限");
                    await packet.WriteAsync(new Packet(packet.Id, Protocol.ConsoleResult, denied));
                    Logger.Log(LogLevel.Warning,
                        $"[控制台] 客户端 {Where(packet.Connection)} 想收控制台输出 → 不是 HAdmin/System,已拒绝");
                    break;
                }

                var text = Encoding.UTF8.GetString(packet.Data);   //负载:服务器编号|1(开) 或 |0(关)
                var bar = text.IndexOf('|');
                var flag = bar > 0 ? text[(bar + 1)..].Trim() : "";
                if (bar <= 0 || flag is not ("0" or "1") || !int.TryParse(text[..bar].Trim(), out var serverId))
                {
                    var bad = Encoding.UTF8.GetBytes("err|参数不对");
                    await packet.WriteAsync(new Packet(packet.Id, Protocol.ConsoleResult, bad));
                    break;
                }

                if (!Servers.TryGet(serverId, out var tapEntry))
                {
                    var missing = Encoding.UTF8.GetBytes("err|没有这个服务器");
                    await packet.WriteAsync(new Packet(packet.Id, Protocol.ConsoleResult, missing));
                    break;
                }

                var on = flag == "1";   //1 = 开始收,0 = 停止
                int? previous = null;   //这条连接原来在收哪台:换台或关掉时要把旧那台的推送停掉
                lock (_consoleTapLock)
                {
                    if (_consoleTaps.TryGetValue(packet.Connection, out var was))
                    {
                        previous = was;
                    }

                    if (on)
                    {
                        _consoleTaps[packet.Connection] = serverId;
                    }
                    else
                    {
                        _consoleTaps.Remove(packet.Connection);
                    }
                }

                // 旧那台可能已经没人订阅了,先让它停;再按新目标重算
                if (previous is int oldId && oldId != serverId)
                {
                    RefreshTap(oldId);
                }

                RefreshTap(serverId);

                var ack = Encoding.UTF8.GetBytes(on
                    ? $"ok|已开始接收 {serverId} 号服务器的控制台输出"
                    : "ok|已停止");
                await packet.WriteAsync(new Packet(packet.Id, Protocol.ConsoleResult, ack));
                Logger.Log(LogLevel.Info, on
                    ? $"[控制台] 客户端 {Where(packet.Connection)} 在收 {serverId}({tapEntry.Name}) 的控制台输出"
                    : $"[控制台] 客户端 {Where(packet.Connection)} 停止接收 {serverId}({tapEntry.Name}) 的控制台输出");
                break;
            }

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
        Console.WriteLine("ZincXManager官网 http://zxm.zincms.top/");
        Console.WriteLine("旧站(带端口) http://zxm.zincms.top:10/");
    }

    /// <summary>读 config.ini 并把该问的问了:名称、服务端码、是否连官方、端口、消息保留天数。 缺的项会当场交互问一次然后写回文件,下次启动就不问了。</summary>
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
        var linkName = config.MapGet(KeyLinkName).Trim();   //配置值
        if (linkName.Length == 0)
        {
            config.WriteFileMap(KeyLinkName, DefaultLinkName);
            linkName = DefaultLinkName;
            Logger.Log(LogLevel.Info, $"[CONFIG]{KeyLinkName} 未配置,已写入默认值 {DefaultLinkName}(1=与官方账号名一致)");
        }

        // 服务端码:本实例的身份证。缺失或格式不对就生成一个 32 位十六进制并写回
        var serverCode = config.MapGet(KeyServerCode).Trim().ToLowerInvariant();   //配置值
        if (serverCode.Length != 32 || serverCode.Any(c => !Uri.IsHexDigit(c)))
        {
            serverCode = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            config.WriteFileMap(KeyServerCode, serverCode);
            Logger.Log(LogLevel.Info, $"[CONFIG]已生成服务端码:{serverCode}");

            // 已经配过 osuser 而且**以前连上过官方服**(config 里有 ozname)= 这是个老部署:
            // 这时缺 servercode 说明 config.ini 被整体换过,官方服记的还是老码 → 接上去会被判"身份不一致"。
            // (刚发下去、还没连过的干净 config 不提醒 —— 那种情况本来就要新生成一个码)
            var knownUser = config.MapGet(KeyOsUser).Trim();   //配置值
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

        // ZXMM 执行接口地址:ZXMS 连过去下发任务(踢人 / 查玩家 / 广播)
        var zxmmApi = config.MapGet(KeyZxmmApi).Trim();   //配置值
        if (zxmmApi.Length == 0)
        {
            config.WriteFileMap(KeyZxmmApi, DefaultZxmmApi);
            zxmmApi = DefaultZxmmApi;
            Logger.Log(LogLevel.Info, $"[CONFIG]{KeyZxmmApi} 未配置,已写入默认值 {DefaultZxmmApi}(ZXMM 执行接口)");
        }

        _zxmmApiAddress = zxmmApi;
        Logger.Log(LogLevel.Info, $"[ZXMM] 专用通道地址:{_zxmmApiAddress}");

        // 限速:缺配置就写默认值,让服主知道有这两个开关
        var packetRate = int.TryParse(config.MapGet(KeyPacketRate).Trim(), out var pr) && pr is >= 0 and <= 10000
            ? pr
            : DefaultPacketRate;
        if (config.MapGet(KeyPacketRate).Trim().Length == 0)
        {
            config.WriteFileMap(KeyPacketRate, packetRate.ToString());
            Logger.Log(LogLevel.Info, $"[CONFIG]{KeyPacketRate} 未配置,已写入默认值 {packetRate}(每连接每秒最多几个包,0=不限)");
        }

        _packetLimiter = new RateLimiter(packetRate);
        Logger.Log(LogLevel.Info, packetRate > 0
            ? $"[限速] 每连接 {packetRate} 包/秒,超出的包直接丢弃"
            : "[限速] 每连接发包不限(服主关掉了)");

        var loginRate = int.TryParse(config.MapGet(KeyLoginRate).Trim(), out var lr) && lr is >= 0 and <= 10000
            ? lr
            : DefaultLoginRate;
        if (config.MapGet(KeyLoginRate).Trim().Length == 0)
        {
            config.WriteFileMap(KeyLoginRate, loginRate.ToString());
            Logger.Log(LogLevel.Info,
                $"[CONFIG]{KeyLoginRate} 未配置,已写入默认值 {loginRate}(每 IP 每 {LoginRateWindowSeconds / 60} 分钟最多失败几次登录/注册,0=不限)");
        }

        _loginLimiter = new FailureThrottle(loginRate, LoginRateWindowSeconds);
        Logger.Log(LogLevel.Info, loginRate > 0
            ? $"[限速] 登录/注册 每 IP 每 {LoginRateWindowSeconds / 60} 分钟最多失败 {loginRate} 次"
            : "[限速] 登录失败次数不限(服主关掉了)");

        // 强制更新:缺配置就写默认(关),让服主知道有这两个开关
        var forceUpdateText = config.MapGet(KeyForceUpdate).Trim();   //配置里的开关值
        if (forceUpdateText.Length == 0)
        {
            config.WriteFileMap(KeyForceUpdate, "0");
            forceUpdateText = "0";
            Logger.Log(LogLevel.Info, $"[CONFIG]{KeyForceUpdate} 未配置,已写入默认值 0(强制更新关)");
        }

        _forceUpdate = forceUpdateText == "1";

        _minVersion = config.MapGet(KeyMinVersion).Trim();
        if (!config.ReadFileMap().ContainsKey(KeyMinVersion))
        {
            config.WriteFileMap(KeyMinVersion, "");
            Logger.Log(LogLevel.Info, $"[CONFIG]{KeyMinVersion} 未配置,已写入空值(强制更新门槛 = 本端版本 {AppVersion})");
        }

        Logger.Log(LogLevel.Info, _forceUpdate
            ? $"[强制更新] 开:客户端版本低于 {MinimumVersion} 一律拒绝登录并踢下线"
            : $"[强制更新] 关(config 里 {KeyForceUpdate}=1 可开,门槛用 {KeyMinVersion},留空 = 本端版本 {AppVersion})");

        // 绑定上限:缺配置就写默认(1),让服主知道有这个开关
        var bindMaxText = config.MapGet(KeyBindMax).Trim();   //配置里的上限值
        if (bindMaxText.Length == 0)
        {
            config.WriteFileMap(KeyBindMax, DefaultBindMax.ToString());
            bindMaxText = DefaultBindMax.ToString();
            Logger.Log(LogLevel.Info, $"[CONFIG]{KeyBindMax} 未配置,已写入默认值 {DefaultBindMax}(一个账号只能绑一个玩家)");
        }

        _bindMax = int.TryParse(bindMaxText, out var parsedBindMax) && parsedBindMax >= 0 ? parsedBindMax : DefaultBindMax;
        Logger.Log(LogLevel.Info, _bindMax > 0
            ? $"[绑定] 上限:一个账号最多绑 {_bindMax} 个玩家(config 里 {KeyBindMax} 可改,0 = 不限)"
            : $"[绑定] 上限:不限(config 里 {KeyBindMax}=0)");

        // 免确认保持天数:缺配置写默认(30 天),0 = 不限期
        var persistDaysText = config.MapGet(KeyPersistDays).Trim();   //配置里的天数
        if (persistDaysText.Length == 0)
        {
            config.WriteFileMap(KeyPersistDays, DefaultPersistDays.ToString());
            persistDaysText = DefaultPersistDays.ToString();
            Logger.Log(LogLevel.Info, $"[CONFIG]{KeyPersistDays} 未配置,已写入默认值 {DefaultPersistDays}(免确认保持天数,0 = 不限期)");
        }

        _persistDays = int.TryParse(persistDaysText, out var parsedPersistDays) && parsedPersistDays >= 0
            ? parsedPersistDays
            : DefaultPersistDays;

        // 每次都要确认:开着就把持久登录表清掉,让所有人下一次进服都重新点一次
        _confirmAlways = config.MapGet(KeyConfirmAlways).Trim() == "1";
        if (_confirmAlways)
        {
            LoginStore.ClearPersistentLogins();
        }

        Logger.Log(LogLevel.Info, _confirmAlways
            ? $"[免确认] 关:每次进服都要确认(config 里 {KeyConfirmAlways}=0 可恢复)"
            : _persistDays > 0
                ? $"[免确认] 开:同玩家同 IP {_persistDays} 天内不用再确认(config 里 {KeyPersistDays} 可改,0 = 不限期)"
                : $"[免确认] 开:同玩家同 IP 永久免确认(config 里 {KeyPersistDays}=0)");

        // 机器码必填:缺配置就写默认(1 = 开),让服主知道有这个开关
        var machineRequireText = config.MapGet(KeyMachineRequire).Trim();   //配置里的开关值
        if (machineRequireText.Length == 0)
        {
            machineRequireText = DefaultMachineRequire ? "1" : "0";
            config.WriteFileMap(KeyMachineRequire, machineRequireText);
            Logger.Log(LogLevel.Info,
                $"[CONFIG]{KeyMachineRequire} 未配置,已写入默认值 {machineRequireText}(1 = 取不到主板序列号就拒绝登录/注册)");
        }

        _machineRequire = machineRequireText != "0";
        Logger.Log(LogLevel.Info, _machineRequire
            ? $"[机器码] 必填:开(客户端取不到主板序列号就拒绝登录/注册;config 里 {KeyMachineRequire}=0 可关)"
            : $"[机器码] 必填:关(取不到主板序列号也能登录,但不记录机器码,机器码封禁对这类账号无效)");

        // 令牌:和模组 config 里的 api.token 一致才校验。两边都空 = 不校验(默认)
        _zxmmToken = config.MapGet(KeyZxmmToken).Trim();
        Logger.Log(LogLevel.Info, _zxmmToken.Length > 0
            ? "[ZXMM] 令牌校验:开(config 里 zxmmtoken 非空)"
            : $"[ZXMM] 令牌校验:关(模组的 api.token 也留空才这样;要在 config 里补 {KeyZxmmToken}=…)");

        // 多服数据分离:先读登记表,再读开关与键表,最后按上次留下的快照做一次迁移。
        // 三步的顺序不能换 —— 迁移要按登记表列服务器、按键表定位文件,而且必须在绑定表被读之前跑完。
        // 每一步的结论(读到几台 / 开关与键 / 迁没迁)在各自的类里已经打了日志,这里只补一句汇总,不重复刷屏。
        // 控制台:ZXMM 连接表(Data/ZXMMC.dat,合并了早期的 Servers.txt)先读进来,
        // 再把两条查询函数交给 ConsoleRouter —— 之后所有"把命令送到某台服"的调用都从那里走。
        var (tableHost, _) = SplitZxmmApiAddress(_zxmmApiAddress);
        Zxms.ZxmmRegistry.Load(tableHost);
        Zxms.ConsoleRouter.Attach(
            id => Zxms.ZxmmRegistry.TryGet(id, out var entry) ? entry : null,
            ZxmmApiOf);
        Servers.Load();
        DataSeparation.Load(config);
        SeparationMigration.Run(config);
        Logger.Log(LogLevel.Info, $"[数据分离] 启动就绪:登记服务器 {Servers.All.Count} 台," +
            $"开关={(DataSeparation.Enabled ? "1(按服分目录)" : "0(全部共享)")}," +
            $"分离键={(DataSeparation.SeparatedKeys.Count == 0 ? "无" : string.Join(",", DataSeparation.SeparatedKeys))}");

        // 和 ZXMM 建一条常驻连接:它推"玩家进服",我们下发"踢人/查询"任务
        var zxmm = CreateZxmmApi();
        _zxmmApi = zxmm;
        zxmm.PlayerJoined += (player, ip) =>
        {
            // 异常必须打出来:Task.Run 里的异常没人看,静默吞掉就等于这条事件凭空消失
            _ = Task.Run(async () =>
            {
                try
                {
                    // 这条事件属于哪台 MC 服:多服数据分离时绑定表就按它分目录(SS-<id>)
                    LoginStore.CurrentScope.Value = DataSeparation.Scope("bind", zxmm.ServerId);
                    await HandlePlayerJoinedAsync(player, ip);
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Error, $"[ZXMM] 处理进服事件异常({player}): {ex}");
                }
                finally
                {
                    LoginStore.CurrentScope.Value = "";   // 线程会被复用,别把分区带给下一件事
                }
            });
        };
        zxmm.PlayerJoining += (player, ip) =>
        {
            // configuration 阶段就来问一句:能放行就不回应,不能就踢(客户端显示理由、不会卡在加载界面)
            _ = Task.Run(async () =>
            {
                try
                {
                    LoginStore.CurrentScope.Value = DataSeparation.Scope("bind", zxmm.ServerId);
                    await HandlePlayerJoiningAsync(player, ip);
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Error, $"[ZXMM] 处理连接中事件异常({player}): {ex}");
                }
                finally
                {
                    LoginStore.CurrentScope.Value = "";
                }
            });
        };
        zxmm.Start();

        // 登记表里还有别的服务器:按同一套 host + 各自的端口把专用通道一并连上(本期只建连备查)
        ConnectAllZxmm();

        // Connected 要等握手才为真,所以刚启动时这里的「在线」经常是 0,属正常
        var bridgeOnline = 0;   //ZXMM 通道已经握上手的台数
        foreach (var entry in Servers.All)
        {
            if (Zxms.ConsoleRouter.IsBridgeOnline(entry.Id))
            {
                bridgeOnline++;
            }
        }

        Logger.Log(LogLevel.Info, $"[控制台] 登记 {Servers.All.Count} 台服务器,桥接在线 {bridgeOnline} 台");

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

        // 聊天后端:BareChat 宿主跑独立进程(NativeAOT 编不进 ASP.NET Core),起不来只影响好友聊天
        StartChatHost(config);

        // 聊天后端:BareChat 宿主跑独立进程(NativeAOT 编不进 ASP.NET Core),起不来只影响好友聊天

        // ================= 启动监听:ZXMC / ZXMM 都连这个端口 =================
        _server = new TcpServer(IPAddress.Any, ushort.Parse(_czport));

        // 有人接进来:先中立记录身份未知,再主动报上"我是 ZXMS",让客户端按模式校验
        _server.Connected += async conn =>
        {
            Logger.Log(LogLevel.Info, $"[网络] 连接接入 {Where(conn)}(等待握手识别身份)");

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
                var squareName = config.MapGet(KeySquareName).Trim();   //配置值
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

            // 限速桶跟着连接一起丢掉,不然爆破时字典只增不减
            _packetLimiter.Forget(conn.Id.ToString());
            RateWarnedAt.TryRemove(conn.Id.ToString(), out _);

            // 记录过切服的连接也一起丢掉,不清理这个字典只增不减
            lock (_clientServerLock)
            {
                _clientServer.Remove(conn);
            }

            // 收控制台输出的订阅同样按连接清掉,否则推送会一直往死连接上写
            int? tappedServer = null;   //这条连接原来在收哪台:断开后要重算那台的开关
            lock (_consoleTapLock)
            {
                if (_consoleTaps.TryGetValue(conn, out var tapped))
                {
                    tappedServer = tapped;
                }

                _consoleTaps.Remove(conn);
            }

            if (tappedServer is int lastTapped)
            {
                RefreshTap(lastTapped);
            }

            Logger.Log(LogLevel.Info, $"[{role}] 断开 {Where(conn)}");
            if (info?.User is { Length: > 0 } gone)
            {
                _ = AnnouncePresenceAsync(gone, Protocol.FriendEventKind.Offline);
            }
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

            // 官方端断线自动重连:每 5 秒看一次,断了打日志并尝试恢复(不需要服主敲 net reconnect)
            _ = Task.Run(async () =>
            {
                var hadLink = _os is { IsConnected: true };   //启动时就连上的话,后续断开才提示
                while (true)
                {
                    await Task.Delay(5000);

                    // 顺带扫一遍等不回来的人:等别服头像超时、等别服回执超时,别让客户端一直挂着
                    await SweepAvatarWaitersAsync();

                    if (_os is { IsConnected: true })
                    {
                        if (!hadLink)
                        {
                            hadLink = true;
                            Logger.Log(LogLevel.Info, "[ZXMOS] 官方服务端连接已恢复");

                            // 断线期间别服收不到我们的上下线,重连后把本服在线的人整份补广播一遍
                            _ = ReannounceAfterReconnectAsync(config);
                        }

                        continue;
                    }

                    if (hadLink)
                    {
                        hadLink = false;
                        Logger.Log(LogLevel.Warning, "[ZXMOS] 与官方服务端的连接已断开,正在自动重连…");

                        // 断线期间的上下线广播都丢了,别服的在线状态不可信 → 清表,别给过期数据
                        ClearRemoteOnline();
                    }

                    if (Interlocked.CompareExchange(ref _osAutoReconnecting, 1, 0) != 0)
                    {
                        continue;
                    }

                    try
                    {
                        await ConnectOServerAsync(config, interactive: false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(LogLevel.Warning, "[ZXMOS] 自动重连失败:" + ex.Message);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _osAutoReconnecting, 0);
                    }
                }
            });
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

                    // 连接阶段就踢走的玩家:记录清掉就行,不再发超时通知、也不再踢(人早不在线)
                    if (late.KickedAtJoining)
                    {
                        continue;
                    }

                    var origin = _server?.Connections.FirstOrDefault(c => c.Id == late.OriginConnId);
                    if (origin is not null)
                    {
                        try { await origin.SendAsync(Protocol.LoginResult, $"timeout|{late.Player}", 0, "ZXMM"); }
                        catch { }
                    }

                    await SendToAccountAsync(late.AccountId, Protocol.LoginTimeout, $"{pair.Key}|{late.Player}");

                    // 新架构:ZXMS 只发配任务,踢人交给 ZXMM 的执行接口(玩家若还挂在服里就踢掉)
                    await KickViaZxmmAsync(late.Player, "登录确认超时(5 分钟)，请重新进入服务器");
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
            whitelistProvider: () => Whitelist,
            serverAccountNameProvider: () => config.MapGet(KeyOsUser).Trim(),
            enterConfig: Command.ConfigMode.Enter,
            zxmmProvider: () => _zxmmApi,
            serverProvider: () => _server);

        // 配置模式:进入后每一行都交给它,不再当命令分发
        cmd.LineInterceptor = line => Command.ConfigMode.Handle(line);
        cmd.IsConfigMode = () => Command.ConfigMode.Active;

        cmd.Start();
        Logger.Log(LogLevel.Info, "启动成功");
        await Task.Delay(Timeout.Infinite);
    }

    /// <summary>本 ZXMS 在 ZXMOS 上注册的账号名(config.ini 的 osuser)</summary>
    private const string KeyOsUser = "osuser";

    /// <summary>该账号的密码密文(config.ini 的 oshash;明文不落盘)。</summary>
    private const string KeyOsHash = "oshash";

    /// <summary>该账号的等级(config.ini 的 oslevel;ZXMOS 上子服务端账号是 Admin / System)。</summary>
    private const string KeyOsLevel = "oslevel";

    /// <summary>本端自己发给 ZXMOS 的账号请求:包 Id → 等回执的等待者(601 登录 / 603 注册 / 612 改名)。 按包 Id 存字典,登录与改名各等各的,不会互相把等待者顶掉。</summary>
    private static readonly ConcurrentDictionary<ulong, TaskCompletionSource<string>> OsAcks = new();

    /// <summary>本端自己发的包 Id 计数(从 10 万起,和"替客户端中转"的 relay Id 段分开)。</summary>
    private static ulong _osOwnPacketId = 100_000;

    /// <summary>用 ZXMS 账号登录 ZXMOS:没注册过就顺手注册(ZXMOS 给子服务端账号 Admin 级,并记下本机 IP)。 账号 / 密码没配过时在这里问一次,密码算成密文写回 config.ini。</summary>
    private static async Task LoginOServerAsync(TcpClient client, ZFile config)
    {
        var user = AskOsUser(config);

        // 密文没有就不问明文存明文,而是问一次、当场算成密文存下来
        var hash = config.MapGet(KeyOsHash).Trim();   //配置值
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
        var serverCode = config.MapGet(KeyServerCode).Trim().ToLowerInvariant();   //配置值
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
    /// <summary>官方端登录 / 注册成功后的账号信息缓存(name → 等级):本地没有时用它按需镜像。</summary>
    private static readonly ConcurrentDictionary<string, string> OfficialAccounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 取账号信息(绑定 / 解绑 / 头像 / 查询都走这里,不再直接查本地表):
    /// 本地有 → 用本地;本地没有但官方端给过这个账号的信息 → 镜像成本地壳账号再用;两端都没有 → null。
    /// </summary>
    private static Task<Account?> ResolveAccountAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult<Account?>(null);
        }

        var local = Accounts.Find(name);
        if (local is not null)
        {
            return Task.FromResult<Account?>(local);
        }

        if (!OfficialAccounts.TryGetValue(name, out var level))
        {
            return Task.FromResult<Account?>(null);
        }

        // 占位密文(随机 64 hex):官方账号走官方端校验,本地永远不会匹配上它
        var placeholder = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        if (Accounts.Add(name, placeholder, level, out var created, out var error))
        {
            Logger.Log(LogLevel.Info, $"[账号] 官方账号 {name} 按需镜像到本地(绑定 / 管理可用)");
            return Task.FromResult<Account?>(created);
        }

        Logger.Log(LogLevel.Warning, $"[账号] 按需镜像官方账号 {name} 失败:{error}");
        return Task.FromResult(Accounts.Find(name));
    }

    private static void RegisterRelayedAccount(Guid connectionId, string reply)
    {
        if (!reply.StartsWith("ok|"))
        {
            return;
        }

        var fields = reply.Split('|');   //拆分结果
        var name = fields.Length > 1 ? fields[1].Trim() : "";
        var level = fields.Length > 3 ? fields[3].Trim() : Protocol.RegisterLevel;

        if (name.Length == 0)
        {
            return;
        }

        _server?.Registry.SetAccount(connectionId, name, level);

        // 记下官方端给过的账号信息,并立刻镜像一次:后续绑定/解绑/查询按本地 ID 正常走
        OfficialAccounts[name] = level;
        _ = ResolveAccountAsync(name);

        Logger.Log(LogLevel.Info, $"[账号] 本服在线名单已登记中转账号:{name}({level})");

        // 中转登录不走 OnPlayerSignedInAsync,聊天后端的门牌(510)和好友上线广播都得在这条路上补发
        _ = AnnouncePresenceByNameAsync(name);
        var chatConnection = _server?.Connections.FirstOrDefault(one => one.Id == connectionId);
        if (chatConnection is not null && _config is not null)
        {
            _ = SendChatHelloByNameAsync(chatConnection, name, _config);
        }
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

    /// <summary>改本端在官方服务端的账号名(顺带改密码):发 611 → 等 612 → 成功才写本地 config。 顺序不能反:先写本地的话,服务器一旦拒绝,config 里就留了一个对不上的账号,下次接入就认证不过去。</summary>
    private static async Task<string> RenameOsAccountAsync(ZFile config, string newName, string newPassword)
    {
        var os = _os;
        if (os is null || !os.IsConnected)
        {
            return "err|官方服务端未连接,请先 net reconnect";
        }

        var oldName = config.MapGet(KeyOsUser).Trim();   //配置值
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

    /// <summary>问本端在 ZXMOS 上的账号名。**控制台命令不算数**:以前有人在提问里输了 `srv show`, 结果官方端账号就叫 "srv show" —— 这里直接拒掉并重问(带空格的名字也不收)。</summary>
    private static string AskOsUser(ZFile config)
    {
        var current = config.MapGet(KeyOsUser).Trim();   //配置值
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

    /// <summary>转发到 ZXMOS 的账号请求:ZXMOS 回执的包 Id → (哪个 ZXMC 连接, 它原来的包 Id)。 ZXMS 只是中转,ZXMC 完全不用知道账号到底在哪边校验的。</summary>
    private static readonly ConcurrentDictionary<ulong, (Guid Connection, ulong PacketId)> AccountRelay = new();

    /// <summary>转给 ZXMOS 的补发请求:包 Id → (哪条 ZXMC 连接要的, 它原来的包 Id, 本服先给了几条)。 ZXMOS 的 504 一条条按这个 Id 找到人转过去;506 到了把"官方条数 + 本服条数"合成一条收尾回给客户端。</summary>
    private static readonly ConcurrentDictionary<ulong, (Guid Connection, ulong PacketId, int LocalSent)> HistoryRelay = new();

    /// <summary>官方验证第二层:等官方服务端回证明(521)的等待者 + 这次发出去的 nonce。 本端同时只连一台官方服务端,所以两个字段够用。</summary>
    private static TaskCompletionSource<string>? _officialWait;

    /// <summary>和 ZXMOS 协商密钥时等 381 用。</summary>
    private static TaskCompletionSource<byte[]>? _osSessionWait;

    private static string _officialNonce = "";

    private static ulong _relayId = 1000;

    /// <summary>这个账号请求该在哪个端处理: 客户端明确指定了端(zxms / zxmos)就听它的; 没指定:玩家账号(Player)官方优先 → zxmos;管理账号按 ZXMS 处理。 载荷第 4 个字段就是"端"("名称|密文|等级|端")。</summary>
    private static string DecideAccountSide(string level, string side)
    {
        side = side.Trim().ToLowerInvariant();
        if (side is "zxms" or "zxmos")
        {
            return side;
        }

        return level == Protocol.RegisterLevel ? "zxmos" : "zxms";
    }

    /// <summary>强制更新要用的门槛版本:配了 minversion 就用它,没配就是本端自己的版本号。</summary>
    private static string MinimumVersion => _minVersion.Length > 0 ? _minVersion : AppVersion;

    /// <summary>
    /// 强制更新:这个客户端版本能不能放进来。返回 null = 放行;返回非空 = 该回给玩家的原因。
    /// <para>开关没开一律放行(开关关着时版本号对错都不该拦人)。开关开着而版本缺失(老客户端没报版本)
    /// 按"太旧"处理 —— 否则只要不发最后一段就能绕过强制更新。</para>
    /// </summary>
    /// <param name="clientVersion">客户端自报的版本号(可能为空 = 老客户端)。</param>
    /// <param name="protocol">"登录" / "注册",只用于日志与文案。</param>
    /// <param name="user">账号名,只用于日志。</param>
    private static string? RejectOutdatedClient(string clientVersion, string protocol, string user)
    {
        if (!_forceUpdate)
        {
            return null;
        }

        var need = MinimumVersion;   //本服要求的最低版本
        var ok = clientVersion.Length > 0 && VersionRules.Compare(clientVersion, need) >= 0;

        if (!ok)
        {
            Logger.Log(LogLevel.Warning,
                $"[强制更新] 拒绝{protocol} {user}(客户端={DescribeVersion(clientVersion)},本条要求 ≥ {need})");
            var shown = clientVersion.Length > 0 ? clientVersion : "老客户端未上报版本";   //给玩家看的版本描述:空版本说人话,不写「未知版本」
            return $"{Protocol.UpdateRequiredMarker}:请更新到最新版 ZXMC(当前 {shown},本服要求 {need})。新版下载: {Protocol.DownloadPageUrl}";
        }

        Logger.Log(LogLevel.Info, $"[强制更新] {protocol}放行 {user}(客户端={clientVersion} ≥ {need})");
        return null;
    }

    /// <summary>版本号给日志 / 弹窗看的样子:空串说成「未知版本」,别让玩家看到一个空括号。</summary>
    private static string DescribeVersion(string version)
        => version.Length > 0 ? version : "未知版本";

    /// <summary>
    /// 填进来的字符串像不像版本号:字母开头可选,后面至少有一位数字,只允许数字 / 字母 / <c>. - +</c>。
    /// <para>不校验就不能拦:随便填个 "abc" 会被解析成 0.0.0,门槛永远高于任何客户端,全服都进不来。</para>
    /// </summary>
    private static bool LooksLikeVersion(string version)
    {
        var text = version.Trim();   //去掉两侧空格
        var digits = 0;   //数字字符的个数

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (char.IsDigit(c))
            {
                digits++;
                continue;
            }

            if (char.IsAsciiLetter(c) || c is '.' or '-' or '+')
            {
                continue;
            }

            return false;
        }

        return digits > 0;
    }

    /// <summary>
    /// 强制更新开启后,把在线且版本不够的 ZXMC 踢下线:先发 634 讲清原因,再断开。
    /// <para>不 await 在调用方等它:发包带 10 秒写超时,处理命令的线程不该为别人的连接卡住。</para>
    /// </summary>
    /// <returns>踢掉几条连接(当场统计,真正的断开在后台完成)。</returns>
    private static int KickOutdatedClients()
    {
        var server = _server;
        if (server is null || !_forceUpdate)
        {
            return 0;
        }

        var need = MinimumVersion;   //本服要求的最低版本
        var victims = new List<TcpConnection>();   //要踢的连接
        var names = new List<string>();   //各自的账号名(只用于日志)

        foreach (var conn in server.Connections)
        {
            if (conn.IsClosed)
            {
                continue;
            }

            var info = server.Registry.Find(conn.Id);
            if (info?.Role != "ZXMC")
            {
                continue;
            }

            if (info.Version.Length > 0 && VersionRules.Compare(info.Version, need) >= 0)
            {
                continue;   //版本够,留着
            }

            victims.Add(conn);
            names.Add($"{info.User}({DescribeVersion(info.Version)})");
        }

        if (victims.Count == 0)
        {
            return 0;
        }

        Logger.Log(LogLevel.Warning,
            $"[强制更新] 踢下线 {victims.Count} 个旧版客户端(本服要求 ≥ {need}):{string.Join("、", names)}");
        _ = Task.Run(KickAsync);

        return victims.Count;

        async Task KickAsync()
        {
            // 先把原因发出去再断:顺序反了客户端只会看到"连接已断开"
            foreach (var conn in victims)
            {
                try
                {
                    await conn.SendAsync(Protocol.ClientTooOld,
                        $"{Protocol.UpdateRequiredMarker}:请更新到最新版 ZXMC(本服要求 {need})。新版下载: {Protocol.DownloadPageUrl}", 0, "ZXMC");
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Warning, $"[强制更新] 通知 {Where(conn)} 失败(对方可能已经断了): {ex.Message}");
                }
            }

            // 断开前留一点时间让包真的发出去(写是同步落 socket 的,这里只是保险)
            await Task.Delay(200);

            foreach (var conn in victims)
            {
                try
                {
                    await conn.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Warning, $"[强制更新] 断开 {Where(conn)} 失败: {ex.Message}");
                }
            }
        }
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
        var userId = (await ResolveAccountAsync(who))?.Id ?? 0;

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

        // 收包是单 worker 串行的:一条客户端连接卡在写包上,逐个 await 会把后面所有人的推送堵住。
        // 先收齐该发给谁,再并发发出去
        var targets = new List<TcpConnection>();   //该收到这条推送的连接
        foreach (var conn in server.Connections)
        {
            if (conn.IsClosed)
            {
                continue;   //已经在关了,发过去只会抛 disposed
            }

            var info = server.Registry.Find(conn.Id);
            if (info?.Role != "ZXMC")
            {
                continue;
            }

            if (target is { Length: > 0 } && !string.Equals(info.User, target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            targets.Add(conn);
        }

        var sent = 0;
        await Task.WhenAll(targets.Select(async conn =>
        {
            try
            {
                await conn.SendAsync(type, payload, 0, "ZXMC");
                Interlocked.Increment(ref sent);
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, $"[消息] 发给 {Where(conn)} 失败(可能对方刚好下线): {ex.Message}");
            }
        }));

        return sent;
    }

    /// <summary>把玩家的卡片动作转交给官方服务端(负载前面加一段账号,官方端才知道是谁点的)。 官方服务端不回执 —— 这条链路的回执只到"已转交",玩家那边已经拿到 ZXMS 的回话了。</summary>
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
            "backgetcount" => _backgetCount.ToString(),
            "officialcheck" => _officialCheck ? "1" : "0",
            "ozname" => config.MapGet(KeyOsName).Trim(),
            "servercode" => config.MapGet(KeyServerCode).Trim(),
            "oshash" => config.MapGet(KeyOsHash).Trim(),
            "configver" => config.MapGet(KeyConfigVer).Trim(),
            "packetrate" => config.MapGet(KeyPacketRate).Trim(),
            "loginrate" => config.MapGet(KeyLoginRate).Trim(),
            "forceupdate" => _forceUpdate ? "1" : "0",
            "minversion" => _minVersion,
            "bindmax" => _bindMax.ToString(),
            KeyPersistDays => _persistDays.ToString(),
            KeyConfirmAlways => _confirmAlways ? "1" : "0",
            KeyMachineRequire => _machineRequire ? "1" : "0",
            KeyDataSeparation => DataSeparation.Enabled ? "1" : "0",
            KeyDataSeparationList => string.Join(",", DataSeparation.SeparatedKeys),
            _ => "",
        };
    }

    /// <summary>配置模式里改一个键:校验 → 写 config.ini → 能立刻生效的立刻生效。返回空串表示成功。</summary>
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

            case "backgetcount":
                if (!int.TryParse(value, out var backgetCount) || backgetCount is < 1 or > 1000)
                {
                    return "范围 1-1000(玩家返回记录最多查几条)";
                }

                _backgetCount = backgetCount;
                config.WriteFileMap(KeyBackget, backgetCount.ToString());
                Logger.Log(LogLevel.Info, $"[配置] 玩家返回查询条数已设为 {backgetCount}");
                return "";

            case "packetrate":
                if (!int.TryParse(value, out var packetRate) || packetRate is < 0 or > 10000)
                {
                    return "范围 0-10000(每连接每秒最多几个包,0 = 不限)";
                }

                _packetLimiter = new RateLimiter(packetRate);
                RateWarnedAt.Clear();
                config.WriteFileMap(KeyPacketRate, packetRate.ToString());
                Logger.Log(LogLevel.Info, packetRate > 0
                    ? $"[限速] 每连接发包上限已改成 {packetRate} 包/秒(新连接立刻生效)"
                    : "[限速] 每连接发包已改为不限");
                return "";

            case "loginrate":
                if (!int.TryParse(value, out var loginRate) || loginRate is < 0 or > 10000)
                {
                    return $"范围 0-10000(每 IP 每 {LoginRateWindowSeconds / 60} 分钟最多失败几次登录/注册,0 = 不限)";
                }

                _loginLimiter = new FailureThrottle(loginRate, LoginRateWindowSeconds);
                config.WriteFileMap(KeyLoginRate, loginRate.ToString());
                Logger.Log(LogLevel.Info, loginRate > 0
                    ? $"[限速] 登录失败上限已改成每 IP 每 {LoginRateWindowSeconds / 60} 分钟 {loginRate} 次"
                    : "[限速] 登录失败次数已改为不限");
                return "";

            case "forceupdate":
                if (value is not ("0" or "1"))
                {
                    return "只能填 1(旧版拒绝登录并踢下线)或 0(不检查)";
                }

                _forceUpdate = value == "1";
                config.WriteFileMap(KeyForceUpdate, value);
                if (!_forceUpdate)
                {
                    Logger.Log(LogLevel.Info, "[强制更新] 已关闭,版本不再拦截");
                    return "";
                }

                // 开着就得有个门槛:minversion 留空时用本端版本
                Logger.Log(LogLevel.Info, $"[强制更新] 已开启,门槛 {MinimumVersion};正在检查在线客户端");
                var kicked = KickOutdatedClients();
                Logger.Log(LogLevel.Info, kicked > 0
                    ? $"[强制更新] 在线旧版客户端 {kicked} 个已踢下线"
                    : "[强制更新] 在线客户端版本都够,没人被踢");
                return "";

            case "bindmax":
                if (!int.TryParse(value, out var bindMax) || bindMax is < 0 or > 1000)
                {
                    return "范围 0-1000(一个账号最多绑几个 MC 玩家,0 = 不限)";
                }

                _bindMax = bindMax;
                config.WriteFileMap(KeyBindMax, bindMax.ToString());
                Logger.Log(LogLevel.Info, bindMax > 0
                    ? $"[绑定] 上限已改成 {bindMax} 个玩家/账号(立刻生效)"
                    : "[绑定] 上限已改为不限(立刻生效)");
                return "";

            case KeyPersistDays:
                if (!int.TryParse(value, out var persistDays) || persistDays is < 0 or > 3650)
                {
                    return "范围 0-3650(免确认保持多少天,0 = 不限期)";
                }

                _persistDays = persistDays;
                config.WriteFileMap(KeyPersistDays, persistDays.ToString());
                Logger.Log(LogLevel.Info, persistDays > 0
                    ? $"[免确认] 保持天数已改成 {persistDays} 天(立刻生效)"
                    : "[免确认] 已改为不限期(立刻生效)");
                return "";

            case KeyConfirmAlways:
                if (value is not ("0" or "1"))
                {
                    return "只能是 1(每次都要确认)或 0(按天数免确认)";
                }

                _confirmAlways = value == "1";
                config.WriteFileMap(KeyConfirmAlways, value);
                if (_confirmAlways)
                {
                    LoginStore.ClearPersistentLogins();   //开了就当所有人重新确认一次
                }

                Logger.Log(LogLevel.Info, _confirmAlways
                    ? "[免确认] 已关闭:持久登录表已清空,之后每次进服都要确认"
                    : "[免确认] 已开启:同玩家同 IP 在期限内不用再确认");
                return "";

            case KeyMachineRequire:
                if (value is not ("0" or "1"))
                {
                    return "只能填 1(取不到主板序列号就拒绝登录/注册)或 0(允许登录但不记录机器码)";
                }

                _machineRequire = value == "1";
                config.WriteFileMap(KeyMachineRequire, value);
                Logger.Log(LogLevel.Info, _machineRequire
                    ? "[机器码] 已开启必填:取不到主板序列号的客户端拒绝登录/注册(立刻生效)"
                    : "[机器码] 已关闭必填:取不到主板序列号也能登录,但不记录机器码,机器码封禁对这类账号无效(立刻生效)");
                return "";

            case "minversion":
                // 留空 = 跟本端版本一致;填了必须是能认出类型的版本号,否则比较永远判旧、谁都进不来
                if (value.Length > 0 && !LooksLikeVersion(value))
                {
                    return "要填版本号(如 b0.3.2a),留空表示跟本端版本一致";
                }

                _minVersion = value;
                config.WriteFileMap(KeyMinVersion, value);
                Logger.Log(LogLevel.Info, $"[强制更新] 门槛已设为 {MinimumVersion}");
                if (_forceUpdate)
                {
                    var kickedByVersion = KickOutdatedClients();
                    Logger.Log(LogLevel.Info, kickedByVersion > 0
                        ? $"[强制更新] 在线旧版客户端 {kickedByVersion} 个已踢下线"
                        : "[强制更新] 在线客户端版本都够,没人被踢");
                }

                return "";

            case KeyDataSeparation:
                // 只写文件:开关在启动时读进静态状态,热改会让内存里的分区规则和磁盘对不上
                if (value is not ("0" or "1"))
                {
                    return "只能是 0 或 1";
                }

                config.WriteFileMap(KeyDataSeparation, value);
                Logger.Log(LogLevel.Warning, $"[数据分离] 开关已写进 config = {value},重启 ZXMS 后生效");
                return "";

            case KeyDataSeparationList:
            {
                var separatedKeys = new List<int>();   //规范化后的键号(升序去重)
                foreach (var raw in value.Split(','))
                {
                    var item = raw.Trim();
                    if (item.Length == 0)
                    {
                        continue;
                    }

                    if (!int.TryParse(item, out var parsedKey) || parsedKey <= 0 || !DataSeparation.KeyTable.ContainsKey(parsedKey))
                    {
                        return $"键 {item} 不在 {DataSeparation.KeyTableFile} 里";
                    }

                    if (!separatedKeys.Contains(parsedKey))
                    {
                        separatedKeys.Add(parsedKey);
                    }
                }

                separatedKeys.Sort();
                var keysText = string.Join(",", separatedKeys);
                config.WriteFileMap(KeyDataSeparationList, keysText);
                Logger.Log(LogLevel.Warning,
                    $"[数据分离] 分离键已写进 config = [{(keysText.Length == 0 ? "无" : keysText)}],重启 ZXMS 后生效");
                return "";
            }

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

    /// <summary>控制台 notice:给本服所有客户端发一条公告;返回投递条数。来源打成 ZXMS-&lt;本服名称&gt;。 先入库拿到本服消息号,再发<b>同一张</b>卡 —— 这样在线收到的和以后补发的能对上(去重靠来源 + 号)。</summary>
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

    /// <summary>连官方服务端:关旧连接 → 读配置 → 建连接 → 报上线 → 验对方身份。<paramref name="interactive"/> = false 时(自动重连)不读控制台,缺配置直接用兜底值。</summary>
    private static async Task ConnectOServerAsync(ZFile config, bool interactive = true)
    {
        // 重连时旧对象不能复用,先关掉
        if (_os is not null)
        {
            await _os.CloseAsync();
            _os = null;
        }

        // 连官方端时还能改一次本端名称:只有"还是默认名"时才问,免得每次重连都打扰
        var nameNow = ResolveName(config);
        if (interactive && (nameNow.Length == 0 || nameNow == DefaultName))
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
            fallback: "127.0.0.1", interactive: interactive);

        var ozport = AskIfMissing(config, KeyOzport,
            "[CONFIG]输入官方服务端端口ozport(1-65535)",
            value => ushort.TryParse(value, out var n) && n > 0,
            fallback: DefaultOsPort, interactive: interactive);

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
                    var role = Encoding.UTF8.GetString(packet.Data).Trim();   //包内容
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

                // ---- 好友:官方服务端推来的东西,这里只做分发 ----
                case Protocol.UserListDown:
                {
                    var listPayload = Encoding.UTF8.GetString(packet.Data);   //包内容
                    var (total, withServer) = IndexUserList(listPayload);
                    SaveUserList(listPayload, config);
                    Logger.Log(LogLevel.Info, $"[用户列表] 收到 ZXMOS 下发的用户列表:{total} 个账号,带归属服 {withServer} 条");
                    break;
                }

                case Protocol.FriendRelay:
                    await HandleFriendRelayAsync(packet, config);
                    break;

                // 中转没送到的明确回执(401/655/659/661):官方端找不到对方子服时才会发,按包 Id 找回发起人
                case Protocol.FriendMsgAck:
                case Protocol.FriendRequestAck:
                case Protocol.FriendAnswerAck:
                case Protocol.FriendRemoveAck:
                    await HandleRelayFailureAsync(packet);
                    break;

                case Protocol.FriendPresence:
                    await HandleFriendPresenceAsync(packet, config);
                    break;

                case Protocol.ServerNameMap:
                    HandleServerNameMapAsync(packet, config);
                    break;

                case Protocol.AvatarRelay:
                    await HandleAvatarRelayAsync(packet);
                    break;

                case Protocol.AvatarRelayAck:
                    await HandleAvatarRelayAckAsync(packet, config);
                    break;

                // 官方服务端自报名称(201):记下来给 net status 显示,也写进 config
                case Protocol.ServerName:
                {
                    var osName = Encoding.UTF8.GetString(packet.Data).Trim();   //包内容
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
                    var lines = Encoding.UTF8.GetString(packet.Data).Split('\n', StringSplitOptions.RemoveEmptyEntries);   //包内容
                    var ok = 0;
                    foreach (var line in lines)
                    {
                        var f = line.Trim().Split('|');   //拆分结果
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
                    var text = Encoding.UTF8.GetString(packet.Data).Trim();   //包内容

                    // ① 是替某个 ZXMC 转发的 → 用它原来的包 Id 转回去
                    if (AccountRelay.TryRemove(packet.Id, out var pending))
                    {
                        var target = _server?.Connections.FirstOrDefault(c => c.Id == pending.Connection);
                        if (target is not null)
                        {
                            // 先登记 + 建本地镜像:第五段「完整ID」要用本地编号,不能凭空算
                            RegisterRelayedAccount(pending.Connection, text);

                            var reply = text;   //转给客户端的负载
                            var forwarded = text.Split('|');   //拆分结果
                            var mirrored = forwarded.Length > 1 ? Accounts.Find(forwarded[1].Trim()) : null;
                            if (mirrored is null && forwarded.Length > 1)
                            {
                                mirrored = await ResolveAccountAsync(forwarded[1].Trim());
                            }

                            var fullId = mirrored is null ? "" : LocalPlayerId(mirrored, config).Full;
                            if (text.StartsWith("ok|") && fullId.Length > 0)
                            {
                                reply = text + "|" + fullId;
                            }

                            await target.WriteAsync(new Packet(pending.PacketId, packet.Type, Encoding.UTF8.GetBytes(reply)));
                            Logger.Log(LogLevel.Info, $"[ZXMOS] 账号回执已转回客户端:{reply}");
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
                    var text = Encoding.UTF8.GetString(packet.Data);   //包内容

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
                    var card = Encoding.UTF8.GetString(packet.Data);   //包内容
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
                    var text = Encoding.UTF8.GetString(packet.Data);   //包内容

                    if (!MessageCard.TryParsePush(text, out var target, out var card))
                    {
                        Logger.Log(LogLevel.Warning, "[消息] 定向推送的负载格式不对,已忽略:" + text);
                        break;
                    }

                    var selfName = config.MapGet(KeyOsUser).Trim();   //配置值

                    // ① 官方服务端点名给本端(子服自己):控制台醒目打印,不转给客户端
                    if (target.StartsWith("@server:", StringComparison.OrdinalIgnoreCase))
                    {
                        var who = target[8..].Trim();   //玩家名
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
                        var who = target[9..].Trim();   //玩家名
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
