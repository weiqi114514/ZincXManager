using System;  // 异常 / 时间
using System.Collections.Generic;  // 配置字典 / 断言列表
using System.IO;  // 文件与目录
using System.Linq;  // 取输出行 / 拆分列表
using System.Text;  // UTF-8
using System.Text.RegularExpressions;  // 绑定码
using System.Threading.Tasks;  // Task
using ZincXManagerShared.Network;  // 协议常量 626/627/628/629/630/631

namespace ZincXManagerTests;

/// <summary>
/// 套件 24:绑定上限(bindmax 配置 + 超限拦截 + 解绑页要的列表)。
/// <para>要验的是四件事:缺配置时默认 1、config 模式能改且改错会被拦、
/// 超限的绑定在提交码这一步就被挡回去(不是等确认后才失败)、
/// 解绑按玩家名逐个来(解一个不动另一个)。</para>
/// </summary>
public static class BindLimitSuite
{
    /// <summary>跑一遍,结果追加到 results。</summary>
    public static async Task RunAsync(List<SuiteResult> results, string ws, string debugMs)
    {
        const int czport = 21493;   // ZXMS 客户端接入端口
        const int zxmmPort = 21458;   // 假 ZXMM 的 21200 替身

        var r = new SuiteResult { Name = "24 绑定上限(bindmax 配置 + 超限拦截 + 解绑列表)" };

        try
        {
            var dir = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "bind-limit"));

            // 模板 bin 里带着开发机的真实数据,链路测试要干净起点
            foreach (var stale in new[] { "PersistentLogin.txt", "loginlog.log" })
            {
                var path = Path.Combine(dir, stale);
                if (File.Exists(path)) { File.Delete(path); }
            }

            foreach (var staleDir in new[] { "Data", "User" })
            {
                var path = Path.Combine(dir, staleDir);
                if (Directory.Exists(path)) { Directory.Delete(path, true); }
            }

            var configPath = Path.Combine(dir, "config.ini");   //服务端配置文件
            var bindPath = Path.Combine(dir, "Data", "BindUser.txt");   //绑定表

            using var fake = new FakeZxmm(zxmmPort);
            fake.Start();

            // 故意不给 bindmax:验默认值
            using var ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, new Dictionary<string, string>
            {
                ["configver"] = "2",
                ["connectOS"] = "0",
                ["linkname"] = "0",
                ["name"] = "CSharp-绑定上限服",
                ["czport"] = czport.ToString(),
                ["zxmmapi"] = "127.0.0.1:" + zxmmPort,
            });

            r.Add("ZXMS 监听 " + czport, ms.WaitPort(czport));
            r.Add("ZXMS 连上假 ZXMM", await fake.WaitConnectedAsync(12000), fake.Dump());

            // ---------- ① 缺配置:写默认 1,并在启动日志里说清 ----------
            r.Add("启动时把 bindmax 默认写成 1",
                await WaitOutAsync(ms, "bindmax 未配置,已写入默认值 1", 8000), LastLines(ms, 3));
            r.Add("启动日志写明上限是 1 个玩家",
                await WaitOutAsync(ms, "[绑定] 上限:一个账号最多绑 1 个玩家", 8000), LastLines(ms, 3));

            // ---------- ② config 模式的表里要有 bindmax ----------
            ms.SendCmd("config");
            r.Add("config 模式表格里有 绑定上限 / bindmax",
                await WaitOutAsync(ms, "绑定上限", 8000) && ms.Out.Contains("bindmax"), LastLines(ms, 4));

            // ---------- ③ 填垃圾 / 超范围要被拦 ----------
            ms.SendCmd("bindmax abc");
            r.Add("bindmax 填非数字被拒绝",
                await WaitOutAsync(ms, "范围 0-1000", 8000), LastLines(ms, 3));
            ms.SendCmd("bindmax 2000");
            r.Add("bindmax 超上限 1000 被拒绝",
                await WaitOutAsync(ms, "范围 0-1000", 8000) && !ReadShared(configPath).Contains("bindmax=2000"),
                ReadShared(configPath).Trim());

            // ---------- ④ 账号登录,准备两个玩家 ----------
            using var probe = await Probe.ConnectAsync("127.0.0.1", czport, "SAOS");
            r.Add("探针注册账号", await probe.RegisterAsync("limituser", "pw123456", "MACH-LIMIT"), probe.LastAck ?? "");
            r.Add("探针登录账号",
                await probe.LoginAsync("limituser", "pw123456", "MACH-LIMIT", "Player", "zxms"), probe.LastAck ?? "");
            probe.StartReader();

            // ---------- ⑤ 绑第一个玩家:上限 1 时成功 ----------
            var code1 = await TakeBindCodeAsync(fake, "limitplayer", "/198.51.100.21:50001", r, "第一个玩家");
            if (code1.Length == 6)
            {
                await probe.SendAsync(11, Protocol.BindApply, Encoding.UTF8.GetBytes(code1));
                var pending = await probe.TakeQueuedAsync(Protocol.BindAck, 8000);
                var token = pending?.Split('|') is { Length: > 1 } parts ? parts[1] : "";
                r.Add("第一个玩家 · 提交码后回 pending", pending?.StartsWith("pending|") == true, pending ?? "没收到回执");
                await probe.SendAsync(15, Protocol.BindConfirm, Encoding.UTF8.GetBytes(token));
                var ack = await probe.TakeQueuedAsync(Protocol.BindAck, 8000);
                r.Add("第一个玩家 · 确认后绑定成功", ack?.StartsWith("ok|limitplayer") == true, ack ?? "没收到回执");
            }

            // ---------- ⑥ 绑第二个玩家:上限 1 时在 626 这一步就被拦 ----------
            var code2 = await TakeBindCodeAsync(fake, "limitplayer2", "/198.51.100.22:50002", r, "第二个玩家");
            if (code2.Length == 6)
            {
                await probe.SendAsync(12, Protocol.BindApply, Encoding.UTF8.GetBytes(code2));
                var refused = await probe.TakeQueuedAsync(Protocol.BindAck, 8000);
                r.Add("超限 · 提交码就被拦(不是等确认后才失败)",
                    refused?.StartsWith("err|") == true, refused ?? "没收到回执");
                r.Add("超限 · 理由带上限数与已绑玩家",
                    refused?.Contains("上限 1") == true && refused?.Contains("limitplayer") == true, refused ?? "");
                r.Add("超限 · 拦下后没有写进绑定表", !ReadShared(bindPath).Contains("limitplayer2"), ReadShared(bindPath).Trim());
            }

            // ---------- ⑦ 把上限改成 2 → 第二个玩家能绑上了 ----------
            ms.SendCmd("bindmax 2");
            r.Add("config 里 bindmax 2 设置成功",
                await WaitOutAsync(ms, "上限已改成 2 个玩家/账号", 8000), LastLines(ms, 3));

            if (code2.Length == 6)
            {
                await probe.SendAsync(13, Protocol.BindApply, Encoding.UTF8.GetBytes(code2));
                var pending2 = await probe.TakeQueuedAsync(Protocol.BindAck, 8000);
                var token2 = pending2?.Split('|') is { Length: > 1 } parts2 ? parts2[1] : "";
                r.Add("放宽上限后 · 提交码回 pending", pending2?.StartsWith("pending|") == true, pending2 ?? "没收到回执");

                await probe.SendAsync(16, Protocol.BindConfirm, Encoding.UTF8.GetBytes(token2));
                var ack2 = await probe.TakeQueuedAsync(Protocol.BindAck, 8000);
                r.Add("放宽上限后 · 第二个玩家绑定成功", ack2?.StartsWith("ok|limitplayer2") == true, ack2 ?? "没收到回执");
            }

            // ---------- ⑧ 630 列表:解绑页按行渲染,两个玩家都要在 ----------
            await probe.SendAsync(17, Protocol.BindQuery, Encoding.UTF8.GetBytes("limituser"));
            var listText = await probe.TakeQueuedAsync(Protocol.BindList, 8000);   //绑定列表
            var names = (listText ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('=')[0].Trim()).ToList();   //列表里的玩家名
            r.Add("630 一行一个玩家(两个都在)",
                names.SequenceEqual(new[] { "limitplayer", "limitplayer2" }), listText ?? "没收到");

            // ---------- ⑨ 628 解一个:只解点名那个,另一个留着 ----------
            await probe.SendAsync(18, Protocol.Unbind, Encoding.UTF8.GetBytes("limitplayer"));
            var unbindAck = await probe.TakeQueuedAsync(Protocol.BindAck, 8000);
            r.Add("按玩家名解绑回 ok", unbindAck?.StartsWith("ok|limitplayer") == true, unbindAck ?? "没收到回执");
            r.Add("回执里只带被解的那一个",
                unbindAck?.Contains("limitplayer2") == false, unbindAck ?? "");

            await probe.SendAsync(19, Protocol.BindQuery, Encoding.UTF8.GetBytes("limituser"));
            var listAfter = await probe.TakeQueuedAsync(Protocol.BindList, 8000);   //解一个之后的列表
            r.Add("解绑后列表只剩另一个", listAfter?.StartsWith("limitplayer2=") == true, listAfter ?? "没收到");

            // ---------- ⑩ 解不存在的玩家要被如实拒绝 ----------
            await probe.SendAsync(20, Protocol.Unbind, Encoding.UTF8.GetBytes("nobody"));
            var badUnbind = await probe.TakeQueuedAsync(Protocol.BindAck, 8000);
            r.Add("解不存在的玩家回 err(不当成功)",
                badUnbind?.StartsWith("err|") == true, badUnbind ?? "没收到回执");

            // ---------- ⑪ 落盘:一个玩家一行,解掉的那个不在表里 ----------
            var bindText = ReadShared(bindPath);   //绑定表全文
            r.Add("绑定表里只剩一个玩家", !bindText.Contains("limitplayer|") || bindText.Contains("limitplayer2="),
                bindText.Trim());
            r.Add("config.ini 里写了 bindmax=2", ReadShared(configPath).Contains("bindmax=2"), ReadShared(configPath).Trim());

            ms.SendCmd("exitc");
            r.Add("exitc 退出配置模式", await WaitOutAsync(ms, "已退出配置模式", 8000), LastLines(ms, 3));
        }
        catch (Exception ex)
        {
            r.Fatal = ex.Message;
        }

        results.Add(r);
    }

    /// <summary>推一条进服事件,从踢人理由里抠出 6 位绑定码(拿不到就当断言失败)。</summary>
    private static async Task<string> TakeBindCodeAsync(FakeZxmm fake, string player, string ip, SuiteResult r, string label)
    {
        fake.PushJoining(player, ip);
        var kick = await fake.TakeAsync("|kick|" + player + "|", 12000);
        r.Add(label + " · 未绑定进服被踢去拿码", kick is not null, kick ?? "没收到:" + fake.Dump());
        if (kick is not null) { await fake.ReplyAsync(kick, "err", "没有这个在线玩家"); }

        var code = Regex.Match(kick ?? "", "绑定码：([A-Z2-9]{6})").Groups[1].Value;   //绑定码
        r.Add(label + " · 踢人理由带 6 位绑定码", code.Length == 6, kick ?? "");
        return code;
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
