using System.Text;  // 文本编码与 StringBuilder
using System.Text.Json;  // JSON 序列化
using ZincXManagerShared.Logging;  // 共享库

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

/// <summary>封禁表(ZXMS / ZXMOS 各一份 bans.ini):可以按账号、IP、机器码封, 支持永久与定时(定时就是写一个解封时间,过期自动失效)。</summary>
public sealed class BanStore : IDisposable
{
    /// <summary>写盘用的 UTF-8(不带 BOM):带 BOM 的文件按裸字节解析会直接报「0xEF 是非法起始值」,外部工具读不了。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly object _sync = new();
    private readonly List<BanEntry> _list = [];

    /// <summary>跨进程独占锁的句柄;拿不到锁时为 null(此时本实例只读)。</summary>
    private FileStream? _lockFile;

    /// <summary>只读模式:拿不到锁,或坏文件无法保留时为 true,Save 只记日志不落盘。</summary>
    private bool _readOnly;

    /// <summary>已释放标记:Dispose 之后本实例一律不再写盘,防止已释放的旧实例把别人新写的数据盖掉。</summary>
    private volatile bool _disposed;

    /// <summary>本次载入时坏文件已被隔离:表为空且盘上没有文件时不创建空表,免得运维把空表当成「数据没了」。</summary>
    private bool _loadedFromBadFile;

    /// <summary>文件路径。</summary>
    public string Path { get; }

    /// <summary>本实例是不是只读(封禁表被另一个进程占用,或坏文件无法保留);只读时不写盘。</summary>
    public bool ReadOnly => _readOnly;

    /// <summary>本实例是否已经 Dispose;释放之后 Add / Remove / PurgeKind 一律失败,不再落盘。</summary>
    public bool Disposed => _disposed;

    /// <summary>打开封禁表:先抢跨进程锁,再读盘。</summary>
    public BanStore(string path = "bans.json")
    {
        Path = path;
        AcquireProcessLock();
        Load();
    }

    /// <summary>退出前先把内存表存盘,再释放跨进程锁(只读实例存盘时自己跳过);重复调用直接返回,第二次不再碰盘。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;   // 幂等:第二次调用直接返回,不重复存盘、不重复释放锁
            }

            _disposed = true;

            // 析构路径不宜抛:存盘失败只记 Error,但日志必须点明是退出时的存盘失败
            if (!SaveCore(allowWhenDisposed: true, out var reason) && !_readOnly)
            {
                Logger.Log(LogLevel.Error, $"[封禁] 退出时存盘失败,重启后会丢失本次内存改动({Path}):{reason}");
            }

            _lockFile?.Dispose();
            _lockFile = null;
        }
    }

    /// <summary>生效条数:全部封禁记录(剔除已过期的);要看「表内条数」用 <see cref="StoredCount"/>。</summary>
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
                    // machine 分支必须先确认传进来的机器码非空:客户端取不到主板序列号时发来的是空串,
                    // 让它去比较就会「所有缺码机器共用一个匹配键」—— 封一台等于封全部缺码机器。
                    // (Add 也不接受空值,但 bans.json 可能是手改或老文件迁移来的,这里再兜一道)
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

    /// <summary>
    /// 加一条封禁;已存在就续期。返回值 true 表示内存态已生效,<paramref name="error"/> 非空表示写盘失败
    /// (重启后这条会丢)—— 两者可以同时成立,调用方必须把 error 打出来,不能当成「一切正常」。
    /// </summary>
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

        string saveError;   // 写盘失败时要给调用方看的文案;空串表示已落盘
        lock (_sync)
        {
            if (_disposed)
            {
                error = "实例已释放,不能再加封禁:" + kind + "=" + value;
                return false;
            }

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

            // 内存表已经改好了,落盘失败不能回退内存态,但必须让调用方看见
            saveError = Save() ? "" : "封禁已记入内存,但写盘失败,重启后会丢失;请检查 bans.json 是否可写";
        }

        var until = duration is null ? "永久" : "到期 " + DateTime.Now.Add(duration.Value);
        Logger.Log(LogLevel.Warning, $"[封禁] {kind}={value} {until} 原因:{reason}");

        if (saveError.Length > 0)
        {
            Logger.Log(LogLevel.Error, $"[封禁] 写盘失败未落盘 {kind}={value}:{saveError}");
        }

        error = saveError;
        return true;
    }

    /// <summary>解封;返回有没有删掉这条。写盘结果看不出来,要区分请用带 out 的重载。</summary>
    public bool Remove(string kind, string value) => Remove(kind, value, out _);

    /// <summary>
    /// 解封。返回值表示内存表里删没删掉这条(与写盘无关);
    /// <paramref name="error"/> 非空表示写盘失败(重启后会复活这条封禁),或实例已释放而未改动。
    /// </summary>
    public bool Remove(string kind, string value, out string error)
    {
        string saveError;   // 写盘失败的原因;空串表示已落盘或无需写盘
        bool removed;   // 内存表里真的删掉了这条

        lock (_sync)
        {
            if (_disposed)
            {
                error = "实例已释放,不能再解封:" + kind + "=" + value;
                return false;
            }

            removed = _list.RemoveAll(b => b.Kind == kind.Trim().ToLowerInvariant()
                && string.Equals(b.Value, value.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;

            saveError = removed && !Save()
                ? "解封已记入内存,但写盘失败,重启后这条封禁会复活;请检查 bans.json 是否可写"
                : "";
        }

        if (saveError.Length > 0)
        {
            Logger.Log(LogLevel.Error, $"[封禁] 写盘失败未落盘 解封 {kind}={value}:{saveError}");
        }

        error = removed ? saveError : "";
        return removed;
    }

    /// <summary>表内条数:封禁表里实际存着的记录条数(含已过期,不做剔除);要「生效条数」用 <see cref="All"/>。</summary>
    public int StoredCount
    {
        get
        {
            lock (_sync)
            {
                return _list.Count;
            }
        }
    }

    /// <summary>表内条数:某一类记录有多少条(含已过期,不做剔除);与 PurgeKind 的口径一致,要「生效条数」按 <see cref="All"/> 自己筛。</summary>
    public int CountKind(string kind)
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();
        if (kind.Length == 0)
        {
            return 0;
        }

        lock (_sync)
        {
            var count = 0;   // 同类记录条数
            foreach (var b in _list)
            {
                if (string.Equals(b.Kind, kind, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>按类型一次性清掉封禁记录;返回清掉的条数。写盘结果看不出来,要区分请用带 out 的重载。</summary>
    public int PurgeKind(string kind) => PurgeKind(kind, out _);

    /// <summary>
    /// 按类型一次性清掉封禁记录;返回内存表里清掉的条数。
    /// <paramref name="error"/> 非空表示写盘失败(重启后被清的记录会回来),或实例已释放而未改动。
    /// </summary>
    public int PurgeKind(string kind, out string error)
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();
        error = "";

        if (kind.Length == 0)
        {
            return 0;
        }

        var removed = 0;   // 被清掉的条数
        string saveError;   // 写盘失败的原因;空串表示已落盘或无需写盘

        lock (_sync)
        {
            if (_disposed)
            {
                error = "实例已释放,不能再清理封禁记录:" + kind;
                return 0;
            }

            // 过期的同类记录也算在清理范围内,一起删掉
            removed = _list.RemoveAll(b => string.Equals(b.Kind, kind, StringComparison.OrdinalIgnoreCase));
            saveError = removed > 0 && !Save()
                ? "清理已记入内存,但写盘失败,重启后被清的记录会回来;请检查 bans.json 是否可写"
                : "";
        }

        if (removed > 0)
        {
            Logger.Log(LogLevel.Info, $"[封禁] 清理 {kind} 类记录 {removed} 条");
        }

        if (saveError.Length > 0)
        {
            Logger.Log(LogLevel.Error, $"[封禁] 写盘失败未落盘 清理 {kind} {removed} 条:{saveError}");
        }

        error = saveError;
        return removed;
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


    /// <summary>读 JSON;没有 json 但有老的 bans.ini 时先迁移。解析失败时把原文件留成 .bad-时间戳,以空表继续跑。</summary>
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

            var text = File.ReadAllText(Path, Encoding.UTF8);   // 整个文件先读成字符串,解析失败时原文件还没被动过
            var parsed = new List<BanEntry>();   // 先解析到临时表:中途抛异常时 _list 还是空的,不会留下半个表

            using (var doc = JsonDocument.Parse(text))
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var expireAt = DateTime.MaxValue;

                    if (item.TryGetProperty("expireAt", out var e) && e.ValueKind == JsonValueKind.String)
                    {
                        expireAt = DateTime.Parse(e.GetString()!, null,
                            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
                    }

                    parsed.Add(new BanEntry
                    {
                        Kind = item.TryGetProperty("kind", out var k) ? k.GetString() ?? "user" : "user",
                        Value = item.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "",
                        ExpireAt = expireAt,
                        Reason = item.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                    });
                }
            }

            _list.AddRange(parsed);
            Logger.Log(LogLevel.Info, $"[封禁] 已载入 {_list.Count} 条({Path})");
        }
        catch (Exception ex)
        {
            // 空表继续跑,但绝不把空表写回去盖掉盘上那份数据:先把坏文件留一份副本
            _list.Clear();
            QuarantineBadFile(ex);
        }
    }

    /// <summary>
    /// 对 &lt;path&gt;.lock 建独占句柄并持有到 Dispose:两个进程同时改同一份表会互相覆盖(丢记录),
    /// 所以拿不到锁的实例降级成只读,不再落盘。
    /// </summary>
    private void AcquireProcessLock()
    {
        var lockPath = Path + ".lock";   // 锁文件路径

        try
        {
            var full = System.IO.Path.GetFullPath(lockPath);
            var dir = System.IO.Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex)
        {
            _readOnly = true;
            Logger.Log(LogLevel.Error,
                $"[封禁] 封禁表已被另一个进程占用({System.IO.Path.GetFullPath(lockPath)}),本实例只读不写盘:{ex.Message}");
        }
    }

    /// <summary>
    /// 解析失败时把原文件改名成 &lt;path&gt;.bad-yyyyMMdd-HHmmss 保留下来;改名失败就复制一份再删原文件。
    /// 两条都失败时原文件一律不动,并把本实例置成只读 —— 否则下一次 Add 就会用空表盖掉这份数据。
    /// </summary>
    private void QuarantineBadFile(Exception cause)
    {
        if (!File.Exists(Path))
        {
            Logger.Log(LogLevel.Error, $"[封禁] 读取封禁表失败(文件不存在,无可保留):{cause.Message}");
            return;
        }

        var bad = Path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");   // 保留的原文件路径

        try
        {
            File.Move(Path, bad);   // 同目录改名,不产生第二份数据,原文件字节原样保留
            _loadedFromBadFile = true;
            Logger.Log(LogLevel.Error, $"[封禁] 封禁表解析失败,原文件已保留为 {bad}(内容未改动,请人工核查):{cause.Message}");
            return;
        }
        catch (Exception moveEx)
        {
            Logger.Log(LogLevel.Error, $"[封禁] 坏文件改名失败({bad}):{moveEx.Message};改用复制保留");
        }

        try
        {
            File.Copy(Path, bad, overwrite: true);
            File.Delete(Path);
            _loadedFromBadFile = true;
            Logger.Log(LogLevel.Error, $"[封禁] 封禁表解析失败,原文件已复制保留为 {bad} 并删除原文件:{cause.Message}");
        }
        catch (Exception copyEx)
        {
            _readOnly = true;
            Logger.Log(LogLevel.Error,
                $"[封禁] 封禁表解析失败且坏文件无法保留({Path}),原文件保持不动,本实例不再写盘:" +
                $"{cause.Message} / 复制失败:{copyEx.Message}");
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

            var f = text.Split(',');   //拆分结果
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

    /// <summary>写盘并返回成功与否;失败原因只记日志,调用方要文案请用带 reason 的 <see cref="SaveCore"/>。</summary>
    private bool Save() => SaveCore(allowWhenDisposed: false, out _);

    /// <summary>
    /// 写 JSON(手写,不依赖反射,AOT 安全)。先写同目录 &lt;path&gt;.tmp,落稳后整体改名替换:
    /// 直接截断原文件再写的话,共享读的读者会读到 0 字节或半截 JSON。
    /// </summary>
    /// <param name="allowWhenDisposed">Dispose 自己存盘时要放行,否则 <see cref="_disposed"/> 会把最后一笔改动挡掉。</param>
    /// <param name="reason">写盘没成功的原因;空串表示成功或本来无需写盘。</param>
    private bool SaveCore(bool allowWhenDisposed, out string reason)
    {
        reason = "";

        if (_disposed && !allowWhenDisposed)
        {
            reason = "实例已释放";
            Logger.Log(LogLevel.Warning, $"[封禁] 实例已释放,忽略写入:{Path}");
            return false;
        }

        if (_readOnly)
        {
            reason = "封禁表被另一个进程占用或坏文件无法保留,本实例只读";
            Logger.Log(LogLevel.Warning, $"[封禁] 只读模式(封禁表被别的进程占用或坏文件无法保留),本次改动不落盘:{Path}");
            return false;
        }

        // 坏文件刚被隔离、表又为空、盘上也没有文件:这时写出去的就是一个空表,运维会当成「数据没了」,所以跳过
        if (_list.Count == 0 && _loadedFromBadFile && !File.Exists(Path))
        {
            Logger.Log(LogLevel.Info,
                $"[封禁] 原文件已在解析失败时隔离,表为空且盘上没有 {Path},本次不创建空表(隔离文件请人工核查)");
            return true;
        }

        var tmp = Path + ".tmp";   // 同目录临时文件:同卷改名是原子的

        try
        {
            var list = _list.ToArray();   // 写盘用的快照
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

            // 独占句柄(FileShare.None)+ 落盘(Flush(true))之后才改名,别的进程任何时刻都看不到半截文件
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                writer.Write(json.ToString());
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(tmp, Path, overwrite: true);
            _loadedFromBadFile = false;   // 盘上已经有正常文件了,后面的空表判断不再适用
            return true;
        }
        catch (Exception ex)
        {
            reason = "写入失败:" + ex.Message;
            Logger.Log(LogLevel.Error, $"[封禁] 保存封禁表失败({Path}):{ex.Message}");
            TryDeleteFile(tmp);   // 失败时不许把半截 tmp 留在盘上
            return false;
        }
    }

    /// <summary>删掉写失败留下的临时文件;删不掉只记 Warning,不抛。</summary>
    private static void TryDeleteFile(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[封禁] 清理临时文件失败({file}):{ex.Message}");
        }
    }

    /// <summary>JSON 字符串转义。</summary>
    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}
