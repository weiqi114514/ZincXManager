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
            var binds = LoadBinds();

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
            var binds = LoadBinds();
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

    /// <summary>写持久登录表(玩家名 + IP + 时间)。</summary>
    public static void AddPersistentLogin(string player, string ip)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(PersistentFile,
                    $"{player}|{ip}|{DateTime.Now:yyyy/MM/dd HH:mm:ss}" + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[登录] 写持久登录表失败: " + ex.Message);
            }
        }
    }

    /// <summary>持久登录表里有没有这个玩家。</summary>
    public static bool HasPersistentLogin(string player)
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(PersistentFile))
                {
                    return false;
                }

                foreach (var line in File.ReadAllLines(PersistentFile, Encoding.UTF8))
                {
                    var name = line.Split('|')[0].Trim();
                    if (string.Equals(name, player, StringComparison.OrdinalIgnoreCase))
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
                var dir = Path.GetDirectoryName(LogFile);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy/MM/dd HH:mm:ss} {text}" + Environment.NewLine,
                    Encoding.UTF8);
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
                foreach (var line in File.ReadAllLines(BindFile, Encoding.UTF8))
                {
                    var parts = line.Split('=', 2);
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
            File.WriteAllLines(BindFile, lines, Encoding.UTF8);
            _cache = map;
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[登录] 写绑定表失败: " + ex.Message);
        }
    }
}