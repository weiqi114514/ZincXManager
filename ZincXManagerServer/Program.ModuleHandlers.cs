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
using ZincXManagerShared.Modules;  // ModuleCatalog / ModuleIds:模块清单
using ZincXManagerShared.Network;  // 共享库
using ZincXManagerShared.Security;  // 共享库
using ZincXManagerShared.Update;  // 版本号比较(强制更新)
using ZincXManagerShared.Verify;  // 共享库
using ZincXManagerServer.Command;  // 子服自身
using ZincXManagerServer.Modules;  // ModuleRegistry / ModuleContext

// 与 BCL 的 TcpClient 同名,起个别名
using TcpClient = ZincXManagerShared.Network.TcpClient;

using ZincXManagerServer.Login;  // 归属地查询
using ZincXManager.Server.Zxms;  // 多服数据分离:服务器登记 / 分区分层
using ZincXManagerShared.Login;  // 绑定表 / 持久登录表 / 登录日志

namespace ZincXManagerServer;

/// <summary>
/// 模块化改动的落地部分(与 Program 同一个分部类):
/// ① 原来写在 <c>HandlePacketAsync</c> 那个大 switch 里的包处理体,原样搬到这里的 internal 方法;
/// ② 模块装配需要的几样东西(上下文、202 负载、数据分离启动段)。
/// 搬出来只是为了给模块类一个可以调用的入口,逻辑一行没改。
/// </summary>
internal static partial class Program
{
    // ===================== 模块装配用到的几个东西 =====================

    /// <summary>
    /// 造一份模块上下文交给注册表:配置、命令表、ZXMM 通道、给客户端发包的通道、202 负载生成器。
    /// 模块只从这里拿东西,不直接摸 Program 的私有字段。
    /// </summary>
    /// <param name="commands">命令处理器(装配模块之前就要建好,模块才好往里注册命令)。</param>
    internal static ModuleContext CreateModuleContext(CommandHandler commands) => new ModuleContext
    {
        Config = _config!,
        Commands = commands,
        ZxmmApiOf = ZxmmApiOf,
        SendToLocalClientsAsync = SendToLocalClientsAsync,
        BuildServerConfigPayload = BuildServerConfigPayload,
    };

    /// <summary>
    /// 生成 202 配置包负载:消息保留天数 / 广场名 / 本次启动关掉的模块 / 半加载的模块。
    /// <para>modules 只放「完全关闭」的,half 放「半加载」的,两行都覆盖模块全集(包括纯客户端入口),
    /// 客户端据此隐藏入口并自动关窗口;哪一项空就写 none(和 config 里的写法一致,空值不会被误读成"关掉模块")。</para>
    /// <para>半加载单独一行是为了兼容:老客户端只认 modules,看到的是"这些功能没了",正合半加载的意思;
    /// 新客户端再看 half,就能显示成"该功能已由服主手动维护"。</para>
    /// </summary>
    internal static string BuildServerConfigPayload()
    {
        var squareName = _config?.MapGet(KeySquareName).Trim() ?? "";   //广场名
        if (squareName.Length == 0)
        {
            squareName = "服务器广场";
        }

        // 按模块全集的顺序列,两个进程看到的是同一份顺序,日志和报错好对照
        var disabled = ModuleCatalog.All
            .Where(module => ModuleRegistry.IsDisabled(module.Id))
            .Select(module => module.Id)
            .ToArray();
        var modulesText = disabled.Length == 0 ? ModuleRegistry.NoneToken : string.Join(",", disabled);

        var half = ModuleCatalog.All
            .Where(module => ModuleRegistry.IsHalf(module.Id))
            .Select(module => module.Id)
            .ToArray();
        var halfText = half.Length == 0 ? ModuleRegistry.NoneToken : string.Join(",", half);

        return $"msgdays={_msgDays}\nsquarename={squareName}\nmodules={modulesText}\nhalf={halfText}";
    }

    /// <summary>
    /// 多服数据分离的启动段:读开关与键表,再按上次留下的快照做一次迁移。
    /// <para>登记表(<see cref="Servers.Load"/>)已经在 LoadConfig 里读完,这里接着做后两步;
    /// 顺序不能换 —— 迁移要按登记表列服务器、按键表定位文件,而且必须在绑定表被读之前跑完。</para>
    /// <para>只有 datasep 模块装配时才会走到这里:模块关掉就一个文件都不读、一个目录都不动。</para>
    /// </summary>
    /// <param name="config">已打开的 config.ini 句柄。</param>
    internal static void InitDataSeparation(ZFile config)
    {
        DataSeparation.Load(config);
        SeparationMigration.Run(config);
        Logger.Log(LogLevel.Info, $"[数据分离] 启动就绪:登记服务器 {Servers.All.Count} 台," +
            $"开关={(DataSeparation.Enabled ? "1(按服分目录)" : "0(全部共享)")}," +
            $"分离键={(DataSeparation.SeparatedKeys.Count == 0 ? "无" : string.Join(",", DataSeparation.SeparatedKeys))}");
    }

    // ===================== 账号注册模块(az.register)接管的包 =====================

    /// <summary>
    /// 账号注册(6002):负载 "名称|密码密文|机器码|nonce|proof|版本";等级固定 Player,回 6003。
    /// <para>这段原来写在核心分发表的大 switch 里,原样搬出来给模块用 —— 逻辑一行没改。</para>
    /// </summary>
    /// <param name="packet">客户端发来的包。</param>
    internal static async Task HandleAccountRegisterAsync(ServerboundPacket packet)
    {
        // 别名:让方法体保持原来的写法
        var server = _server!;   //监听中的服务端
        var config = _config!;   //config.ini 句柄

        var parts = Encoding.UTF8.GetString(packet.Data).Split('|');   //包内容
        var user = parts.Length > 0 ? parts[0].Trim() : "";
        var passwordHash = parts.Length > 1 ? parts[1] : "";
        // 第 3 段是客户端机器码(老客户端没这段,当空处理)
        var regMachine = parts.Length > 2 ? parts[2].Trim() : "";
        // 注册负载最后一段也是客户端版本(名称|密文|机器码|nonce|proof|版本)
        var regVersion = parts.Length > 5 ? parts[5].Trim() : "";

        var regIp = packet.Connection.RemoteEndPoint?.Address.ToString() ?? "";

        server.Registry.SetVersion(packet.Connection.Id, regVersion);

        // 强制更新对注册同样生效:旧客户端注册进来也一样用不了
        if (RejectOutdatedClient(regVersion, protocol: "注册", user) is { } outdatedReg)
        {
            await packet.SendAsync(Protocol.AccountRegisterAck, "err|" + outdatedReg, packet.Id);
            return;
        }

        // 注册和登录共用同一份失败计数:否则可以拿注册接口反过来试口令 / 刷账号
        if (_loginLimiter.IsBlocked(regIp))
        {
            WarnRateLimited("login:" + regIp,
                $"[限速] {regIp} 失败次数过多(注册 {user}),已拒绝");
            await packet.SendAsync(Protocol.AccountRegisterAck,
                "err|失败次数过多,请稍后再试", packet.Id);
            return;
        }

        // 注册这条分支的机器码规则与登录一致:空串 + 严格模式 → 拒绝,不写账号
        if (regMachine.Length == 0 && _machineRequire)
        {
            Logger.Log(LogLevel.Warning,
                $"[机器码] 拒绝注册 {user}:客户端取不到主板序列号({KeyMachineRequire}=1) ← {Where(packet.Connection)}");
            await packet.SendAsync(Protocol.AccountRegisterAck,
                "err|取不到主板序列号,本服要求绑定机器码;请确认主板序列号可读后重试,或联系服主关闭机器码必填", packet.Id);
            return;
        }

        if (Bans.Check(user, regIp, regMachine, out var regBan))
        {
            Logger.Log(LogLevel.Warning, $"[封禁] 拒绝注册 {user}({regBan!.Kind}={regBan.Value})");
            await packet.SendAsync(Protocol.AccountRegisterAck, $"err|已被封禁:{regBan.Reason}({regBan.Remain})", packet.Id);
            return;
        }

        // 玩家账号优先在官方服务端注册(SAOS 下也必须经 ZXMS 中转,不能绕过);
        // 带 "|relay" 让官方服务端知道这是替玩家注册,不是 ZXMS 自己注册服务端账号
        if (await TryRelayAccountAsync(packet, Protocol.AccountRegister, "|relay"))
        {
            return;
        }

        Logger.Log(LogLevel.Info, "[账号] 官方服务端未连接,玩家账号在本端注册");

        if (Accounts.TryRegister(user, passwordHash, out var account, out var error))
        {
            _loginLimiter.RecordSuccess(regIp);
            // 注册成功也要登记账号 + 记下 IP/机器码:否则这条连接在拉黑时踢不掉,也少一条连坐
            server.Registry.SetAccount(packet.Connection.Id, account!.Name, account.Level, account.Id);
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
    }

    // ===================== 消息模块(msg)接管的包 =====================

    /// <summary>
    /// 客户端发来只有服务端能发的消息包(公告 500 / 定向推送 501):一律忽略并记一条,不执行任何动作。
    /// </summary>
    /// <param name="packet">客户端发来的包。</param>
    internal static Task HandleServerOnlyMessageAsync(ServerboundPacket packet)
    {
        Logger.Log(LogLevel.Warning,
            $"[ZXMS] 客户端试图发服务端消息 type={packet.Type},已忽略 ← {Where(packet.Connection)}");
        return Task.CompletedTask;
    }

    /// <summary>卡片动作(502):客户端点了公告 / 推送卡片上的按钮。</summary>
    /// <param name="packet">客户端发来的包。</param>
    internal static async Task HandleCardActionAsync(ServerboundPacket packet)
    {
        var text = Encoding.UTF8.GetString(packet.Data);   //包内容
        var who = _server?.Registry.Find(packet.Connection.Id)?.User ?? "?";

        if (!MessageCard.TryParseAction(text, out var talk))
        {
            Logger.Log(LogLevel.Warning, "[消息] 卡片动作负载格式不对,已忽略:" + text);
            await packet.SendAsync(Protocol.CardActionAck, "err|动作格式不对", packet.Id);
            return;
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
    }

    /// <summary>补发请求(5005):客户端登录后要一次历史消息 → 转给官方服务端,回包再按包 Id 转回这条连接。</summary>
    /// <param name="packet">客户端发来的包。</param>
    internal static async Task HandleMessageHistoryReqAsync(ServerboundPacket packet)
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
            return;
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
    }

    /// <summary>官方端回包(5009):-os 查询结果,唤醒等待的命令。</summary>
    /// <param name="packet">官方端发来的包。</param>
    internal static Task HandleMegBackAckAsync(ServerboundPacket packet)
    {
        var ackText = Encoding.UTF8.GetString(packet.Data);   //包内容
        if (MegBackWaits.TryRemove((int)packet.Id, out var pending))
        {
            pending.TrySetResult(ackText);
        }

        return Task.CompletedTask;
    }

    /// <summary>客户端回执(5007):这几条它已经收下了,记进已发清单。</summary>
    /// <param name="packet">客户端发来的包。</param>
    internal static async Task HandleMessageAckAsync(ServerboundPacket packet)
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
    }

    // ===================== 绑定模块(wd.bind)接管的包 =====================

    /// <summary>绑定码转交(6025):ZXMM 把玩家手里的码转给服务端。</summary>
    /// <param name="packet">ZXMM 发来的包。</param>
    internal static Task HandleBindNoticeAsync(ServerboundPacket packet)
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

        return Task.CompletedTask;
    }

    /// <summary>绑定(626):客户端提交绑定码 —— 只校验并生成确认令牌,真正写绑定要等 631。</summary>
    /// <param name="packet">客户端发来的包。</param>
    internal static async Task HandleBindApplyAsync(ServerboundPacket packet)
    {
        var code = Encoding.UTF8.GetString(packet.Data).Trim().ToUpperInvariant();   //包内容
        var bindWho = _server?.Registry.Find(packet.Connection.Id)?.User ?? "";
        var bindAccount = (await ResolveAccountAsync(bindWho))?.Id ?? 0;
        if (bindAccount <= 0)
        {
            await packet.SendAsync(Protocol.BindAck, "err|请先登录(这条连接没有登录态,断线重连后请重新登录一次)", packet.Id);
            return;
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
            return;
        }

        // 绑定表按这条连接当前选的服务器取:客户端切到哪台,就只认那台的分区(没开分离时 Scope 返回空串)
        var bindScope = DataSeparation.Scope("bind", ServerOf(packet.Connection));

        // 绑定码只是"玩家本人的凭证":码可能被别人看到或转发,
        // 所以真正落盘前还要账号主人在 ZXMC 上亲眼确认一次(6031)
        var alreadyOwner = LoginStore.AccountOf(bindPlayer, bindScope);
        if (alreadyOwner > 0 && alreadyOwner != bindAccount)
        {
            await packet.SendAsync(Protocol.BindAck, $"err|这个玩家已经被账号 {alreadyOwner} 绑定了", packet.Id);
            return;
        }

        // 上限在提交码这一步就先挡:让用户当场看到"先解绑一个",而不是输了码点了确认才失败
        var bindLimit = LoginStore.CheckBindLimit(bindAccount, bindPlayer, _bindMax, bindScope);
        if (bindLimit.Length > 0)
        {
            await packet.SendAsync(Protocol.BindAck, "err|" + bindLimit, packet.Id);
            return;
        }

        var token = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();   //确认令牌
        PendingBinds[token] = new PendingBind(bindAccount, bindPlayer, code, DateTime.Now);
        await packet.SendAsync(Protocol.BindAck, $"pending|{token}|{bindPlayer}", packet.Id);
        Logger.Log(LogLevel.Info,
            $"[绑定] 账号 {bindWho}(ID {bindAccount}) 提交绑定码,待确认玩家 {bindPlayer} 令牌 {token}");
    }

    /// <summary>绑定二次确认(6031):用户点了"确定绑定"才真正写绑定表。</summary>
    /// <param name="packet">客户端发来的包。</param>
    internal static async Task HandleBindConfirmAsync(ServerboundPacket packet)
    {
        var token = Encoding.UTF8.GetString(packet.Data).Trim().ToUpperInvariant();   //包内容
        var confirmWho = _server?.Registry.Find(packet.Connection.Id)?.User ?? "";
        var confirmAccount = (await ResolveAccountAsync(confirmWho))?.Id ?? 0;
        if (confirmAccount <= 0)
        {
            await packet.SendAsync(Protocol.BindAck, "err|请先登录(这条连接没有登录态,断线重连后请重新登录一次)", packet.Id);
            return;
        }

        // 令牌只给发起账号用,别让另一条连接拿着它把别人的码绑到自己名下
        if (!PendingBinds.TryRemove(token, out var pending) ||
            pending.AccountId != confirmAccount ||
            (DateTime.Now - pending.At).TotalMinutes > BindConfirmMinutes)
        {
            await packet.SendAsync(Protocol.BindAck, "err|确认已过期,请重新输入绑定码", packet.Id);
            return;
        }

        // 这个账号名下的旧玩家不做任何处理:上限拦着不许顶掉旧的,
        // 真要让他失去免确认只能走 6028 解绑(解绑会清登录数组、持久登录表并踢人)
        if (!LoginStore.Bind(confirmAccount, pending.Player, _bindMax, out var bindError,
                DataSeparation.Scope("bind", ServerOf(packet.Connection))))
        {
            await packet.SendAsync(Protocol.BindAck, "err|" + bindError, packet.Id);
            return;
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
        // (要让他失去免确认,只能走 6028 解绑)
    }

    /// <summary>解绑(6028):负载是玩家名(空 = 这个账号名下全部解掉,兼容老客户端)。</summary>
    /// <param name="packet">客户端发来的包。</param>
    internal static async Task HandleUnbindAsync(ServerboundPacket packet)
    {
        var unbindWho = _server?.Registry.Find(packet.Connection.Id)?.User ?? "";
        var unbindAccount = (await ResolveAccountAsync(unbindWho))?.Id ?? 0;
        var unbindTarget = Encoding.UTF8.GetString(packet.Data).Trim();   //要解掉哪个玩家
        if (unbindAccount <= 0)
        {
            await packet.SendAsync(Protocol.BindAck, "err|请先登录(这条连接没有登录态,断线重连后请重新登录一次)", packet.Id);
            return;
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
            return;
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
    }

    /// <summary>查绑定(6029):ZXMM 查全部;ZXMC 只查自己绑了哪些。</summary>
    /// <param name="packet">查询方发来的包。</param>
    internal static async Task HandleBindQueryAsync(ServerboundPacket packet)
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
                return;
            }

            // 还没绑定:只说"没绑",不带任何别人的玩家名/绑定码 —— 绑定码是玩家的私密凭据,
            // 服务端在未绑定时并不知道这个账号对应哪个玩家,推"最近进服那个"就是跨玩家泄漏
            await packet.SendAsync(Protocol.BindList, "nobind||", packet.Id);
            return;
        }

        var lines = LoginStore.AllBinds()
            .Select(pair => $"{pair.Value}={Accounts.FindById(pair.Key)?.Name ?? "?"}");
        await packet.SendAsync(Protocol.BindList, string.Join("\n", lines), packet.Id);
    }

    // ===================== 登录确认模块(loginconfirm)接管的包 =====================

    /// <summary>客户端回答(6022):同意 / 拒绝。</summary>
    /// <param name="packet">客户端发来的包。</param>
    internal static async Task HandleLoginAnswerAsync(ServerboundPacket packet)
    {
        var answerText = Encoding.UTF8.GetString(packet.Data).Trim();   //回答负载
        var answerParts = answerText.Split('|');   //拆分结果
        if (answerParts.Length < 2 || !int.TryParse(answerParts[0], out var requestId) ||
            !PendingLogins.TryGetValue(requestId, out var pending))
        {
            return;
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
            return;
        }

        // 认领这条请求(并发下只让一个人处理)
        if (!PendingLogins.TryRemove(requestId, out pending))
        {
            return;
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
    }

    // ===================== 服务器列表模块(serverlist)接管的包 =====================

    /// <summary>客户端要服务器列表(6070):回 6071,每行一台(编号|名称|端口)。</summary>
    /// <param name="packet">客户端发来的包。</param>
    internal static async Task HandleServerListReqAsync(ServerboundPacket packet)
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
    }

    /// <summary>客户端切服(6072):记住这条连接当前选的是哪台,回 6073。</summary>
    /// <param name="packet">客户端发来的包。</param>
    internal static async Task HandleServerSwitchReqAsync(ServerboundPacket packet)
    {
        var targetText = Encoding.UTF8.GetString(packet.Data).Trim();   //目标服务器编号(十进制)
        if (!int.TryParse(targetText, out var targetId) || !Servers.TryGet(targetId, out var target))
        {
            var err = Encoding.UTF8.GetBytes("err|没有这个服务器");
            await packet.WriteAsync(new Packet(packet.Id, Protocol.ServerSwitchAck, err));
            Logger.Log(LogLevel.Warning,
                $"[数据分离] 客户端 {Where(packet.Connection)} 切到服务器「{targetText}」→ 没有这个服务器");
            return;
        }

        lock (_clientServerLock)
        {
            _clientServer[packet.Connection] = target.Id;
        }

        var okText = Encoding.UTF8.GetBytes($"ok|{target.Id}");
        await packet.WriteAsync(new Packet(packet.Id, Protocol.ServerSwitchAck, okText));
        Logger.Log(LogLevel.Info,
            $"[数据分离] 客户端 {Where(packet.Connection)} 切到服务器 {target.Id}({target.Name}) → ok");
    }

    // ===================== 控制台模块(console)接管的包 =====================

    /// <summary>列服务器(6084):每行一台(编号|名称|控制台模式|桥接在线|RCON在线)。</summary>
    /// <param name="packet">客户端发来的包。</param>
    internal static async Task HandleConsoleServersReqAsync(ServerboundPacket packet)
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
    }

    /// <summary>执行一条控制台命令(6080):负载是 服务器编号|命令,回 6081。</summary>
    /// <param name="packet">客户端发来的包。</param>
    internal static async Task HandleConsoleCommandAsync(ServerboundPacket packet)
    {
        // 先卡权限:没权限的连接连表都不查、命令也不执行
        if (!CanUseConsole(packet.Connection))
        {
            var denied = Encoding.UTF8.GetBytes("err|没有这个服务器控制台的权限");
            await packet.WriteAsync(new Packet(packet.Id, Protocol.ConsoleResult, denied));
            Logger.Log(LogLevel.Warning,
                $"[控制台] 客户端 {Where(packet.Connection)} 想执行控制台命令 → 不是 HAdmin/System,已拒绝");
            return;
        }

        var text = Encoding.UTF8.GetString(packet.Data);   //负载:服务器编号|命令
        var bar = text.IndexOf('|');   //命令里可能还有竖线,只切第一根
        if (bar <= 0 || !int.TryParse(text[..bar].Trim(), out var serverId))
        {
            var bad = Encoding.UTF8.GetBytes("err|参数不对");
            await packet.WriteAsync(new Packet(packet.Id, Protocol.ConsoleResult, bad));
            return;
        }

        var command = text[(bar + 1)..].Trim();   //要执行的 MC 命令(不带前导斜杠)
        if (command.Length == 0)
        {
            var empty = Encoding.UTF8.GetBytes("err|命令是空的");
            await packet.WriteAsync(new Packet(packet.Id, Protocol.ConsoleResult, empty));
            return;
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
    }

    /// <summary>开关持续输出(6083):负载是 服务器编号|1 或 0,回 6081。</summary>
    /// <param name="packet">客户端发来的包。</param>
    internal static async Task HandleConsoleTapAsync(ServerboundPacket packet)
    {
        if (!CanUseConsole(packet.Connection))
        {
            var denied = Encoding.UTF8.GetBytes("err|没有这个服务器控制台的权限");
            await packet.WriteAsync(new Packet(packet.Id, Protocol.ConsoleResult, denied));
            Logger.Log(LogLevel.Warning,
                $"[控制台] 客户端 {Where(packet.Connection)} 想收控制台输出 → 不是 HAdmin/System,已拒绝");
            return;
        }

        var text = Encoding.UTF8.GetString(packet.Data);   //负载:服务器编号|1(开) 或 |0(关)
        var bar = text.IndexOf('|');
        var flag = bar > 0 ? text[(bar + 1)..].Trim() : "";
        if (bar <= 0 || flag is not ("0" or "1") || !int.TryParse(text[..bar].Trim(), out var serverId))
        {
            var bad = Encoding.UTF8.GetBytes("err|参数不对");
            await packet.WriteAsync(new Packet(packet.Id, Protocol.ConsoleResult, bad));
            return;
        }

        if (!Servers.TryGet(serverId, out var tapEntry))
        {
            var missing = Encoding.UTF8.GetBytes("err|没有这个服务器");
            await packet.WriteAsync(new Packet(packet.Id, Protocol.ConsoleResult, missing));
            return;
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
    }
}
