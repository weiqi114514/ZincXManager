namespace ZincXManagerShared.Update;

/// <summary>
/// 版本号比较规则(客户端自动更新与测试共用一套,别两边各抄一份 —— 抄出来的副本会跟着错,测试照样全绿)。
/// <para>支持的写法:<c>b0.1.2a</c>、<c>b0.1.2</c>、<c>v1.2.3</c>、<c>a0.5.1</c>。</para>
/// </summary>
public static class VersionRules
{
    /// <summary>
    /// 比较两个版本号:先比系列前缀(a &lt; b,无前缀最旧),再逐段比数字,
    /// 最后比末段的字母修订号(无修订号 &lt; a &lt; b,所以 b0.1.2 &lt; b0.1.2a &lt; b0.1.2b)。
    /// </summary>
    /// <returns>小于 0 表示 a 旧,等于 0 表示相同,大于 0 表示 a 新。</returns>
    public static int Compare(string a, string b)
    {
        var (prefixA, numbersA, revisionA) = Parse(a);
        var (prefixB, numbersB, revisionB) = Parse(b);

        if (prefixA != prefixB)
        {
            return prefixA.CompareTo(prefixB);
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

        return revisionA.CompareTo(revisionB);
    }

    /// <summary>拆成「系列前缀 + 数字段 + 末段字母修订号」;解析不出的部分按 0 / 无处理。</summary>
    private static (char Prefix, int[] Numbers, char Revision) Parse(string version)
    {
        var text = version.Trim().TrimStart('v', 'V');

        var prefix = '\0';   // 系列前缀(a/b/c…),没有就是 0
        if (text.Length > 0 && char.IsLetter(text[0]))
        {
            prefix = char.ToLowerInvariant(text[0]);
            text = text[1..];
        }

        var parts = text.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);   // 数字段
        var numbers = new int[parts.Length];
        var revision = '\0';   // 末段数字后面的字母(b0.1.2a 的 a)

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();

            var digits = 0;   // 这段开头连续数字的个数
            while (digits < part.Length && char.IsDigit(part[digits]))
            {
                digits++;
            }

            numbers[i] = digits > 0 && int.TryParse(part[..digits], out var number) ? number : 0;

            // 只有末段的后缀才是版本修订号:"2a" 的 a 必须参与比较,
            // 否则 b0.1.2a 与 b0.1.2b 会被判成相等,发新版后客户端永远提示"已是最新"
            if (i == parts.Length - 1 && digits > 0 && digits < part.Length)
            {
                var tail = part[digits..].TrimStart('-', '_');
                if (tail.Length > 0 && char.IsLetter(tail[0]))
                {
                    revision = char.ToLowerInvariant(tail[0]);
                }
            }
        }

        return (prefix, numbers, revision);
    }
}
