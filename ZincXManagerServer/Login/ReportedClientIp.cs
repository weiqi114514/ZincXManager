using System;  // 基础类型 / 常用静态方法
using System.Collections.Concurrent;  // 并发集合(多线程共享)
using System.Net;  // IPAddress
using System.Net.Sockets;  // AddressFamily

namespace ZincXManagerServer.Login;

/// <summary>
/// ZXMC 上报的<b>局域网</b> IPv4(协议 632)的内存缓存,键是账号名。
///
/// <para><b>它只用于展示与日志。</b>白名单、封禁、持久登录表、限速这些安全判断一律仍用连接 IP ——
/// 连接 IP 是 TCP 层的既成事实,上报值是客户端自己说的话,不能当凭据。</para>
///
/// <para>为什么按账号名而不是连接 id:同一台 ZXMC 会重连(断线重连、切模式),连接 id 每次都变,
/// 而"这台机器在哪个局域网"跟着账号走。<see cref="Program.SendToAccountAsync"/> 找连接时用的也是账号名。</para>
/// </summary>
internal static class ReportedClientIp
{
    /// <summary>上报值有效期(分钟):超过就当作没有,下次进服时再向客户端要一次。</summary>
    public const int TtlMinutes = 10;

    // 账号名 → (局域网 IPv4, 收到时间)。账号名比较不区分大小写,与连接登记表口径一致。
    private static readonly ConcurrentDictionary<string, (string Ip, DateTime At)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 记下一个账号上报的局域网地址(非法地址直接丢掉并返回 false)。
    /// </summary>
    /// <param name="account">账号名(空 = 这条连接还没登录,丢弃)。</param>
    /// <param name="ip">客户端报上来的地址文本。</param>
    /// <param name="saved">通过校验、真正存下来的地址;失败时是空串。</param>
    public static bool Remember(string account, string ip, out string saved)
    {
        saved = "";
        var name = account.Trim();
        var value = ip.Trim();
        if (name.Length == 0 || !IsUsable(value))
        {
            return false;
        }

        Cache[name] = (value, DateTime.Now);
        saved = value;
        return true;
    }

    /// <summary>取这个账号的局域网地址;没有或已过期返回空串(过期的顺手清掉)。</summary>
    public static string Get(string account)
    {
        var name = account.Trim();
        if (name.Length == 0 || !Cache.TryGetValue(name, out var entry))
        {
            return "";
        }

        if (DateTime.Now - entry.At > TimeSpan.FromMinutes(TtlMinutes))
        {
            Cache.TryRemove(name, out _);
            return "";
        }

        return entry.Ip;
    }

    /// <summary>这个地址是不是能当"局域网 IPv4"用的(回环 / 链路本地 / 非法文本都不算)。</summary>
    private static bool IsUsable(string value)
    {
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        // 127.* 是回环,169.254.* 是网卡没拿到 DHCP 时的自编地址:两者都不是"这台机器在局域网里的地址"
        return !IPAddress.IsLoopback(address) && !value.StartsWith("169.254.", StringComparison.Ordinal);
    }
}
