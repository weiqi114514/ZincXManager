using System;  // 基础类型
using System.Collections.Generic;  // 列表
using System.IO;  // 读写文件
using System.Linq;  // 查询
using System.Text;  // UTF-8
using ZincXManagerShared.Logging;  // 共享库

namespace ZincXManagerShared.Login;

/// <summary>
/// 登录/绑定相关的东西都存在这里:
/// <list type="bullet">
///   <item><c>Data/BindUser.txt</c> —— 绑定关系,一行一条 <c>账号ID=MC玩家名</c></item>
///   <item><c>PersistentLogin.txt</c> —— 持久登录表,一行一条 <c>玩家名|玩家IP|时间</c></item>
///   <item><c>loginlog.log</c> —— 登录日志,带年月日</item>
/// </list>
/// </summary>
public static class LoginStore
{
    /// <summary>绑定表文件。</summary>
    public const string BindFile = "Data/BindUser.txt";

    /// <summary>持久登录表文件。</summary>
    public const string PersistentFile = "PersistentLogin.txt";

    /// <summary>登录日志文件。</summary>
    public const string LogFile = "loginlog.log";

    // 保护文件读写
    private static readonly object _lock = new();

    // 绑定表缓存(账号ID → 玩家名)
    private static Dictionary<int, string>? _cache;

    /// <summary>
    /// 追加一行:用 <see cref="FileShare.ReadWrite"/> 打开,别人 tail / 面板读日志时不会把这次写入顶掉。
    /// <para>默认的 <c>File.AppendAllText</c> 是 <c>FileShare.Read</c>,只要有人正开着读就抛 IOException,
    /// 结果就是"日志里少了几行"这种找不出原因的坑。</para>
    /// </summary>
    private static void AppendLine(string path, string line)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        writer.WriteLine(line);
    }

    /// <summary>整份读出来(允许别人同时写)。</summary>
    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    /// <summary>整份写回(允许别人同时读,不会因为 tail 而失败)。</summary>
    private static void WriteLines(string path, IEnumerable<string> lines)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        foreach (var line in lines)
        {
            writer.WriteLine(line);
        }
    }

    /// <summary>这个账号绑定的玩家名(没绑返回空)。</summary>
    public static string PlayerOf(int accountId)
    {
        lock (_lock)
        {
            return LoadBinds().TryGetValue(accountId, out var player) ? player : "";
        }
    }

    /// <summary>玩家绑在哪个账号上(没绑返回 0)。</summary>
    public static int AccountOf(string player)
    {
        lock (_lock)
        {
            foreach (var pair in LoadBinds())
            {
                if (string.Equals(pair.Value, player, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Key;
                }
            }

            return 0;
        }
    }

    /// <summary>绑定:账号ID ↔ 玩家名(一个账号只绑一个玩家,一个玩家只归一个账号)。</summary>
    public static bool Bind(int accountId, string player, out string error)
    {
        error = "";
        player = player.Trim();
        if (accountId <= 0 || player.Length == 0)
        {
            error = "账号或玩家名不合法";
            return false;
        }

        lock (_lock)
        {
            var binds = LoadBinds();   //全部绑定

            // 玩家已经被别的账号绑了
            var owner = binds.FirstOrDefault(p => string.Equals(p.Value, player, StringComparison.OrdinalIgnoreCase));
            if (owner.Key > 0 && owner.Key != accountId)
            {
                error = $"这个玩家已经被账号 {owner.Key} 绑定了";
                return false;
            }

            binds[accountId] = player;
            SaveBinds(binds);
            Log($"[绑定] 账号 {accountId} 绑定了玩家 {player}");
            return true;
        }
    }

    /// <summary>解绑(按账号ID)。</summary>
    public static bool Unbind(int accountId, out string player)
    {
        player = "";
        lock (_lock)
        {
            var binds = LoadBinds();   //全部绑定
            if (!binds.TryGetValue(accountId, out var found))
            {
                return false;
            }

            player = found;
            binds.Remove(accountId);
            SaveBinds(binds);
            Log($"[解绑] 账号 {accountId} 解绑了玩家 {player}");
            return true;
        }
    }

    /// <summary>全部绑定(玩家名 → 账号ID)。</summary>
    public static List<KeyValuePair<int, string>> AllBinds()
    {
        lock (_lock)
        {
            return LoadBinds().OrderBy(p => p.Key).ToList();
        }
    }

    /// <summary>写持久登录表(玩家名 + IP + 时间)。同一玩家只留最新一条，不会越写越长。</summary>
    public static void AddPersistentLogin(string player, string ip)
    {
        lock (_lock)
        {
            try
            {
                var line = $"{player}|{ip}|{DateTime.Now:yyyy/MM/dd HH:mm:ss}";
                var kept = new List<string> { line };

                if (File.Exists(PersistentFile))
                {
                    foreach (var old in ReadLines(PersistentFile))
                    {
                        if (old.Trim().Length == 0)
                        {
                            continue;
                        }

                        var name = old.Split('|')[0].Trim();   //拆分结果
                        if (!string.Equals(name, player, StringComparison.OrdinalIgnoreCase))
                        {
                            kept.Add(old);
                        }
                    }
                }

                WriteLines(PersistentFile, kept);
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[登录] 写持久登录表失败: " + ex.Message);
            }
        }
    }

    /// <summary>从持久登录表里删掉这个玩家(解绑时调用,让"已信任"立刻失效)。</summary>
    public static void RemovePersistentLogin(string player)
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(PersistentFile))
                {
                    return;
                }

                var lines = ReadLines(PersistentFile);
                var kept = new List<string>();

                foreach (var line in lines)
                {
                    var name = line.Split('|')[0].Trim();   //拆分结果
                    if (!string.Equals(name, player, StringComparison.OrdinalIgnoreCase))
                    {
                        kept.Add(line);
                    }
                }

                WriteLines(PersistentFile, kept);
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[登录] 清持久登录表失败: " + ex.Message);
            }
        }
    }

    /// <summary>持久登录表里有没有这个玩家(同玩家 + 同 IP 才算免确认;换 IP 要重新确认)。</summary>
    public static bool HasPersistentLogin(string player, string ip)
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(PersistentFile))
                {
                    return false;
                }

                foreach (var line in ReadLines(PersistentFile))
                {
                    var fields = line.Split('|');   //拆分结果
                    var name = fields.Length > 0 ? fields[0].Trim() : "";
                    var savedIp = fields.Length > 1 ? fields[1].Trim() : "";

                    if (!string.Equals(name, player, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // ip 传空 = 不校验 IP(控制台/历史数据兼容),正常登录都带 IP
                    if (ip.Length == 0 || string.Equals(savedIp, ip, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[登录] 读持久登录表失败: " + ex.Message);
            }

            return false;
        }
    }

    /// <summary>写一条登录日志(带年月日)。</summary>
    public static void Log(string text)
    {
        lock (_lock)
        {
            try
            {
                AppendLine(LogFile, $"{DateTime.Now:yyyy/MM/dd HH:mm:ss} {text}");
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[登录] 写登录日志失败: " + ex.Message);
            }
        }
    }

    /// <summary>读绑定表(读过就缓存)。每行 <c>账号ID=玩家名</c>。</summary>
    private static Dictionary<int, string> LoadBinds()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        var map = new Dictionary<int, string>();
        try
        {
            if (File.Exists(BindFile))
            {
                foreach (var line in ReadLines(BindFile))
                {
                    var parts = line.Split('=', 2);   //拆分结果
                    if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out var id) && id > 0 &&
                        parts[1].Trim().Length > 0)
                    {
                        map[id] = parts[1].Trim();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[登录] 读绑定表失败,当作空的: " + ex.Message);
        }

        _cache = map;
        return map;
    }

    /// <summary>落盘绑定表。</summary>
    private static void SaveBinds(Dictionary<int, string> map)
    {
        try
        {
            var dir = Path.GetDirectoryName(BindFile);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var lines = map.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}");
            WriteLines(BindFile, lines);
            _cache = map;
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[登录] 写绑定表失败: " + ex.Message);
        }
    }
}