using System.Globalization;            // CultureInfo:时间格式
using System.Text;                      // Encoding:读包负载
using ZincXManagerShared.Account;        // Account:本服账号
using ZincXManagerShared.FileIO;         // ZFile:config.ini
using ZincXManagerShared.Friend;         // PlayerId / FriendStore / FriendCodec
using ZincXManagerShared.Logging;        // Logger / LogLevel
using ZincXManagerShared.Network;        // Protocol / ServerboundPacket

namespace ZincXManagerServer;

/// <summary>
/// 好友申请(654 发申请 / 655 回执 / 656 拉列表 / 657 列表 / 658 回答 / 659 回答回执)。
///
/// <para>判断全在这里:能不能加、是不是已经好友、目标在哪台服。客户端不许自己拦(它拦得对不对没人检查),
/// 它只管把玩家的动作发上来。</para>
/// </summary>
internal static partial class Program
{
    /// <summary>654 入口:玩家点了「发送申请」。</summary>
    private static async Task HandleFriendRequestAsync(ServerboundPacket packet, ZFile config)
    {
        var self = PlayerOf(packet.Connection.Id, config);
        if (self.IsEmpty)
        {
            await packet.SendAsync(Protocol.FriendRequestAck, "err|请先登录", packet.Id);
            return;
        }

        if (!FriendCodec.TryParseFriendRequest(Encoding.UTF8.GetString(packet.Data), out var targetText, out var message, out var pendingRemark))
        {
            await packet.SendAsync(Protocol.FriendRequestAck, "err|对方 ID 不对", packet.Id);
            return;
        }

        if (message.Length == 0)
        {
            await packet.SendAsync(Protocol.FriendRequestAck, "err|写一句让对方知道你是谁", packet.Id);
            return;
        }

        if (!PlayerId.TryParse(targetText, out var target))
        {
            await packet.SendAsync(Protocol.FriendRequestAck, "err|对方 ID 不对", packet.Id);
            return;
        }

        if (target == self)
        {
            await packet.SendAsync(Protocol.FriendRequestAck, "err|不能加自己", packet.Id);
            return;
        }

        if (FriendStore.AreFriends(self, target) || FriendStore.AreFriends(target, self))
        {
            await packet.SendAsync(Protocol.FriendRequestAck, "err|你们已经是好友了", packet.Id);
            return;
        }

        // 申请时填的备注先挂在自己名下(PendingRemarks.dat),对方同意那一刻才落进 Friends.dat;拒绝就作废。
        // 限长与 665 聊天窗备注同一把尺子。
        var remark = pendingRemark.Length > RemarkMaxLength ? pendingRemark[..RemarkMaxLength].Trim() : pendingRemark;
        FriendStore.SetPendingRemark(self, target.Full, remark);

        var isLocal = SameServer(target.ServerId, SelfServerId(config));
        if (!isLocal)
        {
            // 中继成功才回 ok:失败由中继失败回执(err|原因)如实告诉玩家,两条分支各回各的
            if (await RelayFriendActionAsync(Protocol.FriendRelayAction.Request, self, target, message, packet.Id))
            {
                await packet.SendAsync(Protocol.FriendRequestAck, "ok|", packet.Id);
            }

            Logger.Log(LogLevel.Info, $"[好友] {self.Full} 向 {target.Full}(别服)发出好友申请,已交给 ZXMOS 中转");
            return;
        }

        var stored = FriendStore.AppendRequest(
            target,
            new FriendRequestEntry(self.Full, message, DateTime.Now.ToString(ChatLog.TimeFormat, CultureInfo.InvariantCulture)),
            out var storeError);
        if (!stored)
        {
            Logger.Log(LogLevel.Error, $"[好友] {self.Full} 的申请没能写进 {target.Full} 的存档:{storeError}");
            await packet.SendAsync(Protocol.FriendRequestAck, "err|服务端存不下,请稍后再试", packet.Id);
            return;
        }

        await packet.SendAsync(Protocol.FriendRequestAck, "ok|", packet.Id);
        // 662 的 Request 语义是「有人给我发了好友申请」,要推给被申请人
        await NotifyAsync(target, Protocol.FriendEventKind.Request, self.Full);
        await PushRequestsAsync(target);

        Logger.Log(LogLevel.Info, $"[好友] {self.Full} 向 {target.Full} 发出好友申请");
    }

    /// <summary>656 入口:要「别人发给我的申请」列表。</summary>
    private static async Task HandleFriendRequestsQueryAsync(ServerboundPacket packet, ZFile config)
    {
        var self = PlayerOf(packet.Connection.Id, config);
        await packet.Connection.SendAsync(Protocol.FriendRequestsReply, RequestsPayload(self), packet.Id);
    }

    /// <summary>658 入口:玩家点了「同意 / 拒绝」。</summary>
    private static async Task HandleFriendAnswerAsync(ServerboundPacket packet, ZFile config)
    {
        var self = PlayerOf(packet.Connection.Id, config);
        if (self.IsEmpty)
        {
            await packet.SendAsync(Protocol.FriendAnswerAck, "err|请先登录", packet.Id);
            return;
        }

        if (!FriendCodec.TryParseFriendAnswer(Encoding.UTF8.GetString(packet.Data), out var fromText, out var accepted)
            || !PlayerId.TryParse(fromText, out var from))
        {
            await packet.SendAsync(Protocol.FriendAnswerAck, "err|申请者 ID 不对", packet.Id);
            return;
        }

        // 没有这条申请就不许处理:否则任何人发一条 658 就能把自己塞进别人的好友列表
        var pending = FriendStore.ReadRequests(self, out var requestError);
        if (requestError.Length > 0)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 读 {self.Full} 的申请列表(658): {requestError}");
        }

        if (!pending.Any(one => string.Equals(one.FromId, from.Full, StringComparison.Ordinal)))
        {
            await packet.SendAsync(Protocol.FriendAnswerAck, "err|没有这条申请", packet.Id);
            Logger.Log(LogLevel.Warning, $"[好友] {self.Full} 想处理一条不存在的申请({from.Full}),已拒绝");
            return;
        }

        FriendStore.RemoveRequest(self, from.Full, out _);

        var isLocal = SameServer(from.ServerId, SelfServerId(config));
        if (!accepted)
        {
            // 拒绝了:申请人当初填的备注一并作废
            FriendStore.SetPendingRemark(from, self.Full, "");

            // 别服的申请人得知道自己被拒了,否则他那边永远显示「等待通过」
            if (!isLocal)
            {
                await RelayFriendActionAsync(Protocol.FriendRelayAction.Answer, self, from, "no", packet.Id);
            }

            await packet.SendAsync(Protocol.FriendAnswerAck, "ok|", packet.Id);
            Logger.Log(LogLevel.Info, $"[好友] {self.Full} 拒绝了 {from.Full} 的申请");
            return;
        }

        var fromName = AccountNameOf(from);
        if (!isLocal)
        {
            // 别服的对方由它自己的服写档;这边先把「我这侧」写好,再让 ZXMOS 通知它
            FriendStore.AddFriend(self, from.Full, "", false, ServerNameOf(from.ServerId, config), out var selfError);
            await RelayFriendActionAsync(Protocol.FriendRelayAction.Add, self, from, "", packet.Id);
            await packet.SendAsync(Protocol.FriendAnswerAck, "ok|", packet.Id);
            Logger.Log(LogLevel.Info, $"[好友] {self.Full} 同意 {from.Full}(别服),本端列表已写入,对方走中转{(selfError.Length > 0 ? ":" + selfError : "")}");
            return;
        }

        FriendStore.AddFriend(self, from.Full, "", false, "", out var e1);
        // 申请人(from)当初填的备注这时候才落进他自己那份档;取走就删,不留尾巴
        FriendStore.AddFriend(from, self.Full, FriendStore.TakePendingRemark(from, self.Full), false, "", out var e2);
        await packet.SendAsync(Protocol.FriendAnswerAck, "ok|", packet.Id);
        await NotifyAsync(from, Protocol.FriendEventKind.Agree, self.Full);
        await NotifyAsync(self, Protocol.FriendEventKind.Agree, from.Full);
        Logger.Log(LogLevel.Info,
            $"[好友] {self.Full} 同意 {fromName}({from.Full}) 的申请,双方列表都已写入"
            + (e1.Length + e2.Length > 0 ? $"(存档错误:{e1}{e2})" : ""));

        // 刚成为好友的这两个人,聊天后端里还没有他们的私聊频道,这里补建
        var selfAccount = Accounts.FindById(self.Code);
        var fromAccount = Accounts.FindById(from.Code);
        if (selfAccount is not null && fromAccount is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await EnsureChatChannelForPairAsync(selfAccount, fromAccount, config);
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Warning, $"[聊天] 给新好友 {self.Full} ↔ {from.Full} 建频道失败:{ex.Message}");
                }
            });
        }
    }

    /// <summary>把某个玩家的申请列表推给他(在线才有意义);顺带用作 656 的应答源。</summary>
    private static async Task PushRequestsAsync(PlayerId target)
    {
        var payload = RequestsPayload(target);
        if (payload.Length == 0)
        {
            return;
        }

        if (await SendToPlayerAsync(target, Protocol.FriendRequestsReply, payload))
        {
            Logger.Log(LogLevel.Info, $"[好友] 已把申请列表推给 {target.Full}({payload.Split('\n').Length} 条)");
        }
    }

    /// <summary>读某个玩家的申请列表并拼成 657 负载。</summary>
    private static string RequestsPayload(PlayerId owner)
    {
        if (owner.IsEmpty)
        {
            return "";
        }

        var entries = FriendStore.ReadRequests(owner, out var error);
        if (error.Length > 0)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 读 {owner.Full} 的申请列表: {error}");
        }

        return FriendCodec.BuildFriendRequests(entries);
    }

    /// <summary>给某个玩家推一条好友事件(662);不在线就什么也不做。</summary>
    private static async Task NotifyAsync(PlayerId target, string kind, string otherFullId)
    {
        await SendToPlayerAsync(target, Protocol.FriendEvent, $"{kind}|{otherFullId}");
    }

    /// <summary>跨服账号名:本服账号表没有就问全网账号表,都没有就只给编号。</summary>
    private static string AccountNameOf(PlayerId who)
    {
        var local = Accounts.FindById(who.Code);
        if (local is not null && SameServer(who.ServerId, SelfServerId(_config!)))
        {
            return local.Name;
        }

        if (NetworkAccounts.TryGetValue(who.Full, out var known))
        {
            return known.Name;
        }

        return "#" + who.Code;
    }
}
