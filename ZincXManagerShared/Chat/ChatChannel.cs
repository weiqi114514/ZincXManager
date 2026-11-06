using System;   // Math:取大小号

namespace ZincXManagerShared.Chat;

/// <summary>
/// 好友私聊的频道号:两个人一对,按账号编号从小到大拼,谁先谁后算出来是同一个。
/// </summary>
public static class ChatChannel
{
    /// <summary>频道前缀;后面接 <c>小号-大号</c>。</summary>
    public const string Prefix = "dm-";

    /// <summary>两个账号之间的私聊频道号,如 3 和 11 → <c>dm-3-11</c>。</summary>
    /// <param name="first">一方账号编号。</param>
    /// <param name="second">另一方账号编号。</param>
    public static string ForPair(int first, int second)
    {
        var low = Math.Min(first, second);
        var high = Math.Max(first, second);
        return $"{Prefix}{low}-{high}";
    }
}
