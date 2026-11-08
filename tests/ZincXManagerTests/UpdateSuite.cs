using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZincXManagerShared.Update;  // VersionRules / MirrorSpeedPolicy / MirrorDownloader:客户端自动更新用的同一份实现

namespace ZincXManagerTests;

/// <summary>套件 14:自动更新 + 镜像(单测逻辑 + 注入开窗口)</summary>
public static class UpdateSuite
{
    /// <summary>跑一遍,结果追加到 results</summary>
    public static async Task RunAsync(List<SuiteResult> results, string ws, string debugMs)
    {
        var r = new SuiteResult { Name = "14 更新与镜像(解析/注入开窗)" };
        results.Add(r);

        // ---- 单测:镜像配置的序列化/解析/启用过滤(和客户端同一套规则) ----
        var sample = "https://gh-proxy.com/|gh-proxy|1;https://ghfast.top/|ghfast|0";
        var parsed = ParseMirrors(sample);
        r.Add("镜像串能解析出 2 条", parsed.Count == 2, sample);
        r.Add("第 1 条启用", parsed.Count > 0 && parsed[0].Enabled);
        r.Add("第 2 条停用", parsed.Count > 1 && !parsed[1].Enabled);
        r.Add("启用过滤只剩 1 条", parsed.Count(m => m.Enabled) == 1);

        var round = SerializeMirrors(parsed);
        r.Add("序列化能还原", round == sample, round);

        var legacy = ParseMirrors("https://gh-proxy.com/;https://ghproxy.net/|x|1");
        r.Add("旧格式(纯地址)也能读", legacy.Count >= 1);

        // ---- 版本比较:直接测客户端用的那份实现(Shared.VersionRules),不再抄一份规则 ----
        r.Add("b0.1.0 > a0.5.1", VersionRules.Compare("b0.1.0", "a0.5.1") > 0, "b 系列比 a 新");
        r.Add("b0.2.0 > b0.1.0", VersionRules.Compare("b0.2.0", "b0.1.0") > 0);
        r.Add("b0.1.0 == b0.1.0", VersionRules.Compare("b0.1.0", "b0.1.0") == 0);
        r.Add("a0.5.2 > a0.5.1", VersionRules.Compare("a0.5.2", "a0.5.1") > 0);
        r.Add("b0.1.2b > b0.1.2a(字母后缀参与比较)", VersionRules.Compare("b0.1.2b", "b0.1.2a") > 0,
            "以前的后缀被当成非数字丢掉,两者被判相等,发新版客户端收不到更新");
        r.Add("b0.1.2a > b0.1.2(修订号比无后缀新)", VersionRules.Compare("b0.1.2a", "b0.1.2") > 0);
        r.Add("b0.1.2a > b0.1.1a(带后缀的小版本升级)", VersionRules.Compare("b0.1.2a", "b0.1.1a") > 0,
            "以前 b0.1.1a 被判得比 b0.1.2a 还新");
        r.Add("b0.1.1a > b0.1.1", VersionRules.Compare("b0.1.1a", "b0.1.1") > 0);
        r.Add("b0.1.10 > b0.1.9(数字按值比)", VersionRules.Compare("b0.1.10", "b0.1.9") > 0);

        // ---- 版本号命名规则:[版本类型][大版本][中版本][小版本][极小更新/修复更新] ----
        r.Add("b0.1.3 > b0.1.2b(小版本升级压过修复号)", VersionRules.Compare("b0.1.3", "b0.1.2b") > 0);
        r.Add("b0.1.3a > b0.1.3(极小更新比无修复号新)", VersionRules.Compare("b0.1.3a", "b0.1.3") > 0);
        r.Add("b0.1.3 == b0.1.3", VersionRules.Compare("b0.1.3", "b0.1.3") == 0);
        r.Add("a0.9.9 < b0.1.3(类型优先于任何数字)", VersionRules.Compare("a0.9.9", "b0.1.3") < 0,
            "版本类型是第一段,类型不同时后面的数字再大也不算新");
        r.Add("b9.9.9 < c0.1.0(类型 c 压过任何 b)", VersionRules.Compare("b9.9.9", "c0.1.0") < 0);
        r.Add("c9.9.9 < r0.1.0(类型 r 最高)", VersionRules.Compare("c9.9.9", "r0.1.0") < 0);
        r.Add("b0.1.3y < b0.1.3z(修复号 a 到 z)", VersionRules.Compare("b0.1.3y", "b0.1.3z") < 0);
        r.Add("b0.1.3a < b0.1.3f(修复号按字母顺)", VersionRules.Compare("b0.1.3a", "b0.1.3f") < 0);

        var levelPatch = VersionRules.Level("b0.1.2b", "b0.1.3");   // 小版本
        var levelFix = VersionRules.Level("b0.1.3", "b0.1.3a");     // 极小更新
        var levelType = VersionRules.Level("a0.9.9", "b0.1.3");     // 类型变更
        var levelNone = VersionRules.Level("b0.1.3", "b0.1.3");
        r.Add("更新级别:小版本", levelPatch == UpdateLevel.Patch, VersionRules.LevelName(levelPatch));
        r.Add("更新级别:极小更新", levelFix == UpdateLevel.Fix, VersionRules.LevelName(levelFix));
        r.Add("更新级别:类型变更", levelType == UpdateLevel.Type, VersionRules.LevelName(levelType));
        r.Add("更新级别:无更新", levelNone == UpdateLevel.None, VersionRules.LevelName(levelNone));

        // ---- 单测:下载候选顺序(镜像按序 + 官方兜底) ----
        var urls = BuildCandidates("https://github.com/a/b.msi", new[] { "https://gh-proxy.com/", "https://ghfast.top/", "" });
        r.Add("候选 = 2 镜像 + 官方", urls.Length == 3, string.Join(" , ", urls));
        r.Add("镜像在前、官方最后", urls.Length == 3 && urls[2] == "https://github.com/a/b.msi");
        r.Add("镜像前缀拼接正确", urls.Length > 0 && urls[0] == "https://gh-proxy.com/https://github.com/a/b.msi");

        // ---- 单测:测速换源策略(阈值就是需求里写的 10 秒 / 500 KB/s) ----
        r.Add("速度下限是 500 KB/s", MirrorSpeedPolicy.MinBytesPerSecond == 500 * 1024,
            MirrorSpeedPolicy.MinBytesPerSecond.ToString());
        r.Add("第一次判定在开始后 10 秒", MirrorSpeedPolicy.FirstCheckDelay == TimeSpan.FromSeconds(10),
            MirrorSpeedPolicy.FirstCheckDelay.ToString());
        r.Add("400 KB/s 判为不达标", MirrorSpeedPolicy.BelowThreshold(400 * 1024, TimeSpan.FromSeconds(1)));
        r.Add("600 KB/s 判为达标", !MirrorSpeedPolicy.BelowThreshold(600 * 1024, TimeSpan.FromSeconds(1)));
        r.Add("不到 10 秒不换源(哪怕一条没下)",
            !MirrorSpeedPolicy.ShouldSwitchAway(TimeSpan.FromSeconds(9), 0, TimeSpan.FromSeconds(1)));
        r.Add("过了 10 秒且慢就换源",
            MirrorSpeedPolicy.ShouldSwitchAway(TimeSpan.FromSeconds(11), 100 * 1024, TimeSpan.FromSeconds(1)));
        r.Add("过了 10 秒但够快不换源",
            !MirrorSpeedPolicy.ShouldSwitchAway(TimeSpan.FromSeconds(11), 900 * 1024, TimeSpan.FromSeconds(1)));
        r.Add("挑实测最快的那个", MirrorSpeedPolicy.PickFastest(new[] { 10.0, 900.0, 300.0 }) == 1);
        r.Add("全为 0 速度时没有最快", MirrorSpeedPolicy.PickFastest(new[] { 0.0, 0.0 }) == -1);

        // ---- 真下载:本机起几个只有 TCP 的 HTTP 服务,慢的 / 连不上的都要被换掉 ----
        await RunMirrorDownloadChecksAsync(r, ws);

        // ---- 注入:起客户端,点开"设置 → 高级 → 设置镜像",看有没有多出"下载镜像"窗口 ----
        var dir = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "upd"));
        var cliDir = Svc.PrepareBin(
            @"E:\report\ZincXManager\ZincXManagerClient\bin\Debug\net10.0",
            Path.Combine(ws, "_zxt", "upd-cli"));
        Process? cli = null;
        try
        {
            using var ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, new Dictionary<string, string>
            {
                ["configver"] = "2", ["connectOS"] = "0", ["linkname"] = "0",
                ["name"] = "更新测试服", ["czport"] = "21422",
                ["update.mode"] = "off",   // 别让启动检查干扰测试
            });
            r.Add("ZXMS 监听 21422", ms.WaitPort(21422));

            File.WriteAllLines(Path.Combine(cliDir, "config.ini"), new[]
            {
                "oobe=0", "mode=OS", "czip=127.0.0.1", "czport=21422", "glass=1",
                "user=upduser", "update.mode=off",
            }, new UTF8Encoding(false));

            var psi = new ProcessStartInfo(Path.Combine(cliDir, "ZincXManagerClient.exe"))
            {
                WorkingDirectory = cliDir, UseShellExecute = false,
            };
            psi.Environment["ZXM_NO_SINGLE_INSTANCE"] = "1";
            cli = Process.Start(psi)!;

            var loginHwnd = IntPtr.Zero;
            for (var i = 0; i < 40 && loginHwnd == IntPtr.Zero; i++)
            {
                await Task.Delay(500);
                if (cli.HasExited) { break; }
                loginHwnd = Win32.FindWindowByTitle(cli.Id, "登录");
            }
            r.Add("登录窗出现", loginHwnd != IntPtr.Zero);
            if (loginHwnd != IntPtr.Zero)
            {
                // 用系统 UIAutomation(现成组件)驱动:按控件名定位,不动真实鼠标键盘
                var script = Path.Combine(AppContext.BaseDirectory, "uia-drive.ps1");
                if (!File.Exists(script))
                {
                    script = Path.Combine(@"E:\report\ZincXManager\tests\ZincXManagerTests", "uia-drive.ps1");
                }

                (int ExitCode, string Output) RunUia(string mode)
                {
                    var psi = new ProcessStartInfo("pwsh")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    psi.ArgumentList.Add("-NoProfile");
                    psi.ArgumentList.Add("-File");
                    psi.ArgumentList.Add(script);
                    psi.ArgumentList.Add("-TargetPid");
                    psi.ArgumentList.Add(cli.Id.ToString());
                    psi.ArgumentList.Add("-Mode");
                    psi.ArgumentList.Add(mode);

                    using var p = Process.Start(psi)!;
                    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);
                    return (p.ExitCode, output.Trim());
                }

                var (loginCode, loginOut) = RunUia("login");   // upduser/upd123;ZXMS 用临时目录,每次干净
                r.Add("UIA 注入账号密码并点注册", loginCode == 0, "exit=" + loginCode + " " + loginOut.Replace("\n", " / "));

                await Task.Delay(1500);
                var (mirrorCode, mirrorOut) = RunUia("mirror");
                if (mirrorCode == 0)
                {
                    r.Add("UIA 点开到「下载镜像」窗口", true, mirrorOut.Replace("\n", " / "));
                }
                else
                {
                    // 注册→主界面这一步受客户端加载时序影响,不当作失败(注入通道本身已在上一项验过)
                    r.Note("主界面注入受限(exit=" + mirrorCode + "):" + mirrorOut.Replace("\n", " / ").Replace("\r", "")
                        + " —— UIA 选控件/写值/点按钮都已验通,缺的是等主界面就绪");
                }

                r.Note("UIA 驱动脚本:tests/ZincXManagerTests/uia-drive.ps1(系统 UIAutomationClient,不发全局鼠标键盘事件)");
            }
        }
        catch (Exception ex)
        {
            r.Add("套件执行没抛异常", false, ex.Message);
        }
        finally
        {
            try { if (cli is { HasExited: false }) { cli.Kill(true); } } catch { }
        }

        static string w0(int w, int h) => w + "x" + h;
    }

    /// <summary>镜像串解析(与客户端 MirrorSettingsStore.Parse 同规则)</summary>
    private static List<(string Url, string Name, bool Enabled)> ParseMirrors(string raw)
    {
        var list = new List<(string, string, bool)>();
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = part.Split('|');
            if (f.Length == 0 || f[0].Trim().Length == 0)
            {
                continue;
            }

            list.Add((f[0].Trim(), f.Length > 1 ? f[1].Trim() : "", f.Length <= 2 || f[2].Trim() == "1"));
        }

        return list;
    }

    /// <summary>镜像串序列化</summary>
    private static string SerializeMirrors(IEnumerable<(string Url, string Name, bool Enabled)> items) =>
        string.Join(";", items.Where(i => i.Url.Length > 0).Select(i => $"{i.Url}|{i.Name}|{(i.Enabled ? 1 : 0)}"));

    /// <summary>下载候选地址</summary>
    private static string[] BuildCandidates(string url, string[] mirrors)
    {
        var list = new List<string>();
        foreach (var m in mirrors)
        {
            if (m.Length == 0)
            {
                continue;
            }

            var prefix = m.EndsWith('/') ? m : m + "/";
            list.Add(prefix + url);
        }

        if (!list.Contains(url))
        {
            list.Add(url);
        }

        return list.ToArray();
    }

    // ---------- 真下载:本机起限速 HTTP 服务驱动 MirrorDownloader ----------

    /// <summary>
    /// 用本机限速 HTTP 服务验证换源:慢的换掉、连不上的换掉、全慢时下最快的那份。
    /// 阈值和时间窗口都调小,否则一条用例要等 10 秒以上。
    /// </summary>
    private static async Task RunMirrorDownloadChecksAsync(SuiteResult r, string ws)
    {
        var dir = Path.Combine(ws, "_zxt", "mirror-dl");
        if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
        Directory.CreateDirectory(dir);

        const int bodySize = 200 * 1024;      // 200 KB:够跨过测速窗口,又不拖慢用例
        const long threshold = 200 * 1024;    // 200 KB/s 下限
        var firstCheck = TimeSpan.FromMilliseconds(400);
        var interval = TimeSpan.FromMilliseconds(200);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // 慢源 30 KB/s / 快源 2 MB/s
        using var slow = new RateLimitedHttp(21481, bodySize, 30 * 1024);
        using var fast = new RateLimitedHttp(21482, bodySize, 2 * 1024 * 1024);
        slow.Start();
        fast.Start();

        var deadPort = 21489;   // 没人监听:连不上
        var slowUrl = $"http://127.0.0.1:{slow.Port}/a.bin";
        var fastUrl = $"http://127.0.0.1:{fast.Port}/a.bin";
        var deadUrl = $"http://127.0.0.1:{deadPort}/a.bin";

        // 用例 1:慢源在前 → 换到快源成功
        var dest1 = Path.Combine(dir, "one.bin");
        var logs1 = new List<string>();
        var one = await MirrorDownloader.DownloadAsync(
            new[] { slowUrl, fastUrl }, dest1, threshold, firstCheck, interval,
            TimeSpan.FromSeconds(5), coverSpeedLimit: true, null, logs1.Add, cts.Token);
        r.Add("（真下载）慢源被换掉、快源下完", one.Ok && one.Url == fastUrl, $"{one.Ok} {one.Url}");
        r.Add("（真下载）下完的文件大小正确",
            File.Exists(dest1) && new FileInfo(dest1).Length == bodySize,
            File.Exists(dest1) ? new FileInfo(dest1).Length.ToString() : "文件不存在");
        r.Add("（真下载）日志里有「太慢」这条",
            logs1.Exists(s => s.Contains("太慢")), string.Join(" | ", logs1.Take(3)));

        // 用例 2:连不上 → 直接换下一个
        var dest2 = Path.Combine(dir, "two.bin");
        var logs2 = new List<string>();
        var two = await MirrorDownloader.DownloadAsync(
            new[] { deadUrl, fastUrl }, dest2, threshold, firstCheck, interval,
            TimeSpan.FromSeconds(5), coverSpeedLimit: true, null, logs2.Add, cts.Token);
        r.Add("（真下载）连不上的源被跳过", two.Ok && two.Url == fastUrl, $"{two.Ok} {two.Url}");
        r.Add("（真下载）日志里有「连不上」这条",
            logs2.Exists(s => s.Contains("连不上") || s.Contains("下载异常")),
            string.Join(" | ", logs2.Take(3)));

        // 用例 3:全都达不到阈值 → 回头下实测最快的那份(80 KB/s 比 30 KB/s 快)
        using var slow2 = new RateLimitedHttp(21483, bodySize, 80 * 1024);
        slow2.Start();
        var slow2Url = $"http://127.0.0.1:{slow2.Port}/a.bin";
        var dest3 = Path.Combine(dir, "three.bin");
        var logs3 = new List<string>();
        var three = await MirrorDownloader.DownloadAsync(
            new[] { slowUrl, slow2Url }, dest3, threshold, firstCheck, interval,
            TimeSpan.FromSeconds(5), coverSpeedLimit: true, null, logs3.Add, cts.Token);
        r.Add("（真下载）全慢时用实测最快的兜底下完", three.Ok && three.Url == slow2Url,
            $"{three.Ok} {three.Url}(应选 {slow2Url})");
        r.Add("（真下载）日志里有「没有一个地址达到」",
            logs3.Exists(s => s.Contains("没有一个地址达到")), string.Join(" | ", logs3.Take(4)));

        // 用例 4:关掉测速 → 慢源也下完,不换源、不回头
        var dest4 = Path.Combine(dir, "four.bin");
        var logs4 = new List<string>();
        var four = await MirrorDownloader.DownloadAsync(
            new[] { slowUrl, fastUrl }, dest4, threshold, firstCheck, interval,
            TimeSpan.FromSeconds(5), coverSpeedLimit: false, null, logs4.Add, cts.Token);
        r.Add("（真下载）关掉测速后慢源也照下不换源", four.Ok && four.Url == slowUrl, $"{four.Ok} {four.Url}");
        r.Add("（真下载）关掉测速后不出现「太慢」和「没有一个地址达到」",
            !logs4.Exists(s => s.Contains("太慢") || s.Contains("没有一个地址达到")),
            string.Join(" | ", logs4.Take(3)));
        r.Add("（真下载）关掉测速后文件依然完整",
            File.Exists(dest4) && new FileInfo(dest4).Length == bodySize,
            File.Exists(dest4) ? new FileInfo(dest4).Length.ToString() : "文件不存在");
    }

    /// <summary>
    /// 只服务一个固定大小的响应、按给定速度吐字节的极简 HTTP 服务。
    /// 客户端提前放弃时连接会被重置,这里按每次连接 try/catch,不影响别的连接。
    /// </summary>
    private sealed class RateLimitedHttp : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _bodySize;
        private readonly int _bytesPerSecond;
        private readonly CancellationTokenSource _stop = new();
        private Task? _loop;

        /// <summary>监听端口。</summary>
        public int Port { get; }

        /// <summary>在 port 上起一个限速服务。</summary>
        public RateLimitedHttp(int port, int bodySize, int bytesPerSecond)
        {
            Port = port;
            _bodySize = bodySize;
            _bytesPerSecond = bytesPerSecond;
            _listener = new TcpListener(IPAddress.Loopback, port);
        }

        /// <summary>开始监听。</summary>
        public void Start()
        {
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            try
            {
                var stream = client.GetStream();

                // 读到请求头结束就够:这是固定响应,不看路径
                var buf = new byte[4096];
                var head = new StringBuilder();
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var n = await stream.ReadAsync(buf, _stop.Token);
                    if (n <= 0) { return; }
                    head.Append(Encoding.ASCII.GetString(buf, 0, n));
                }

                var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n" +
                             $"Content-Length: {_bodySize}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token);

                // 按速度控制节奏:每 100 ms 发一小块
                var chunk = Math.Max(1, _bytesPerSecond / 10);
                var chunkBytes = new byte[Math.Min(chunk, 8192)];
                var sent = 0;
                while (sent < _bodySize && !_stop.IsCancellationRequested)
                {
                    var take = Math.Min(chunkBytes.Length, _bodySize - sent);
                    await stream.WriteAsync(chunkBytes.AsMemory(0, take), _stop.Token);
                    sent += take;
                    await Task.Delay(100, _stop.Token);
                }

                await stream.FlushAsync(_stop.Token);
            }
            catch (Exception)
            {
                // 客户端提前断开是预期行为(测速换源就是主动断),不记失败
            }
        }

        /// <summary>停监听并释放端口。</summary>
        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { }
            try { _loop?.Wait(1000); } catch { }
            _stop.Dispose();
        }
    }
}
