using System.Security.Cryptography;
using ZincXManagerShared.Logging;

namespace ZincXManagerShared.Verify;

/// <summary>
/// 官方构建的"闸门"。**故意做成多处、各自独立校验**,而且都不返回"给外面看的详细原因":
///
/// <list type="number">
///   <item><see cref="RequireOfficial"/> —— 启动时:过不了直接退出进程(断掉"跑起来再打补丁"的路);</item>
///   <item><see cref="Allow"/> —— 每次有连接接入时再来一遍(只拒这条连接);</item>
///   <item><see cref="Pepper"/> —— 校验通过才有的 32 字节,给业务层混进握手 / 派生密钥,
///         所以"把上面两个 if 注释掉"并不能让程序正常工作。</item>
/// </list>
///
/// <para>只校验 <b>1 次</b>、只留 <b>1 个</b> bool 的写法最容易被一句 <c>return true</c> 干掉,这里刻意避开。</para>
/// </summary>
public static class OfficialGuard
{
    private static OfficialVerdict _last = new() { Ok = false, Code = "init" };

    /// <summary>上一次校验的结论(给日志/命令用)。</summary>
    public static OfficialVerdict Last => _last;

    /// <summary>
    /// 校验通过才有的 32 字节 pepper。<b>没通过时返回的是随机数</b> ——
    /// 这样"跳过校验"的一方握手/派生出来的值必然和别人对不上,不会静默地"看起来正常"。
    /// </summary>
    public static byte[] Pepper => _last.Ok ? _last.Pepper : RandomPepper();

    /// <summary>启动闸:不通过就写日志并退出进程(退出码 2)。</summary>
    public static void RequireOfficial(string? directory = null)
    {
        var verdict = OfficialLicense.Check(directory);
        _last = verdict;

        if (!verdict.Ok)
        {
            // 管理员自己要看清楚为什么,所以这里照实打;对外(协议/日志给玩家看的部分)一律只说"未通过"
            Logger.Log(LogLevel.Fatal, $"[官方验证] 未通过({verdict.Code}):{verdict.Detail}");
            Logger.Log(LogLevel.Fatal, "[官方验证] 本程序是官方发行版,没有合法的 official.lic 不提供服务");
            Environment.Exit(2);
            return;
        }

        Logger.Log(LogLevel.Info,
            $"[官方验证] 通过:{verdict.Name}(序列号 {verdict.Serial},{(verdict.Expires == DateTime.MinValue ? "永久" : $"有效期到 {verdict.Expires:yyyy-MM-dd}")})");
    }

    /// <summary>接入闸:每次有新连接进来都再校验一次(便宜,一次 RSA 验签),不通过就拒这条连接。</summary>
    public static bool Allow(string? directory = null)
    {
        var verdict = OfficialLicense.Check(directory);
        _last = verdict;

        if (!verdict.Ok)
        {
            Logger.Log(LogLevel.Error, $"[官方验证] 这条连接被拒({verdict.Code})");
            return false;
        }

        return true;
    }

    /// <summary>没通过时用的假 pepper:每次都不同,避免"没校验"反而得到一个稳定值。</summary>
    private static byte[] RandomPepper()
    {
        var seed = Guid.NewGuid().ToByteArray();
        return SHA256.HashData(seed);
    }
}
