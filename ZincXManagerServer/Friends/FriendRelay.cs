using System.Collections.Concurrent;     // ConcurrentDictionary:等回执的中转表
using System.Text;                      // Encoding:读包负载
using ZincXManagerShared.Account;        // Account:本服账号
using ZincXManagerShared.FileIO;         // ZFile:config.ini
using ZincXManagerShared.Friend;         // PlayerId / FriendStore / FriendCodec
using ZincXManagerShared.Logging;        // Logger / LogLevel
using ZincXManagerShared.Network;        // Protocol / ServerboundPacket

namespace ZincXManagerServer;

/// <summary>
/// 跨服好友中转(615 发 / 收)、上下线广播(616)、服务器名上报与映射(617 / 618)。
///
/// <para>本服管不了别服的事:申请、同意、删除都先在本服落盘,再把动作交给 ZXMOS 转给对方服,
/// 由对方服自己写自己的档。ZXMOS 只是传话的,不判断业务。</para>
/// </summary>
internal static partial class Program
{
    /// <summary>
    /// 已经交给 ZXMOS、还没等到回执的跨服动作:键 = 发 615 时用的包 Id。
    /// 官方端在目标子服连不上时会把同一个 Id 带回来,靠它找回是哪个玩家在等。
    /// </summary>
    private static readonly ConcurrentDictionary<ulong, PendingRelay> PendingRelays = new();

    /// <summary>等回执的条目最多攒这么多,超了先丢过期的、再丢最旧的,别让它变成内存缺口。</summary>
    private const int PendingRelayLimit = 256;

    /// <summary>回执等这么久还没来就当它丢了(对方那条链路早断了)。</summary>
    private const long PendingRelayTtlMs = 120_000;

    /// <summary>615 的包 Id 从这里往上发;不用 0,0 表示「这条不需要回执」。</summary>
    private static long _relaySequence;

    /// <summary>一条还在等 ZXMOS 回执的跨服动作。</summary>
    private sealed record PendingRelay(PlayerId Self, PlayerId Target, string Action, long At);

    /// <summary>
    /// 把一个好友动作交给 ZXMOS。ZXMOS 连不上时不假装成功,回 false 让调用方如实告诉玩家。
    /// ZXMOS 收到但目标子服不在线时,它会按同一个包 Id 回一条失败回执(见 <see cref="HandleRelayFailureAsync"/>)。
    /// </summary>
    private static async Task<bool> RelayFriendActionAsync(string action, PlayerId self, PlayerId target, string extra, ulong packetId)
    {
        var os = _os;
        if (os is null)
        {
            await ReplyRelayUnavailableAsync(self, action, packetId);
            return false;
        }

        // 记录里的顺序是(发起ID, 目标ID, 动作, 附加内容);写错位置会让对方读成「发起者叫 request」
        var payload = FriendCodec.BuildRelay(new FriendRelayPacket(self.Full, target.Full, action, extra));
        var relayId = (ulong)Interlocked.Increment(ref _relaySequence);   //这条编号会跟着失败回执一起回来
        PendingRelays[relayId] = new PendingRelay(self, target, action, Environment.TickCount64);
        PrunePendingRelays();
        try
        {
            await os.SendAsync(Protocol.FriendRelay, payload, relayId);
            Logger.Log(LogLevel.Info, $"[好友] 跨服 {action} 已交给 ZXMOS: {self.Full} → {target.Full}(编号 {relayId})");
            return true;
        }
        catch (Exception ex)
        {
            PendingRelays.TryRemove(relayId, out _);   //包都没发出去,不会再有回执
            Logger.Log(LogLevel.Error, $"[好友] 跨服 {action} 发给 ZXMOS 失败: {ex.Message}");
            await ReplyRelayUnavailableAsync(self, action, packetId);
            return false;
        }
    }

    /// <summary>
    /// 收 ZXMOS 的「中转没送到」回执(401 / 655 / 659 / 661):按包 Id 找回发起人,把原因原样转给他的客户端。
    /// ZXMOS 只会在目标子服连不上时这么回,所以这就是一次按需的链路探活 —— 不发额外查询,没有轮询,也没有定时任务。
    /// </summary>
    private static async Task HandleRelayFailureAsync(Packet packet)
    {
        var text = Encoding.UTF8.GetString(packet.Data).Trim();   //官方端拼好的 err|原因
        if (!text.StartsWith("err|", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Log(LogLevel.Warning,
                $"[好友] 收到类型 {packet.Type} 的回执但不是失败回执,已丢弃:{text[..Math.Min(80, text.Length)]}");
            return;
        }

        if (!PendingRelays.TryRemove(packet.Id, out var pending))
        {
            Logger.Log(LogLevel.Warning, $"[好友] 收到没人认领的中转失败回执(编号 {packet.Id},类型 {packet.Type}):{text}");
            return;
        }

        if (await SendToPlayerAsync(pending.Self, packet.Type, text))
        {
            Logger.Log(LogLevel.Warning,
                $"[好友] 跨服 {pending.Action} 没送到 {pending.Target.Full},已如实告诉 {pending.Self.Full}:{text}");
            return;
        }

        Logger.Log(LogLevel.Warning,
            $"[好友] 跨服 {pending.Action} 没送到 {pending.Target.Full},而 {pending.Self.Full} 已经不在线,原因无处可发:{text}");
    }

    /// <summary>等回执的中转不能无限攒:超上限时先丢过期的,还超就丢最旧的(丢了的回执会落到「没人认领」那条日志里)。</summary>
    private static void PrunePendingRelays()
    {
        if (PendingRelays.Count <= PendingRelayLimit)
        {
            return;
        }

        var now = Environment.TickCount64;   //毫秒级计时,只用来算「等了多久」
        foreach (var pair in PendingRelays)
        {
            if (now - pair.Value.At >= PendingRelayTtlMs)
            {
                PendingRelays.TryRemove(pair.Key, out _);
            }
        }

        var over = PendingRelays.Count - PendingRelayLimit;   //还超多少条
        if (over <= 0)
        {
            return;
        }

        var oldest = PendingRelays.OrderBy(pair => pair.Value.At).Take(over).ToArray();
        foreach (var pair in oldest)
        {
            PendingRelays.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>中转不可用时给玩家一句人话(有 packetId 才回,没有就只记日志)。回执类型按动作选,别一律发 655。</summary>
    private static async Task ReplyRelayUnavailableAsync(PlayerId self, string action, ulong packetId)
    {
        if (packetId == 0)
        {
            return;
        }

        var ackType = action switch
        {
            Protocol.FriendRelayAction.Request => Protocol.FriendRequestAck,
            Protocol.FriendRelayAction.Add => Protocol.FriendAnswerAck,
            Protocol.FriendRelayAction.Answer => Protocol.FriendAnswerAck,
            Protocol.FriendRelayAction.Remove => Protocol.FriendRemoveAck,
            Protocol.FriendRelayAction.Message => Protocol.FriendMsgReceipt,
            _ => Protocol.FriendRequestAck
        };

        var reply = await SendToPlayerAsync(self, ackType, "err|官方服务端未连接,先只能加本服的人");
        if (!reply)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 中转不可用,而且 {self.Full} 已经不在线了");
        }
    }

    /// <summary>收 615:ZXMOS 把别服的动作转过来。负载 `动作|发起者完整ID|目标完整ID|附带`。</summary>
    private static async Task HandleFriendRelayAsync(Packet packet, ZFile config)
    {
        var text = Encoding.UTF8.GetString(packet.Data);
        if (!FriendCodec.TryParseRelay(text, out var relay))
        {
            Logger.Log(LogLevel.Warning, $"[好友] 收到一条读不懂的跨服中转:{text[..Math.Min(80, text.Length)]}");
            return;
        }

        if (!PlayerId.TryParse(relay.ToId, out var target) || !PlayerId.TryParse(relay.FromId, out var from))
        {
            Logger.Log(LogLevel.Warning, $"[好友] 跨服中转里的 ID 不对:{text[..Math.Min(80, text.Length)]}");
            return;
        }

        switch (relay.Action)
        {
            case Protocol.FriendRelayAction.Request:
                await ApplyRemoteRequestAsync(from, target, relay.Extra);
                break;

            case Protocol.FriendRelayAction.Add:
                ApplyRemoteAdd(target, from);
                break;

            case Protocol.FriendRelayAction.Remove:
                ApplyRemoteRemove(target, from);
                break;

            case Protocol.FriendRelayAction.Answer:
                await ApplyRemoteAnswerAsync(target, from, relay.Extra);
                break;

            case Protocol.FriendRelayAction.Message:
                await ApplyRemoteMessageAsync(from, target, relay.Extra);
                break;

            case Protocol.FriendRelayAction.Receipt:
                await ApplyRemoteReceiptAsync(from, target, relay.Extra);
                break;

            default:
                Logger.Log(LogLevel.Warning, $"[好友] 不认识的跨服动作:{relay.Action}");
                break;
        }
    }

    /// <summary>别服的人来加本服的人:写申请 + 推 657。</summary>
    private static async Task ApplyRemoteRequestAsync(PlayerId from, PlayerId target, string message)
    {
        if (!FriendStore.AppendRequest(target,
                new FriendRequestEntry(from.Full, message, DateTime.Now.ToString(ChatLog.TimeFormat, System.Globalization.CultureInfo.InvariantCulture)),
                out var error))
        {
            Logger.Log(LogLevel.Error, $"[好友] 跨服申请没写进 {target.Full} 的存档:{error}");
            return;
        }

        await NotifyAsync(target, Protocol.FriendEventKind.Request, from.Full);
        await PushRequestsAsync(target);
        Logger.Log(LogLevel.Info, $"[好友] 收到跨服申请:{from.Full} → {target.Full}");
    }

    /// <summary>对方服已经同意,本服把这一侧也写成好友。</summary>
    private static void ApplyRemoteAdd(PlayerId target, PlayerId from)
    {
        // target(本服申请人)当初在申请里填的备注,这一刻落进他自己那份档;取走就删
        if (!FriendStore.AddFriend(target, from.Full, FriendStore.TakePendingRemark(target, from.Full), false, ServerNameOf(from.ServerId, _config!), out var error))
        {
            Logger.Log(LogLevel.Error, $"[好友] 跨服同意后没写进 {target.Full} 的列表:{error}");
            return;
        }

        _ = NotifyAsync(target, Protocol.FriendEventKind.Agree, from.Full);
        Logger.Log(LogLevel.Info, $"[好友] 跨服好友已建立({target.Full} 这一侧):{from.Full}");
    }

    /// <summary>对方服把好友删了(或对方单方面删了),本服跟着删,别留下单向的死关系。</summary>
    private static void ApplyRemoteRemove(PlayerId target, PlayerId from)
    {
        FriendStore.RemoveFriend(target, from.Full, out _);
        _ = NotifyAsync(target, Protocol.FriendEventKind.Remove, from.Full);

        // 频道在本服的聊天后端里,只有本服能撤;发起方那边撤不到这一份
        _ = RevokeChatChannelForPairAsync(target, from);
        Logger.Log(LogLevel.Info, $"[好友] 跨服好友已删除({target.Full} 这一侧):{from.Full}");
    }

    /// <summary>别服把申请转回来(一般是对方点了同意/拒绝后回传),按回执处理。</summary>
    private static async Task ApplyRemoteAnswerAsync(PlayerId target, PlayerId from, string verdict)
    {
        FriendStore.RemoveRequest(target, from.Full, out _);
        if (string.Equals(verdict, "yes", StringComparison.OrdinalIgnoreCase))
        {
            ApplyRemoteAdd(target, from);
            return;
        }

        // 拒绝了:申请人当初填的备注一并作废
        FriendStore.SetPendingRemark(target, from.Full, "");
        await NotifyAsync(target, Protocol.FriendEventKind.Remove, from.Full);
    }

    /// <summary>收 616:别服好友上线/下线。先记进在线表(651 的在线位靠它),再推给把他当好友的人。</summary>
    private static async Task HandleFriendPresenceAsync(Packet packet, ZFile config)
    {
        var parts = Encoding.UTF8.GetString(packet.Data).Split('|');
        if (parts.Length < 2 || !PlayerId.TryParse(parts[0].Trim(), out var who))
        {
            return;
        }

        var state = parts[1].Trim();

        // 官方端是整网广播,本服的人也会回传一份;这是正常现象,不要记成告警
        if (SameServer(who.ServerId, SelfServerId(config)))
        {
            Logger.Log(LogLevel.Debug, $"[好友] 收到本服人的上下线广播({who.Full} {state}),已忽略");
            return;
        }

        SetRemoteOnline(who, state);

        // 收包是单 worker 串行的:这里对每条连接逐个 await,只要有一条连接卡在写包上,
        // 后面所有好友的上下线提醒都得排队。所以先收齐该通知谁,再并发发出去
        var targets = new List<TcpConnection>();   // 该收到这条上下线的人
        foreach (var connection in _server?.Connections ?? [])
        {
            if (connection.IsClosed)
            {
                continue;   // 已经在关了,发过去只会抛 disposed
            }

            var name = _server?.Registry.Find(connection.Id)?.User ?? "";
            var self = LocalPlayerId(Accounts.Find(name), config);
            if (self.IsEmpty || !FriendStore.AreFriends(self, who))
            {
                continue;
            }

            targets.Add(connection);
        }

        await Task.WhenAll(targets.Select(async connection =>
        {
            try
            {
                await connection.SendAsync(Protocol.FriendEvent, $"{state}|{who.Full}");
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, $"[好友] 给 {connection.RemoteEndPoint} 推上下线失败:{ex.Message}");
            }
        }));

        Logger.Log(LogLevel.Info, $"[好友] {who.Full} {state},已通知 {targets.Count} 个把他当好友的人");
    }

    /// <summary>收 618:ZXMOS 下发的「服务端编号 → 名字」映射,好友条的来源服就靠它显示。</summary>
    private static void HandleServerNameMapAsync(Packet packet, ZFile config)
    {
        var payload = Encoding.UTF8.GetString(packet.Data);
        var count = IndexServerNames(payload);
        Logger.Log(LogLevel.Info, $"[好友] 收到服务端名字映射:{count} 条");

        // 618 是第一处「本服知道自己是谁」的地方(第一行 self=);官方端表里没有本服名字就立刻补报,
        // 否则要等第一个玩家登录,期间别服好友条只能显示服ID
        _ = ReportSelfNameIfNeededAsync(config);
    }

    /// <summary>617 上报:告诉 ZXMOS「我是哪台、我叫什么」,它再广播给各服。本服没配名字就不报。</summary>
    private static async Task ReportServerNameAsync(ZFile config)
    {
        var os = _os;
        var self = SelfServerId(config);
        if (os is null || self.Length == 0)
        {
            return;
        }

        var name = config.MapGet("name").Trim();
        if (name.Length == 0)
        {
            // 服ID 不是名字:报上去各服的好友条就会显示 ZXMS-testserver-1
            Logger.Log(LogLevel.Warning, $"[好友] 本服没配名字(name 为空),不把服ID {self} 当名字上报");
            return;
        }

        try
        {
            await os.SendAsync(Protocol.ServerNameReport, $"{self}|{name}");
            Logger.Log(LogLevel.Info, $"[好友] 已上报本服名字:{self} = {name}");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 上报本服名字失败:{ex.Message}");
        }
    }

    /// <summary>
    /// 官方端名字表里没有本服、或者名字跟配置不一致时补报一次(617)。收到 618、与官方端重连成功后都调它。
    /// 上报会触发官方端重发 618,所以名字已经对上就不再发,免得两头来回刷;本服没配名字时直接跳过。
    /// </summary>
    private static async Task ReportSelfNameIfNeededAsync(ZFile config)
    {
        var self = SelfServerId(config);
        if (self.Length == 0)
        {
            // 还不知道自己是哪台,报了官方端也只能记成空键
            return;
        }

        var name = config.MapGet(KeyName).Trim();
        if (name.Length == 0)
        {
            // 没名字就没得报:拿服ID 顶上去只会让别服显示 ZXMS-xxx,还会每 10 秒空转一次
            return;
        }

        if (ServerNames.TryGetValue(self, out var known) && string.Equals(known, name, StringComparison.Ordinal))
        {
            return;
        }

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastNameReportAt);
        if (now - last < NameReportCooldownMs || Interlocked.CompareExchange(ref _lastNameReportAt, now, last) != last)
        {
            return;
        }

        await ReportServerNameAsync(config);
    }

    /// <summary>最近一次补报本服名字的时间(Environment.TickCount64),给 617/618 回环做冷却。</summary>
    private static long _lastNameReportAt;

    /// <summary>补报冷却:官方端名字表异常时不让 617/618 无限来回。</summary>
    private const int NameReportCooldownMs = 10_000;

    /// <summary>玩家登录成功后:把本服账号表整份上报给 ZXMOS,再告诉各服「他上线了」。</summary>
    private static async Task OnPlayerSignedInAsync(Account account, ZFile config)
    {
        await PushUserListUpAsync(config);
        await ReportServerNameAsync(config);
        await AnnouncePresenceAsync(account.Name, Protocol.FriendEventKind.Online);
    }

    /// <summary>
    /// 按账号名广播上线。中转登录的账号是「按需镜像」的,这会儿本地可能还没这份档,
    /// 所以最多等 2 秒;等不到就只记日志(下次重连会整份补广播)。
    /// </summary>
    private static async Task AnnouncePresenceByNameAsync(string name)
    {
        var account = Accounts.Find(name);
        for (var waited = 0; account is null && waited < 2000; waited += 100)
        {
            await Task.Delay(100);
            account = Accounts.Find(name);
        }

        if (account is null)
        {
            Logger.Log(LogLevel.Warning, $"[好友] {name} 上线了但本地账号还没镜像出来,这次没广播上线");
            return;
        }

        await AnnouncePresenceAsync(name, Protocol.FriendEventKind.Online);
    }

    /// <summary>把本服账号表整份报给 ZXMOS(613);它据此算出每个账号的归属服,再广播给全网。</summary>
    private static async Task PushUserListUpAsync(ZFile config)
    {
        var os = _os;
        if (os is null)
        {
            return;
        }

        var payload = string.Join('\n', Accounts.All.OrderBy(one => one.Id)
            .Select(one => (one.Id, one.Name, one.Level))
            .Select(row => $"{row.Id}|{row.Name}|{row.Level}"));
        try
        {
            await os.SendAsync(Protocol.UserListUp, payload);
            Logger.Log(LogLevel.Info, $"[用户列表] 已把本服账号表报给 ZXMOS:{Accounts.All.Count} 个账号");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[用户列表] 上报失败:{ex.Message}");
        }
    }

    /// <summary>告诉全网「某个人上线/下线了」;别服会自己过滤出跟他有关的人。</summary>
    private static async Task AnnouncePresenceAsync(string userName, string state)
    {
        var os = _os;
        var config = _config;
        if (os is null || config is null)
        {
            return;
        }

        var who = LocalPlayerId(Accounts.Find(userName), config);
        if (who.IsEmpty)
        {
            return;
        }

        try
        {
            await os.SendAsync(Protocol.FriendPresence, $"{who.Full}|{state}");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 广播上下线失败:{ex.Message}");
        }
    }
}
