using System;  // 基础类型
using System.Collections.Generic;  // 列表
using System.Globalization;  // 数字格式化
using System.IO;  // 读写文件
using System.Text;  // UTF-8
using ZincXManagerShared.Logging;  // Logger:搬目录时记日志

namespace ZincXManagerShared.Account;

/// <summary>
/// 好友关系的落盘(<c>User/Friend/&lt;服务端ID&gt;/&lt;编码&gt;/</c>):
/// <list type="bullet">
///   <item><c>FriendList.dat</c> —— 好友列表,一行一个 <c>&lt;服务端ID&gt; &lt;编码&gt;</c></item>
///   <item><c>Requests.dat</c> —— 收到的好友申请,一行 <c>&lt;申请者完整ID&gt;\t&lt;留言&gt;\t&lt;时间&gt;</c></item>
///   <item><c>Ch/&lt;对方完整玩家ID&gt;.dat</c> —— 与该好友的聊天记录(一人一个文件)</item>
/// </list>
/// <para>文件一律 UTF-8 无 BOM + CRLF;读取时容忍空行、行首行尾空格、末行有无换行。</para>
/// </summary>
public static class FriendStore
{
    /// <summary>好友数据根目录(相对服务端根)。</summary>
    public const string RootDir = "User/Friend";

    /// <summary>好友列表文件名。</summary>
    public const string ListFile = "FriendList.dat";

    /// <summary>聊天记录目录名(在每个玩家的好友目录下)。</summary>
    public const string ChatDir = "Ch";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    // 保护文件读写
    private static readonly object _lock = new();

    /// <summary>某个玩家的好友目录:<c>User/Friend/&lt;服务端ID&gt;/&lt;编码&gt;</c>。</summary>
    public static string DirectoryOf(PlayerId owner)
    {
        var server = SafeName(owner.ServerId);
        return Path.Combine(RootDir, server, owner.Code.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 账号编码变了(本地编号对齐官方账号 ID)时,把整个好友目录搬到新编码下:
    /// 列表 / 申请 / 聊天记录都跟着走,不然等于换了一个人。
    /// </summary>
    /// <param name="serverId">服务端 ID。</param>
    /// <param name="fromId">原编码。</param>
    /// <param name="toId">新编码。</param>
    /// <returns>搬好了、或者原本就没有老数据,返回 true;目标目录已经有数据则返回 false(不覆盖)。</returns>
    public static bool MoveAccountDir(string serverId, int fromId, int toId)
    {
        if (fromId <= 0 || toId <= 0 || fromId == toId)
        {
            return true;
        }

        var from = DirectoryOf(new PlayerId(serverId, fromId));   //老编码目录
        var to = DirectoryOf(new PlayerId(serverId, toId));       //新编码目录

        lock (_lock)
        {
            if (!Directory.Exists(from))
            {
                return true;   //本来就没好友数据
            }

            if (Directory.Exists(to))
            {
                Logger.Log(LogLevel.Warning, $"[好友] 编码 {serverId}-{fromId} 要改成 {toId},但 {to} 已经有数据,没搬");
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Directory.Move(from, to);
                Logger.Log(LogLevel.Info, $"[好友] 编码 {serverId}-{fromId} 改成 {toId},好友目录已搬过去");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, $"[好友] 搬好友目录失败({serverId}-{fromId} → {toId}):{ex.Message}");
                return false;
            }
        }
    }

    /// <summary>某个玩家的好友列表文件路径。</summary>
    public static string ListPathOf(PlayerId owner) => Path.Combine(DirectoryOf(owner), ListFile);

    /// <summary>好友申请文件名(放在被申请人的目录下)。</summary>
    public const string RequestFile = "Requests.dat";

    /// <summary>好友申请文件路径。</summary>
    public static string RequestPathOf(PlayerId owner) => Path.Combine(DirectoryOf(owner), RequestFile);

    /// <summary>一条好友申请:谁发的、留言、什么时候。</summary>
    public readonly record struct FriendRequest(PlayerId From, string Message, DateTime At);

    /// <summary>与某个好友的聊天记录文件路径:<c>Ch/&lt;对方完整玩家ID&gt;.dat</c>。</summary>
    public static string ChatPathOf(PlayerId owner, PlayerId friend)
        => Path.Combine(DirectoryOf(owner), ChatDir, SafeName(friend.Full) + ".dat");

    /// <summary>读好友列表;文件不存在时返回空列表(不算错误)。</summary>
    public static List<PlayerId> ReadList(PlayerId owner)
    {
        var friends = new List<PlayerId>();
        var path = ListPathOf(owner);
        if (!File.Exists(path))
        {
            return friends;
        }

        lock (_lock)
        {
            foreach (var line in ReadLines(path))
            {
                var text = line.Trim();
                if (text.Length == 0 || text.StartsWith('#'))
                {
                    continue;
                }

                if (TryParseLine(text, out var friend) && friend != owner && !friends.Contains(friend))
                {
                    friends.Add(friend);
                }
            }
        }

        return friends;
    }

    /// <summary>整份写回好友列表(自动建目录)。</summary>
    public static void WriteList(PlayerId owner, IEnumerable<PlayerId> friends)
    {
        var dir = DirectoryOf(owner);
        var path = Path.Combine(dir, ListFile);
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var friend in friends)
        {
            if (friend == owner || !seen.Add(friend.Full))
            {
                continue;
            }

            lines.Add($"{friend.ServerId} {friend.Code.ToString(CultureInfo.InvariantCulture)}");
        }

        lock (_lock)
        {
            Directory.CreateDirectory(dir);
            var text = string.Join("\r\n", lines);
            if (lines.Count > 0)
            {
                text += "\r\n";
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, text, Utf8NoBom);
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
    }

    /// <summary>加好友;已在列表里返回 false。</summary>
    public static bool Add(PlayerId owner, PlayerId friend)
    {
        if (friend == owner)
        {
            return false;
        }

        var list = ReadList(owner);
        if (list.Contains(friend))
        {
            return false;
        }

        list.Add(friend);
        WriteList(owner, list);
        return true;
    }

    /// <summary>删好友;本来就没有返回 false。</summary>
    public static bool Remove(PlayerId owner, PlayerId friend)
    {
        var list = ReadList(owner);
        if (!list.Remove(friend))
        {
            return false;
        }

        WriteList(owner, list);
        return true;
    }

    /// <summary><paramref name="left"/> 与 <paramref name="right"/> 是否互为好友。</summary>
    public static bool AreFriends(PlayerId left, PlayerId right)
        => ReadList(left).Contains(right);

    /// <summary>读一个文件的全部行(允许别人同时写)。</summary>
    /// <summary>读收到的好友申请;文件不存在返回空列表。</summary>
    public static List<FriendRequest> ReadRequests(PlayerId owner)
    {
        var list = new List<FriendRequest>();
        var path = RequestPathOf(owner);
        if (!File.Exists(path))
        {
            return list;
        }

        lock (_lock)
        {
            foreach (var line in ReadLines(path))
            {
                var parts = line.Split('\t');
                if (parts.Length < 2 || !PlayerId.TryParse(parts[0].Trim(), out var from))
                {
                    continue;
                }

                var at = parts.Length > 2 &&
                         DateTime.TryParse(parts[2].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed
                    : DateTime.MinValue;
                list.Add(new FriendRequest(from, parts[1].Trim(), at));
            }
        }

        return list;
    }

    /// <summary>整份写回好友申请(内部先排序去重)。</summary>
    public static void WriteRequests(PlayerId owner, IEnumerable<FriendRequest> requests)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(DirectoryOf(owner));

            var text = new StringBuilder();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in requests)
            {
                if (!seen.Add(request.From.Full))
                {
                    continue;
                }

                text.Append(request.From.Full).Append('\t')
                    .Append(Clean(request.Message)).Append('\t')
                    .Append(request.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                    .Append("\r\n");
            }

            var path = RequestPathOf(owner);
            var temp = path + ".tmp";
            File.WriteAllText(temp, text.ToString(), Utf8NoBom);
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
    }

    /// <summary>记一条申请:同一个人重复申请就更新留言与时间,返回 true 表示这是新申请。</summary>
    public static bool AppendRequest(PlayerId owner, FriendRequest request)
    {
        lock (_lock)
        {
            var list = ReadRequests(owner);
            var index = list.FindIndex(r => r.From.Full.Equals(request.From.Full, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                list[index] = request;
                WriteRequests(owner, list);
                return false;
            }

            list.Add(request);
            WriteRequests(owner, list);
            return true;
        }
    }

    /// <summary>删掉某个人的申请(同意或拒绝后调用);没有这条返回 false。</summary>
    public static bool RemoveRequest(PlayerId owner, PlayerId from)
    {
        lock (_lock)
        {
            var list = ReadRequests(owner);
            var removed = list.RemoveAll(r => r.From.Full.Equals(from.Full, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                WriteRequests(owner, list);
            }

            return removed;
        }
    }

    /// <summary>字段里不许出现 Tab 与换行(一行一条记录)。</summary>
    private static string Clean(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>逐行读出(跳过空行与 # 注释),好友列表与申请共用。</summary>
    private static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            yield return line;
        }
    }

    /// <summary>解析一行 <c>&lt;服务端ID&gt; &lt;编码&gt;</c>。</summary>
    private static bool TryParseLine(string line, out PlayerId friend)
    {
        friend = default;
        var space = line.LastIndexOf(' ');
        if (space <= 0 || space == line.Length - 1)
        {
            return false;
        }

        var server = line[..space].Trim();
        var codeText = line[(space + 1)..].Trim();
        if (!PlayerId.IsServerId(server) ||
            !int.TryParse(codeText, NumberStyles.None, CultureInfo.InvariantCulture, out var code) ||
            code <= 0)
        {
            return false;
        }

        friend = new PlayerId(server, code);
        return true;
    }

    /// <summary>文件名里不能出现路径分隔符与非法字符。</summary>
    private static string SafeName(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}
