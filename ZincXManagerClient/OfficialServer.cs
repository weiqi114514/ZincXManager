using System;

namespace ZincXManagerClient;

/// <summary>
/// 官方服务端(ZXMOS)的默认地址 —— 只在**客户端这边**写死:<c>zxm.zincms.top:25000</c>。
///
/// <para>界面上它就是「官方服务端(默认)」这个选项的值:装完不用填;要用别人的官方服务端,
/// 必须显式勾「自定义 IP:端口」并读完一段警告(见 <c>MessageDialog.ReadConfirmAsync</c>)才能改。</para>
///
/// <para>注意:<b>服务端自己</b>(ZXMOS / ZXMS)的配置不受这里影响,它们的 ozip / ozport 仍以 config.ini 为准。</para>
/// </summary>
internal static class OfficialServer
{
    /// <summary>官方服务端域名。</summary>
    public const string Host = "zxm.zincms.top";

    /// <summary>官方服务端端口。</summary>
    public const ushort Port = 25000;

    /// <summary>给人看的一行。</summary>
    public static string Display => $"{Host}:{Port}";

    /// <summary>端口文本(写进 config.ini 用)。</summary>
    public static string PortText => Port.ToString();

    /// <summary>这两个值是不是"官方默认地址"(留空也算 —— 留空就是走默认)。</summary>
    public static bool IsOfficial(string host, string port)
        => (host.Trim().Length == 0 || host.Trim().Equals(Host, StringComparison.OrdinalIgnoreCase))
           && (port.Trim().Length == 0 || port.Trim() == PortText);
}
