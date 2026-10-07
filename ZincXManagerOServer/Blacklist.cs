using System.Globalization;
using System.Text;
using System.Text.Json;
using ZincXManagerShared.Account;
using ZincXManagerShared.Logging;
using ZincXManagerShared.Network;

namespace ZincXManagerOServer;

/// <summary>
/// 黑名单归档:被拉黑(<c>srv ban</c>)的 ZXMS 名下的玩家账号不删除,连同拉黑信息一起挪到
/// <c>blacklist/zxms/&lt;名称&gt;/</c>:
/// <list type="bullet">
///   <item><c>accounts.json</c>:它名下(经它中转注册)的玩家账号,字段和账号表一致,解封时能原样读回来</item>
///   <item><c>info.json</c>:拉黑时间 / 到期时间 / 原因 / 来源 IP / 账号数</item>
/// </list>
/// </summary>
internal static class Blacklist
{
    /// <summary>归档根目录(相对进程工作目录,和 bans.json / osusers.json 一致)。</summary>
    public const string Root = "blacklist";

    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>某个 ZXMS 的归档目录。</summary>
    public static string DirOf(string zxms) => Path.Combine(Root, "zxms", Safe(zxms));

    /// <summary>把账号归档进 <c>blacklist/zxms/&lt;名称&gt;/</c>,并写入拉黑信息;返回归档的账号数。</summary>
    public static int Archive(string zxms, IReadOnlyCollection<Account> accounts, string ip, string reason, DateTime? expireAt)
    {
        try
        {
            var dir = DirOf(zxms);
            Directory.CreateDirectory(dir);

            File.WriteAllText(Path.Combine(dir, "accounts.json"), AccountsJson(accounts), Utf8);
            File.WriteAllText(Path.Combine(dir, "info.json"), InfoJson(zxms, ip, reason, expireAt, accounts.Count), Utf8);

            Logger.Log(LogLevel.Info, $"[黑名单] {zxms} 名下 {accounts.Count} 个账号已归档 → {Path.GetFullPath(dir)}");
            return accounts.Count;
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[黑名单] 归档失败: " + ex.Message);
            return 0;
        }
    }

    /// <summary>读归档里的账号(没有归档或读失败就是空表)。</summary>
    public static List<Account> LoadAccounts(string zxms)
    {
        var list = new List<Account>();

        try
        {
            var file = Path.Combine(DirOf(zxms), "accounts.json");
            if (!File.Exists(file))
            {
                return list;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var level = Text(item, "level");

                list.Add(new Account
                {
                    Name = Text(item, "name"),
                    Password = Text(item, "password"),
                    Level = level.Length > 0 ? level : Protocol.RegisterLevel,
                    Id = item.TryGetProperty("id", out var id) && id.TryGetInt32(out var n) ? n : 0,
                    Ip = Text(item, "ip"),
                    LastIp = Text(item, "lastIp"),
                    Machine = Text(item, "machine"),
                    Owner = Text(item, "owner"),
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, $"[黑名单] 读归档失败({zxms}): {ex.Message}");
        }

        return list;
    }

    /// <summary>归档过的 ZXMS 名称(按目录名排序)。</summary>
    public static List<string> Names()
    {
        var root = Path.Combine(Root, "zxms");
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>一条归档摘要(给 srv blacklist 显示)。</summary>
    public static string Describe(string zxms)
    {
        var file = Path.Combine(DirOf(zxms), "info.json");
        if (!File.Exists(file))
        {
            return $"{zxms,-16} 账号 0    (缺 info.json)";
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
            var root = doc.RootElement;

            var ip = Text(root, "ip");
            var bannedAt = Text(root, "bannedAt");
            var expireAt = Text(root, "expireAt");
            var reason = Text(root, "reason");
            var count = root.TryGetProperty("accounts", out var c) && c.TryGetInt32(out var n) ? n : 0;

            return $"{zxms,-16} 账号 {count,-3} IP {(ip.Length > 0 ? ip : "-"),-15} 拉黑 {bannedAt} 到期 {(expireAt.Length > 0 ? expireAt : "永久")} 原因:{reason}";
        }
        catch (Exception ex)
        {
            return $"{zxms,-16} info.json 读失败:{ex.Message}";
        }
    }

    /// <summary>账号表 → JSON(手写,不依赖反射,AOT 安全)。</summary>
    private static string AccountsJson(IReadOnlyCollection<Account> accounts)
    {
        var json = new StringBuilder("[\n");
        var index = 0;

        foreach (var a in accounts)
        {
            json.Append("  {\"name\":\"").Append(Escape(a.Name))
                .Append("\",\"password\":\"").Append(Escape(a.Password))
                .Append("\",\"level\":\"").Append(Escape(a.Level))
                .Append("\",\"id\":").Append(a.Id)
                .Append(",\"ip\":\"").Append(Escape(a.Ip))
                .Append("\",\"lastIp\":\"").Append(Escape(a.LastIp))
                .Append("\",\"machine\":\"").Append(Escape(a.Machine))
                .Append("\",\"owner\":\"").Append(Escape(a.Owner))
                .Append("\"}").Append(++index == accounts.Count ? "\n" : ",\n");
        }

        json.Append(']');
        return json.ToString();
    }

    /// <summary>拉黑信息 → JSON。</summary>
    private static string InfoJson(string zxms, string ip, string reason, DateTime? expireAt, int count)
    {
        var expire = expireAt is null
            ? "null"
            : "\"" + expireAt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\"";

        return new StringBuilder("{\n")
            .Append("  \"zxms\":\"").Append(Escape(zxms)).Append("\",\n")
            .Append("  \"ip\":\"").Append(Escape(ip)).Append("\",\n")
            .Append("  \"reason\":\"").Append(Escape(reason)).Append("\",\n")
            .Append("  \"bannedAt\":\"").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\",\n")
            .Append("  \"expireAt\":").Append(expire).Append(",\n")
            .Append("  \"accounts\":").Append(count).Append('\n')
            .Append('}')
            .ToString();
    }

    /// <summary>账号名当目录名时去掉非法字符(Windows 上 \ / : * ? " &lt; &gt; | 都不能进目录名)。</summary>
    private static string Safe(string name)
    {
        var text = name.Trim();

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            text = text.Replace(c, '_');
        }

        return text.Length > 0 ? text : "_";
    }

    /// <summary>取字符串字段;不是字符串或没有就当空串。</summary>
    private static string Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>JSON 字符串转义。</summary>
    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}
