using System.Security.Cryptography;  // Rfc2898DeriveBytes / RandomNumberGenerator / CryptographicOperations
using System.Text;  // Encoding:把待哈希的文本转成字节

namespace ZincXManagerShared.Account;

/// <summary>
/// 密码密文。
///
/// <para>客户端算的仍然是 <c>SHA-256(账号名小写 + ":" + 密码)</c> —— 那是协议的一部分,
/// 客户端、模组、官方端都按它对接,不能改,否则所有人都得改密码。</para>
///
/// <para>服务端存的不再是这个裸哈希,而是 <c>PBKDF2-SHA256(裸哈希, 随机盐, 10 万轮)</c>:
/// 裸哈希一次 SHA-256 就算完,拿到存档文件离线爆破一个账号几乎零成本;
/// 加了随机盐和迭代之后,每个候选口令都要重算 10 万轮,也没法预先建彩虹表。</para>
///
/// <para>盐是随机的,所以同样的密码在不同账号下密文不同。存的是
/// <c>pbkdf2-sha256$轮数$盐$摘要</c> —— 轮数写在里面,以后调参不用猜老存档是多少轮。</para>
/// </summary>
public static class PasswordHash
{
    /// <summary>迭代轮数:登录一次几十毫秒,对玩家无感;对爆破方是实打实的成本。</summary>
    public const int Iterations = 100_000;

    /// <summary>盐长度(字节)。</summary>
    private const int SaltBytes = 16;

    /// <summary>PBKDF2 输出的摘要长度(字节)。</summary>
    private const int DigestBytes = 32;

    /// <summary>存档前缀,用来区分「新格式」和「老的裸十六进制」。</summary>
    private const string Prefix = "pbkdf2-sha256";

    /// <summary>算客户端要发出去的裸哈希。名称与密码都区分大小写,只有名称按小写归一。</summary>
    /// <param name="userName">账号名。</param>
    /// <param name="password">明文密码(只存在于客户端内存)。</param>
    public static string Hash(string userName, string password)
    {
        var text = userName.Trim().ToLowerInvariant() + ":" + password;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>
    /// 把客户端发来的裸哈希变成要落盘的密文:随机盐 + PBKDF2。
    /// 已经是新格式就原样返回,不会越套越深。
    /// </summary>
    /// <param name="legacyHash">客户端算出来的 64 位十六进制裸哈希。</param>
    public static string Protect(string legacyHash)
    {
        if (IsProtected(legacyHash))
        {
            return legacyHash;
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var digest = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(legacyHash.ToLowerInvariant()),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            DigestBytes);

        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(digest)}";
    }

    /// <summary>
    /// 校验一次登录:客户端发来裸哈希,跟存着的密文比。
    /// 老存档(裸十六进制)也认,由调用方顺手升级成新格式。
    /// </summary>
    /// <param name="stored">存档里存的值(新格式密文,或老的裸哈希)。</param>
    /// <param name="legacyHash">客户端这次发来的 64 位十六进制裸哈希。</param>
    public static bool Verify(string stored, string legacyHash)
    {
        if (stored.Length == 0 || legacyHash.Length == 0)
        {
            return false;
        }

        // 老存档:本身就是一个裸哈希,按原样比
        if (!IsProtected(stored))
        {
            return string.Equals(stored, legacyHash, StringComparison.OrdinalIgnoreCase);
        }

        var parts = stored.Split('$');   //拆分结果
        if (parts.Length != 4 || !int.TryParse(parts[1], out var iterations) || iterations <= 0)
        {
            return false;
        }

        byte[] salt;   //存档里的盐
        byte[] expected;   //存档里的摘要
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(legacyHash.ToLowerInvariant()),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>是不是新格式(带随机盐和迭代)的密文。</summary>
    /// <param name="value">存档里的值。</param>
    public static bool IsProtected(string value) => value.StartsWith(Prefix + "$", StringComparison.Ordinal);

    /// <summary>
    /// 这个值能不能当密文存:新格式,或者老格式的 64 位十六进制裸哈希。
    /// 参数校验、控制台建号、改名都走它。
    /// </summary>
    /// <param name="value">待检查的值。</param>
    public static bool IsHash(string value)
        => IsProtected(value) || (value.Length == 64 && value.All(Uri.IsHexDigit));
}
