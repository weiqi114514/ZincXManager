using System;
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
using ZincXManagerOServer.Command;

namespace ZincXManagerOServer;

internal static class Program
{
    // 默认监听端口(config.ini 里没写 port 时的兜底值)
    private const string DefaultPort = "25565";

    /// <summary>本端名称(config.ini 的 name):连接时发给对方。</summary>
    private const string KeyName = "name";

    /// <summary>名称没配时的兜底值。</summary>
    private const string DefaultName = "锌能源官方服务端";

    // 协议常量统一放在共享库 Protocol 里(三端共用一份),这里只是别名,阅读方便
    private const ushort SOnline = Protocol.SOnline;
    private const ushort SBack = Protocol.SBack;
    private const ushort ServerIdent = Protocol.ServerIdent;
    private const ushort Conline = Protocol.Conline;
    private const ushort CBack = Protocol.CBack;
    private const ushort AccountLogin = Protocol.AccountLogin;
    private const ushort AccountLoginAck = Protocol.AccountLoginAck;
    private const ushort AccountRegister = Protocol.AccountRegister;
    private const ushort AccountRegisterAck = Protocol.AccountRegisterAck;

    /// <summary>封禁表:账号 / IP / 机器码,永久或定时。</summary>
    private static readonly BanStore Bans = new("bans.json");

    /// <summary>
    /// 用户账号表(玩家 + 官方服务端的管理账号):等级 Player / Admin / System。
    /// 和"服务端账号"是两回事,所以分开两个文件存。
    /// </summary>
    private static readonly AccountStore Users =
        new("osusers.json", Protocol.OSLevels, Protocol.OSRegisterLevel, alsoTaken: n => Servers.Find(n) is not null);

    /// <summary>
    /// 服务端账号表(哪个 ZXMS 接入本官方服务端):注册时记下它的 IP。
    /// 实名不与用户账号重名:两边互相查重。
    /// </summary>
    private static readonly AccountStore Servers =
        new("zxms.json", Protocol.ServerLevels, Protocol.ServerAccountLevel, alsoTaken: n => Users.Find(n) is not null);

    /// <summary>监听中的服务端;连接登记表在它的 <c>Registry</c> 上。</summary>
    private static TcpServer? _server;

    static void OpenLog()
    {
        if (!Logger.OpenDefaultLogFile("ZXMOS", out var logFile))
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
    /// 回复握手确认(收到 100 回 101、收到 300 回 301)。
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
        Console.WriteLine(" _____   _           _  __ __  ___                                   ____   _____\r\n/__  /  (_)___  ____| |/ //  |/  /___ _____  ____ _____ ____  _____ / __ \\ / ___/\r\n  / /  / / __ \\/ ___/   // /|_/ / __ `/ __ \\/ __ `/ __ `/ _ \\/ ___// / / / \\__ \\\r\n / /__/ / / / / /__/   |/ /  / / /_/ / / / / /_/ / /_/ /  __/ /   / /_/ / ___/ /\r\n/____/_/_/ /_/\\___/_/|_/_/  /_/\\__,_/_/ /_/\\__,_/\\__, /\\___/_/    \\____/ /____/");
        Console.WriteLine("欢迎使用ZincXManager官方服务端");
        Console.WriteLine("ZincXManager官网 zxm.zincms.top");
        Console.ForegroundColor = ConsoleColor.Green;
        OpenLog();

        // 设置文件
        var config = new ZFile();
        config.FileOperate("config.ini", "rwNFC");

        // 配置检查:没有 port 就问一次,问完写回 config.ini
        var port = AskIfMissing(config, "port",
            "[CONFIG]输入本服务端监听端口(1-65535)",
            value => ushort.TryParse(value, out var n) && n > 0,
            fallback: DefaultPort);

        // 本端名称:连接时发给对方 —— 客户端 / ZXMS 都不用自己填服务端名称
        var serverName = config.MapGet(KeyName).Trim();
        if (serverName.Length == 0)
        {
            serverName = DefaultName;
            config.WriteFileMap(KeyName, serverName);
            Logger.Log(LogLevel.Info, $"[CONFIG]{KeyName} 未配置,已写入默认名称 {serverName}");
        }

        // 起服务端:ZXMS / ZXMC 都连这个端口(连接登记表在 _server.Registry 上)
        _server = new TcpServer(IPAddress.Any, ushort.Parse(port));
        var server = _server;

        // 有人接进来:还不知道对方是 ZXMS 还是 ZXMC,先中立记录,再主动报上"我是 ZXMOS"
        server.Connected += async conn =>
        {
            Logger.Log(LogLevel.Info, $"[网络] 连接接入 {conn.RemoteEndPoint}(等待握手识别身份)");

            try
            {
                await conn.WriteAsync(new Packet(0, ServerIdent, Encoding.UTF8.GetBytes("ZXMOS")));
                await conn.WriteAsync(new Packet(0, Protocol.ServerName, Encoding.UTF8.GetBytes(serverName)));
                Logger.Log(LogLevel.Info, $"[ZXMOS] 已发送身份包(200):ZXMOS,名称(201):{serverName}");
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[ZXMOS] 发送身份包失败: " + ex.Message);
            }
        };

        server.Disconnected += conn =>
        {
            // 登记表里还留着这条数据(身份 / 模式 / 在线时长),取出来记审计
            var info = _server?.Registry.Find(conn.Id);
            var role = info?.Role ?? "未识别";
            var mode = info?.Mode ?? "-";

            Logger.Log(LogLevel.Info, $"[{role}] 断开 {conn.RemoteEndPoint}");
            ClientLog.Offline(role, mode, info?.Address ?? conn.RemoteEndPoint?.ToString() ?? "?",
                info?.Online ?? TimeSpan.Zero, info?.Packets ?? 0);
        };

        server.AddHandler(async packet =>
        {
            switch (packet.Type)
            {
                // ---- ZXMS(子服务端)上线 ----
                case SOnline:
                    server.Registry.SetRole(packet.Connection.Id, "ZXMS", "-");
                    Logger.Log(LogLevel.Info, "[ZXMS] 上线(握手): " + Encoding.UTF8.GetString(packet.Data));
                    ClientLog.Online("ZXMS", "-", packet.Connection.RemoteEndPoint?.ToString() ?? "?");
                    await ReplyHandshakeAsync(packet, SBack, "101", "ZXMS", "ZXMOS ok");
                    break;

                // ---- ZXMC(客户端)上线:负载形如 "ZXMC:SAOS"(客户端把自己的连接模式报上来) ----
                case Conline:
                {
                    var text = Encoding.UTF8.GetString(packet.Data).Trim();
                    var parts = text.Split(':', 2);
                    var mode = parts.Length > 1 ? parts[1] : "-";

                    server.Registry.SetRole(packet.Connection.Id, "ZXMC", mode);
                    Logger.Log(LogLevel.Info, $"[ZXMC] 上线(握手): {text} 模式={mode}");
                    ClientLog.Online("ZXMC", mode, packet.Connection.RemoteEndPoint?.ToString() ?? "?");

                    await ReplyHandshakeAsync(packet, CBack, "301", "ZXMC", "ZXMOS ok");
                    break;
                }

                // ---- 账号登录(600):玩家账号与 ZXMS 接入账号都走这里 ----
                case AccountLogin:
                {
                    var text = Encoding.UTF8.GetString(packet.Data).Trim();
                    var fields = text.Split('|');
                    var name = fields.Length > 0 ? fields[0].Trim() : "";
                    var hash = fields.Length > 1 ? fields[1].Trim() : "";
                    var level = fields.Length > 2 && Users.IsLevel(fields[2].Trim())
                        ? fields[2].Trim()
                        : Users.RegisterLevel;
                    var address = packet.Connection.RemoteEndPoint?.ToString() ?? "?";
                    var machine = fields.Length > 4 ? fields[4].Trim() : "";
                    var fromIp = packet.Connection.RemoteEndPoint?.Address.ToString() ?? "";

                    if (Bans.Check(name, fromIp, machine, out var ban))
                    {
                        Logger.Log(LogLevel.Warning, $"[封禁] 拒绝登录 {name}({ban!.Kind}={ban.Value},剩余 {ban.Remain})");
                        await packet.WriteAsync(new Packet(packet.Id, AccountLoginAck,
                            Encoding.UTF8.GetBytes($"err|已被封禁:{ban.Reason}({ban.Remain})")));
                        break;
                    }

                    // 服务端账号(ZXMS 接入)查 zxms.ini(它没有等级,级别字段固定用占位符),用户账号查 osusers.ini
                    var isServer = server.Registry.Find(packet.Connection.Id)?.Role == "ZXMS";
                    var store = isServer ? Servers : Users;
                    if (isServer)
                    {
                        level = Protocol.ServerAccountLevel;
                    }

                    if (store.TryLogin(name, hash, level, out var account, out var error))
                    {
                        server.Registry.SetAccount(packet.Connection.Id, account!.Name, account.Level);
                        store.Touch(account.Name, fromIp, machine);
                        Logger.Log(LogLevel.Info, $"[账号] 登录成功 {account.Name}({account.Level}) ID={account.Id} ← {address}");
                        ClientLog.Data("账号", address, $"登录成功 {account.Name}({account.Level})");
                        await packet.WriteAsync(new Packet(packet.Id, AccountLoginAck,
                            Encoding.UTF8.GetBytes($"ok|{account.Name}|{account.Id}|{account.Level}")));
                    }
                    else
                    {
                        Logger.Log(LogLevel.Warning, $"[账号] 登录失败 {name} ← {address}:{error}");
                        ClientLog.Data("账号", address, $"登录失败 {name}:{error}");
                        await packet.WriteAsync(new Packet(packet.Id, AccountLoginAck,
                            Encoding.UTF8.GetBytes("err|" + error)));
                    }

                    break;
                }

                // ---- 注册(602):ZXMS 连上来的 → 子服务端账号(Admin,并记下它的 IP);其他 → 玩家账号(Player) ----
                case AccountRegister:
                {
                    var identity = server.Registry.Find(packet.Connection.Id);
                    var isZxms = identity?.Role == "ZXMS";
                    var ip = packet.Connection.RemoteEndPoint?.Address.ToString() ?? "";
                    var address = packet.Connection.RemoteEndPoint?.ToString() ?? "?";
                    // 服务端账号没有等级;用户账号自助注册只能是 Player
                    var text = Encoding.UTF8.GetString(packet.Data).Trim();
                    var fields = text.Split('|');
                    var name = fields.Length > 0 ? fields[0].Trim() : "";
                    var hash = fields.Length > 1 ? fields[1].Trim() : "";

                    // ZXMS 转发过来的是"替玩家注册":标记 "relay" 由 ZXMS 追加在最后一段
                    // (客户端注册载荷 = 名称|密文|机器码,所以标记在第 4 段,不能只看第 3 段)
                    var relayed = fields.Skip(2).Any(f => f.Trim() == "relay");
                    var machine = fields.Length > 2 && fields[2].Trim() != "relay" ? fields[2].Trim() : "";
                    var isServerAccount = isZxms && !relayed;
                    var level = isServerAccount ? Protocol.ServerAccountLevel : Users.RegisterLevel;
                    var owner = relayed ? server.Registry.Find(packet.Connection.Id)?.User ?? "" : "";

                    if (Bans.Check(name, ip, machine, out var regBan))
                    {
                        Logger.Log(LogLevel.Warning, $"[封禁] 拒绝注册 {name}({regBan!.Kind}={regBan.Value})");
                        await packet.WriteAsync(new Packet(packet.Id, AccountRegisterAck,
                            Encoding.UTF8.GetBytes($"err|已被封禁:{regBan.Reason}({regBan.Remain})")));
                        break;
                    }

                    // 服务端账号单独注册(记 IP),用户账号注册为 Player
                    var store = isServerAccount ? Servers : Users;
                    if (store.TryRegisterAs(name, hash, level, ip, out var account, out var error))
                    {
                        // 注册成功也要把这条连接登记成该账号 + 记下 IP/机器码:
                        // 否则"首次接入就是注册"的 ZXMS 在拉黑时踢不掉(Registry.User 为空)、也少一条 IP 连坐。
                        // 替玩家注册(relayed)不登记:那条连接是 ZXMS、账号是玩家,记混了会连带出错
                        if (!relayed)
                        {
                            server.Registry.SetAccount(packet.Connection.Id, account!.Name, account.Level);
                            store.Touch(account.Name, ip, machine);
                        }

                        if (owner.Length > 0)
                        {
                            Users.SetOwner(account!.Name, owner);
                        }
                        Logger.Log(LogLevel.Info,
                            $"[账号] 注册成功 {account!.Name}({account.Level}) ID={account.Id} IP={ip} ← {address}" +
                            (isZxms ? " [子服务端账号]" : " [玩家账号]"));
                        ClientLog.Data("账号", address, $"注册成功 {account.Name}({account.Level})");
                        await packet.WriteAsync(new Packet(packet.Id, AccountRegisterAck,
                            Encoding.UTF8.GetBytes($"ok|{account.Name}|{account.Id}|{account.Level}")));
                    }
                    else
                    {
                        Logger.Log(LogLevel.Warning, $"[账号] 注册失败 {name} ← {address}:{error}");
                        ClientLog.Data("账号", address, $"注册失败 {name}:{error}");
                        await packet.WriteAsync(new Packet(packet.Id, AccountRegisterAck,
                            Encoding.UTF8.GetBytes("err|" + error)));
                    }

                    break;
                }

                // 其它类型先记日志;但 ZXMS 必须先在本端通过账号认证,没认证就发业务包一律拒绝
                default:
                    if (Protocol.IsBusiness(packet.Type))
                    {
                        var who = server.Registry.Find(packet.Connection.Id);
                        if (who?.Role == "ZXMS" && string.IsNullOrEmpty(who.User))
                        {
                            Logger.Log(LogLevel.Warning,
                                $"[ZXMS] 未通过账号认证就发业务包 type={packet.Type},已拒绝(先在 ZXMOS 注册 ZXMS 账号再用 osuser/oshash 接入)");
                            await packet.WriteAsync(new Packet(packet.Id, AccountLoginAck,
                                Encoding.UTF8.GetBytes("err|未通过 ZXMOS 账号认证")));
                            break;
                        }
                    }

                    Logger.Log(LogLevel.Warning, $"[网络] 未处理的包 type={packet.Type} len={packet.Data.Length}");
                    break;
            }
        });

        try
        {
            server.Open();
            Logger.Log(LogLevel.Info, $"[ZXMOS] 已开始监听 0.0.0.0:{port}");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Fatal, "[ZXMOS] 监听失败: " + ex.Message);
            Environment.Exit(1);
        }

        var cmd = new CommandHandler();//命令线程

        // 本端专属命令(共享类库里没有),必须在 Start() 之前挂上
        cmd.RegisterZxmosCommands(
            listenProvider: () => $"0.0.0.0:{port}",
            registryProvider: () => _server?.Registry,
            accountProvider: () => Users,
            serverAccountProvider: () => Servers,
            banProvider: () => Bans,
            srvBlacklist: new SrvBlacklistHooks
            {
                Ban = SrvBan,
                Unban = SrvUnban,
                List = SrvBlacklistList,
            });

        cmd.Start();
        Logger.Log(LogLevel.Info, "启动成功");
        await Task.Delay(Timeout.Infinite);
    }

    /// <summary>
    /// <c>srv ban</c> 的落地动作:① 归档该 ZXMS 名下的玩家账号 →
    /// ② 按"账号名 + 它的 IP"拉黑 → ③ 断开它已经连着的连接。
    /// 返回归档的账号数;没有这个服务端账号返回 -1(调用方据此不再重复提示)。
    /// </summary>
    static int SrvBan(string key, TimeSpan? duration, string reason)
    {
        var target = Servers.FindByKey(key);
        if (target is null)
        {
            Logger.Log(LogLevel.Warning, "[黑名单] 没有这个服务端账号:" + key);
            return -1;
        }

        var expireAt = duration is null ? (DateTime?)null : DateTime.Now.Add(duration.Value);

        // ① 名下账号(经它中转注册的玩家账号)先归档进 blacklist/zxms/<名称>/,再挪出账号表
        var owned = Users.All
            .Where(a => string.Equals(a.Owner, target.Name, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var archived = Blacklist.Archive(target.Name, owned, target.LastIp, reason, expireAt);

        foreach (var account in owned)
        {
            Users.Remove(account.Name);
        }

        // ② 封"账号名 + 它的 IP":账号名拦住它换 IP 重连,IP 拦住它换账号
        Bans.Add("user", target.Name, duration, reason, out _);
        if (target.LastIp.Length > 0)
        {
            Bans.Add("ip", target.LastIp, duration, reason, out _);
        }

        // ③ 已经连着的立刻断开(之后重连在登录查封禁那一步被拦)
        _ = KickAsync(target.Name);

        Logger.Log(LogLevel.Info,
            $"[黑名单] 已拉黑 {target.Name}(IP {(target.LastIp.Length > 0 ? target.LastIp : "-")})," +
            $"归档 {archived} 个名下账号,原因:{reason}");
        return archived;
    }

    /// <summary><c>srv unban</c>:解封该 ZXMS,并把归档里的账号读回账号表;返回恢复的账号数。</summary>
    static int SrvUnban(string key)
    {
        var target = Servers.FindByKey(key);
        var name = target?.Name ?? key.Trim();

        var unbaned = Bans.Remove("user", name);
        if (target is { LastIp.Length: > 0 })
        {
            unbaned |= Bans.Remove("ip", target.LastIp);
        }

        var restored = 0;

        foreach (var archived in Blacklist.LoadAccounts(name))
        {
            if (!Users.Add(archived.Name, archived.Password, archived.Level, archived.Ip, out var account, out var error))
            {
                Logger.Log(LogLevel.Warning, $"[黑名单] 恢复 {archived.Name} 失败:{error}");
                continue;
            }

            if (archived.Owner.Length > 0)
            {
                Users.SetOwner(account!.Name, archived.Owner);
            }

            Users.Touch(account!.Name, archived.LastIp, archived.Machine);
            restored++;
        }

        Logger.Log(LogLevel.Info, unbaned
            ? $"[黑名单] {name} 的封禁已解除,从归档恢复 {restored} 个账号"
            : $"[黑名单] {name} 本来没有封禁记录,从归档恢复 {restored} 个账号");
        return restored;
    }

    /// <summary><c>srv blacklist</c>:列出已归档的 ZXMS 及拉黑信息(每项一行)。</summary>
    static IReadOnlyList<string> SrvBlacklistList()
    {
        var names = Blacklist.Names();
        return names.Count == 0 ? [] : names.Select(Blacklist.Describe).ToArray();
    }

    /// <summary>断开某个账号的在线连接(拉黑之后立刻生效,不等对方自己下)。</summary>
    static async Task KickAsync(string user)
    {
        if (_server is null)
        {
            return;
        }

        foreach (var conn in _server.Connections)
        {
            var info = _server.Registry.Find(conn.Id);
            if (info is null || !string.Equals(info.User, user, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Logger.Log(LogLevel.Warning, $"[黑名单] 断开 {user} 的连接 {info.Address}");
            ClientLog.Data("黑名单", info.Address, $"拉黑断开 {user}");

            try
            {
                await conn.DisposeAsync();
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[黑名单] 断开连接失败: " + ex.Message);
            }
        }
    }
}
