using System.Globalization;   // CultureInfo:时间戳格式

namespace ZincXManagerShared.Friend;

/// <summary>一条聊天记录。</summary>
/// <param name="Seq">这条在这段对话里的序号(从 1 开始,等于文件里的行号)。</param>
/// <param name="Time">发生时间。</param>
/// <param name="Incoming">true = 对方发来的,false = 自己发出去的。</param>
/// <param name="Text">正文。</param>
public sealed record ChatLine(int Seq, DateTime Time, bool Incoming, string Text);

/// <summary>
/// 聊天记录的本地存档:<c>User/Friend/&lt;本服ID&gt;/&lt;账号编号&gt;/Ch/&lt;对方完整ID&gt;.dat</c>,
/// 一行一条 <c>时间\tin|out\t正文</c>。
///
/// <para>序号不落盘 —— 它就是行号。所以这个文件只许往后追加,不许删中间的行,
/// 否则序号会和客户端手里那份对不上。</para>
///
/// <para>按账号分目录存 —— 同一台机器上多个账号的私聊不会串。文件名用对方完整ID,不用名字:
/// 名字会改,而且跨服会重名。</para>
///
/// <para>服务端存「这台服替玩家留的」;客户端存「我本机看得到的副本」,
/// 客户端拿不到自己的服ID,所以它的目录会落在 <c>User/Friend/Local/&lt;账号编号&gt;/Ch/</c>。</para>
/// </summary>
public static class ChatLog
{
    /// <summary>聊天记录的目录名(账号目录下的子目录)。</summary>
    public const string DirectoryName = "Ch";

    /// <summary>时间格式:固定 <c>yyyy-MM-dd HH:mm:ss</c>,排序按字符串也是对的。</summary>
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>同一个文件可能被「发消息」和「收消息」同时写,追加时串行排一下队。</summary>
    private static readonly object Gate = new();

    /// <summary>某个账号与某个人的对话存档目录。</summary>
    public static string DirectoryOf(PlayerId owner) =>
        Path.Combine(FriendStore.DirectoryOf(owner), DirectoryName);

    /// <summary>某个账号与某个人的对话存档路径。</summary>
    public static string PathOf(PlayerId owner, string peerFullId) =>
        Path.Combine(DirectoryOf(owner), SafeName(peerFullId) + ".dat");

    /// <summary>这段对话已经存到第几号(没有文件 = 0)。</summary>
    public static int LastSeq(PlayerId owner, string peerFullId)
    {
        var path = PathOf(owner, peerFullId);
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            return File.ReadAllLines(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>给 <paramref name="owner"/> 追加一条,成功时 <paramref name="seq"/> 是它分到的序号。</summary>
    public static bool Append(PlayerId owner, string peerFullId, bool incoming, string text, DateTime time, out int seq, out string error)
    {
        seq = 0;
        error = "";
        var path = PathOf(owner, peerFullId);
        try
        {
            System.IO.Directory.CreateDirectory(DirectoryOf(owner));
            var line = string.Join(
                FriendCodec.FieldSeparator,
                time.ToString(TimeFormat, CultureInfo.InvariantCulture),
                incoming ? "in" : "out",
                Flatten(text));

            lock (Gate)
            {
                // 行号就是序号:先数一遍现有行,再追加,两边在同一把锁里,不会算重
                seq = File.Exists(path) ? File.ReadAllLines(path).Length + 1 : 1;
                File.AppendAllText(path, line + "\n");
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            seq = 0;
            return false;
        }
    }

    /// <summary>
    /// 读序号大于 <paramref name="afterSeq"/> 的若干条(<paramref name="maxLines"/> &lt;= 0 = 全读)。
    /// 从头要就传 0。
    /// </summary>
    public static List<ChatLine> Read(PlayerId owner, string peerFullId, int afterSeq, int maxLines, out string error)
    {
        error = "";
        var path = PathOf(owner, peerFullId);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var lines = File.ReadAllLines(path);
            var bad = 0;
            var result = new List<ChatLine>();
            for (var index = 0; index < lines.Length; index++)
            {
                var raw = lines[index];
                if (raw.Length == 0)
                {
                    continue;
                }

                var seq = index + 1;
                var parts = raw.Split(FriendCodec.FieldSeparator, 3);
                if (parts.Length < 3 || !DateTime.TryParseExact(parts[0], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                {
                    bad++;
                    continue;
                }

                if (seq <= afterSeq)
                {
                    continue;
                }

                result.Add(new ChatLine(seq, time, parts[1] == "in", parts[2]));
            }

            if (bad > 0)
            {
                error = $"聊天记录有 {bad} 行格式不对,已跳过";
            }

            if (maxLines > 0 && result.Count > maxLines)
            {
                result = result.Skip(result.Count - maxLines).ToList();
            }

            return result;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return [];
        }
    }

    /// <summary>正文压成单行(换行与制表符换成空格),保证一行一条。</summary>
    private static string Flatten(string text) =>
        (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace(FriendCodec.FieldSeparator, ' ');

    /// <summary>把完整ID洗成安全的文件名。</summary>
    private static string SafeName(string peerFullId)
    {
        var name = (peerFullId ?? "").Trim();
        if (name.Length == 0)
        {
            return "_";
        }

        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(bad, '_');
        }

        return name.Replace("..", "_");
    }
}
