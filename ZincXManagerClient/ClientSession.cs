namespace ZincXManagerClient;

using ZincXManagerShared.Friend;   // PlayerId:本机账号地址

/// <summary>当前会话:持有连接视图模型,保证"连接界面关了 / 切到主界面"时连接不被回收、不被关掉。 以后要做主界面收服务端推送,也从这里拿连接。</summary>
internal static class ClientSession
{
    /// <summary>当前连接;没连过就是 null。</summary>
    public static ViewModels.ConnectViewModel? Connect { get; set; }

    /// <summary>当前登录的账号名(没登录就是空)。</summary>
    public static string User { get; set; } = "";

    /// <summary>当前登录账号的数字 ID。</summary>
    public static string UserId { get; set; } = "";

    /// <summary>
    /// 当前账号的完整 ID(形如 <c>ZXMS-子服名-1-14</c>),别人拿这个搜得到我。
    /// 由 601/603 的第五段下发;老服务端不给时是空串,界面退回只显示数字 ID。
    /// </summary>
    public static string FullId { get; set; } = "";

    /// <summary>当前权限等级。</summary>
    public static string Level { get; set; } = "Player";

    /// <summary>登录成功后缓存的密码密文(断线重连时自动重登用;只放内存,不落盘)。</summary>
    public static string LoginHash { get; set; } = "";

    /// <summary>登录时选的等级(重登要原样带上)。</summary>
    public static string LoginLevel { get; set; } = "";

    /// <summary>登录时选的归属端(zxms / zxmos)。</summary>
    public static string LoginSide { get; set; } = "";

    /// <summary>
    /// 本机账号的地址。客户端不知道自己在子服上的完整ID(那是服务端内部的事),
    /// 所以服ID留空 —— 聊天记录副本会落在 <c>User/Friend/Local/&lt;账号编号&gt;/Ch/</c>。
    /// </summary>
    public static PlayerId SelfId => new("", int.TryParse(UserId, out var code) ? code : 0);

    /// <summary>启动时先把上次登录的账号填进会话,免得"7 天内免登录"握手还没完成的那几秒, 主界面左下角显示成「未登录」。</summary>
    public static void SeedFromConfig()
    {
        var user = ClientConfig.GetOrEmpty("user").Trim();
        if (user.Length == 0)
        {
            return;
        }

        User = user;

        var id = ClientConfig.GetOrEmpty("userid").Trim();
        if (id.Length > 0)
        {
            UserId = id;
        }

        var level = ClientConfig.GetOrEmpty("level").Trim();
        if (level.Length > 0)
        {
            Level = level;
        }

        var full = ClientConfig.GetOrEmpty("fullid").Trim();
        if (full.Length > 0)
        {
            FullId = full;
        }
    }
}
