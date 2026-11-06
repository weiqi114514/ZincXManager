using System.Collections.Concurrent;   // ConcurrentDictionary:多线程共享的在线表/等待表
using System.Text;                     // Encoding:包负载编解码
using ZincXManagerShared.Account;       // AccountStore / Account / AvatarStore
using ZincXManagerShared.FileIO;        // ZFile:config.ini
using ZincXManagerShared.Friend;        // PlayerId / FriendCodec / FriendStore
using ZincXManagerShared.Logging;       // Logger / LogLevel
using ZincXManagerShared.Network;       // Protocol / TcpConnection / ClientRegistry

namespace ZincXManagerServer;

/// <summary>
/// 好友功能的公共底座:本服是谁、谁是哪个玩家、怎么找到他的连接、全网账号表怎么进内存。
///
/// <para>这些是 <see cref="Program"/> 的 partial 部分,所以能直接用 Program 里的
/// <c>Accounts</c>、<c>_server</c>、<c>_os</c>、<c>_config</c>,不用到处传上下文对象。
/// 各协议处理器(同目录其它文件)只允许通过这里的方法拿数据,不自己遍历连接表。</para>
/// </summary>
internal static partial class Program
{
    /// <summary>
    /// 全网账号表:键 = 账号的完整ID(<c>ZXMS-x-1-13</c>);没有归属服的直连官方账号键为 <c>#13</c>。
    /// 内容由 614 整份替换,断线时清空。
    /// </summary>
    private static readonly ConcurrentDictionary<string, NetworkAccount> NetworkAccounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 别服玩家的在线表:键 = 完整ID。由 616 上下线广播维护,与官方端断开时清空。
    /// 本服的人不查这张表 —— 那是 <see cref="FindOnline"/> 的事,这张表只补「别服的人现在在不在」。
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> RemoteOnline = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本服在官方端那边的完整服ID(形如 <c>ZXMS-testserver-1</c>);还没拿到时是空串。</summary>
    private static volatile string _selfServerId = "";

    /// <summary>本服的完整服ID(<c>ZXMS-登录名-编号</c>);官方端还没告诉过就是空串。</summary>
    private static string SelfServerId(ZFile config) => _selfServerId;

    /// <summary>官方端告诉本服「你是哪台」时记下来(618 的第一行 <c>self=服ID</c>)。</summary>
    private static void SetSelfServerId(string serverId)
    {
        if (serverId.Length == 0 || string.Equals(serverId, _selfServerId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selfServerId = serverId;
        Logger.Log(LogLevel.Info, $"[好友] 官方端把我的服ID定成 {serverId}");
    }

    /// <summary>某个账号在本服的完整ID;账号不存在返回空地址。</summary>
    private static PlayerId LocalPlayerId(Account? account, ZFile config)
    {
        var server = SelfServerId(config);
        return account is null || server.Length == 0 ? default : new PlayerId(server, account.Id);
    }

    /// <summary>某条连接当前登录的玩家;未登录或拿不到官方编号时返回空地址。</summary>
    private static PlayerId PlayerOf(Guid connectionId, ZFile config)
    {
        var name = _server?.Registry.Find(connectionId)?.User ?? "";
        return name.Length == 0 ? default : LocalPlayerId(Accounts.Find(name), config);
    }

    /// <summary>两个服ID是不是同一个(忽略大小写 —— 官方端的登录名大小写不一定跟本端配置一致)。</summary>
    private static bool SameServer(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 网络账号表里的一条,换算成完整ID。没有归属服(直连官方注册的玩家)返回空地址 ——
    /// 这种账号不跨服找,只能按编号在本服认。
    /// </summary>
    private static PlayerId PlayerIdOf(NetworkAccount account) =>
        account.ServerId.Length == 0 ? default : new PlayerId(account.ServerId, account.Code);

    /// <summary>把 614 的负载整份装进内存表。返回(总条数, 带归属服的条数),交给调用方记日志。</summary>
    private static (int Total, int WithServer) IndexUserList(string payload)
    {
        var parsed = FriendCodec.ParseNetworkAccounts(payload, out var badLines);
        var fresh = new Dictionary<string, NetworkAccount>(StringComparer.OrdinalIgnoreCase);
        var withServer = 0;
        foreach (var one in parsed)
        {
            var id = PlayerIdOf(one);
            var key = id.IsEmpty ? "#" + one.Code : id.Full;
            if (one.ServerId.Length > 0)
            {
                withServer++;
            }

            fresh[key] = one;
        }

        // 先建新表再换引用:中途失败也不会留下半份数据。
        NetworkAccounts.Clear();
        foreach (var pair in fresh)
        {
            NetworkAccounts[pair.Key] = pair.Value;
        }

        if (badLines > 0)
        {
            Logger.Log(LogLevel.Warning, $"[用户列表] 有 {badLines} 行格式不对,已跳过");
        }

        return (parsed.Count, withServer);
    }

    /// <summary>清空全网账号表(名字 + 条数进日志);与官方端断开时调用。</summary>
    private static void ClearNetworkAccounts(string reason)
    {
        var count = NetworkAccounts.Count;
        NetworkAccounts.Clear();
        if (count > 0)
        {
            Logger.Log(LogLevel.Info, $"[用户列表] {reason},已清空全网账号表 {count} 条(重连后 614 会整份重发)");
        }
    }

    /// <summary>
    /// 按完整ID 找一条在线连接。
    /// 找不到返回 null —— 调用方要么落盘等下次登录,要么如实回 err,不许当成"发成功了"。
    /// </summary>
    private static TcpConnection? FindOnline(PlayerId target)
    {
        if (target.IsEmpty || _server is null)
        {
            return null;
        }

        foreach (var connection in _server.Connections)
        {
            var info = _server.Registry.Find(connection.Id);
            if (info is null || info.User.Length == 0)
            {
                continue;
            }

            var account = Accounts.Find(info.User);
            if (account is not null && account.Id == target.Code && SameServer(SelfServerId(_config!), target.ServerId))
            {
                return connection;
            }
        }

        return null;
    }

    /// <summary>给某个玩家投一个包;不在线返回 false(不报错 —— 由调用方决定是落盘还是回 err)。</summary>
    private static async Task<bool> SendToPlayerAsync(PlayerId target, ushort type, string payload)
    {
        var connection = FindOnline(target);
        if (connection is null)
        {
            return false;
        }

        await connection.SendAsync(type, payload, 0, "-");
        return true;
    }

    /// <summary>
    /// 某个玩家在不在线。<b>本服的看连接</b>,<b>别服的看 616 广播留下的在线表</b> ——
    /// 光看连接永远查不到别服的人,好友列表的在线位就会一直是「离线」。
    /// </summary>
    private static bool IsOnline(PlayerId target, ZFile config)
    {
        if (target.IsEmpty)
        {
            return false;
        }

        if (SameServer(target.ServerId, SelfServerId(config)))
        {
            return FindOnline(target) is not null;
        }

        // 这张表的值是「在不在」,键不在表里就是不知道 → 按离线算,不瞎猜在线
        return RemoteOnline.TryGetValue(target.Full, out var online) && online;
    }

    /// <summary>记下别服某人的上下线(616 来的);state 只认 online / offline,其它原样记日志忽略。</summary>
    private static void SetRemoteOnline(PlayerId who, string state)
    {
        if (who.IsEmpty)
        {
            return;
        }

        if (string.Equals(state, Protocol.FriendEventKind.Online, StringComparison.OrdinalIgnoreCase))
        {
            RemoteOnline[who.Full] = true;
            return;
        }

        if (string.Equals(state, Protocol.FriendEventKind.Offline, StringComparison.OrdinalIgnoreCase))
        {
            RemoteOnline.TryRemove(who.Full, out _);
            return;
        }

        Logger.Log(LogLevel.Warning, $"[好友] 收到不认识的在线状态 {state}({who.Full}),已忽略");
    }

    /// <summary>与官方端断开时清空别服在线表:那时广播已经不可靠,留着就是过期数据。</summary>
    private static void ClearRemoteOnline()
    {
        var count = RemoteOnline.Count;
        RemoteOnline.Clear();
        if (count > 0)
        {
            Logger.Log(LogLevel.Info, $"[好友] 与官方端断开,已清空别服在线表 {count} 条");
        }
    }

    /// <summary>把本服所有在线玩家的「上线」重新广播一遍(重连官方端、或本服刚启动时用)。</summary>
    private static async Task ReannounceLocalOnlineAsync(ZFile config)
    {
        foreach (var info in _server?.Registry.All ?? [])
        {
            if (info.User.Length > 0)
            {
                await AnnouncePresenceAsync(info.User, Protocol.FriendEventKind.Online);
            }
        }
    }

    /// <summary>
    /// 重连后补广播:再等 3 秒补一遍。两台子服先后重连时,先回来的那台会漏收另一台的补广播,
    /// 补第二遍就把它兜住了。
    /// </summary>
    private static async Task ReannounceAfterReconnectAsync(ZFile config)
    {
        // 官方端重启过的话它的名字表是空的,重连成功后立刻补报一次,别等玩家登录
        await ReportSelfNameIfNeededAsync(config);
        await ReannounceLocalOnlineAsync(config);
        await Task.Delay(3000);
        await ReannounceLocalOnlineAsync(config);
    }

    /// <summary>
    /// 某个服ID 的显示名。本服直接给配置里的名字;别服查 617/618 下来的表;
    /// 查不到返回空串,由界面显示「未知服务端」—— 不拿服ID 冒充名字。
    /// </summary>
    private static string ServerNameOf(string serverId, ZFile config)
    {
        if (serverId.Length == 0)
        {
            return "";
        }

        var self = SelfServerId(config);
        if (SameServer(serverId, self))
        {
            return config.MapGet(KeyName).Trim();
        }

        return ServerNames.TryGetValue(serverId, out var name) && name.Length > 0 ? name : "";
    }

    /// <summary>别服显示名表(618 下发,键 = 服ID);本服名不存这里,直接读配置。</summary>
    private static readonly ConcurrentDictionary<string, string> ServerNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>把 618 的负载(<c>self=本服ID</c> 第一行 + 每行 <c>服ID\t显示名</c>)装进内存表。</summary>
    private static int IndexServerNames(string payload)
    {
        var count = 0;
        foreach (var line in (payload ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("self=", StringComparison.OrdinalIgnoreCase))
            {
                SetSelfServerId(line[5..].Trim());
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 2 || parts[0].Trim().Length == 0)
            {
                continue;
            }

            ServerNames[parts[0].Trim()] = parts[1].Trim();
            count++;
        }

        return count;
    }

    /// <summary>
    /// 查一个账号名对应的等级。先看本服账号表,再看全网账号表(跨服镜像账号只有后者有)。
    /// 都不知道就给"不知道"占位符。
    /// </summary>
    private static string LevelOf(string name)
    {
        var local = Accounts.Find(name);
        if (local is not null)
        {
            return local.Level;
        }

        foreach (var one in NetworkAccounts.Values)
        {
            if (string.Equals(one.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return one.Level;
            }
        }

        return FriendCodec.Unknown;
    }

    /// <summary>把 614 的负载原样存一份到 <c>Data/&lt;本服ID&gt;/UserList.dat</c>,重启后还能拿它兜底。</summary>
    private static void SaveUserList(string payload, ZFile config)
    {
        var self = SelfServerId(config);
        var directory = Path.Combine("Data", self.Length > 0 ? self : "ZXMS-local");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "UserList.dat"), payload, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[用户列表] 存到 {directory} 失败:{ex.Message}");
        }
    }
}
