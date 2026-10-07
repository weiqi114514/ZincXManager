using System.Security.Cryptography;
using System.Text;

namespace ZincXManagerShared.Account;

/// <summary>
/// 密码密文:SHA-256(账号名小写 + ":" + 密码),十六进制小写。
///
/// <para>两端用同一份实现:</para>
/// <code>
/// 客户端:明文不出本机,发出去的是密文(PasswordHash.Hash(名称, 密码))
/// 服务端:只存密文、只比密文(users.ini 里没有明文)
/// </code>
///
/// <para>用账号名当盐:同样的密码在不同账号下密文不同,又不用多一次"取盐"的往返。</para>
/// </summary>
public static class PasswordHash
{
    /// <summary>算密文。名称与密码都区分大小写,只有名称按小写归一。</summary>
    public static string Hash(string userName, string password)
    {
        var text = userName.Trim().ToLowerInvariant() + ":" + password;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>看起来像不像本实现产出的密文(64 位十六进制)。</summary>
    public static bool IsHash(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);
}
