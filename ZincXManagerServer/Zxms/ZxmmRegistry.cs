using System;                                // Array / Environment / Exception
using System.Collections.Generic;            // List / IReadOnlyList
using System.Globalization;                  // CultureInfo / NumberStyles
using System.IO;                             // File / Directory / Path
using System.Text;                           // StringBuilder / UTF8Encoding
using ZincXManagerShared.Logging;            // Logger / LogLevel

namespace ZincXManagerServer.Zxms;

/// <summary>
/// 一台登记在册的 ZXMM 连接：怎么连它、以及它的控制台走哪条路（ZXMM 桥接 / 直连 RCON）。
///
/// <para>必须 public：<see cref="RconHub"/> 与 <see cref="ConsoleRouter"/> 是 public 的，
/// 参数类型比方法更封闭会编译不过（CS0051）。</para>
/// </summary>
public sealed class ZxmmEntry
{
    /// <summary>服务器编号，大于 0 且全局唯一；控制台命令与客户端都用它指名服务器。</summary>
    public int Id { get; set; }

    /// <summary>服务器显示名称，日志与界面里给人看。</summary>
    public string Name { get; set; } = "";

    /// <summary>ZXMM 所在主机；RCON 主机留空时也用它。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>ZXMM 专用通道端口（1-65535），默认 21200。</summary>
    public int Port { get; set; } = 21200;

    /// <summary>ZXMS↔ZXMM 专用通道令牌；空串表示该服不校验令牌。</summary>
    public string Token { get; set; } = "";

    /// <summary>控制台模式：0 = 走 ZXMM 桥接（默认），1 = 直连 RCON。</summary>
    public int ConsoleMode { get; set; } = ZxmmRegistry.ConsoleModeBridge;

    /// <summary>RCON 主机；空串表示与 <see cref="Host"/> 同一台机器。</summary>
    public string RconHost { get; set; } = "";

    /// <summary>RCON 端口，默认 25575（MC 的默认 rcon.port）。</summary>
    public int RconPort { get; set; } = ZxmmRegistry.DefaultRconPort;

    /// <summary>RCON 密码；空串表示还没配，日志里一律不打印。</summary>
    public string RconPassword { get; set; } = "";

    /// <summary>RCON 地址 "host:port"，给日志和界面用；RCON 主机留空时沿用 ZXMM 那台主机。</summary>
    public string RconAddress => $"{(string.IsNullOrWhiteSpace(RconHost) ? Host : RconHost.Trim())}:{RconPort}";
}

/// <summary>
/// ZXMM 连接表 <c>Data/ZXMMC.dat</c> 的读取、查询与写回；表取代早期的 <c>Data/Servers.txt</c>。
///
/// <para>读写都走即时开关的 <see cref="File"/>：这张表要能被运维随时手改，不能长期占着文件句柄；
/// 而且整表写回要先截断，ZFile 只能追加。</para>
/// </summary>
internal static class ZxmmRegistry
{
    /// <summary>连接表路径，相对进程工作目录。</summary>
    public const string FileName = "Data/ZXMMC.dat";

    /// <summary>旧连接表路径（只在首次启动迁移时读）。</summary>
    private const string LegacyFileName = "Data/Servers.txt";

    /// <summary>RCON 端口缺省值，与 MC 的默认 rcon.port 一致。</summary>
    public const int DefaultRconPort = 25575;

    /// <summary>控制台模式：0 = 走 ZXMM 桥接。</summary>
    public const int ConsoleModeBridge = 0;

    /// <summary>控制台模式：1 = 直连 RCON。</summary>
    public const int ConsoleModeRcon = 1;

    /// <summary>一行至少几段（编号|名称|主机|端口|令牌）。</summary>
    private const int MinimumFields = 5;

    /// <summary>一行最多几段（9 段全填）。</summary>
    private const int MaximumFields = 9;

    /// <summary>模板与写回共用的表头注释。</summary>
    private static readonly string HeaderText = string.Join(
        Environment.NewLine,
        "# ZXMM 连接表：一行一台，格式 编号|名称|主机|端口|令牌|控制台模式|RCON主机|RCON端口|RCON密码",
        "# 后 4 段可省略：控制台模式默认 0（0 = ZXMM 桥接，1 = 直连 RCON），RCON 端口默认 25575，其余留空",
        "# RCON 主机留空 = 与 ZXMM 同一台机器；令牌与模组 config 里的 api.token 一致才校验，两边都留空 = 不校验",
        "# 示例：1|ZXM测试服|127.0.0.1|21200||0|||",
        "#       2|二服-创造|127.0.0.1|21201|tok123|1|127.0.0.1|25575|rconpass",
        "# 以 # 开头的行为注释") + Environment.NewLine;

    private static IReadOnlyList<ZxmmEntry> _all = Array.Empty<ZxmmEntry>();   //已载入的连接表(不可变)

    /// <summary>全部已登记的连接，按文件里的顺序排列；尚未载入时为空。</summary>
    public static IReadOnlyList<ZxmmEntry> All => _all;

    /// <summary>
    /// 载入连接表：新表不存在而旧表存在时迁移过来，两张表都没有时建一份只有注释的模板。
    /// </summary>
    /// <param name="defaultHost">迁移旧表时给每台服填的 ZXMM 主机（旧格式里没有主机字段）。</param>
    public static void Load(string defaultHost)
    {
        var host = string.IsNullOrWhiteSpace(defaultHost) ? "127.0.0.1" : defaultHost.Trim();

        if (File.Exists(FileName))
        {
            _all = ParseTable(FileName).AsReadOnly();
            Logger.Log(LogLevel.Info, $"[控制台] 读取连接表 {FileName} → {_all.Count} 台");
            return;
        }

        if (File.Exists(LegacyFileName))
        {
            Migrate(host);
            return;
        }

        _all = Array.Empty<ZxmmEntry>();
        if (WriteAll(FileName, HeaderText))
        {
            Logger.Log(LogLevel.Info,
                $"[控制台] 连接表 {FileName} 不存在 → 已建空白模板，请按「编号|名称|主机|端口|令牌|控制台模式|RCON主机|RCON端口|RCON密码」登记");
        }
    }

    /// <summary>
    /// 按编号查一台服务器。
    /// </summary>
    /// <param name="id">服务器编号。</param>
    /// <param name="entry">查到时为对应连接项，查不到为 null。</param>
    /// <returns>查到返回 true。</returns>
    public static bool TryGet(int id, out ZxmmEntry entry)
    {
        foreach (var item in _all)
        {
            if (item.Id == id)
            {
                entry = item;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    /// <summary>
    /// 按编号（十进制）或名称查一台服务器，名称不区分大小写。
    /// </summary>
    /// <param name="key">服务器编号或显示名称。</param>
    /// <returns>查到时为对应连接项，查不到为 null。</returns>
    public static ZxmmEntry? Find(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var text = key.Trim();
        if (TryParseNumber(text, out var id))
        {
            return TryGet(id, out var byId) ? byId : null;
        }

        foreach (var item in _all)
        {
            if (string.Equals(item.Name, text, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>把当前内存里的整张表写回文件（带注释模板）。</summary>
    public static void Save()
    {
        if (WriteAll(FileName, BuildText(_all)))
        {
            Logger.Log(LogLevel.Info, $"[控制台] 已写回连接表 {FileName} → {_all.Count} 台");
        }
    }

    /// <summary>旧表 <c>Data/Servers.txt</c>（编号|名称|端口|令牌）迁到新表，旧表改名留底。</summary>
    private static void Migrate(string defaultHost)
    {
        var list = ParseLegacy(LegacyFileName, defaultHost);
        if (!WriteAll(FileName, BuildText(list)))
        {
            _all = Array.Empty<ZxmmEntry>();
            Logger.Log(LogLevel.Warning,
                $"[控制台] 迁移失败 → 旧表 {LegacyFileName} 保持不动，可按新格式手工新建 {FileName}");
            return;
        }

        // 旧表改名而不是删除:迁移结果要能被运维翻回去对账
        try
        {
            File.Move(LegacyFileName, LegacyFileName + ".bak", true);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[控制台] 旧表 {LegacyFileName} 改名 .bak 失败 → {ex.Message}");
        }

        _all = list.AsReadOnly();
        Logger.Log(LogLevel.Info, $"[控制台] 已把 Servers.txt 迁移到 ZXMMC.dat({list.Count} 台)");
    }

    /// <summary>读新格式（9 段，后 4 段可缺省）；坏行跳过并记 Warn。</summary>
    private static List<ZxmmEntry> ParseTable(string path)
    {
        var list = new List<ZxmmEntry>();
        var lines = ReadLines(path);
        if (lines is null)
        {
            return list;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            var lineNumber = index + 1;   //物理行号,报错时好定位

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split('|');
            if (parts.Length < MinimumFields || parts.Length > MaximumFields)
            {
                LogBadLine(path, lineNumber, $"字段数 {parts.Length} 不在 {MinimumFields}-{MaximumFields} 段", line);
                continue;
            }

            if (!TryParseNumber(parts[0], out var id) || id <= 0)
            {
                LogBadLine(path, lineNumber, $"编号「{parts[0].Trim()}」不是大于 0 的整数", line);
                continue;
            }

            var name = parts[1].Trim();
            if (name.Length == 0)
            {
                LogBadLine(path, lineNumber, "名称为空", line);
                continue;
            }

            var host = parts[2].Trim();
            if (host.Length == 0)
            {
                LogBadLine(path, lineNumber, "主机为空", line);
                continue;
            }

            if (!TryParseNumber(parts[3], out var port) || port is < 1 or > 65535)
            {
                LogBadLine(path, lineNumber, $"端口「{parts[3].Trim()}」不在 1-65535", line);
                continue;
            }

            var mode = ConsoleModeBridge;
            if (HasField(parts, 5) && (!TryParseNumber(parts[5], out mode) || (mode != ConsoleModeBridge && mode != ConsoleModeRcon)))
            {
                LogBadLine(path, lineNumber, $"控制台模式「{parts[5].Trim()}」只能是 0（ZXMM 桥接）或 1（RCON）", line);
                continue;
            }

            var rconPort = DefaultRconPort;
            if (HasField(parts, 7) && (!TryParseNumber(parts[7], out rconPort) || rconPort is < 1 or > 65535))
            {
                LogBadLine(path, lineNumber, $"RCON 端口「{parts[7].Trim()}」不在 1-65535", line);
                continue;
            }

            if (Find(list, id) is not null)
            {
                LogBadLine(path, lineNumber, $"编号 {id} 与前面的登记重复，保留第一条", line);
                continue;
            }

            list.Add(new ZxmmEntry
            {
                Id = id,
                Name = name,
                Host = host,
                Port = port,
                Token = Field(parts, 4),
                ConsoleMode = mode,
                RconHost = Field(parts, 6),
                RconPort = rconPort,
                RconPassword = Field(parts, 8),
            });
        }

        return list;
    }

    /// <summary>读旧格式（编号|名称|端口|令牌）；主机统一填 <paramref name="defaultHost"/>，其余按缺省值。</summary>
    private static List<ZxmmEntry> ParseLegacy(string path, string defaultHost)
    {
        var list = new List<ZxmmEntry>();
        var lines = ReadLines(path);
        if (lines is null)
        {
            return list;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            var lineNumber = index + 1;   //物理行号,报错时好定位

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split('|');
            if (parts.Length is < 3 or > 4)
            {
                LogBadLine(path, lineNumber, $"旧表字段数 {parts.Length} 不是 3-4 段", line);
                continue;
            }

            if (!TryParseNumber(parts[0], out var id) || id <= 0)
            {
                LogBadLine(path, lineNumber, $"编号「{parts[0].Trim()}」不是大于 0 的整数", line);
                continue;
            }

            var name = parts[1].Trim();
            if (name.Length == 0)
            {
                LogBadLine(path, lineNumber, "名称为空", line);
                continue;
            }

            if (!TryParseNumber(parts[2], out var port) || port is < 1 or > 65535)
            {
                LogBadLine(path, lineNumber, $"端口「{parts[2].Trim()}」不在 1-65535", line);
                continue;
            }

            if (Find(list, id) is not null)
            {
                LogBadLine(path, lineNumber, $"编号 {id} 与前面的登记重复，保留第一条", line);
                continue;
            }

            list.Add(new ZxmmEntry
            {
                Id = id,
                Name = name,
                Host = defaultHost,
                Port = port,
                Token = parts.Length == 4 ? parts[3].Trim() : "",
                ConsoleMode = ConsoleModeBridge,
            });
        }

        return list;
    }

    /// <summary>把整张表拼成文件文本（表头注释 + 每台一行九段）。</summary>
    private static string BuildText(IReadOnlyList<ZxmmEntry> entries)
    {
        var builder = new StringBuilder(HeaderText);   //表头原样保留,运维改表时能照着格式抄
        foreach (var entry in entries)
        {
            builder.Append(entry.Id).Append('|')
                .Append(entry.Name).Append('|')
                .Append(entry.Host).Append('|')
                .Append(entry.Port).Append('|')
                .Append(entry.Token).Append('|')
                .Append(entry.ConsoleMode).Append('|')
                .Append(entry.RconHost).Append('|')
                .Append(entry.RconPort).Append('|')
                .Append(entry.RconPassword)
                .AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>整表写回：先写同目录临时文件再替换，避免写到一半留下半张表。</summary>
    private static bool WriteAll(string path, string text)
    {
        var temp = path + ".tmp";   //同目录才保证 Move 是同一卷上的原子替换
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, $"[控制台] 写 {path} 失败 → {ex.Message}");
            TryDelete(temp);
            return false;
        }
    }

    /// <summary>读整个文件的行；读不了返回 null（调用方按 0 台处理）。</summary>
    private static string[]? ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, $"[控制台] 读取 {path} 失败 → 按 0 台处理：{ex.Message}");
            return null;
        }
    }

    /// <summary>清理临时文件（失败只记 Debug，不影响主流程）。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Debug, $"[控制台] 清理临时文件 {path} 失败 → {ex.Message}");
        }
    }

    private static void LogBadLine(string path, int lineNumber, string reason, string line)
        => Logger.Log(LogLevel.Warning, $"[控制台] {path} 第 {lineNumber} 行{reason} → 已跳过：{line}");

    /// <summary>第 <paramref name="index"/> 段存在且不是空串（后 4 段可整段省略）。</summary>
    private static bool HasField(string[] parts, int index)
        => parts.Length > index && parts[index].Trim().Length > 0;

    private static string Field(string[] parts, int index)
        => parts.Length > index ? parts[index].Trim() : "";

    private static ZxmmEntry? Find(List<ZxmmEntry> list, int id)
    {
        foreach (var item in list)
        {
            if (item.Id == id)
            {
                return item;
            }
        }

        return null;
    }

    private static bool TryParseNumber(string text, out int value)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
