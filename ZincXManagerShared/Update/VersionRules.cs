namespace ZincXManagerShared.Update;

/// <summary>
/// 一次版本更新跨了几级,从低到高 —— 级别名称对应版本号命名规则的各段。
/// </summary>
public enum UpdateLevel
{
    /// <summary>没有更新(远端不比当前新)。</summary>
    None = 0,

    /// <summary>极小更新 / 修复更新:末段字母变化,如 b0.1.3 → b0.1.3a。</summary>
    Fix = 1,

    /// <summary>小版本更新:第三段数字变化,如 b0.1.3 → b0.1.4。</summary>
    Patch = 2,

    /// <summary>中版本更新:第二段数字变化,如 b0.1.3 → b0.2.0。</summary>
    Minor = 3,

    /// <summary>大版本更新:第一段数字变化,如 b0.1.3 → b1.0.0。</summary>
    Major = 4,

    /// <summary>版本类型变化:首字母变化,如 a0.9.9 → b0.1.0。</summary>
    Type = 5,
}

/// <summary>
/// 版本号规则:<c>[版本类型][大版本][中版本][小版本][极小更新/修复更新]</c>,
/// 例如 <c>b0.1.3</c>、<c>b0.1.2a</c>、<c>a0.5.1</c>。
/// <para>
/// 比较顺序固定为:类型 → 大版本 → 中版本 → 小版本 → 极小更新,前一段分出大小就不再往后比。
/// 客户端自动更新、服务端版本查询与回归测试共用这一份实现(各抄一份会把同一个错误复制到所有地方)。
/// </para>
/// </summary>
public static class VersionRules
{
    /// <summary>版本号拆开后的五段;缺失的段按「无类型 / 0 / 无修复号」处理。</summary>
    /// <param name="Type">版本类型(首字母,无类型为 '\0')。</param>
    /// <param name="Major">大版本。</param>
    /// <param name="Minor">中版本。</param>
    /// <param name="Patch">小版本。</param>
    /// <param name="Fix">极小更新 / 修复更新(末段字母,无为 '\0')。</param>
    public readonly record struct Parts(char Type, int Major, int Minor, int Patch, char Fix);

    /// <summary>
    /// 比较两个版本号:先比版本类型(无类型 &lt; a &lt; b &lt; c),
    /// 再比大 / 中 / 小版本数字,最后比极小更新号(无 &lt; a &lt; b,所以 b0.1.2 &lt; b0.1.2a &lt; b0.1.2b)。
    /// </summary>
    /// <returns>小于 0 表示 a 旧,等于 0 表示相同,大于 0 表示 a 新。</returns>
    public static int Compare(string a, string b)
    {
        var (typeA, numbersA, fixA) = Split(a);
        var (typeB, numbersB, fixB) = Split(b);

        if (typeA != typeB)
        {
            return typeA.CompareTo(typeB);
        }

        var length = Math.Max(numbersA.Length, numbersB.Length);
        for (var i = 0; i < length; i++)
        {
            var x = i < numbersA.Length ? numbersA[i] : 0;
            var y = i < numbersB.Length ? numbersB[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return fixA.CompareTo(fixB);
    }

    /// <summary>按命名规则拆成五段,给需要单独读某一段的地方用(如更新提示里说明跨了几级)。</summary>
    public static Parts Parse(string version)
    {
        var (type, numbers, fix) = Split(version);
        return new Parts(
            type,
            numbers.Length > 0 ? numbers[0] : 0,
            numbers.Length > 1 ? numbers[1] : 0,
            numbers.Length > 2 ? numbers[2] : 0,
            fix);
    }

    /// <summary>
    /// 判断从 <paramref name="current"/> 更新到 <paramref name="latest"/> 跨了哪几级;
    /// 远端不比当前新时返回 <see cref="UpdateLevel.None"/>。
    /// </summary>
    public static UpdateLevel Level(string current, string latest)
    {
        if (Compare(latest, current) <= 0)
        {
            return UpdateLevel.None;
        }

        var left = Parse(current);
        var right = Parse(latest);

        if (left.Type != right.Type)
        {
            return UpdateLevel.Type;
        }

        if (left.Major != right.Major)
        {
            return UpdateLevel.Major;
        }

        if (left.Minor != right.Minor)
        {
            return UpdateLevel.Minor;
        }

        if (left.Patch != right.Patch)
        {
            return UpdateLevel.Patch;
        }

        return UpdateLevel.Fix;
    }

    /// <summary>把更新级别翻成给日志/界面看的中文。</summary>
    public static string LevelName(UpdateLevel level) => level switch
    {
        UpdateLevel.Fix => "极小更新",
        UpdateLevel.Patch => "小版本更新",
        UpdateLevel.Minor => "中版本更新",
        UpdateLevel.Major => "大版本更新",
        UpdateLevel.Type => "版本类型变更",
        _ => "无更新",
    };

    /// <summary>拆成「版本类型 + 数字段 + 末段字母修复号」;解析不出的部分按 0 / 无处理。</summary>
    private static (char Type, int[] Numbers, char Fix) Split(string version)
    {
        var text = version.Trim().TrimStart('v', 'V');

        var type = '\0';   // 版本类型(首字母),没有就是最旧
        if (text.Length > 0 && char.IsLetter(text[0]))
        {
            type = char.ToLowerInvariant(text[0]);
            text = text[1..];
        }

        var parts = text.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);   // 数字段:大 / 中 / 小(第四段起按极小更新的数字写法参与比较)
        var numbers = new int[parts.Length];
        var fix = '\0';   // 末段数字后面的字母(b0.1.2a 的 a)

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();

            var digits = 0;   // 这段开头连续数字的个数
            while (digits < part.Length && char.IsDigit(part[digits]))
            {
                digits++;
            }

            numbers[i] = digits > 0 && int.TryParse(part[..digits], out var number) ? number : 0;

            // 只有末段的后缀才是修复号:"2a" 的 a 必须参与比较,
            // 否则 b0.1.2a 与 b0.1.2b 会被判成相等,发新版后客户端永远提示"已是最新"
            if (i == parts.Length - 1 && digits > 0 && digits < part.Length)
            {
                var tail = part[digits..].TrimStart('-', '_');
                if (tail.Length > 0 && char.IsLetter(tail[0]))
                {
                    fix = char.ToLowerInvariant(tail[0]);
                }
            }
        }

        return (type, numbers, fix);
    }
}
