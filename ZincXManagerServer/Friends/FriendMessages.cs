using System.Collections.Concurrent;    // ConcurrentDictionary:重发去重表
using System.Text;                      // Encoding:读包负载
using ZincXManagerShared.FileIO;         // ZFile:config.ini
using ZincXManagerShared.Friend;         // PlayerId / FriendStore / ChatLog / FriendCodec
using ZincXManagerShared.Logging;        // Logger / LogLevel
using ZincXManagerShared.Network;        // Protocol / ServerboundPacket

namespace ZincXManagerServer;

/// <summary>
/// 好友私聊(400 发 / 401 回执 / 402 查 / 403 回 / 404 送达与已读 / 405 已读上报)。
///
/// <para>只有好友之间能说话 —— 校验在这里做,客户端说「我们是好友」不算数。
/// 正文按原样存,但换行和制表符会压成空格,否则存档里的行就乱了。</para>
///
/// <para>三件事靠「消息编号 + 会话序号」串起来:重发去重(编号),断线增量拉(序号),
/// 送达与已读回执(编号或序号)。序号就是聊天文件的行号,见 <see cref="ChatLog"/>。</para>
/// </summary>
internal static partial class Program
{
    /// <summary>重发去重表:<c>发送者完整ID|消息编号</c> → 当时分到的序号。满了先丢最老的。</summary>
    private static readonly ConcurrentDictionary<string, (int Seq, DateTime At)> SeenMessages = new();

    /// <summary>去重表最多记这么多条(单服几十人,足够覆盖断线重连那几十秒)。</summary>
    private const int SeenMessageLimit = 4096;

    /// <summary>400 入口:给好友发一句话。</summary>
    private static async Task HandleFriendMessageAsync(ServerboundPacket packet, ZFile config)
    {
        var self = PlayerOf(packet.Connection.Id, config);
        if (self.IsEmpty)
        {
            await packet.SendAsync(Protocol.FriendMsgAck, "err|请先登录", packet.Id);
            return;
        }

        if (!FriendCodec.TryParseFriendMessage(Encoding.UTF8.GetString(packet.Data), out var targetText, out var messageId, out var text))
        {
            await packet.SendAsync(Protocol.FriendMsgAck, "err|对方 ID 不对", packet.Id);
            return;
        }

        if (!PlayerId.TryParse(targetText, out var target))
        {
            await packet.SendAsync(Protocol.FriendMsgAck, "err|对方 ID 不对", packet.Id);
            return;
        }

        if (!FriendStore.AreFriends(self, target))
        {
            // 三段格式带上消息编号,客户端能把红叹号标到具体那条气泡上
            await packet.SendAsync(Protocol.FriendMsgAck, FriendCodec.BuildMessageError(messageId, "你们还不是好友"), packet.Id);
            return;
        }

        // 同一条重发:回上次那个序号,不再存第二遍、也不再投一次
        var seenKey = $"{self.Full}|{messageId}";
        if (messageId.Length > 0 && SeenMessages.TryGetValue(seenKey, out var seen))
        {
            await packet.SendAsync(Protocol.FriendMsgAck, FriendCodec.BuildMessageAck(messageId, seen.Seq, duplicate: true), packet.Id);
            return;
        }

        // 两个人的存档各写一条:发送方记 out,接收方记 in
        ChatLog.Append(target, self.Full, true, text, DateTime.Now, out var inSeq, out var saveError);
        ChatLog.Append(self, target.Full, false, text, DateTime.Now, out var outSeq, out _);
        if (saveError.Length > 0)
        {
            Logger.Log(LogLevel.Warning, $"[好友] {self.Full} → {target.Full} 的聊天记录没存下:{saveError}");
        }

        Remember(messageId.Length > 0 ? seenKey : "", outSeq);
        await packet.SendAsync(Protocol.FriendMsgAck, FriendCodec.BuildMessageAck(messageId, outSeq, duplicate: false), packet.Id);

        // 别服的好友:本服送不到,交给 ZXMOS 转过去(对方服自己存、自己送、自己回送达)
        if (!SameServer(target.ServerId, SelfServerId(config)))
        {
            // 中转本身发不出去(ZXMS 到 ZXMOS 断了):这条消息永远到不了,得让发送者看见红叹号
            if (!await RelayFriendActionAsync(Protocol.FriendRelayAction.Message, self, target, FriendCodec.BuildRelayedMessage(messageId, text), 0))
            {
                await SendToPlayerAsync(self, Protocol.FriendMsgReceipt, FriendCodec.BuildReceipt(target.Full, messageId, "failed"));
            }

            return;
        }

        var delivered = await SendToPlayerAsync(target, Protocol.FriendMsg, FriendCodec.BuildIncomingMessage(self.Full, text));
        Logger.Log(LogLevel.Info,
            $"[好友] 私聊 {self.Full} → {target.Full}({text.Length} 字){(delivered ? "" : ",对方不在线,已存记录")}");
        if (delivered)
        {
            await SendToPlayerAsync(self, Protocol.FriendMsgReceipt, FriendCodec.BuildReceipt(target.Full, messageId, "delivered"));
        }
    }

    /// <summary>402 入口:拉序号大于起点的聊天记录。</summary>
    private static async Task HandleFriendHistoryQueryAsync(ServerboundPacket packet, ZFile config)
    {
        var self = PlayerOf(packet.Connection.Id, config);
        if (self.IsEmpty)
        {
            await packet.SendAsync(Protocol.FriendMsgHistoryReply, "", packet.Id);
            return;
        }

        if (!FriendCodec.TryParseHistoryQuery(Encoding.UTF8.GetString(packet.Data), out var peerText, out var afterSeq)
            || !PlayerId.TryParse(peerText, out var peer))
        {
            await packet.SendAsync(Protocol.FriendMsgHistoryReply, "", packet.Id);
            return;
        }

        var lines = ChatLog.Read(self, peer.Full, afterSeq, 200, out var error);
        if (error.Length > 0)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 读 {self.Full} 与 {peer.Full} 的记录:{error}");
        }

        await packet.SendAsync(Protocol.FriendMsgHistoryReply, FriendCodec.BuildHistoryReply(lines), packet.Id);
    }

    /// <summary>405 入口:本人把这段对话读到第几号了(用来算对方看到的未读数,以及回已读)。</summary>
    private static async Task HandleFriendReadMarkAsync(ServerboundPacket packet, ZFile config)
    {
        var self = PlayerOf(packet.Connection.Id, config);
        if (self.IsEmpty)
        {
            return;
        }

        if (!FriendCodec.TryParseReadMark(Encoding.UTF8.GetString(packet.Data), out var peerText, out var seq)
            || !PlayerId.TryParse(peerText, out var peer))
        {
            return;
        }

        FriendStore.WriteCursor(self, peer.Full, seq);

        // 告诉发消息那一方:我读到了第几号
        if (SameServer(peer.ServerId, SelfServerId(config)))
        {
            await SendToPlayerAsync(peer, Protocol.FriendMsgReceipt, FriendCodec.BuildReceipt(self.Full, seq.ToString(), "read"));
            return;
        }

        await RelayFriendActionAsync(Protocol.FriendRelayAction.Receipt, self, peer, $"{seq}|read", 0);
    }

    /// <summary>收 615 里「发消息」这种动作:别服的人给我发了一句话(<c>消息编号|正文</c>)。</summary>
    private static async Task ApplyRemoteMessageAsync(PlayerId from, PlayerId target, string extra)
    {
        var cut = extra.IndexOf(FriendCodec.PipeSeparator);
        var messageId = cut > 0 ? extra[..cut].Trim() : "";
        var text = cut > 0 ? extra[(cut + 1)..] : extra;

        ChatLog.Append(target, from.Full, true, text, DateTime.Now, out _, out _);
        var delivered = await SendToPlayerAsync(target, Protocol.FriendMsg, FriendCodec.BuildIncomingMessage(from.Full, text));
        Logger.Log(LogLevel.Info,
            $"[好友] 收到跨服私聊:{from.Full} → {target.Full}({text.Length} 字){(delivered ? "" : ",对方不在线,已存记录")}");

        // 送达回执沿原路退回:本服 → ZXMOS → 发起服 → 客户端
        if (delivered)
        {
            await RelayFriendActionAsync(Protocol.FriendRelayAction.Receipt, target, from, $"{messageId}|delivered", 0);
        }
    }

    /// <summary>收 615 里的「已读」:对方读到了第几号。</summary>
    private static async Task ApplyRemoteReceiptAsync(PlayerId from, PlayerId target, string extra)
    {
        var cut = extra.IndexOf(FriendCodec.PipeSeparator);
        var key = cut > 0 ? extra[..cut].Trim() : extra;
        var state = cut > 0 ? extra[(cut + 1)..].Trim() : "delivered";
        await SendToPlayerAsync(target, Protocol.FriendMsgReceipt, FriendCodec.BuildReceipt(from.Full, key, state));
    }

    /// <summary>记一条去重记录;表满了先把最老的清掉一半。</summary>
    private static void Remember(string key, int seq)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (SeenMessages.Count >= SeenMessageLimit)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            foreach (var one in SeenMessages)
            {
                if (one.Value.At < cutoff)
                {
                    SeenMessages.TryRemove(one.Key, out _);
                }
            }

            if (SeenMessages.Count >= SeenMessageLimit)
            {
                SeenMessages.Clear();
            }
        }

        SeenMessages[key] = (seq, DateTime.UtcNow);
    }
}
