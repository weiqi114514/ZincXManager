namespace ZincXManagerShared.Account;

/// <summary>
/// 头像存取:一个账号一张图,存成 <c>avatars/账号名.png</c>。
///
/// <para>服务端用它存账号头像;客户端用同一份把头像缓存到本地,
/// 这样登录界面在"还没登录"时也能按账号名把上次的头像显示出来。</para>
/// </summary>
public static class AvatarStore
{
    /// <summary>头像大小上限(512 KB),超了直接拒绝,免得撑爆协议负载和磁盘。</summary>
    public const int MaxBytes = 512 * 1024;

    /// <summary>头像目录(相对当前工作目录);服务端与客户端各用各的。</summary>
    public static string Directory { get; set; } = "avatars";

    /// <summary>某个账号的头像文件路径。</summary>
    public static string PathOf(string user)
    {
        var name = SafeName(user);
        return Path.Combine(Directory, name + ".png");
    }

    /// <summary>读头像;没有或读失败就返回空数组。</summary>
    public static byte[] Load(string user)
    {
        try
        {
            var path = PathOf(user);
            return File.Exists(path) ? File.ReadAllBytes(path) : [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>存头像(覆盖);数据不合法时返回 false 并给出原因。</summary>
    public static bool Save(string user, byte[] data, out string error)
    {
        if (SafeName(user).Length < 2)
        {
            error = "账号名不合法";
            return false;
        }

        if (data.Length == 0)
        {
            error = "图片是空的";
            return false;
        }

        if (data.Length > MaxBytes)
        {
            error = $"图片太大(上限 {MaxBytes / 1024} KB)";
            return false;
        }

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllBytes(PathOf(user), data);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>删头像;本来就没有也算成功。</summary>
    public static bool Delete(string user)
    {
        try
        {
            var path = PathOf(user);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把账号名洗成安全的文件名(去掉路径分隔符等)。</summary>
    private static string SafeName(string user)
    {
        var name = (user ?? "").Trim();
        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(bad, '_');
        }

        return name.Replace("..", "_");
    }
}
