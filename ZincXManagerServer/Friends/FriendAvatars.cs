using System.Text;                      // Encoding:读包负载
using ZincXManagerShared.Account;        // AvatarStore:头像存盘
using ZincXManagerShared.FileIO;         // ZFile:config.ini
using ZincXManagerShared.Friend;         // PlayerId / FriendStore / FriendCodec
using ZincXManagerShared.Logging;        // Logger / LogLevel
using ZincXManagerShared.Network;        // Protocol / ServerboundPacket

namespace ZincXManagerServer;

/// <summary>
/// 好友头像(607 要 / 608 回)与跨服要头像(648 要 / 649 回)。
///
/// <para>头像存在各服自己的 avatars 目录里,ZXMOS 不存图,只帮忙转话。
/// 本服有图就直接给;没有就问对方所在的服;对方也没有就算了,客户端画首字。</para>
/// </summary>
internal static partial class Program
{
    /// <summary>等别服头像的最长时间;超时就当没有,别让界面一直转圈。</summary>
    private const int AvatarWaitSeconds = 6;

    /// <summary>还在等别服回音的要头像请求。键是「账户名|请求者完整ID」。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, AvatarWaiter> AvatarWaiters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>一个挂着等回音的要头像请求。</summary>
    private sealed record AvatarWaiter(Guid ConnectionId, ulong PacketId, string Name, DateTime AskedAt);

    /// <summary>607 入口:客户端要某个账号的头像(通常是好友条上的)。</summary>
    private static async Task HandleFriendAvatarAsync(ServerboundPacket packet, ZFile config)
    {
        var self = PlayerOf(packet.Connection.Id, config);
        var raw = Encoding.UTF8.GetString(packet.Data).Trim();
        var split = raw.LastIndexOf('|');                       // 新客户端发「完整ID|账户名」,老的只发名字
        var askedFull = split > 0 ? raw[..split] : "";
        var name = (split > 0 ? raw[(split + 1)..] : raw).Trim();

        if (name.Length == 0)
        {
            await packet.Connection.SendAsync(Protocol.FriendAvatarAck, "|", packet.Id);
            return;
        }

        if (!MayViewAvatar(self, name, askedFull, config))
        {
            Logger.Log(LogLevel.Warning, $"[好友] {(self.IsEmpty ? "(未登录)" : self.Full)} 要账号 {name} 的头像,不是本人也不是好友,拒绝");
            await packet.Connection.SendAsync(Protocol.FriendAvatarAck, FriendCodec.BuildAvatarAck(name, []), packet.Id);
            return;
        }

        var bytes = AvatarStore.Load(name);
        if (bytes.Length > 0)
        {
            await packet.Connection.SendAsync(Protocol.FriendAvatarAck, FriendCodec.BuildAvatarAck(name, bytes), packet.Id);
            return;
        }

        if (self.IsEmpty || !PlayerId.TryParse(askedFull, out var asked) || SameServer(asked.ServerId, SelfServerId(config)))
        {
            await packet.Connection.SendAsync(Protocol.FriendAvatarAck, FriendCodec.BuildAvatarAck(name, []), packet.Id);
            return;
        }

        if (!await RelayAvatarRequestAsync(packet, name, asked, self))
        {
            await packet.Connection.SendAsync(Protocol.FriendAvatarAck, FriendCodec.BuildAvatarAck(name, []), packet.Id);
        }
    }

    /// <summary>本服没有这张图:记下等人,再让 ZXMOS 去问对方所在的服。</summary>
    private static async Task<bool> RelayAvatarRequestAsync(ServerboundPacket packet, string name, PlayerId asked, PlayerId self)
    {
        var os = _os;
        if (os is null)
        {
            return false;
        }

        var key = AvatarKeyOf(name, self.Full);
        AvatarWaiters[key] = new AvatarWaiter(packet.Connection.Id, packet.Id, name, DateTime.Now);
        try
        {
            await os.SendAsync(Protocol.AvatarRelay,
                FriendCodec.BuildAvatarRelay(asked.ServerId, name, self.Full));
            Logger.Log(LogLevel.Info, $"[好友] {self.Full} 要 {name} 的头像,本服没有,已向 {asked.ServerId} 要");
            return true;
        }
        catch (Exception ex)
        {
            AvatarWaiters.TryRemove(key, out _);
            Logger.Log(LogLevel.Warning, $"[好友] 向 {asked.ServerId} 要头像失败:{ex.Message}");
            return false;
        }
    }

    /// <summary>收 648:别服来要这张图。有就给,没有就明确说没有。</summary>
    private static async Task HandleAvatarRelayAsync(Packet packet)
    {
        var text = Encoding.UTF8.GetString(packet.Data);
        if (!FriendCodec.TryParseAvatarRelay(text, out var targetServerId, out var accountName, out var requesterId))
        {
            Logger.Log(LogLevel.Warning, $"[好友] 收到一条读不懂的要头像请求:{text[..Math.Min(80, text.Length)]}");
            return;
        }

        var bytes = AvatarStore.Load(accountName);
        var os = _os;
        if (os is null)
        {
            return;
        }

        await os.SendAsync(Protocol.AvatarRelayAck, FriendCodec.BuildAvatarRelayAck(accountName, bytes, requesterId));
        Logger.Log(LogLevel.Info, bytes.Length > 0
            ? $"[好友] {requesterId} 来要 {accountName} 的头像(目标是 {targetServerId}),已送出去({bytes.Length} 字节)"
            : $"[好友] {requesterId} 来要 {accountName} 的头像(目标是 {targetServerId}),本服也没有");
    }

    /// <summary>收 649:别服把图送回来了(也可能是「我也没有」的空图)。</summary>
    private static async Task HandleAvatarRelayAckAsync(Packet packet, ZFile config)
    {
        var text = Encoding.UTF8.GetString(packet.Data);
        if (!FriendCodec.TryParseAvatarRelayAck(text, out var accountName, out var imageBytes, out var requesterId))
        {
            Logger.Log(LogLevel.Warning, $"[好友] 收到一条读不懂的头像回音:{text[..Math.Min(80, text.Length)]}");
            return;
        }

        if (!AvatarWaiters.TryRemove(AvatarKeyOf(accountName, requesterId), out var waiter))
        {
            Logger.Log(LogLevel.Info, $"[好友] 收到 {accountName} 的头像,但已经没人等了(可能早超时了)");
            return;
        }

        var connection = _server?.Connections.FirstOrDefault(one => one.Id == waiter.ConnectionId);
        if (connection is null)
        {
            return;
        }

        await connection.SendAsync(Protocol.FriendAvatarAck, FriendCodec.BuildAvatarAck(waiter.Name, imageBytes), waiter.PacketId);
        Logger.Log(LogLevel.Info, imageBytes.Length > 0
            ? $"[好友] {waiter.Name} 的头像已从别服取到({imageBytes.Length} 字节),转给客户端"
            : $"[好友] 别服也没有 {waiter.Name} 的头像,回空给客户端");
    }

    /// <summary>每轮 (约5秒) 扫一遍:等太久的请求回空图,免得客户端一直挂着。</summary>
    private static async Task SweepAvatarWaitersAsync()
    {
        foreach (var pair in AvatarWaiters)
        {
            if ((DateTime.Now - pair.Value.AskedAt).TotalSeconds < AvatarWaitSeconds)
            {
                continue;
            }

            if (!AvatarWaiters.TryRemove(pair.Key, out var waiter))
            {
                continue;
            }

            var connection = _server?.Connections.FirstOrDefault(one => one.Id == waiter.ConnectionId);
            if (connection is not null)
            {
                await connection.SendAsync(Protocol.FriendAvatarAck, FriendCodec.BuildAvatarAck(waiter.Name, []), waiter.PacketId);
            }

            Logger.Log(LogLevel.Warning, $"[好友] 等 {waiter.Name} 的头像超时,已回空");
        }
    }

    /// <summary>能不能看这张头像:本人可以,好友可以,别人不行。</summary>
    private static bool MayViewAvatar(PlayerId self, string name, string askedFull, ZFile config)
    {
        if (self.IsEmpty)
        {
            return false;
        }

        // 客户端会带上完整 ID,先用它判 —— 本地编号跨服会撞号,只按编号认会认错人
        if (askedFull.Length > 0 && PlayerId.TryParse(askedFull, out var declared))
        {
            if (declared != self && !FriendStore.AreFriends(self, declared))
            {
                return false;
            }

            // 鉴权的人和要图的人必须是同一个:否则拿一个好友的 ID 当挡箭牌,填上别人的名字就能取走那张图。
            // 名字查不到(只有 #编号)时宁可不给,也不按客户端自报的名字放行
            var expected = AccountNameOf(declared);
            return expected.Length > 0
                && !expected.StartsWith('#')
                && string.Equals(expected, name, StringComparison.OrdinalIgnoreCase);
        }

        var account = Accounts.Find(name);
        if (account is null)
        {
            return false;
        }

        var guess = new PlayerId(SelfServerId(config), account.Id);
        return guess == self || FriendStore.AreFriends(self, guess);
    }

    /// <summary>等头像用的键:账户名 + 请求者完整 ID,同名的人也不会串。</summary>
    private static string AvatarKeyOf(string name, string requester)
    {
        return name.Trim().ToLowerInvariant() + "|" + requester.Trim().ToLowerInvariant();
    }
}
