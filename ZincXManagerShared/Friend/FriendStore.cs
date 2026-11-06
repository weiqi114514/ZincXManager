namespace ZincXManagerShared.Friend;

/// <summary>
/// 好友关系与好友申请的存取。
///
/// <para>存法:<c>User/Friend/&lt;本服ID&gt;/&lt;账号编号&gt;/Friends.dat</c> 和 <c>Requests.dat</c>。
/// 一个账号一个目录,只存「这个账号自己的」记录 —— 好友关系是双向的,
/// 所以加/删好友时由调用方对两边各写一次(见服务端 <c>FriendListHandler</c>)。
/// 这样读的时候不需要在文件名里拼两个人的信息,也不会出现「A 面有 B、B 面没 A」的半截状态无从判断。</para>
///
/// <para>这一层只做读写和查询,不做业务判断(能不能加、是不是自己、上限多少 —— 都在服务端)。</para>
/// </summary>
public static class FriendStore
{
    /// <summary>
    /// 同一个账号的档可能被不同连接同时读写(收包是按连接并发跑的)——
    /// 「读出来改一改再整份写回」这种写法不加锁会丢更新,所以所有写入口都串在这把锁上。
    /// </summary>
    private static readonly object Gate = new();

    /// <summary>根目录(相对当前工作目录)。服务端用它;客户端只写自己的聊天记录,不碰这里。</summary>
    public static string RootDirectory { get; set; } = Path.Combine("User", "Friend");

    /// <summary>好友列表文件名。</summary>
    public const string FriendsFileName = "Friends.dat";

    /// <summary>好友申请文件名。</summary>
    public const string RequestsFileName = "Requests.dat";

    /// <summary>没有归属服(直连官方注册,或者本服ID还没拿到)时用的目录名。</summary>
    public const string LocalServerDirectory = "Local";

    /// <summary>某个账号的目录:<c>User/Friend/&lt;本服ID&gt;/&lt;账号编号&gt;</c>,没有归属服时本服ID写成 <c>Local</c>。</summary>
    public static string DirectoryOf(PlayerId owner) =>
        Path.Combine(RootDirectory, owner.ServerId.Trim().Length == 0 ? LocalServerDirectory : SafePart(owner.ServerId), owner.Code.ToString());

    /// <summary>某个账号的好友列表文件。</summary>
    public static string FriendsPathOf(PlayerId owner) => Path.Combine(DirectoryOf(owner), FriendsFileName);

    /// <summary>某个账号的好友申请文件。</summary>
    public static string RequestsPathOf(PlayerId owner) => Path.Combine(DirectoryOf(owner), RequestsFileName);

    /// <summary>读好友列表;文件不存在返回空集合。读失败返回空集合并把原因写进 <paramref name="error"/>。</summary>
    public static List<FriendEntry> ReadFriends(PlayerId owner, out string error)
    {
        error = "";
        var path = FriendsPathOf(owner);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var entries = FriendCodec.ParseFriendList(File.ReadAllText(path), out var badLines);
            if (badLines > 0)
            {
                error = $"{FriendsFileName} 有 {badLines} 行格式不对,已跳过";
            }

            return entries;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return [];
        }
    }

    /// <summary>整份覆盖写好友列表。</summary>
    public static bool WriteFriends(PlayerId owner, IEnumerable<FriendEntry> entries, out string error)
    {
        error = "";
        try
        {
            Directory.CreateDirectory(DirectoryOf(owner));
            File.WriteAllText(FriendsPathOf(owner), FriendCodec.BuildFriendList(entries));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>两个人是不是好友(只看 <paramref name="owner"/> 这一侧的记录)。</summary>
    public static bool AreFriends(PlayerId owner, PlayerId other)
    {
        var entries = ReadFriends(owner, out _);
        return entries.Any(one => string.Equals(one.Id, other.Full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 给 <paramref name="owner"/> 的好友列表加一条(已存在就更新备注)。
    /// 返回 false 时 <paramref name="error"/> 是原因。
    /// </summary>
    public static bool AddFriend(PlayerId owner, string friendFullId, string remark, bool online, string serverName, out string error)
    {
        lock (Gate)
        {
            var entries = ReadFriends(owner, out var readError);
            if (readError.Length > 0)
            {
                error = readError;
                return false;
            }

            entries.RemoveAll(one => string.Equals(one.Id, friendFullId, StringComparison.OrdinalIgnoreCase));
            // 名字先留空:它是「查询时才去账号表拿」的展示信息,落盘里只认 ID 和备注
            entries.Add(new FriendEntry(friendFullId, "", remark, online, serverName));
            return WriteFriends(owner, entries, out error);
        }
    }

    /// <summary>从 <paramref name="owner"/> 的好友列表里删掉一条;本来就没有也算成功。</summary>
    public static bool RemoveFriend(PlayerId owner, string friendFullId, out string error)
    {
        lock (Gate)
        {
            var entries = ReadFriends(owner, out var readError);
            if (readError.Length > 0)
            {
                error = readError;
                return false;
            }

            var removed = entries.RemoveAll(one => string.Equals(one.Id, friendFullId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                error = "";
                return true;
            }

            return WriteFriends(owner, entries, out error);
        }
    }

    /// <summary>读好友申请。</summary>
    public static List<FriendRequestEntry> ReadRequests(PlayerId owner, out string error)
    {
        error = "";
        var path = RequestsPathOf(owner);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var entries = FriendCodec.ParseFriendRequests(File.ReadAllText(path), out var badLines);
            if (badLines > 0)
            {
                error = $"{RequestsFileName} 有 {badLines} 行格式不对,已跳过";
            }

            return entries;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return [];
        }
    }

    /// <summary>追加一条好友申请;同一个人的旧申请会被新的替换(不刷屏)。</summary>
    public static bool AppendRequest(PlayerId owner, FriendRequestEntry request, out string error)
    {
        lock (Gate)
        {
            var entries = ReadRequests(owner, out var readError);
            if (readError.Length > 0)
            {
                error = readError;
                return false;
            }

            entries.RemoveAll(one => string.Equals(one.FromId, request.FromId, StringComparison.OrdinalIgnoreCase));
            entries.Add(request);
            return WriteRequests(owner, entries, out error);
        }
    }

    /// <summary>删掉某个人发来的申请;没有也算成功。</summary>
    public static bool RemoveRequest(PlayerId owner, string fromFullId, out string error)
    {
        lock (Gate)
        {
            var entries = ReadRequests(owner, out var readError);
            if (readError.Length > 0)
            {
                error = readError;
                return false;
            }

            var removed = entries.RemoveAll(one => string.Equals(one.FromId, fromFullId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                error = "";
                return true;
            }

            return WriteRequests(owner, entries, out error);
        }
    }

    /// <summary>整份覆盖写申请列表。</summary>
    public static bool WriteRequests(PlayerId owner, IEnumerable<FriendRequestEntry> entries, out string error)
    {
        error = "";
        try
        {
            Directory.CreateDirectory(DirectoryOf(owner));
            File.WriteAllText(RequestsPathOf(owner), FriendCodec.BuildFriendRequests(entries));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 已经读到第几号(每个好友一行:<c>对方完整ID\t已读序号</c>)。
    /// 单独一个文件,不动上面两个的格式;没有记录 = 一条都没读过。
    /// </summary>
    public const string CursorsFileName = "Cursors.dat";

    /// <summary>某个账号的已读游标文件。</summary>
    public static string CursorsPathOf(PlayerId owner) => Path.Combine(DirectoryOf(owner), CursorsFileName);

    /// <summary>读某个好友的已读序号;没记录返回 0。</summary>
    public static int ReadCursor(PlayerId owner, string peerFullId)
    {
        var path = CursorsPathOf(owner);
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split(FriendCodec.FieldSeparator);
                if (parts.Length >= 2 && string.Equals(parts[0].Trim(), (peerFullId ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return int.TryParse(parts[1].Trim(), out var seq) ? seq : 0;
                }
            }
        }
        catch (IOException)
        {
            return 0;
        }

        return 0;
    }

    /// <summary>记下某个好友已经读到第几号;只往后不改小。</summary>
    public static void WriteCursor(PlayerId owner, string peerFullId, int seq)
    {
        lock (Gate)
        {
            try
            {
                var path = CursorsPathOf(owner);
                var rows = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
                var peer = (peerFullId ?? "").Trim();
                var replaced = false;
                for (var index = 0; index < rows.Count; index++)
                {
                    var parts = rows[index].Split(FriendCodec.FieldSeparator);
                    if (parts.Length < 2 || !string.Equals(parts[0].Trim(), peer, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var old = int.TryParse(parts[1].Trim(), out var value) ? value : 0;
                    rows[index] = $"{peer}{FriendCodec.FieldSeparator}{Math.Max(old, seq)}";
                    replaced = true;
                    break;
                }

                if (!replaced)
                {
                    rows.Add($"{peer}{FriendCodec.FieldSeparator}{seq}");
                }

                Directory.CreateDirectory(DirectoryOf(owner));
                File.WriteAllLines(path, rows);
            }
            catch (IOException)
            {
                // 游标丢了大不了未读数不准,不影响收发;不往上层抛
            }
        }
    }

    /// <summary>
    /// 申请好友时填的备注(每个目标一行:<c>目标完整ID\t备注</c>)。
    /// 好友关系还没建立,不能直接进 Friends.dat,先挂在这里;对方同意那一刻由服务端取走落档。
    /// </summary>
    public const string PendingRemarksFileName = "PendingRemarks.dat";

    /// <summary>最多攒多少条待生效备注;超了丢最旧的,防止对着一大堆人乱发申请把文件撑大。</summary>
    public const int PendingRemarkLimit = 32;

    /// <summary>某个账号的待生效备注文件。</summary>
    public static string PendingRemarksPathOf(PlayerId owner) => Path.Combine(DirectoryOf(owner), PendingRemarksFileName);

    /// <summary>记一条待生效备注;同一目标已有就替换,备注为空等于删掉这条。</summary>
    public static void SetPendingRemark(PlayerId owner, string targetFullId, string remark)
    {
        lock (Gate)
        {
            try
            {
                var path = PendingRemarksPathOf(owner);
                var target = (targetFullId ?? "").Trim();
                var text = (remark ?? "").Trim();
                var rows = File.Exists(path) ? File.ReadAllLines(path).Where(HasText).ToList() : [];
                rows.RemoveAll(row => PendingRemarkTargetOf(row).Equals(target, StringComparison.OrdinalIgnoreCase));
                if (text.Length > 0)
                {
                    rows.Add($"{target}{FriendCodec.FieldSeparator}{text}");
                }

                // 只留最近 PendingRemarkLimit 条,要丢就从最老的丢起
                while (rows.Count > PendingRemarkLimit)
                {
                    rows.RemoveAt(0);
                }

                // 没有要留的行时连文件一起收掉:空文件留着会被当成「有档」,也会让目录里多出无名文件
                if (rows.Count == 0)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    return;
                }

                Directory.CreateDirectory(DirectoryOf(owner));
                File.WriteAllLines(path, rows);
            }
            catch (IOException)
            {
                // 备注丢了只是显示名不个性化,不影响好友流程;不往上层抛
            }
        }
    }

    /// <summary>取走某个目标的待生效备注(读完就删,只消费一次);没有返回空串。</summary>
    public static string TakePendingRemark(PlayerId owner, string targetFullId)
    {
        lock (Gate)
        {
            try
            {
                var path = PendingRemarksPathOf(owner);
                if (!File.Exists(path))
                {
                    return "";
                }

                var target = (targetFullId ?? "").Trim();
                var rows = File.ReadAllLines(path).Where(HasText).ToList();
                var remark = "";
                var removed = rows.RemoveAll(row =>
                {
                    var hit = PendingRemarkTargetOf(row).Equals(target, StringComparison.OrdinalIgnoreCase);
                    if (hit)
                    {
                        var parts = row.Split(FriendCodec.FieldSeparator, 2);
                        remark = parts.Length > 1 ? parts[1].Trim() : "";
                    }

                    return hit;
                });

                if (removed > 0)
                {
                    if (rows.Count == 0)
                    {
                        File.Delete(path);
                    }
                    else
                    {
                        File.WriteAllLines(path, rows);
                    }
                }

                return remark;
            }
            catch (IOException)
            {
                return "";
            }
        }
    }

    /// <summary>一行待生效备注的目标 ID;行坏了给空串。</summary>
    private static string PendingRemarkTargetOf(string row)
    {
        var cut = row.IndexOf(FriendCodec.FieldSeparator);
        return cut > 0 ? row[..cut].Trim() : "";
    }

    /// <summary>这行是不是还有内容(空行不算)。</summary>
    private static bool HasText(string line) => line.Trim().Length > 0;

    /// <summary>把目录名部分洗成安全的文件夹名(服ID 里不会有非法字符,这里是兜底)。</summary>
    private static string SafePart(string text)
    {
        var name = (text ?? "").Trim();
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
