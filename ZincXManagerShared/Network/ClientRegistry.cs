using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace ZincXManagerShared.Network;

/// <summary>一条连接的登记信息(在线期间有效)。</summary>
public sealed class ClientInfo
{
    /// <summary>连接 id。</summary>
    public Guid Id { get; init; }

    /// <summary>身份:ZXMS / ZXMC / 未识别(收到握手包后补上)。</summary>
    public string Role { get; set; } = "未识别";

    /// <summary>连接模式:ZXMC 报上来的 OS / OOS / SAOS;其它端是 "-"。</summary>
    public string Mode { get; set; } = "-";

    /// <summary>登录后的账号名(没登录就是空)。</summary>
    public string User { get; set; } = "";

    /// <summary>权限等级(登录后由账号决定;没登录是 "-")。</summary>
    public string Level { get; set; } = "-";

    /// <summary>对端 IP。</summary>
    public string Ip { get; init; } = "?";

    /// <summary>对端端口。</summary>
    public int Port { get; init; }

    /// <summary>上线时间。</summary>
    public DateTime OnlineAt { get; init; } = DateTime.Now;

    /// <summary>收到的包数量。</summary>
    public int Packets { get; set; }

    /// <summary>已在线多久。</summary>
    public TimeSpan Online => DateTime.Now - OnlineAt;

    /// <summary>IP:端口。</summary>
    public string Address => $"{Ip}:{Port}";
}

/// <summary>
/// 客户端连接登记表(内存):谁用什么 IP、什么身份、什么模式连进来,登录了哪个账号,
/// 在线多久,收了多少包。
///
/// 由 <see cref="TcpServer"/> 自动登记和注销;身份 / 模式在收到握手包时用 <see cref="SetRole"/> 补上,
/// 账号与权限等级在登录成功后用 <see cref="SetAccount"/> 补上。
/// </summary>
public sealed class ClientRegistry
{
    private readonly ConcurrentDictionary<Guid, ClientInfo> _map = new();

    /// <summary>当前在线数量。</summary>
    public int Count => _map.Count;

    /// <summary>在线快照,按上线时间排序。</summary>
    public IReadOnlyCollection<ClientInfo> All
        => _map.Values.OrderBy(c => c.OnlineAt).ToArray();

    /// <summary>接入时登记(由 <see cref="TcpServer"/> 调用)。</summary>
    public void Add(TcpConnection connection)
    {
        var end = connection.RemoteEndPoint;
        _map[connection.Id] = new ClientInfo
        {
            Id = connection.Id,
            Ip = end?.Address.ToString() ?? "?",
            Port = end?.Port ?? 0,
        };
    }

    /// <summary>按连接 id 查;没有就返回 null。</summary>
    public ClientInfo? Find(Guid id) => _map.TryGetValue(id, out var info) ? info : null;

    /// <summary>握手后补身份和连接模式;没有这条记录就忽略。</summary>
    public void SetRole(Guid id, string role, string mode = "-")
    {
        if (_map.TryGetValue(id, out var info))
        {
            info.Role = role;
            info.Mode = mode;
        }
    }

    /// <summary>登录成功后补账号名与权限等级。</summary>
    public void SetAccount(Guid id, string user, string level)
    {
        if (_map.TryGetValue(id, out var info))
        {
            info.User = user;
            info.Level = level;
        }
    }

    /// <summary>收到一个包,计数 +1。</summary>
    public void Touch(Guid id)
    {
        if (_map.TryGetValue(id, out var info))
        {
            info.Packets++;
        }
    }

    /// <summary>断开时注销,返回被注销的那条(没有就返回 null)。</summary>
    public ClientInfo? Remove(Guid id) => _map.TryRemove(id, out var info) ? info : null;

    /// <summary>按身份统计在线数,例如 <c>CountOf("ZXMC")</c>。</summary>
    public int CountOf(string role) => _map.Values.Count(c => c.Role == role);

    /// <summary>按 IP 找第一个匹配的。</summary>
    public ClientInfo? FindByIp(string ip)
        => _map.Values.FirstOrDefault(c => c.Ip == ip);

    /// <summary>按账号名找在线连接。</summary>
    public ClientInfo? FindByUser(string user)
        => _map.Values.FirstOrDefault(c => string.Equals(c.User, user, StringComparison.OrdinalIgnoreCase));

    /// <summary>格式化成表格文本(ZXMS / ZXMOS 的 net clients 命令共用这一份)。</summary>
    public string ToTable()
    {
        if (Count == 0)
        {
            return "当前没有客户端接入";
        }

        var text = new StringBuilder("身份   账号         等级     模式   地址                 连接id    在线       收包\n");
        foreach (var c in All)
        {
            var user = c.User.Length > 0 ? c.User : "-";
            text.Append($"{c.Role,-6} {user,-12} {c.Level,-8} {c.Mode,-6} {c.Address,-20} {c.Id.ToString()[..8]}  {c.Online:hh\\:mm\\:ss}  {c.Packets,5}\n");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>带行前缀的表格(命令输出用,例如 <c>ToTable("[网络] ")</c>)。</summary>
    public string ToTable(string linePrefix)
        => string.Join('\n', ToTable().Split('\n').Select(line => linePrefix + line));
}
