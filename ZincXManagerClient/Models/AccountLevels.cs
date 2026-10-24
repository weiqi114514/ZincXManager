namespace ZincXManagerClient.Models;

/// <summary>
/// 登录界面用:按连接方式与"是否管理账号"算出可选的权限等级,以及这条请求该由哪一端处理。
///
/// <para>规矩(按需求):管理账号只能在服务端(user reg)注册,客户端只负责登录;
/// 玩家账号永远官方优先 —— OOS 直连官方,OS 经 ZXMS 中转(官方没连就本端),
/// SAOS 必须经 ZXMS 中转,不能绕过。</para>
/// </summary>
public static class AccountLevels
{
    /// <summary>玩家账号:只有 Player。</summary>
    public static readonly string[] Player = ["Player"];

    /// <summary>ZXMS 的管理等级。</summary>
    public static readonly string[] ZxmsAdmin = ["LAdmin", "HAdmin", "System"];

    /// <summary>ZXMOS 的管理等级。</summary>
    public static readonly string[] ZxmosAdmin = ["Admin", "System"];

    /// <summary>按模式 / 是否管理账号 / SAOS 下选的端,给出下拉里能选的等级。</summary>
    public static string[] For(string mode, bool isAdmin, string adminSide)
    {
        if (!isAdmin)
        {
            return Player;
        }

        return mode switch
        {
            "OOS" => ZxmosAdmin,
            "OS" => ZxmsAdmin,
            _ => adminSide == "ZXMOS" ? ZxmosAdmin : ZxmsAdmin,
        };
    }

    /// <summary>这条账号请求交给哪一端处理(zxms / zxmos)。</summary>
    public static string SideFor(string mode, bool isAdmin, string adminSide)
    {
        if (!isAdmin)
        {
            return "zxmos";   // 玩家账号:官方优先(ZXMS 会按连接情况决定中转还是本端)
        }

        return mode switch
        {
            "OOS" => "zxmos",
            "OS" => "zxms",
            _ => adminSide == "ZXMOS" ? "zxmos" : "zxms",
        };
    }
}