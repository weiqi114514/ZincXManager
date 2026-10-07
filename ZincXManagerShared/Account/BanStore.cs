using System.Text;
using System.Text.Json;
using ZincXManagerShared.Logging;

namespace ZincXManagerShared.Account;

/// <summary>一条封禁记录。</summary>
public sealed class BanEntry
{
    /// <summary>类型:user(账号) / ip / machine(机器码)。</summary>
    public string Kind { get; init; } = "user";

    /// <summary>被封的值(账号名 / IP / 机器码)。</summary>
    public string Value { get; init; } = "";

    /// <summary>解封时间(UTC);<see cref="DateTime.MaxValue"/> 表示永久。</summary>
    public DateTime ExpireAt { get; set; } = DateTime.MaxValue;

    /// <summary>原因。</summary>
    public string Reason { get; set; } = "";

    /// <summary>永久封禁吗。</summary>
    public bool IsPermanent => ExpireAt == DateTime.MaxValue;

    /// <summary>还在封禁期内吗。</summary>
    public bool IsActive => IsPermanent || ExpireAt > DateTime.UtcNow;

    /// <summary>剩余时长文案。</summary>
    public string Remain => IsPermanent ? "永久" : Format(ExpireAt - DateTime.UtcNow);

    /// <summary>把时长格式化成 "3 天 4 小时" 这样。</summary>
    public static string Format(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
        {
            return "已过期";
        }

        if (span.TotalDays >= 1) return $"{(int)span.TotalDays} 天 {span.Hours} 小时";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
        return $"{Math.Max(1, (int)span.TotalMinutes)} 分钟";
    }
}

/// <summary>
/// 封禁表(ZXMS / ZXMOS 各一份 bans.ini):可以按账号、IP、机器码封,
/// 支持永久与定时(定时就是写一个解封时间,过期自动失效)。
/// </summary>
public sealed class BanStore
{
    private readonly object _sync = new();
    private readonly List<BanEntry> _list = [];

    /// <summary>文件路径。</summary>
    public string Path { get; }

    public BanStore(string path = "bans.json")
    {
        Path = path;
        Load();
    }

    /// <summary>全部封禁记录(剔除已过期的)。</summary>
    public IReadOnlyList<BanEntry> All
    {
        get
        {
            lock (_sync)
            {
                _list.RemoveAll(b => !b.IsActive);
                return _list.ToArray();
            }
        }
    }

    /// <summary>这个账号 / IP / 机器码被封了吗;被封就给出记录。</summary>
    public bool Check(string? user, string? ip, string? machine, out BanEntry? hit)
    {
        hit = null;
        lock (_sync)
        {
            _list.RemoveAll(b => !b.IsActive);

            foreach (var ban in _list)
            {
                var matched = ban.Kind switch
                {
                    "user" => user is { Length: > 0 } && string.Equals(ban.Value, user, StringComparison.OrdinalIgnoreCase),
                    "ip" => ip is { Length: > 0 } && ban.Value == ip,
                    "machine" => machine is { Length: > 0 } && string.Equals(ban.Value, machine, StringComparison.OrdinalIgnoreCase),
                    _ => false,
                };

                if (matched)
                {
                    hit = ban;
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>加一条封禁;已存在就续期。</summary>
    public bool Add(string kind, string value, TimeSpan? duration, string reason, out string error)
    {
        kind = kind.Trim().ToLowerInvariant();
        value = value.Trim();

        if (kind is not ("user" or "ip" or "machine"))
        {
            error = "类型只能是 user / ip / machine";
            return false;
        }

        if (value.Length == 0)
        {
            error = "被封的值不能为空";
            return false;
        }

        lock (_sync)
        {
            var expire = duration is null ? DateTime.MaxValue : DateTime.UtcNow.Add(duration.Value);
            var old = _list.FirstOrDefault(b => b.Kind == kind && string.Equals(b.Value, value, StringComparison.OrdinalIgnoreCase));

            if (old is not null)
            {
                old.ExpireAt = expire;
                old.Reason = reason;
            }
            else
            {
                _list.Add(new BanEntry { Kind = kind, Value = value, ExpireAt = expire, Reason = reason });
            }

            Save();
        }

        var until = duration is null ? "永久" : "到期 " + DateTime.Now.Add(duration.Value);
        Logger.Log(LogLevel.Warning, $"[封禁] {kind}={value} {until} 原因:{reason}");
        error = "";
        return true;
    }

    /// <summary>解封。</summary>
    public bool Remove(string kind, string value)
    {
        lock (_sync)
        {
            var removed = _list.RemoveAll(b => b.Kind == kind.Trim().ToLowerInvariant()
                && string.Equals(b.Value, value.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Save();
            }

            return removed;
        }
    }

    /// <summary>把 "30d / 12h / 30m / perm" 解析成时长;null 表示永久。</summary>
    public static bool TryParseDuration(string text, out TimeSpan? duration, out string error)
    {
        duration = null;
        error = "";
        text = text.Trim().ToLowerInvariant();

        if (text is "" or "0" or "perm" or "forever")
        {
            return true;
        }

        var unit = text[^1];
        var number = text[..^1];

        if (!double.TryParse(number, out var n) || n <= 0)
        {
            error = "时长格式:perm / 30d / 12h / 30m";
            return false;
        }

        duration = unit switch
        {
            'd' => TimeSpan.FromDays(n),
            'h' => TimeSpan.FromHours(n),
            'm' => TimeSpan.FromMinutes(n),
            _ => TimeSpan.Zero,
        };

        if (duration == TimeSpan.Zero)
        {
            error = "时长格式:perm / 30d / 12h / 30m";
            duration = null;
            return false;
        }

        return true;
    }


    /// <summary>读 JSON;没有 json 但有老的 bans.ini 时先迁移。</summary>
    private void Load()
    {
        try
        {
            var legacy = System.IO.Path.ChangeExtension(Path, ".ini");

            if (!File.Exists(Path) && File.Exists(legacy))
            {
                Logger.Log(LogLevel.Info, $"[封禁] 发现老文件 {legacy},正在迁移成 {Path}");
                LoadIni(legacy);
                Save();
                return;
            }

            if (!File.Exists(Path))
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(Path, Encoding.UTF8));

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var expireAt = DateTime.MaxValue;

                if (item.TryGetProperty("expireAt", out var e) && e.ValueKind == JsonValueKind.String)
                {
                    expireAt = DateTime.Parse(e.GetString()!, null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
                }

                _list.Add(new BanEntry
                {
                    Kind = item.TryGetProperty("kind", out var k) ? k.GetString() ?? "user" : "user",
                    Value = item.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "",
                    ExpireAt = expireAt,
                    Reason = item.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                });
            }

            Logger.Log(LogLevel.Info, $"[封禁] 已载入 {_list.Count} 条({Path})");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[封禁] 读取封禁表失败: " + ex.Message);
        }
    }

    /// <summary>读老格式:类型,值,ticks或perm,原因</summary>
    private void LoadIni(string legacyPath)
    {
        foreach (var line in File.ReadAllLines(legacyPath, Encoding.UTF8))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#'))
            {
                continue;
            }

            var f = text.Split(',');
            if (f.Length < 3)
            {
                continue;
            }

            _list.Add(new BanEntry
            {
                Kind = f[0].Trim(),
                Value = f[1].Trim(),
                ExpireAt = f[2].Trim() == "perm" ? DateTime.MaxValue : new DateTime(long.Parse(f[2].Trim()), DateTimeKind.Utc),
                Reason = f.Length > 3 ? f[3].Trim() : "",
            });
        }
    }

    /// <summary>写 JSON(手写,不依赖反射,AOT 安全)。</summary>
    private void Save()
    {
        try
        {
            var list = _list.ToArray();
            var json = new StringBuilder("[\n");

            for (var index = 0; index < list.Length; index++)
            {
                var b = list[index];
                var expire = b.IsPermanent ? "null" : "\"" + b.ExpireAt.ToString("o") + "\"";

                json.Append("  {\"kind\":\"").Append(Escape(b.Kind))
                    .Append("\",\"value\":\"").Append(Escape(b.Value))
                    .Append("\",\"expireAt\":").Append(expire)
                    .Append(",\"reason\":\"").Append(Escape(b.Reason))
                    .Append("\"}").Append(index == list.Length - 1 ? "\n" : ",\n");
            }

            json.Append(']');
            File.WriteAllText(Path, json.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[封禁] 保存封禁表失败: " + ex.Message);
        }
    }

    /// <summary>JSON 字符串转义。</summary>
    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}
