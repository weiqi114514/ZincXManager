namespace ZincXManagerShared.Friend;

/// <summary>
/// 一个玩家的全网唯一地址 = <c>归属服ID-本地账号编号</c>,例如 <c>ZXMS-testserver-2-13</c>。
///
/// <para>为什么需要它:各子服的账号编号是各自从 1 开始排的(见 <c>AccountStore</c>),
/// 所以「13 号」在 A 服和 B 服是两个人。跨服传人必须带服ID,否则必然张冠李戴。</para>
///
/// <para>官方端自己不挂任何子服,它名下的玩家写成 <c>ZXMOS-编号</c>。</para>
/// </summary>
public readonly record struct PlayerId(string ServerId, int Code)
{
    /// <summary>官方端的服ID。</summary>
    public const string OfficialServerId = "ZXMOS";

    /// <summary>子服服ID的前缀。</summary>
    public const string ChildPrefix = "ZXMS-";

    /// <summary>解析出来的完整写法。</summary>
    public string Full => Code > 0 ? $"{ServerId}-{Code}" : "";

    /// <summary>是不是一个空地址(没解析出来 / 编号无效)。</summary>
    public bool IsEmpty => Code <= 0;

    /// <summary>不带头部服ID、只有编号时(<c>"-13"</c>)能解析成 <c>ServerId=""</c> 的形式,用于「只知道编号」的场合。</summary>
    public override string ToString() => Full;

    /// <summary>
    /// 解析 <c>服ID-编号</c>。要求:最后一个 <c>-</c> 之后全是数字(且 &gt; 0),
    /// 前面的服ID要么是 <c>ZXMOS</c>,要么以 <c>ZXMS-</c> 开头。
    ///
    /// <para>严格是故意的:宁可解析失败让对方走「按名字找」或「原样回显」,
    /// 也不要把 <c>testplayer</c> 这种名字猜成一个地址。</para>
    /// </summary>
    public static bool TryParse(string? text, out PlayerId id)
    {
        id = default;
        var raw = (text ?? "").Trim();
        if (raw.Length == 0)
        {
            return false;
        }

        var dash = raw.LastIndexOf('-');
        if (dash <= 0 || dash == raw.Length - 1)
        {
            return false;
        }

        var server = raw[..dash];
        var tail = raw[(dash + 1)..];
        if (!int.TryParse(tail, out var code) || code <= 0)
        {
            return false;
        }

        if (!IsServerId(server))
        {
            return false;
        }

        id = new PlayerId(server, code);
        return true;
    }

    /// <summary>服ID是否合法(<c>ZXMOS</c> 或 <c>ZXMS-名称-编号</c>,前缀与官方端名不区分大小写)。</summary>
    public static bool IsServerId(string? text)
    {
        var raw = (text ?? "").Trim();
        if (raw.Equals(OfficialServerId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!raw.StartsWith(ChildPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // ZXMS-<登录名>-<官方端给的编号>;登录名里可能带横线,所以只看最后一段是不是数字。
        var dash = raw.LastIndexOf('-');
        return dash > ChildPrefix.Length - 1 && int.TryParse(raw[(dash + 1)..], out _);
    }

    /// <summary>
    /// 由「子服登录名 + 官方端分配的编号」拼出服ID。
    /// 与官方端 ZXMOS 侧的拼法必须逐字节一致,否则跨服找不到人。
    /// </summary>
    public static string ChildServerId(string loginName, int osId) => $"{ChildPrefix}{loginName}-{osId}";
}
