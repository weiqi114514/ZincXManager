using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ZincXManagerShared.Account;

/// <summary>
/// 登录挑战-响应:客户端每次登录生成一个 nonce,证明 = SHA256(密码密文 : nonce)。
///
/// <para>服务端拿存着的密码密文重算一遍比对:即使抓到一次证明,换一个 nonce 就用不了,
/// 静态密文也不再直接出现在登录请求里(纵深防御,配合 SessionCrypto 的整包加密)。</para>
/// </summary>
public static class LoginProof
{
    /// <summary>nonce 允许的时间偏差(秒)。</summary>
    private const long Window = 300;

    private static readonly ConcurrentDictionary<string, long> Used = new();

    /// <summary>生成 nonce:unix 秒 + 16 字节随机(形如 1760000000-9f3c...)。</summary>
    public static string NewNonce()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return ts.ToString() + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    /// <summary>算证明。</summary>
    public static string Compute(string passwordHash, string nonce)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash.ToLowerInvariant() + ":" + nonce))).ToLowerInvariant();

    /// <summary>服务端校验证明(定长比较,避免时序泄漏)。</summary>
    public static bool Verify(string passwordHash, string nonce, string proof)
    {
        if (string.IsNullOrEmpty(passwordHash) || string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(proof))
        {
            return false;
        }

        var expected = Compute(passwordHash, nonce);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(proof.ToLowerInvariant()));
    }

    /// <summary>nonce 的时间戳是否在允许窗口内。</summary>
    public static bool IsFresh(string nonce)
    {
        var dash = nonce.IndexOf('-');
        if (dash <= 0 || !long.TryParse(nonce[..dash], out var ts))
        {
            return false;
        }

        return Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) <= Window;
    }

    /// <summary>登记 nonce;同一个 nonce 第二次出现就当作重放(返回 false)。</summary>
    public static bool TryUse(string nonce)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 顺手清掉过期的,免得缓存无限长
        foreach (var pair in Used)
        {
            if (now - pair.Value > Window * 2)
            {
                Used.TryRemove(pair.Key, out _);
            }
        }

        return Used.TryAdd(nonce, now);
    }
}
