using System.Net;  // IP 地址与网络端点

namespace ZincXManagerShared.Network;

/// <summary>
/// 地址文本的判断与归一化 —— 三端共用一份,免得各写各的规则。
///
/// <para>为什么要有它:官方服务端对外是<b>域名</b>(<c>zxm.zincms.top</c>),子服/客户端填地址时
/// 只允许 IP 会把最常见的情况挡在外面。所以"能不能当主机用"统一走这里:IP 或合法主机名都行。</para>
/// </summary>
public static class HostText
{
    /// <summary>
    /// 能不能当"主机"用:IPv4 / IPv6,或者合法的 主机名 / 域名。
    ///
    /// <para>主机名规则(宽松但够用):只允许字母、数字、<c>-</c>、<c>_</c>、<c>.</c>;
    /// 每段 1-63 字符;总长 ≤ 253;首尾不能是 <c>.</c> 或 <c>-</c>;不允许空白。</para>
    /// </summary>
    public static bool IsHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var s = value.Trim();

        // IP(含 IPv6)直接放行 —— 注意 IPv6 带方括号的写法也能过(上面的括号判断会拦,先剥掉)
        var bare = s.StartsWith('[') && s.EndsWith(']') ? s[1..^1] : s;
        if (IPAddress.TryParse(bare, out _))
        {
            return true;
        }

        if (s.Length > 253)
        {
            return false;
        }

        if (s.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (s.StartsWith('.') || s.EndsWith('.') || s.StartsWith('-') || s.EndsWith('-'))
        {
            return false;
        }

        foreach (var label in s.Split('.'))
        {
            if (label.Length is 0 or > 63)
            {
                return false;
            }

            foreach (var c in label)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>去掉首尾空白(域名不区分大小写,但没必要强行改写大小写:显示给用户看时保留原样更亲切)。</summary>
    public static string Normalize(string? value) => (value ?? string.Empty).Trim();
}
