using System.Linq;                      // Enumerable.Count:数未读
using System.Text;                      // Encoding:读包负载
using ZincXManagerShared.FileIO;         // ZFile:config.ini
using ZincXManagerShared.Friend;         // PlayerId / FriendStore / FriendCodec
using ZincXManagerShared.Logging;        // Logger / LogLevel
using ZincXManagerShared.Network;        // Protocol / ServerboundPacket

namespace ZincXManagerServer;

/// <summary>
/// 好友列表(650 查 / 651 回)与删除好友(660 删 / 661 回执)。
///
/// <para>列表里只放「我这侧」的档;对方是不是也把我当好友,那边有它自己的档。
/// 在线状态是查出来的,不落盘 —— 落盘就会变成过期数据。</para>
/// </summary>
internal static partial class Program
{
    /// <summary>650 入口:把好友列表发回去。</summary>
    private static async Task HandleFriendListQueryAsync(ServerboundPacket packet, ZFile config)
    {
        var self = PlayerOf(packet.Connection.Id, config);
        if (self.IsEmpty)
        {
            await packet.Connection.SendAsync(Protocol.FriendListReply, "", packet.Id);
            return;
        }

        var friends = FriendStore.ReadFriends(self, out var error);
        if (error.Length > 0)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 读 {self.Full} 的好友列表: {error}");
        }

        var rows = new List<FriendEntry>(friends.Count);
        foreach (var friend in friends)
        {
            if (!PlayerId.TryParse(friend.Id, out var id))
            {
                Logger.Log(LogLevel.Warning, $"[好友] {self.Full} 的列表里有一行 ID 读不懂:{friend.Id}");
                continue;
            }

            // 未读只数「对方发来的」行:自己发出去的那几条也在存档里,拿总数减游标就会给自己冒红点。
            // 预览要整段的最后一条 —— 未读和预览共用这一次读盘,不再为预览单独读一遍。
            var lines = ChatLog.Read(self, id.Full, 0, 0, out var chatError);
            if (chatError.Length > 0)
            {
                Logger.Log(LogLevel.Debug, $"[好友] 读 {self.Full} 与 {id.Full} 的聊天记录:{chatError}");
            }

            var cursor = FriendStore.ReadCursor(self, id.Full);                          //已读到第几号
            var unread = lines.Count(line => line.Incoming && line.Seq > cursor);        //未读条数
            var preview = "";
            if (lines.Count > 0)
            {
                var tail = lines[lines.Count - 1];
                // 对方发的带上他的名字,自己发的只给正文 —— 一眼看出这句是谁说的
                var who = friend.Remark.Length > 0 ? friend.Remark : AccountNameOf(id);
                preview = tail.Incoming ? who + "：" + tail.Text : tail.Text;
            }

            rows.Add(new FriendEntry(
                id.Full,
                AccountNameOf(id),
                friend.Remark,
                IsOnline(id, config),
                ServerNameOf(id.ServerId, config),
                unread,
                preview));
        }

        await packet.Connection.SendAsync(Protocol.FriendListReply, FriendCodec.BuildFriendList(rows), packet.Id);
        Logger.Log(LogLevel.Info, $"[好友] 查好友列表:{self.Full} {rows.Count} 个");
    }

    /// <summary>660 入口:删掉一个好友。本侧立刻删,别服那边交给 ZXMOS 通知它删。</summary>
    private static async Task HandleFriendRemoveAsync(ServerboundPacket packet, ZFile config)
    {
        var self = PlayerOf(packet.Connection.Id, config);
        if (self.IsEmpty)
        {
            await packet.SendAsync(Protocol.FriendRemoveAck, "err|请先登录", packet.Id);
            return;
        }

        var text = Encoding.UTF8.GetString(packet.Data).Trim();
        if (!PlayerId.TryParse(text, out var target) || target == self)
        {
            await packet.SendAsync(Protocol.FriendRemoveAck, "err|对方 ID 不对", packet.Id);
            return;
        }

        if (!FriendStore.RemoveFriend(self, target.Full, out var error))
        {
            Logger.Log(LogLevel.Warning, $"[好友] {self.Full} 删 {target.Full} 失败:{error}");
            await packet.SendAsync(Protocol.FriendRemoveAck, "err|" + error, packet.Id);
            return;
        }

        if (!SameServer(target.ServerId, SelfServerId(config)))
        {
            // 跨服:先交给中继,成功才回 ok;失败由中继失败回执(同一个包 Id)如实告诉玩家。
            // 不能传 0 —— 传 0 时失败回执根本发不出去,玩家会以为对方那边也删了
            if (await RelayFriendActionAsync(Protocol.FriendRelayAction.Remove, self, target, "", packet.Id))
            {
                await packet.SendAsync(Protocol.FriendRemoveAck, "ok|", packet.Id);
            }

            await NotifyAsync(self, Protocol.FriendEventKind.Remove, target.Full);
            Logger.Log(LogLevel.Info, $"[好友] {self.Full} 删除了好友 {target.Full}(跨服,已交给 ZXMOS)");
            return;
        }

        await packet.SendAsync(Protocol.FriendRemoveAck, "ok|", packet.Id);
        await NotifyAsync(self, Protocol.FriendEventKind.Remove, target.Full);
        Logger.Log(LogLevel.Info, $"[好友] {self.Full} 删除了好友 {target.Full}");

        // 本服的对方也要跟着删,否则他会看到一个已经不存在的好友
        FriendStore.RemoveFriend(target, self.Full, out _);
        await NotifyAsync(target, Protocol.FriendEventKind.Remove, self.Full);
        // 聊天频道的成员关系不会跟着好友表变,不撤的话两人开着窗口还能继续聊;
        // 删频道要走 HTTP,这里不能等它(dispatcher 是串行的),所以只发出去不 await
        _ = RevokeChatChannelForPairAsync(self, target);
    }
}
