using System;  // 基础类型
using System.Collections.Generic;  // 列表
using System.IO;  // 读写文件
using System.Linq;  // 查询
using System.Text;  // UTF-8
using System.Threading;  // AsyncLocal:当前执行流的数据分区
using ZincXManagerShared.Logging;  // 共享库

namespace ZincXManagerShared.Login;

/// <summary>
/// 登录/绑定相关的东西都存在这里:
/// <list type="bullet">
///   <item><c>Data/BindUser.txt</c> —— 绑定关系,一行一条 <c>账号ID=MC玩家名</c>(一个账号可以有多行)</item>
///   <item><c>PersistentLogin.txt</c> —— 持久登录表,一行一条 <c>玩家名|玩家IP|时间</c></item>
///   <item><c>loginlog.log</c> —— 登录日志,带年月日</item>
/// </list>
/// <para>多服数据分离时,绑定表按 <c>scope</c> 分文件:<c>Data/SS-2/BindUser.txt</c>;
/// 其余两项是登录态,与哪台 MC 服无关,始终只有一份。</para>
/// </summary>
public static class LoginStore
{
    /// <summary>共享区的绑定表文件。分区文件用 <see cref="BindFileOf"/> 拼。</summary>
    public const string BindFile = "Data/BindUser.txt";

    /// <summary>持久登录表文件。</summary>
    public const string PersistentFile = "PersistentLogin.txt";

    /// <summary>登录日志文件。</summary>
    public const string LogFile = "loginlog.log";

    // 保护文件读写
    private static readonly object _lock = new();

    // 绑定表缓存(scope → 账号ID → 玩家名)。scope 为空 = 共享区,非空 = 那台服的分区(SS-<id>)
    private static readonly Dictionary<string, Dictionary<int, List<string>>> _cache = new();

    /// <summary>
    /// 当前执行流的数据分区。ZXMS 处理某个 ZXMM 推来的进服事件时设一次,
    /// 方法内部就不用给每个 <see cref="LoginStore"/> 调用都补 scope 参数;
    /// 由外部显式传了 scope 的调用优先,不看这里。
    /// </summary>
    public static readonly AsyncLocal<string> CurrentScope = new();

    /// <summary>显式传了 scope 就用显式的;没传就用当前执行流设的分区。</summary>
    private static string ResolveScope(string scope)
    {
        if (scope.Length > 0)
        {
            return scope;
        }

        return CurrentScope.Value ?? "";
    }

    /// <summary>
    /// 按分区取绑定表路径。<paramref name="scope"/> 为空 = 共享区 <c>Data/BindUser.txt</c>;
    /// 非空 = <c>Data/&lt;scope&gt;/BindUser.txt</c>(多服数据分离时 scope 形如 <c>SS-2</c>)。
    /// </summary>
    public static string BindFileOf(string scope)
    {
        return scope.Length == 0 ? BindFile : Path.Combine("Data", scope, "BindUser.txt");
    }

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

    /// <summary>这个账号绑定的第一个玩家名(没绑返回空)。多绑时要全部,用 <see cref="PlayersOf"/>。</summary>
    /// <param name="accountId">账号 ID。</param>
    /// <param name="scope">数据分区,空 = 共享区。</param>
    public static string PlayerOf(int accountId, string scope = "")
    {
        lock (_lock)
        {
            return LoadBinds(scope).TryGetValue(accountId, out var players) && players.Count > 0 ? players[0] : "";
        }
    }

    /// <summary>这个账号名下的全部玩家名(按绑定先后;没绑返回空列表)。</summary>
    /// <param name="accountId">账号 ID。</param>
    /// <param name="scope">数据分区,空 = 共享区。</param>
    public static List<string> PlayersOf(int accountId, string scope = "")
    {
        lock (_lock)
        {
            return LoadBinds(scope).TryGetValue(accountId, out var players)
                ? new List<string>(players)
                : new List<string>();
        }
    }

    /// <summary>这个账号名下有几个玩家。</summary>
    /// <param name="accountId">账号 ID。</param>
    /// <param name="scope">数据分区,空 = 共享区。</param>
    public static int BindCount(int accountId, string scope = "")
    {
        lock (_lock)
        {
            return LoadBinds(scope).TryGetValue(accountId, out var players) ? players.Count : 0;
        }
    }

    /// <summary>玩家绑在哪个账号上(没绑返回 0)。一个玩家始终只归一个账号,多绑只发生在账号这一侧。</summary>
    /// <param name="player">MC 玩家名。</param>
    /// <param name="scope">数据分区,空 = 共享区。</param>
    public static int AccountOf(string player, string scope = "")
    {
        lock (_lock)
        {
            foreach (var pair in LoadBinds(scope))
            {
                foreach (var name in pair.Value)
                {
                    if (string.Equals(name, player, StringComparison.OrdinalIgnoreCase))
                    {
                        return pair.Key;
                    }
                }
            }

            return 0;
        }
    }

    /// <summary>
    /// 绑定:把一个玩家加到账号名下。
    /// <para>一个玩家始终只归一个账号;一个账号能挂几个由 <paramref name="maxBind"/> 定(&lt;= 0 表示不限)。</para>
    /// <para>已经绑在同一个账号上算成功(重复提交同一个码不会报错)。</para>
    /// </summary>
    /// <param name="accountId">账号 ID。</param>
    /// <param name="player">MC 玩家名。</param>
    /// <param name="maxBind">这个账号最多能绑几个玩家,<c>&lt;= 0</c> = 不限。</param>
    /// <param name="error">失败原因(成功时是空串)。</param>
    /// <param name="scope">数据分区,空 = 共享区。</param>
    public static bool Bind(int accountId, string player, int maxBind, out string error, string scope = "")
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
            var binds = LoadBinds(scope);   //全部绑定

            // 玩家已经被别的账号绑了
            foreach (var pair in binds)
            {
                if (pair.Key == accountId)
                {
                    continue;
                }

                if (pair.Value.Any(name => string.Equals(name, player, StringComparison.OrdinalIgnoreCase)))
                {
                    error = $"这个玩家已经被账号 {pair.Key} 绑定了";
                    return false;
                }
            }

            if (!binds.TryGetValue(accountId, out var mine))
            {
                mine = new List<string>();
            }

            // 已经挂在这个账号下:重复绑定当成成功,不动顺序
            if (mine.Any(name => string.Equals(name, player, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // 上限只管"新挂一个":已经绑满就得先解绑,不允许悄悄顶掉旧的
            if (maxBind > 0 && mine.Count >= maxBind)
            {
                error = mine.Count == 1
                    ? $"这个账号已经绑定了玩家 {mine[0]},上限 {maxBind} 个;要绑新的请先解绑 {mine[0]}"
                    : $"这个账号已经绑定了 {mine.Count} 个玩家,上限 {maxBind} 个;要绑新的请先解绑一个";
                return false;
            }

            var added = new List<string>(mine) { player };
            binds[accountId] = added;
            SaveBinds(scope, binds);
            Log($"[绑定] 账号 {accountId} 绑定了玩家 {player}(现在 {added.Count} 个){(scope.Length == 0 ? "" : ",数据分区 " + scope)}");
            return true;
        }
    }

    /// <summary>
    /// 先问一句"现在能不能再绑这个玩家",不写盘。返回空串 = 能绑,否则是给用户看的原因。
    /// <para>已绑在同一个账号下的玩家算能绑(重复提交同一个码不报错)。</para>
    /// </summary>
    /// <param name="accountId">账号 ID。</param>
    /// <param name="player">MC 玩家名。</param>
    /// <param name="maxBind">这个账号最多能绑几个玩家,<c>&lt;= 0</c> = 不限。</param>
    /// <param name="scope">数据分区,空 = 共享区。</param>
    public static string CheckBindLimit(int accountId, string player, int maxBind, string scope = "")
    {
        player = player.Trim();
        if (accountId <= 0 || player.Length == 0)
        {
            return "账号或玩家名不合法";
        }

        lock (_lock)
        {
            var binds = LoadBinds(scope);   //全部绑定

            foreach (var pair in binds)
            {
                if (pair.Key == accountId)
                {
                    continue;
                }

                if (pair.Value.Any(name => string.Equals(name, player, StringComparison.OrdinalIgnoreCase)))
                {
                    return $"这个玩家已经被账号 {pair.Key} 绑定了";
                }
            }

            if (!binds.TryGetValue(accountId, out var mine) || mine.Count == 0)
            {
                return "";
            }

            if (mine.Any(name => string.Equals(name, player, StringComparison.OrdinalIgnoreCase)))
            {
                return "";
            }

            if (maxBind > 0 && mine.Count >= maxBind)
            {
                return mine.Count == 1
                    ? $"这个账号已经绑定了玩家 {mine[0]},上限 {maxBind} 个;要绑新的请先解绑 {mine[0]}"
                    : $"这个账号已经绑定了 {mine.Count} 个玩家,上限 {maxBind} 个;要绑新的请先解绑一个";
            }

            return "";
        }
    }

    /// <summary>解绑一个指定的玩家。<paramref name="removed"/> 是真正被解掉的玩家名。</summary>
    /// <param name="accountId">账号 ID。</param>
    /// <param name="player">MC 玩家名。</param>
    /// <param name="removed">真正被解掉的玩家名(失败时空串)。</param>
    /// <param name="scope">数据分区,空 = 共享区。</param>
    public static bool Unbind(int accountId, string player, out string removed, string scope = "")
    {
        removed = "";
        player = player.Trim();
        if (accountId <= 0 || player.Length == 0)
        {
            return false;
        }

        lock (_lock)
        {
            var binds = LoadBinds(scope);   //全部绑定
            if (!binds.TryGetValue(accountId, out var mine))
            {
                return false;
            }

            var at = mine.FindIndex(name => string.Equals(name, player, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                return false;
            }

            removed = mine[at];
            mine.RemoveAt(at);
            if (mine.Count == 0)
            {
                binds.Remove(accountId);
            }

            SaveBinds(scope, binds);
            Log($"[解绑] 账号 {accountId} 解绑了玩家 {removed}(还剩 {mine.Count} 个){(scope.Length == 0 ? "" : ",数据分区 " + scope)}");
            return true;
        }
    }

    /// <summary>解绑这个账号名下的全部玩家(老客户端的 628 负载是空的,按这条走)。</summary>
    /// <param name="accountId">账号 ID。</param>
    /// <param name="scope">数据分区,空 = 共享区。</param>
    public static List<string> UnbindAll(int accountId, string scope = "")
    {
        var removed = new List<string>();
        if (accountId <= 0)
        {
            return removed;
        }

        lock (_lock)
        {
            var binds = LoadBinds(scope);   //全部绑定
            if (!binds.TryGetValue(accountId, out var mine) || mine.Count == 0)
            {
                return removed;
            }

            removed.AddRange(mine);
            binds.Remove(accountId);
            SaveBinds(scope, binds);
            Log($"[解绑] 账号 {accountId} 解绑了全部玩家 {string.Join(" , ", removed)}{(scope.Length == 0 ? "" : ",数据分区 " + scope)}");
        }

        return removed;
    }

    /// <summary>全部绑定:一个玩家一行(同一个账号有多个玩家时会出现多行)。</summary>
    /// <param name="scope">数据分区,空 = 共享区。</param>
    public static List<KeyValuePair<int, string>> AllBinds(string scope = "")
    {
        lock (_lock)
        {
            var rows = new List<KeyValuePair<int, string>>();
            foreach (var pair in LoadBinds(scope).OrderBy(p => p.Key))
            {
                foreach (var name in pair.Value)
                {
                    rows.Add(new KeyValuePair<int, string>(pair.Key, name));
                }
            }

            return rows;
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
    /// <param name="player">玩家名,大小写不敏感。</param>
    /// <param name="ip">本次连接的 IP;传空串 = 不校验 IP(老数据兼容)。</param>
    /// <param name="maxAgeDays">免确认有效期(天),0 = 不限期;超期的记录当作没有。</param>
    public static bool HasPersistentLogin(string player, string ip, int maxAgeDays = 0)
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
                        if (Expired(fields.Length > 2 ? fields[2].Trim() : "", maxAgeDays))
                        {
                            continue;
                        }

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

    /// <summary>这条记录的确认时间是不是已经超过有效期。</summary>
    /// <remarks>没有时间字段的老记录一律当永不过期 —— 升个版本不该把所有人重新踢去确认。</remarks>
    private static bool Expired(string savedAt, int maxAgeDays)
    {
        if (maxAgeDays <= 0 || savedAt.Length == 0)
        {
            return false;
        }

        return DateTime.TryParse(savedAt, out var when) && DateTime.Now - when > TimeSpan.FromDays(maxAgeDays);
    }

    /// <summary>清空持久登录表:所有人下次进服都要重新确认一次。</summary>
    public static void ClearPersistentLogins()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(PersistentFile))
                {
                    return;
                }

                var count = ReadLines(PersistentFile).Count(line => line.Trim().Length > 0);
                WriteLines(PersistentFile, new List<string>());
                Log($"[免确认] 已清空持久登录表({count} 条),之后的进服都要重新确认");
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[登录] 清空持久登录表失败: " + ex.Message);
            }
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

    /// <summary>读绑定表(读过就缓存)。每行 <c>账号ID=玩家名</c>,同一账号出现多行就是绑了多个玩家。</summary>
    /// <param name="scope">数据分区,空 = 共享区。</param>
    private static Dictionary<int, List<string>> LoadBinds(string scope)
    {
        scope = ResolveScope(scope);
        if (_cache.TryGetValue(scope, out var cached))
        {
            return cached;
        }

        var map = new Dictionary<int, List<string>>();
        var file = BindFileOf(scope);
        try
        {
            if (File.Exists(file))
            {
                foreach (var line in ReadLines(file))
                {
                    var parts = line.Split('=', 2);   //拆分结果
                    if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out var id) || id <= 0)
                    {
                        continue;
                    }

                    var player = parts[1].Trim();
                    if (player.Length == 0)
                    {
                        continue;
                    }

                    if (!map.TryGetValue(id, out var players))
                    {
                        players = new List<string>();
                        map[id] = players;
                    }

                    // 手工编辑时同一账号下可能写重复,只留一份;顺序按文件里的先后
                    if (!players.Any(name => string.Equals(name, player, StringComparison.OrdinalIgnoreCase)))
                    {
                        players.Add(player);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[登录] 读绑定表失败,当作空的: " + ex.Message);
        }

        _cache[scope] = map;
        return map;
    }

    /// <summary>落盘绑定表(一个玩家一行,同一账号的多行挨在一起)。</summary>
    /// <param name="scope">数据分区,空 = 共享区。</param>
    /// <param name="map">账号 ID → 玩家名列表。</param>
    private static void SaveBinds(string scope, Dictionary<int, List<string>> map)
    {
        scope = ResolveScope(scope);
        try
        {
            var file = BindFileOf(scope);
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var lines = map.OrderBy(p => p.Key)
                .SelectMany(p => p.Value.Select(player => $"{p.Key}={player}"));
            WriteLines(file, lines);
            _cache[scope] = map;
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[登录] 写绑定表失败: " + ex.Message);
        }
    }
}
