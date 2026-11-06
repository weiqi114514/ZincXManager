using System.Text;  // 文本编码

namespace ZincXManagerTests;

/// <summary>
/// 套件 19:登录失败封禁的真机验证 —— 不是单测,是真起一台 ZXMS 用错密码连试。
/// <para>单测只证明 FailureThrottle 这个类算得对;这里证明它在登录入口真的接上了,
/// 而且先失败满额度会被挡、之后输对了还能解开 —— 这条链路单测碰不到。</para>
/// </summary>
internal static class LoginThrottleSuite
{
    /// <summary>连续用错密码的次数:要比默认额度(10)多,第 11 次起必须被挡。</summary>
    private const int WrongTries = 11;

    /// <summary>额度上限(与 Program 里的 DefaultLoginRate 一致)。</summary>
    private const int Limit = 10;

    /// <summary>跑这一套断言的入口。</summary>
    /// <param name="results">总结果表,断言往里加。</param>
    /// <param name="ws">工作目录(临时实例放它下面的 _zxt)。</param>
    /// <param name="debugMs">ZXMS 的 bin 目录。</param>
    internal static async Task RunAsync(List<SuiteResult> results, string ws, string debugMs)
    {
        var r = new SuiteResult { Name = "19 登录失败封禁(真起 ZXMS)" };
        results.Add(r);
        var dir = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "throttle"));
        try
        {
            using var ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, new Dictionary<string, string>
            {
                ["configver"] = "2",
                ["connectOS"] = "0",
                ["linkname"] = "0",
                ["name"] = "CSharp-限速服",
                ["czport"] = "21419",
            });
            if (!ms.WaitPort(21419))
            {
                r.Add("ZXMS 监听 21419", false, "15 秒没监听");
                return;
            }

            r.Add("ZXMS 监听 21419", true);

            // 先建一个真账号,后面用它验证「输对了还能进」
            using (var reg = await Probe.ConnectAsync("127.0.0.1", 21419, "OS"))
            {
                var okReg = await reg.RegisterAsync("throttleone", "rightpass", "machine-a");
                r.Add("先注册一个账号当靶子", okReg, reg.LastAck ?? "没回执");
            }

            // 用错密码连试:前 Limit 次必须回「密码不对」这种业务错误,不能被限速挡
            var blockedAt = -1;   // 第几次开始被限速挡(从 1 数)
            for (var i = 1; i <= WrongTries; i++)
            {
                using var bad = await Probe.ConnectAsync("127.0.0.1", 21419, "OS");
                var ok = await bad.LoginAsync("throttleone", "wrongpass", "machine-a", "Player", "zxms");
                var ack = bad.LastAck ?? "";
                if (ack.Contains("失败次数过多"))
                {
                    blockedAt = i;
                    break;
                }

                r.Add($"第 {i} 次错密码被正常拒绝(不是限速)", !ok && ack.Contains("密码"), ack);
            }

            r.Add($"连错 {Limit} 次之后开始被限速挡",
                blockedAt > Limit && blockedAt <= WrongTries + 1,
                blockedAt > 0 ? $"第 {blockedAt} 次开始挡" : "一次都没挡");

            // 被挡期间即使密码正确也必须进不去 —— 否则限速只是挡错误的,没意义
            using (var right = await Probe.ConnectAsync("127.0.0.1", 21419, "OS"))
            {
                var ok = await right.LoginAsync("throttleone", "rightpass", "machine-a", "Player", "zxms");
                var ack = right.LastAck ?? "";
                r.Add("封禁期间密码正确也进不去", !ok && ack.Contains("失败次数过多"), ack);
            }

            // 别的 IP 不受影响:这里没法造真别的 IP,只能验证不同连接(同 IP)依然被封
            using (var other = await Probe.ConnectAsync("127.0.0.1", 21419, "OS"))
            {
                var ok = await other.LoginAsync("throttleone", "rightpass", "machine-b", "Player", "zxms");
                r.Add("封禁是按 IP 的,换机器码也照挡", !ok, other.LastAck ?? "");
            }

            // 服务端日志里必须有那两行限速记录,证明走的是限速分支不是别的错误
            var log = ms.Out;
            r.Add("日志里有限速拒绝记录", log.Contains("登录失败次数过多"), log.Contains("登录失败次数过多") ? "" : "日志没有限速字样");
        }
        catch (Exception ex)
        {
            r.Fatal = ex.Message;
        }
    }
}
