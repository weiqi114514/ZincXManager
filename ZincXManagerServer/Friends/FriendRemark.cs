using System.Text;                      // Encoding:读包负载
using ZincXManagerShared.FileIO;         // ZFile:config.ini
using ZincXManagerShared.Friend;         // PlayerId / FriendStore / FriendCodec
using ZincXManagerShared.Logging;        // Logger / LogLevel
using ZincXManagerShared.Network;        // Protocol / ServerboundPacket

namespace ZincXManagerServer;

/// <summary>
/// 665 好友备注:设 / 清「我给对方起的备注」。
///
/// <para>备注是本端私有的展示信息,只写自己那份好友档,不通知对方,
/// 所以不经 615 中继 —— 对方服既无从也无需知道我叫他什么。</para>
/// </summary>
internal static partial class Program
{
    /// <summary>备注最多存这么多个字符;超出的直接截断,不报错。</summary>
    private const int RemarkMaxLength = 32;

    /// <summary>665 入口:负载 <c>对方完整ID\t备注</c>,备注为空串表示清除。</summary>
    private static async Task HandleFriendRemarkAsync(ServerboundPacket packet, ZFile config)
    {
        var self = PlayerOf(packet.Connection.Id, config);
        if (self.IsEmpty)
        {
            await packet.SendAsync(Protocol.FriendRemarkAck, "err|请先登录", packet.Id);
            return;
        }

        var text = Encoding.UTF8.GetString(packet.Data);
        var cut = text.IndexOf(FriendCodec.FieldSeparator);   //ID 与备注之间的制表符
        if (cut <= 0 || !PlayerId.TryParse(text[..cut], out var target) || target == self)
        {
            await packet.SendAsync(Protocol.FriendRemarkAck, "err|对方 ID 不对", packet.Id);
            return;
        }

        // 竖线和空格留着(备注里常见),制表符与换行必须去掉 —— 好友档是「一行一条、tab 分列」
        var remark = text[(cut + 1)..]
            .Replace(FriendCodec.FieldSeparator, ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (remark.Length > RemarkMaxLength)
        {
            remark = remark[..RemarkMaxLength];
        }

        if (!FriendStore.AreFriends(self, target))
        {
            await packet.SendAsync(Protocol.FriendRemarkAck, "err|你们还不是好友", packet.Id);
            return;
        }

        // 好友档里「已存在则更新」,所以写备注就是把它整条重写一遍(在线状态顺带刷成当前的)
        if (!FriendStore.AddFriend(self, target.Full, remark, IsOnline(target, config), ServerNameOf(target.ServerId, config), out var error))
        {
            Logger.Log(LogLevel.Warning, $"[好友] {self.Full} 设 {target.Full} 的备注失败:{error}");
            await packet.SendAsync(Protocol.FriendRemarkAck, "err|" + error, packet.Id);
            return;
        }

        await packet.SendAsync(Protocol.FriendRemarkAck, "ok", packet.Id);
        Logger.Log(LogLevel.Info, remark.Length == 0
            ? $"[好友] {self.Full} 已清除 {target.Full} 的备注"
            : $"[好友] {self.Full} 把 {target.Full} 的备注设成「{remark}」");
    }
}
