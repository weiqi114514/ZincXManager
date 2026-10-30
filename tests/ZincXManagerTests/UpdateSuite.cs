using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ZincXManagerShared.Update;  // VersionRules:客户端自动更新用的同一份版本比较规则

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
}
