using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZincXManagerShared.Account;
using ZincXManagerShared.Command;
using ZincXManagerShared.FileIO;
using ZincXManagerShared.Logging;
using ZincXManagerShared.Network;
using ZincXManagerServer.Command;

// 与 BCL 的 TcpClient 同名,起个别名
using TcpClient = ZincXManagerShared.Network.TcpClient;

namespace ZincXManagerServer;

internal static class Program
{
    // 配置键名:ozip/ozport = 官方服务端地址端口,czport = 客户端接入端口
    // 缺省值:正式环境一般不用,只在配置缺失且拿不到输入时兜底
    /// <summary>本端名称(config.ini 的 name):客户端连进来时发给它。</summary>
    private const string KeyName = "name";

    /// <summary>名称没配时的兜底值。</summary>
    private const string DefaultName = "锌能源服务器";

    /// <summary>官方服务端名称(收到 201 后写进 config.ini 的 ozname)。</summary>
    private const string KeyOsName = "ozname";

    private const string KeyConnectOs = "connectOS";
    private const string KeyOzip = "ozip";
    private const string KeyOzport = "ozport";
    private const string KeyCzport = "czport";
    private const string DefaultOsPort = "25565";
    private const string DefaultCzPort = "25566";

    // 协议常量统一放在共享库 Protocol 里(三端共用一份),这里只是别名,阅读方便
    private const ushort SOnline = Protocol.SOnline;
    private const ushort SBack = Protocol.SBack;
    private const ushort ServerIdent = Protocol.ServerIdent;
    private const ushort Conline = Protocol.Conline;
    private const ushort CBack = Protocol.CBack;

    /// <summary>连向 ZXMOS 的客户端;存字段,不然会被回收(中转要用它)。</summary>
    private static TcpClient? _os;

    /// <summary>监听 ZXMC / ZXMM 的服务端;登记表在它的 Registry 上,中转时也靠它推消息。</summary>
    private static TcpServer? _server;

    /// <summary>账号表(users.ini):注册 / 登录都查它;控制台 account 命令也用它。</summary>
    private static readonly AccountStore Accounts = new("users.json");

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

    static async Task Main(string[] args)
    {
        Console.WriteLine(" _____   _           _  __ __  ___                                     _____    \r\n/__  /  (_)___  ____| |/ //  |/  /___ _____  ____ _____ ____  _____   / ___/    \r\n  / /  / / __ \\/ ___/   // /|_/ / __ `/ __ \\/ __ `/ __ `/ _ \\/ ___/   \\__ \\     \r\n / /__/ / / / / /__/   |/ /  / / /_/ / / / / /_/ / /_/ /  __/ /      ___/ /     \r\n/____/_/_/ /_/\\___/_/|_/_/  /_/\\__,_/_/ /_/\\__,_/\\__, /\\___/_/      /____/      \r\n                                                /____/                          ");
        Console.WriteLine("欢迎使用ZincXManager子服务端");
        Console.WriteLine("ZincXManager官网 zxm.zincms.top");
        Console.ForegroundColor = ConsoleColor.Green;
        OpenLog();

        // 设置文件
        var config = new ZFile();
        config.FileOperate("config.ini", "rwNFC");

        // 本端名称:客户端连进来时发给它(客户端不用自己填服务端名称)
        var serverName = config.MapGet(KeyName).Trim();
        if (serverName.Length == 0)
        {
            serverName = DefaultName;
            config.WriteFileMap(KeyName, serverName);
            Logger.Log(LogLevel.Info, $"[CONFIG]{KeyName} 未配置,已写入默认名称 {serverName}");
        }

        // ================= 配置检查:缺哪项就问哪项,问完写回 config.ini =================

        // 要不要连官方服务端
        var connectOS = AskIfMissing(config, KeyConnectOs,
            "[CONFIG]输入0启用默认连接OS，输入1关闭默认连接OS",
            value => value is "0" or "1",
            fallback: "0");

        // 客户端接入端口:ZXMC / ZXMM 连这里
        var czport = AskIfMissing(config, KeyCzport,
            "[CONFIG]输入客户端接入端口czport(ZXMC/ZXMM 连这里,1-65535)",
            value => ushort.TryParse(value, out var n) && n > 0,
            fallback: DefaultCzPort);

        // ================= 启动监听:ZXMC / ZXMM 都连这个端口 =================
        _server = new TcpServer(IPAddress.Any, ushort.Parse(czport));

        // 有人接进来:先中立记录身份未知,再主动报上"我是 ZXMS",让客户端按模式校验
        _server.Connected += async conn =>
        {
            Logger.Log(LogLevel.Info, $"[网络] 连接接入 {conn.RemoteEndPoint}(等待握手识别身份)");

            try
            {
                await conn.WriteAsync(new Packet(0, ServerIdent, Encoding.UTF8.GetBytes("ZXMS")));
                await conn.WriteAsync(new Packet(0, Protocol.ServerName, Encoding.UTF8.GetBytes(serverName)));
                Logger.Log(LogLevel.Info, $"[ZXMS] 已发送身份包(200):ZXMS,名称(201):{serverName}");
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

        _server.AddHandler(async packet =>
        {
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

                // ---- 账号:登录(负载 "名称|密码密文|等级") ----
                case Protocol.AccountLogin:
                {
                    var parts = Encoding.UTF8.GetString(packet.Data).Split('|');
                    var user = parts.Length > 0 ? parts[0].Trim() : "";
                    var passwordHash = parts.Length > 1 ? parts[1] : "";
                    var level = parts.Length > 2 ? parts[2].Trim() : Protocol.RegisterLevel;
                    var side = DecideAccountSide(level, parts.Length > 3 ? parts[3] : "");
                    var machine = parts.Length > 4 ? parts[4].Trim() : "";
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
                        if (await TryRelayAccountAsync(packet, Protocol.AccountLogin))
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

                    if (Accounts.TryLogin(user, passwordHash, level, out var account, out var error))
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

                // 其它类型先记日志(ZXMM 模组端的类型还没定,定了在这里加 case)
                default:
                    Logger.Log(LogLevel.Warning, $"[网络] 未处理的包 type={packet.Type} len={packet.Data.Length}");
                    break;
            }
        });

        try
        {
            _server.Open();
            Logger.Log(LogLevel.Info, $"[ZXMS] 已开始监听 0.0.0.0:{czport}(czport,ZXMC/ZXMM 从这里接入)");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Fatal, "[ZXMS] 监听失败: " + ex.Message);
            Environment.Exit(1);
        }

        // ================= 按配置连接官方服务端 =================
        // connectOS:0 = 启用(默认),1 = 关闭 —— 和提示语一致
        if (connectOS != "1")
        {
            await ConnectOServerAsync(config);
        }

        var cmd = new CommandHandler();//命令线程

        // 本端专属命令(共享类库里没有),必须在 Start() 之前挂上
        cmd.RegisterZxmsCommands(
            osProvider: () => _os,
            osTargetProvider: () => config.MapGet(KeyOzip) + ":" + config.MapGet(KeyOzport),
            osNameProvider: () => config.MapGet(KeyOsName),
            clientCountProvider: () => _server?.Connections.Count ?? 0,
            registryProvider: () => _server?.Registry,
            accountProvider: () => Accounts,
            osReconnect: () => ConnectOServerAsync(config),
            banProvider: () => Bans,
            serverAccountNameProvider: () => config.MapGet(KeyOsUser).Trim(),
            serverAccountRegister: (name, password) =>
            {
                // 在本端注册服务端账号:写进 config(只存密文),再重连一次让官方服务端那边也注册上
                config.WriteFileMap(KeyOsUser, name);
                config.WriteFileMap(KeyOsHash, PasswordHash.Hash(name, password));
                Logger.Log(LogLevel.Info, $"[服务端账号] 已在本端注册 {name},正在向官方服务端同步…");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ConnectOServerAsync(config);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(LogLevel.Error, "[服务端账号] 同步到官方服务端失败: " + ex.Message);
                    }
                });
            });

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

    /// <summary>等 ZXMOS 的账号回执(601 登录 / 603 注册)。</summary>
    private static TaskCompletionSource<string>? _osAccountAck;

    /// <summary>
    /// 用 ZXMS 账号登录 ZXMOS:没注册过就顺手注册(ZXMOS 给子服务端账号 Admin 级,并记下本机 IP)。
    /// 账号 / 密码没配过时在这里问一次,密码算成密文写回 config.ini。
    /// </summary>
    private static async Task LoginOServerAsync(TcpClient client, ZFile config)
    {
        var user = AskIfMissing(config, KeyOsUser,
            "[CONFIG]输入本 ZXMS 在官方服务端注册的账号名",
            value => value.Length >= 2,
            fallback: Dns.GetHostName());

        // 密文没有就不问明文存明文,而是问一次、当场算成密文存下来
        var hash = config.MapGet(KeyOsHash).Trim();
        if (!PasswordHash.IsHash(hash))
        {
            hash = AskPasswordHash(config, user);
        }

        // 服务端账号没有等级分类,用占位符
        var level = Protocol.ServerAccountLevel;

        var reply = await SendAccountAsync(client, Protocol.AccountLogin, $"{user}|{hash}|{level}");

        if (reply.StartsWith("err|") && reply.Contains("不存在"))
        {
            Logger.Log(LogLevel.Info, $"[ZXMOS] 账号 {user} 还没注册过,正在注册…");
            reply = await SendAccountAsync(client, Protocol.AccountRegister, $"{user}|{hash}");
        }

        if (reply.StartsWith("ok|"))
        {
            Logger.Log(LogLevel.Info, $"[ZXMOS] 账号认证通过:{reply[3..]}");
        }
        else
        {
            Logger.Log(LogLevel.Error, "[ZXMOS] 账号认证失败:" + (reply.StartsWith("err|") ? reply[4..] : reply));
        }
    }

    /// <summary>发账号包并等回执(超时 5 秒)。</summary>
    private static async Task<string> SendAccountAsync(TcpClient client, ushort type, string payload)
    {
        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _osAccountAck = waiter;

        try
        {
            await client.WriteAsync(new Packet(1, type, Encoding.UTF8.GetBytes(payload)));
        }
        catch (Exception ex)
        {
            _osAccountAck = null;
            return "err|" + ex.Message;
        }

        var finished = await Task.WhenAny(waiter.Task, Task.Delay(5000));
        _osAccountAck = null;

        return finished == waiter.Task ? waiter.Task.Result : "err|ZXMOS 没有回执(超时)";
    }

    /// <summary>问一次密码并算成密文写回 config.ini(明文只在内存里过一下)。</summary>
    private static string AskPasswordHash(ZFile config, string user)
    {
        Logger.Log(LogLevel.Info, $"[CONFIG]输入账号 {user} 的密码(只保存密文)");

        var input = Console.ReadLine();

        if (string.IsNullOrEmpty(input))
        {
            // 管道 / 重定向 / 非交互启动:随机生成一个,打在日志里让管理员能看到
            input = Guid.NewGuid().ToString("N")[..16];
            Logger.Log(LogLevel.Warning, "[CONFIG]没有读到输入,已随机生成密码(请改用 user rep 修改): " + input);
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

    private static async Task ConnectOServerAsync(ZFile config)
    {
        // 重连时旧对象不能复用,先关掉
        if (_os is not null)
        {
            await _os.CloseAsync();
            _os = null;
        }

        // 官方服务端(ZXMOS)的地址 / 端口:没有或不合法就问
        var ozip = AskIfMissing(config, KeyOzip,
            "[CONFIG]输入官方服务端地址ozip(例如 127.0.0.1)",
            value => IPAddress.TryParse(value, out _),
            fallback: "127.0.0.1");

        var ozport = AskIfMissing(config, KeyOzport,
            "[CONFIG]输入官方服务端端口ozport(1-65535)",
            value => ushort.TryParse(value, out var n) && n > 0,
            fallback: DefaultOsPort);

        var osIp = IPAddress.Parse(ozip);
        var osPort = ushort.Parse(ozport);

        var client = new TcpClient(osIp, osPort);

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

                // ZXMOS 确认握手
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

                // ZXMOS 的账号回执(601 登录 / 603 注册)
                case Protocol.AccountLoginAck:
                case Protocol.AccountRegisterAck:
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
                        }
                        else
                        {
                            Logger.Log(LogLevel.Warning, "[ZXMOS] 要转的客户端已断开,回执丢弃:" + text);
                        }

                        break;
                    }

                    // ② 是本端自己的接入账号认证回执
                    Logger.Log(LogLevel.Info, "[ZXMOS] 账号回执: " + text);
                    _osAccountAck?.TrySetResult(text);
                    break;
                }

                // TODO 中转:ZXMOS 下发的业务消息,应该在这里转发给对应的 ZXMC 连接
                //       (转发入口:_server.Connections 找到目标连接 → conn.WriteAsync(packet))
                default:
                    Logger.Log(LogLevel.Info, $"[ZXMOS] 收到 type={packet.Type}: {Encoding.UTF8.GetString(packet.Data)}");
                    break;
            }

        });

        try
        {
            await client.ConnectAsync();
            _os = client;
            await client.WriteAsync(new Packet(1, SOnline, Encoding.UTF8.GetBytes("ZXMS online")));
            Logger.Log(LogLevel.Info, $"[ZXMOS] 已连接 {osIp}:{osPort}");

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
