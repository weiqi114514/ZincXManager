namespace ZincXManagerClient;

/// <summary>
/// 当前会话:持有连接视图模型,保证"连接界面关了 / 切到主界面"时连接不被回收、不被关掉。
/// 以后要做主界面收服务端推送,也从这里拿连接。
/// </summary>
internal static class ClientSession
{
    /// <summary>当前连接;没连过就是 null。</summary>
    public static ViewModels.ConnectViewModel? Connect { get; set; }

    /// <summary>当前登录的账号名(没登录就是空)。</summary>
    public static string User { get; set; } = "";

    /// <summary>当前登录账号的数字 ID。</summary>
    public static string UserId { get; set; } = "";

    /// <summary>当前权限等级。</summary>
    public static string Level { get; set; } = "Player";
}
