using System;  // 异常 / 时间
using System.Collections.Generic;  // List<SuiteResult>
using System.IO;  // 文件与目录
using System.Linq;  // 取输出行
using System.Threading.Tasks;  // Task
using ZincXManagerShared.Network;  // Protocol:601 / 603 / 634

namespace ZincXManagerTests;

/// <summary>
/// 套件 22:强制更新(forceupdate / minversion + 634 踢线)。
///
/// <para>要验的是四件事:开关关着不拦人、开着拒绝旧版(含"不报版本"的老客户端)、
/// 门槛版本能单独配、在线旧版会被先通知再断开。</para>
///
/// <para>另一条是配置面:两个键都得出现在 config 模式的表里,填错要被拦、清空要能回到本端版本。</para>
/// </summary>
public static class ForceUpdateSuite
{
    /// <summary>跑一遍,结果追加到 results。</summary>
    public static async Task RunAsync(List<SuiteResult> results, string ws, string debugMs)
    {
        var r = new SuiteResult { Name = "22 强制更新(forceupdate / minversion / 634)" };

        const int czport = 21492;   // ZXMS 客户端接入端口

        try
        {
            var dir = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "force-update"));

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

            var configPath = Path.Combine(dir, "config.ini");   //服务端配置文件

            using var ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, new Dictionary<string, string>
            {
                ["configver"] = "2",
                ["connectOS"] = "0",
                ["linkname"] = "0",
                ["name"] = "CSharp-强制更新服",
                ["czport"] = czport.ToString(),
            });

            r.Add("ZXMS 监听 " + czport, ms.WaitPort(czport));

            // ---------- ① 缺配置时写默认:开关关、门槛留空 ----------
            r.Add("启动时把 forceupdate 默认写成 0",
                await WaitOutAsync(ms, "forceupdate 未配置,已写入默认值 0", 8000), LastLines(ms, 3));
            r.Add("启动时把 minversion 默认写成空",
                await WaitOutAsync(ms, "minversion 未配置,已写入空值", 8000), LastLines(ms, 3));
            r.Add("启动日志写明强制更新是关的",
                await WaitOutAsync(ms, "[强制更新] 关", 8000), LastLines(ms, 3));

            // ---------- ② 开关关着:老客户端(不带版本段)照常注册 / 登录 ----------
            using (var oldProbe = await Probe.ConnectAsync("127.0.0.1", czport, "OS"))
            {
                r.Add("开关关着 · 老客户端注册成功",
                    await oldProbe.RegisterAsync("oldclient", "pw123456", "MACH-OLD"), oldProbe.LastAck ?? "没收到");
                r.Add("开关关着 · 老客户端登录成功",
                    await oldProbe.LoginAsync("oldclient", "pw123456", "MACH-OLD", "Player", "zxms"), oldProbe.LastAck ?? "");
            }

            // ---------- ③ config 模式的表里要有这两个键 ----------
            ms.SendCmd("config");
            r.Add("config 模式表格里有 强制更新 / forceupdate",
                await WaitOutAsync(ms, "forceupdate", 8000), LastLines(ms, 3));
            r.Add("config 模式表格里有 最低版本 / minversion",
                await WaitOutAsync(ms, "minversion", 8000), LastLines(ms, 3));

            // ---------- ④ minversion 填垃圾要被拦 ----------
            ms.SendCmd("minversion abc");
            r.Add("minversion 填非版本号被拒绝",
                await WaitOutAsync(ms, "要填版本号", 8000), LastLines(ms, 3));

            // ---------- ⑤ 打开开关:老客户端被拒,且回的是 601 上的 err ----------
            ms.SendCmd("forceupdate 1");
            r.Add("config 里 forceupdate 1 设置成功",
                await WaitOutAsync(ms, "forceupdate(强制更新)已设置为 1", 8000), LastLines(ms, 3));

            using (var oldProbe = await Probe.ConnectAsync("127.0.0.1", czport, "OS"))
            {
                var oldOk = await oldProbe.LoginAsync("oldclient", "pw123456", "MACH-OLD", "Player", "zxms");
                var oldAck = oldProbe.LastAck ?? "";   //服务端回执原文
                r.Add("开着时 · 老客户端(没版本段)登录被拒", !oldOk, oldAck);
                r.Add("拒绝回执以统一记号开头(客户端靠它识别强更)",
                    oldAck.StartsWith("err|" + Protocol.UpdateRequiredMarker, StringComparison.Ordinal), oldAck);
                r.Add("拒绝回执里有本服要求的版本",
                    oldAck.Contains("本服要求"), oldAck);
                r.Add("拒绝原因落日志(版本未知)",
                    await WaitOutAsync(ms, "客户端=未知版本", 8000), LastLines(ms, 3));

                // 老客户端连注册也拦:否则能用旧版刷出新账号
                var oldRegOk = await oldProbe.RegisterAsync("oldclient2", "pw123456", "MACH-OLD2");
                r.Add("开着时 · 老客户端注册也被拒", !oldRegOk, oldProbe.LastAck ?? "");
            }

            // ---------- ⑥ 版本号低于门槛(b0.3.1)一样被拒 ----------
            using (var midProbe = await Probe.ConnectAsync("127.0.0.1", czport, "OS"))
            {
                r.Add("b0.3.2a 客户端注册成功",
                    await midProbe.RegisterAsync("newclient", "pw123456", "MACH-NEW", version: "b0.3.2a"),
                    midProbe.LastAck ?? "");
                var midOk = await midProbe.LoginAsync("newclient", "pw123456", "MACH-NEW", "Player", "zxms", version: "b0.3.1");
                r.Add("b0.3.1 客户端登录被拒", !midOk, midProbe.LastAck ?? "");
            }

            // ---------- ⑦ 达到门槛的版本(b0.3.2a)放行 ----------
            using (var newProbe = await Probe.ConnectAsync("127.0.0.1", czport, "OS"))
            {
                r.Add("b0.3.2a 客户端登录成功",
                    await newProbe.LoginAsync("newclient", "pw123456", "MACH-NEW", "Player", "zxms", version: "b0.3.2a"),
                    newProbe.LastAck ?? "");
                r.Add("放行也记日志(客户端版本 ≥ 门槛)",
                    await WaitOutAsync(ms, "登录放行 newclient", 8000), LastLines(ms, 3));
            }

            // ---------- ⑧ 门槛能单独抬到比本端更高的版本 ----------
            ms.SendCmd("minversion b0.9.9");
            r.Add("minversion 抬到 b0.9.9",
                await WaitOutAsync(ms, "minversion(最低版本)已设置为 b0.9.9", 8000), LastLines(ms, 3));

            using (var newProbe = await Probe.ConnectAsync("127.0.0.1", czport, "OS"))
            {
                var highOk = await newProbe.LoginAsync("newclient", "pw123456", "MACH-NEW", "Player", "zxms", version: "b0.3.2a");
                r.Add("门槛高于客户端版本时被拒", !highOk, newProbe.LastAck ?? "");
            }

            // ---------- ⑨ minversion 清空 = 回到本端版本 ----------
            ms.SendCmd("minversion");
            r.Add("minversion 允许清空(印成「空」)",
                await WaitOutAsync(ms, "minversion(最低版本)已设置为 空", 8000), LastLines(ms, 3));

            using (var newProbe = await Probe.ConnectAsync("127.0.0.1", czport, "OS"))
            {
                r.Add("清空门槛后 b0.3.2a 又能登录",
                    await newProbe.LoginAsync("newclient", "pw123456", "MACH-NEW", "Player", "zxms", version: "b0.3.2a"),
                    newProbe.LastAck ?? "");
            }

            // ---------- ⑩ 关掉开关后老客户端恢复可登录 ----------
            ms.SendCmd("forceupdate 0");
            r.Add("config 里 forceupdate 0 关闭成功",
                await WaitOutAsync(ms, "forceupdate(强制更新)已设置为 0", 8000), LastLines(ms, 3));

            using (var oldProbe = await Probe.ConnectAsync("127.0.0.1", czport, "OS"))
            {
                r.Add("关掉后老客户端又能登录",
                    await oldProbe.LoginAsync("oldclient", "pw123456", "MACH-OLD", "Player", "zxms"), oldProbe.LastAck ?? "");
            }

            // ---------- ⑪ 在线旧版被踢:先收 634 讲原因,再断开 ----------
            var veteran = await Probe.ConnectAsync("127.0.0.1", czport, "OS");
            r.Add("踢线前先让老客户端在线登录",
                await veteran.LoginAsync("oldclient", "pw123456", "MACH-OLD", "Player", "zxms"), veteran.LastAck ?? "");
            veteran.StartReader();

            ms.SendCmd("forceupdate 1");
            r.Add("重新打开开关",
                await WaitOutAsync(ms, "forceupdate(强制更新)已设置为 1", 8000), LastLines(ms, 3));

            var notice = await veteran.TakeQueuedAsync(Protocol.ClientTooOld, 10000);
            r.Add("在线旧版收到 634", notice is not null, notice ?? "没收到 634");
            r.Add("634 说的是统一记号 + 本服要求",
                notice is not null && notice.StartsWith(Protocol.UpdateRequiredMarker, StringComparison.Ordinal)
                    && notice.Contains("本服要求"), notice ?? "");
            r.Add("发完 634 就断开", await veteran.WaitClosedAsync(8000),
                "10 秒后连接还在(服务端没踢)");
            veteran.Dispose();

            r.Add("踢线动作落日志",
                await WaitOutAsync(ms, "踢下线", 8000), LastLines(ms, 3));

            // ---------- ⑫ 两个键都要落到 config.ini 里 ----------
            var ini = ReadShared(configPath);   //config.ini 全文
            r.Add("config.ini 里写了 forceupdate=1", ini.Contains("forceupdate=1"), ini.Trim());
            r.Add("config.ini 里 minversion 被清成空", ini.Contains("minversion=" + Environment.NewLine)
                || ini.TrimEnd().EndsWith("minversion=", StringComparison.Ordinal), ini.Trim());

            ms.SendCmd("exitc");
            r.Add("exitc 退出配置模式", await WaitOutAsync(ms, "已退出配置模式", 8000), LastLines(ms, 3));
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
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
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
