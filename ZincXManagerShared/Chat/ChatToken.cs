using System;   // Convert / DateTimeOffset
using System.Globalization;   // 数字解析
using System.Security.Cryptography;   // HMACSHA256
using System.Text;   // 编码

namespace ZincXManagerShared.Chat;   // 聊天后端的账号令牌:子服签发,聊天宿主验签

/// <summary>
/// 聊天令牌:子服把「用户编号 + 显示名 + 过期时刻」签名后交给客户端,聊天宿主只验签不查账号表。
/// 这样聊天宿主不碰子服账号目录,也就不越权。
/// </summary>
public static class ChatToken
{
    /// <summary>签名算法:HMAC-SHA256。</summary>
    private const string Algorithm = "HMAC-SHA256";   // 只在文档里写明,代码用固定实现

    /// <summary>令牌最长有效期(秒),超过视为畸形。</summary>
    public const long MaxLifetimeSeconds = 7 * 24 * 3600;

    /// <summary>
    /// 签发令牌。
    /// </summary>
    /// <param name="secret">与聊天宿主共享的密钥,至少 16 字符。</param>
    /// <param name="userId">玩家编号(账号编号的十进制字符串)。</param>
    /// <param name="displayName">显示名,进消息体。</param>
    /// <param name="lifetimeSeconds">有效期秒数,会被 <see cref="MaxLifetimeSeconds"/> 截断。</param>
    /// <returns>形如 <c>payload.signature</c> 的 URL 安全令牌。</returns>
    public static string Sign(string secret, string userId, string displayName, long lifetimeSeconds)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        if (secret.Length < 16)
        {
            throw new ArgumentException("签名密钥至少 16 个字符", nameof(secret));
        }
        if (displayName.IndexOf('\n') >= 0)
        {
            throw new ArgumentException("显示名不能含换行", nameof(displayName));
        }

        var seconds = Math.Clamp(lifetimeSeconds, 60, MaxLifetimeSeconds);
        var expiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + seconds;
        var payload = $"{userId}\n{displayName}\n{expiry.ToString(CultureInfo.InvariantCulture)}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payloadBytes);
        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}";
    }

    /// <summary>
    /// 验签并取回身份。
    /// </summary>
    /// <param name="secret">与签发端一致的密钥。</param>
    /// <param name="token">客户端带来的令牌。</param>
    /// <param name="userId">验签通过时的玩家编号。</param>
    /// <param name="displayName">验签通过时的显示名。</param>
    /// <returns>验签通过返回 true;格式错、签名错、过期都返回 false。</returns>
    public static bool TryVerify(string secret, string token, out string userId, out string displayName)
    {
        userId = "";
        displayName = "";
        if (secret.Length < 16 || token.Length == 0)
        {
            return false;
        }
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1)
        {
            return false;
        }

        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = Base64UrlDecode(token[..dot]);
            signature = Base64UrlDecode(token[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(payloadBytes).Split('\n');
        if (parts.Length != 3 || parts[0].Length == 0)
        {
            return false;
        }
        if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiry))
        {
            return false;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (expiry <= now || expiry > now + MaxLifetimeSeconds)
        {
            return false;
        }

        userId = parts[0];
        displayName = parts[1].Length > 0 ? parts[1] : parts[0];
        return true;
    }

    /// <summary>
    /// 从令牌里取出被签进去的账号编号,<b>不验签</b>。
    /// <para>只给「本来就是这张令牌主人」的客户端用来认自己是谁 —— 它不是身份证明,谁都能伪造一份,
    /// 聊天宿主那一侧仍必须走 <see cref="TryVerify"/>。</para>
    /// </summary>
    /// <param name="token">形如 <c>payload.signature</c> 的令牌。</param>
    /// <param name="userId">解析出来的账号编号。</param>
    /// <returns>解析成功返回 true;格式不对返回 false。</returns>
    public static bool TryReadUserId(string token, out string userId)
    {
        userId = "";
        var dot = token.IndexOf('.');
        if (dot <= 0)
        {
            return false;
        }

        byte[] payloadBytes;
        try
        {
            payloadBytes = Base64UrlDecode(token[..dot]);
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(payloadBytes).Split('\n');
        if (parts.Length != 3 || parts[0].Length == 0)
        {
            return false;
        }

        userId = parts[0];
        return true;
    }

    /// <summary>URL 安全 base64 编码(去掉填充)。</summary>
    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>URL 安全 base64 解码(补回填充)。</summary>
    private static byte[] Base64UrlDecode(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        var padding = base64.Length % 4;
        if (padding > 0)
        {
            base64 += new string('=', 4 - padding);
        }
        return Convert.FromBase64String(base64);
    }
}
