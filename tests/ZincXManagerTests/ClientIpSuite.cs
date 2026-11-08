using System;  // 异常 / 时间
using System.Collections.Generic;  // List<SuiteResult>
using System.IO;  // 文件与目录
using System.Linq;  // 拆分与查询
using System.Text;  // UTF-8 / GBK
using System.Text.RegularExpressions;  // 绑定码
using System.Threading.Tasks;  // Task
using ZincXManagerShared.Network;  // Protocol:632 / 633 / 621

namespace ZincXManagerTests;

/// <summary>
/// 套件 21:客户端局域网地址上报(632 / 633)与 621 第五段。
///
/// <para>要验的是三件事:上报值能进 621 给账号主人看、只有<b>登录后</b>的合法局域网地址才收、
/// 621 的第五段<b>只在真有值时</b>才追加(老客户端只读四段,不能错位)。</para>
///
/// <para>另外顺手钉死两条安全口径:上报值不参与持久登录表、未登录连接连上报都不许写进日志原文。</para>
/// </summary>
public static class ClientIpSuite
{
    /// <summary>跑一遍,结果追加到 results。</summary>
    public static async Task RunAsync(List<SuiteResult> results, string ws, string debugMs)
    {
        var r = new SuiteResult { Name = "21 局域网地址上报(632/633 + 621 第五段)" };

        const int czport = 21490;   // ZXMS 客户端接入端口
        const int zxmmPort = 21491; // 假 ZXMM 端口

        try
        {
            var dir = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "client-ip"));

            // 模板 bin 里带着开发机的真实数据,链路测试要干净起点
            foreach (var stale in new[] { "PersistentLogin.txt", "loginlog.log", "BindUser.txt" })
            {
                var path = Path.Combine(dir, stale);
                if (File.Exists(path)) { File.Delete(path); }
            }

            foreach (var staleDir in new[] { "Data", "User" })
            {
                var path = Path.Combine(dir, staleDir);
                if (Directory.Exists(path)) { Directory.Delete(path, true); }
            }

            var bindPath = Path.Combine(dir, "Data", "BindUser.txt");   // 绑定表
            var persistentPath = Path.Combine(dir, "PersistentLogin.txt");   // 持久登录表
            var loginlogPath = Path.Combine(dir, "loginlog.log");   // 登录审计

            using var fake = new FakeZxmm(zxmmPort);
            fake.Start();

            using var ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, new Dictionary<string, string>
            {
                ["configver"] = "2",
                ["connectOS"] = "0",
                ["linkname"] = "0",
                ["name"] = "CSharp-局域网地址服",
                ["czport"] = czport.ToString(),
                ["zxmmapi"] = "127.0.0.1:" + zxmmPort,
            });

            r.Add("ZXMS 监听 " + czport, ms.WaitPort(czport));
            r.Add("ZXMS 连上假 ZXMM", await fake.WaitConnectedAsync(15000), fake.Dump());

            // ---------- ① 未登录的连接上报:要拒绝,而且长文本不能原样进日志 ----------
            var junk = new string('x', 200);   //超长垃圾负载
            using (var anonymous = await Probe.ConnectAsync("127.0.0.1", czport, "OS"))
            {
                await anonymous.SendAsync(31, Protocol.ClientIpReport, Encoding.UTF8.GetBytes(junk));
                r.Add("未登录连接上报地址被拒(日志有告警)",
                    await WaitOutAsync(ms, "未登录的连接上报了地址", 6000), LastLines(ms, 3));
                r.Add("超长上报负载写日志前被截断(带原长度)",
                    await WaitOutAsync(ms, "(共 200 字符)", 6000), LastLines(ms, 3));
                r.Add("日志里没有把 200 个字符原样打出来", !ms.Out.Contains(junk), "日志里出现了完整负载");
            }

            // ---------- ② 登录后上报合法的局域网地址:接受并记日志 ----------
            using var probe = await Probe.ConnectAsync("127.0.0.1", czport, "OS");
            r.Add("账号注册", await probe.RegisterAsync("iptester", "pw123456", "MACH-IP"), probe.LastAck ?? "");
            r.Add("账号登录", await probe.LoginAsync("iptester", "pw123456", "MACH-IP", "Player", "zxms"), probe.LastAck ?? "");
            probe.StartReader();

            const string lanIp = "192.168.31.77";   //客户端自报的局域网地址
            await probe.SendAsync(32, Protocol.ClientIpReport, Encoding.UTF8.GetBytes(lanIp));
            r.Add("登录后上报局域网地址被接受",
                await WaitOutAsync(ms, "上报本机局域网地址 " + lanIp, 6000), LastLines(ms, 3));
            r.Add("接受时日志写明安全判断仍用连接地址",
                await WaitOutAsync(ms, "安全判断只用连接地址", 6000), LastLines(ms, 3));

            // ---------- ③ 非法地址(回环 / 链路本地 / 非 IPv4 / 乱码)一律丢掉,且不覆盖已有值 ----------
            foreach (var bad in new[] { "abc", "127.0.0.1", "169.254.10.20", "::1" })
            {
                await probe.SendAsync(33, Protocol.ClientIpReport, Encoding.UTF8.GetBytes(bad));
                r.Add("非法上报被丢掉:" + bad,
                    await WaitOutAsync(ms, "上报的地址不可用,已忽略:" + bad, 6000), LastLines(ms, 2));
            }

            // ---------- ④ 绑定一个玩家(未绑定 → 拿码 → 626 → 631) ----------
            const string playerA = "ipplayer";   //玩家名
            fake.PushJoining(playerA, "/10.9.9.9:50001");
            var kick1 = await fake.TakeAsync("|kick|" + playerA + "|", 12000);
            var code1 = Regex.Match(kick1 ?? "", "绑定码：([A-Z2-9]{6})").Groups[1].Value;
            r.Add("未绑定玩家拿到绑定码", code1.Length == 6, kick1 ?? "没收到:" + fake.Dump());
            if (kick1 is not null) { await fake.ReplyAsync(kick1, "err", "没有这个在线玩家"); }

            if (code1.Length == 6)
            {
                await probe.SendAsync(34, Protocol.BindApply, Encoding.UTF8.GetBytes(code1));
                var pending = await probe.TakeQueuedAsync(Protocol.BindAck, 10000);
                var token = pending?.Split('|') is { Length: > 1 } parts ? parts[1] : "";
                if (token.Length > 0)
                {
                    await probe.SendAsync(35, Protocol.BindConfirm, Encoding.UTF8.GetBytes(token));
                    var confirm = await probe.TakeQueuedAsync(Protocol.BindAck, 10000);
                    r.Add("绑定二次确认成功", confirm?.StartsWith("ok|" + playerA) == true, confirm ?? "没收到");
                }
            }

            r.Add("绑定表写入", ReadShared(bindPath).Contains("=" + playerA), ReadShared(bindPath).Trim());

            // ---------- ⑤ 已绑定玩家进服:621 五段,第五段是上报地址;归属地仍按连接 IP 查 ----------
            fake.PushJoining(playerA, "/10.9.9.9:50002");
            var askA = await probe.TakeQueuedAsync(Protocol.LoginAsk, 12000);
            r.Add("账号主人收到 621", askA is not null, askA ?? "没收到");
            var partsA = (askA ?? "").Split('|');   //请求ID|玩家|连接IP|归属地|上报IP
            r.Add("621 第一段是请求号", partsA.Length > 0 && int.TryParse(partsA[0], out _), askA ?? "");
            r.Add("621 第二段是玩家名", partsA.Length > 1 && partsA[1] == playerA, askA ?? "");
            r.Add("621 第三段是连接 IP(不是上报地址)", partsA.Length > 2 && partsA[2] == "10.9.9.9", askA ?? "");
            r.Add("621 第四段归属地按连接 IP 查(内网)", partsA.Length > 3 && partsA[3] == "内网", askA ?? "");
            r.Add("621 第五段是上报的局域网地址", partsA.Length > 4 && partsA[4] == lanIp, askA ?? "");

            var kickAsk = await fake.TakeAsync("|kick|" + playerA + "|", 12000);
            r.Add("发完 621 把玩家踢走(点同意后重进)", kickAsk is not null, kickAsk ?? "没收到:" + fake.Dump());
            if (kickAsk is not null) { await fake.ReplyAsync(kickAsk, "err", "没有这个在线玩家"); }

            // 点同意:走持久登录表那条路,同时验证上报地址不参与安全判断
            await probe.SendAsync(36, Protocol.LoginAnswer, Encoding.UTF8.GetBytes(partsA[0] + "|yes"));
            r.Add("同意后写持久登录表", await WaitFileAsync(persistentPath, playerA + "|10.9.9.9|", 12000),
                ReadShared(persistentPath).Trim());
            var persistentText = ReadShared(persistentPath);   //持久登录表全文
            r.Add("持久登录表里没有上报的局域网地址", !persistentText.Contains(lanIp), persistentText.Trim());

            // ---------- ⑥ 第二个账号没上报过:进服时向它的客户端要一次(633),这次 621 仍是四段 ----------
            using var probe2 = await Probe.ConnectAsync("127.0.0.1", czport, "OS");
            r.Add("第二个账号注册", await probe2.RegisterAsync("iptester2", "pw123456", "MACH-IP2"), probe2.LastAck ?? "");
            r.Add("第二个账号登录", await probe2.LoginAsync("iptester2", "pw123456", "MACH-IP2", "Player", "zxms"), probe2.LastAck ?? "");
            probe2.StartReader();

            const string playerB = "ipplayer2";   //第二个玩家名
            fake.PushJoining(playerB, "/10.9.9.10:50003");
            var kick2 = await fake.TakeAsync("|kick|" + playerB + "|", 12000);
            var code2 = Regex.Match(kick2 ?? "", "绑定码：([A-Z2-9]{6})").Groups[1].Value;
            r.Add("第二个玩家拿到绑定码", code2.Length == 6, kick2 ?? "没收到:" + fake.Dump());
            if (kick2 is not null) { await fake.ReplyAsync(kick2, "err", "没有这个在线玩家"); }

            if (code2.Length == 6)
            {
                await probe2.SendAsync(37, Protocol.BindApply, Encoding.UTF8.GetBytes(code2));
                var pending2 = await probe2.TakeQueuedAsync(Protocol.BindAck, 10000);
                var token2 = pending2?.Split('|') is { Length: > 1 } parts2 ? parts2[1] : "";
                if (token2.Length > 0)
                {
                    await probe2.SendAsync(38, Protocol.BindConfirm, Encoding.UTF8.GetBytes(token2));
                    var confirm2 = await probe2.TakeQueuedAsync(Protocol.BindAck, 10000);
                    r.Add("第二个账号绑定成功", confirm2?.StartsWith("ok|" + playerB) == true, confirm2 ?? "没收到");
                }
            }

            // 第二次进服(换端口,免得被连接层 2 秒去重拦下)
            fake.PushJoining(playerB, "/10.9.9.10:50004");
            var askB = await probe2.TakeQueuedAsync(Protocol.LoginAsk, 12000);
            var partsB = (askB ?? "").Split('|');   //请求ID|玩家|连接IP|归属地
            r.Add("没上报过的账号进服,621 只有四段", askB is not null && partsB.Length == 4, askB ?? "没收到");
            r.Add("服务端向客户端索要地址(633)",
                await WaitOutAsync(ms, "已向 iptester2 的客户端索要一次", 6000), LastLines(ms, 3));
            r.Add("633 真的发到了这条连接", await probe2.TakeQueuedAsync(Protocol.ClientIpAsk, 6000) is not null, "没收到 633");

            // 客户端补报 → 服务端收下
            const string lanIp2 = "192.168.5.6";   //第二个客户端自报的局域网地址
            await probe2.SendAsync(39, Protocol.ClientIpReport, Encoding.UTF8.GetBytes(lanIp2));
            r.Add("补报的地址被接受",
                await WaitOutAsync(ms, "上报本机局域网地址 " + lanIp2, 6000), LastLines(ms, 3));

            // 这次先拒绝,把待确认请求清掉(否则同一玩家 5 分钟内不再弹新的 621)
            await probe2.SendAsync(40, Protocol.LoginAnswer, Encoding.UTF8.GetBytes(partsB[0] + "|no"));
            var rejectKick = await fake.TakeAsync("|kick|" + playerB + "|", 12000);
            r.Add("拒绝后下发踢人", rejectKick is not null, rejectKick ?? "没收到:" + fake.Dump());
            if (rejectKick is not null) { await fake.ReplyAsync(rejectKick, "err", "没有这个在线玩家"); }

            // ---------- ⑦ 再进一次:这次用上了刚补报的地址(说明补报从下一次进服生效) ----------
            fake.PushJoining(playerB, "/10.9.9.10:50005");
            var askB2 = await probe2.TakeQueuedAsync(Protocol.LoginAsk, 12000);
            var partsB2 = (askB2 ?? "").Split('|');   //请求ID|玩家|连接IP|归属地|上报IP
            r.Add("补报的地址从下一次进服开始生效", partsB2.Length > 4 && partsB2[4] == lanIp2, askB2 ?? "没收到");
            r.Add("这次不再索要地址(缓存有效期内不重复问)",
                !probe2.HasQueued(Protocol.ClientIpAsk), "又收到一条 633");
        }
        catch (Exception ex)
        {
            r.Fatal = ex.Message;
        }

        results.Add(r);
    }

    /// <summary>读服务端正在写的文件:FileShare.ReadWrite 打开,免得把服务端的写入顶掉。</summary>
    private static string ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>等某个文本文件里出现某段文字(登录日志这类落盘证据)。</summary>
    private static async Task<bool> WaitFileAsync(string path, string text, int timeoutMs)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (File.Exists(path) && ReadShared(path).Contains(text)) { return true; }
            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>等进程输出里出现某段文字。</summary>
    private static async Task<bool> WaitOutAsync(Svc svc, string text, int timeoutMs)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (svc.Out.Contains(text)) { return true; }
            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>取输出最后几行,失败时当证据。</summary>
    private static string LastLines(Svc svc, int count)
    {
        var lines = svc.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", lines.TakeLast(count).Select(l => l.Trim()));
    }
}
