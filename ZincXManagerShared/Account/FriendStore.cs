using System;  // 基础类型
using System.Collections.Generic;  // 列表
using System.Globalization;  // 数字格式化
using System.IO;  // 读写文件
using System.Text;  // UTF-8

namespace ZincXManagerShared.Account;

/// <summary>
/// 好友关系的落盘(<c>User/Friend/&lt;服务端ID&gt;/&lt;编码&gt;/</c>):
/// <list type="bullet">
///   <item><c>FriendList.dat</c> —— 好友列表,一行一个 <c>&lt;服务端ID&gt; &lt;编码&gt;</c></item>
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

    /// <summary>某个玩家的好友列表文件路径。</summary>
    public static string ListPathOf(PlayerId owner) => Path.Combine(DirectoryOf(owner), ListFile);

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
