using System;  // 基础类型
using System.Globalization;  // 数字解析

namespace ZincXManagerShared.Account;

/// <summary>
/// 玩家的完整 ID:<c>&lt;服务端ID&gt;-&lt;编码&gt;</c>。
/// <para>服务端 ID 自带类型前缀:官方端是 <c>ZXMOS</c>,子服是 <c>ZXMS-&lt;osuser&gt;-&lt;osid&gt;</c>。</para>
/// <para>编码是账户在所属服务端内部的数字 ID,各服务端独立 —— 同样是 1,子服的 1 和官方的 1 是两个人。</para>
/// </summary>
public readonly record struct PlayerId
{
    /// <summary>服务端 ID(官方端 <c>ZXMOS</c>,子服 <c>ZXMS-短名-编号</c>)。</summary>
    public string ServerId { get; }

    /// <summary>账户在所属服务端内部的编码。</summary>
    public int Code { get; }

    /// <summary>用服务端 ID 与编码拼一个完整玩家 ID。</summary>
    public PlayerId(string serverId, int code)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new ArgumentException("服务端 ID 不能为空", nameof(serverId));
        }

        if (code <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(code), "编码必须大于 0");
        }

        ServerId = serverId.Trim();
        Code = code;
    }

    /// <summary>完整 ID 文本,如 <c>ZXMS-ZES-1-14</c> 或 <c>ZXMOS-5</c>。</summary>
    public string Full => $"{ServerId}-{Code.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// 解析完整 ID。规则:最后一个 <c>-</c> 后面必须是纯数字,前面必须是 <c>ZXMOS</c> 或以 <c>ZXMS-</c> 开头。
    /// </summary>
    public static bool TryParse(string? text, out PlayerId id)
    {
        id = default;
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return false;
        }

        var dash = value.LastIndexOf('-');
        if (dash <= 0 || dash == value.Length - 1)
        {
            return false;
        }

        var head = value[..dash];
        var tail = value[(dash + 1)..];
        if (!IsServerId(head) ||
            !int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var code) ||
            code <= 0)
        {
            return false;
        }

        id = new PlayerId(head, code);
        return true;
    }

    /// <summary>解析失败直接抛,给"外面一定传对了"的调用点用。</summary>
    public static PlayerId Parse(string? text)
        => TryParse(text, out var id)
            ? id
            : throw new FormatException($"不是合法的玩家 ID:{text}");

    /// <summary>服务端 ID 是否合规:官方端固定 <c>ZXMOS</c>,子服必须是 <c>ZXMS-短名-编号</c>。</summary>
    public static bool IsServerId(string? serverId)
    {
        var value = (serverId ?? string.Empty).Trim();
        if (value.Equals(OfficialServerId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!value.StartsWith(ChildPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 子服形如 ZXMS-ZES-1:再切两段,短名非空、编号是正整数
        var rest = value[ChildPrefix.Length..];
        var dash = rest.LastIndexOf('-');
        return dash > 0 &&
               dash < rest.Length - 1 &&
               int.TryParse(rest[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var osid) &&
               osid > 0;
    }

    /// <summary>官方端服务端 ID。</summary>
    public const string OfficialServerId = "ZXMOS";

    /// <summary>子服服务端 ID 前缀。</summary>
    public const string ChildPrefix = "ZXMS-";

    /// <summary>子服完整服务端 ID:<c>ZXMS-&lt;osuser&gt;-&lt;osid&gt;</c>。</summary>
    public static string ChildServerId(string osuser, int osid)
        => $"{ChildPrefix}{osuser}-{osid.ToString(CultureInfo.InvariantCulture)}";

    /// <inheritdoc/>
    public override string ToString() => Full;
}
