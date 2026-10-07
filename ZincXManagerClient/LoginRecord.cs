using System;

namespace ZincXManagerClient;

/// <summary>
/// 登录记录:记住账号密码 + 7 天内免登录。存进 config.ini。
///
/// <para>记住的密码是**密文**(和登录时发给服务端的是同一串),文件里没有明文。</para>
/// </summary>
internal static class LoginRecord
{
    /// <summary>免登录有效期(天)。</summary>
    public const int RememberDays = 7;

    /// <summary>上次登录的账号名。</summary>
    public static string User => ClientConfig.GetOrEmpty("lastuser");

    /// <summary>上次登录用的密码密文(没勾"记住密码"就是空)。</summary>
    public static string Hash => ClientConfig.GetOrEmpty("lasthash");

    /// <summary>上次登录的权限等级。</summary>
    public static string Level
    {
        get
        {
            var level = ClientConfig.GetOrEmpty("lastlevel");
            return level.Length > 0 ? level : "Player";
        }
    }

    /// <summary>是否勾了"7 天内免登录"。</summary>
    public static bool Remember => ClientConfig.GetOrEmpty("remember") == "1";

    /// <summary>是否勾了"记住账号密码"。</summary>
    public static bool SavePassword => ClientConfig.GetOrEmpty("savepassword") == "1";

    /// <summary>记录还剩几天(0 = 没有 / 已过期)。</summary>
    public static int DaysLeft()
    {
        if (!DateTime.TryParse(ClientConfig.GetOrEmpty("lastlogin"), out var time))
        {
            return 0;
        }

        var left = RememberDays - (int)(DateTime.Now - time).TotalDays;
        return left > 0 ? left : 0;
    }

    /// <summary>能不能免登录:勾了记 7 天 + 勾了记住密码 + 有账号密文 + 没过期。</summary>
    public static bool CanAutoLogin()
        => Remember && SavePassword && User.Length > 0 && Hash.Length > 0 && DaysLeft() > 0;

    /// <summary>登录成功后写记录。</summary>
    public static void Save(string user, string hash, string level, bool remember, bool savePassword)
    {
        ClientConfig.Set("lastuser", user);
        ClientConfig.Set("lastlevel", level);
        ClientConfig.Set("remember", remember ? "1" : "0");
        ClientConfig.Set("savepassword", savePassword ? "1" : "0");
        ClientConfig.Set("lasthash", savePassword ? hash : "");
        ClientConfig.Set("lastlogin", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    /// <summary>退出登录:清掉免登录记录(账号名留着,方便下次填)。</summary>
    public static void Clear()
    {
        ClientConfig.Set("lasthash", "");
        ClientConfig.Set("remember", "0");
        ClientConfig.Set("savepassword", "0");
    }
}
