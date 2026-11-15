using System;  // Guid / Array
using System.Collections.Generic;  // Dictionary / List
using System.IO;  // Path / File
using System.Text;  // Encoding
using System.Threading.Tasks;  // Task / Delay
using ZincXManagerShared.Login;  // LoginStore
using ZincXManagerShared.Network;  // Protocol

namespace ZincXManagerTests;

/// <summary>
/// 套件 27:多服数据分离的端到端(起一个真 ZXMS,走真报文)。
///
/// <para>套件 26 只测共享层(协议号、分区路径、AsyncLocal);这里把 ZXMS 起在临时目录,
/// 用 <see cref="Probe"/> 走 380/381 加密握手,然后:</para>
/// <list type="bullet">
///   <item>670 要登记表 → 671 里有没有两台服(编号|名称|端口);</item>
///   <item>672 切到存在的服 → 673 回 <c>ok|编号</c>;切不存在的 → <c>err|没有这个服务器</c>(不许假成功);</item>
///   <item>配置键能读能改:<c>data-separation-list 4,2</c> 落盘要排好序去重,非法值要被拒;</item>
///   <item>首次启动自建 <c>countdataseparationkey.ini</c> 与 <c>Data/separation-state.ini</c>。</item>
/// </list>
/// </summary>
public static class DataSeparationLiveSuite
{
    private const string DebugMs = @"E:\report\ZincXManager\ZincXManagerServer\bin\Debug\net10.0";   //服务端编译输出
    private const string Ws = @"G:\文档\deepseek-harness\default-workspace";   //测试产物落这里,不污染仓库
    private const int Port = 21510;   //避开其它套件占的 214xx

    /// <summary>跑一遍,结果追加到 results。</summary>
    /// <param name="results">总表。</param>
    public static async Task RunAsync(List<SuiteResult> results)
    {
        var r = new SuiteResult { Name = "27 多服数据分离(端到端)" };
        results.Add(r);

        string dir;
        try
        {
            dir = Svc.PrepareBin(DebugMs, Path.Combine(Ws, "_zxt", "data-separation"));
            Directory.CreateDirectory(Path.Combine(dir, "Data"));
            //编号|名称|端口|令牌 —— 端口故意用没人监听的,ConnectAllZxmm 连不上只会 Warn,不影响报文
            File.WriteAllLines(Path.Combine(dir, "Data", "Servers.txt"), ["# 编号|名称|端口|令牌", "1|一服|21511|", "2|二服|21512|"], new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            r.Fatal = "准备目录失败:" + ex.Message;
            return;
        }

        try
        {
            using var ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, new Dictionary<string, string>
            {
                ["configver"] = "2",
                ["connectOS"] = "0",
                ["linkname"] = "0",
                ["name"] = "CSharp-分离服",
                ["czport"] = Port.ToString(),
                ["data-separation"] = "1",
                ["data-separation-list"] = "4",
            });

            var up = ms.WaitPort(Port);
            r.Add("ZXMS 监听 " + Port, up, up ? "" : "15 秒没监听");
            if (!up)
            {
                r.Note(ms.Out);
                return;
            }

            await Task.Delay(600);   //LoadConfig 里的登记/分离/迁移三步落盘,等它跑完再问
            await PacketRoundTripAsync(r, dir);
            ConfigItems(r, ms, dir);
            StartupFiles(r, dir);
            await AccountInLogAsync(r, dir);
            StartupClearsPersistent(r);
        }
        catch (Exception ex)
        {
            r.Fatal = ex.Message;
        }
    }

    /// <summary>670/671 列表 + 672/673 切服(含不存在的编号)。</summary>
    private static async Task PacketRoundTripAsync(SuiteResult r, string dir)
    {
        using var probe = await Probe.ConnectAsync("127.0.0.1", Port, "OS");
        r.Add("380/381 加密协商", true);

        await probe.SendAsync(1, Protocol.ServerListReq, Array.Empty<byte>());
        var list = await probe.TakeAsync(Protocol.ServerList, 6000);
        r.Add("670 有回应(671)", list is not null, list ?? "6 秒没回");
        if (list is not null)
        {
            var lines = list.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            r.Add("671 带两台服", lines.Length == 2, $"实际 {lines.Length} 行:{list.Replace("\n", " / ")}");
            r.Add("671 第一行是 1|一服|21511", lines.Length > 0 && lines[0] == "1|一服|21511", lines.Length > 0 ? lines[0] : "");
            r.Add("671 第二行是 2|二服|21512", lines.Length > 1 && lines[1] == "2|二服|21512", lines.Length > 1 ? lines[1] : "");
        }

        await probe.SendAsync(2, Protocol.ServerSwitchReq, Encoding.UTF8.GetBytes("2"));
        var ok = await probe.TakeAsync(Protocol.ServerSwitchAck, 6000);
        r.Add("672 切到 2 号服回 ok|2", ok == "ok|2", ok ?? "6 秒没回");

        await probe.SendAsync(3, Protocol.ServerSwitchReq, Encoding.UTF8.GetBytes("99"));
        var bad = await probe.TakeAsync(Protocol.ServerSwitchAck, 6000);
        r.Add("672 切不存在的服回 err(不是假成功)", bad is not null && bad.StartsWith("err|", StringComparison.Ordinal), bad ?? "6 秒没回");
        r.Add("err 里说明了原因", bad is not null && bad.Contains("没有这个服务器", StringComparison.Ordinal), bad ?? "");

        await probe.SendAsync(4, Protocol.ServerSwitchReq, Encoding.UTF8.GetBytes("abc"));
        var nan = await probe.TakeAsync(Protocol.ServerSwitchAck, 6000);
        r.Add("672 非数字编号也回 err", nan is not null && nan.StartsWith("err|", StringComparison.Ordinal), nan ?? "6 秒没回");

        _ = dir;
    }

    /// <summary>配置键:读得到、改得动、非法值被拒、写盘排好序。</summary>
    private static void ConfigItems(SuiteResult r, Svc ms, string dir)
    {
        var ini = Path.Combine(dir, "config.ini");

        // 配置键只在配置模式里认,先 config 进去,收尾 exitc
        ms.SendCmd("config");
        System.Threading.Thread.Sleep(500);

        // 控制台命令:改完要重启才生效,但文件得当场写下去
        ms.SendCmd("data-separation-list 4,2");
        var waited = WaitFor(() => ReadIni(ini, "data-separation-list") == "2,4", 4000);
        r.Add("data-separation-list 4,2 写盘成 2,4(排序去重)", waited, "实际 " + ReadIni(ini, "data-separation-list"));

        ms.SendCmd("data-separation-list 4,99");
        var rejected = WaitFor(() => ms.Out.Contains("不在 countdataseparationkey.ini", StringComparison.Ordinal), 4000);
        r.Add("不存在的键号被拒", rejected, "没看到拒绝日志");
        r.Add("被拒后文件没被改坏", ReadIni(ini, "data-separation-list") == "2,4", "实际 " + ReadIni(ini, "data-separation-list"));

        ms.SendCmd("data-separation 3");
        var badSwitch = WaitFor(() => ms.Out.Contains("只能是 0 或 1", StringComparison.Ordinal), 4000);
        r.Add("data-separation 只收 0/1", badSwitch, "没看到拒绝日志");

        // 免确认的两个开关:缺配置启动时就该写默认 30 / 0
        r.Add("启动写了默认 persistdays=30", ReadIni(ini, "persistdays") == "30", "实际 " + ReadIni(ini, "persistdays"));

        ms.SendCmd("persistdays 7");
        r.Add("persistdays 7 写盘", WaitFor(() => ReadIni(ini, "persistdays") == "7", 4000), "实际 " + ReadIni(ini, "persistdays"));

        ms.SendCmd("persistdays -1");
        var badDays = WaitFor(() => ms.Out.Contains("范围 0-3650", StringComparison.Ordinal), 4000);
        r.Add("persistdays 负数被拒", badDays, "没看到拒绝日志");
        r.Add("被拒后 persistdays 还是 7", ReadIni(ini, "persistdays") == "7", "实际 " + ReadIni(ini, "persistdays"));

        ms.SendCmd("confirmalways 1");
        r.Add("confirmalways 1 写盘", WaitFor(() => ReadIni(ini, "confirmalways") == "1", 4000), "实际 " + ReadIni(ini, "confirmalways"));

        ms.SendCmd("confirmalways 2");
        var badAlways = WaitFor(() => ms.Out.Contains("只能是 1(每次都要确认)", StringComparison.Ordinal), 4000);
        r.Add("confirmalways 只收 0/1", badAlways, "没看到拒绝日志");

        ms.SendCmd("exitc");
    }

    /// <summary>首次启动会自建的两个文件,以及注释头。</summary>
    private static void StartupFiles(SuiteResult r, string dir)
    {
        var table = Path.Combine(dir, "countdataseparationkey.ini");
        r.Add("自建 countdataseparationkey.ini", File.Exists(table), table);
        if (File.Exists(table))
        {
            var text = "";
            try
            {
                using var fs = new FileStream(table, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                text = reader.ReadToEnd();
            }
            catch (IOException)
            {
                //服务端正写在中途,下面的断言会以空串判失败并带上原因
            }
            r.Add("键表里有 4=bind", text.Contains("4=bind", StringComparison.OrdinalIgnoreCase) || text.Contains("4 = bind", StringComparison.OrdinalIgnoreCase), FirstLine(text));
        }

        var state = Path.Combine(dir, "Data", "separation-state.ini");
        r.Add("自建 Data/separation-state.ini(迁移快照)", File.Exists(state), state);
    }

    /// <summary>
    /// 登录后的日志:凡是打 IP 的地方要顺带写「[账号#ID/名称]」,没登录才只剩 IP。
    /// 注册 + 登录 + 断开各会打一条,断开那条走的是 Where(conn) 的「已登录」分支。
    /// </summary>
    private static async Task AccountInLogAsync(SuiteResult r, string dir)
    {
        const string user = "日志账号";
        const string pass = "Log-9f3a!";
        string ack;

        using (var probe = await Probe.ConnectAsync("127.0.0.1", Port, "OS"))
        {
            var registered = await probe.RegisterAsync(user, pass, "m-log", 8000, "b0.4.0");
            ack = probe.LastAck ?? "";
            r.Add("注册测试账号(给日志用)", registered, ack);
            if (!registered)
            {
                return;
            }

            var logged = await probe.LoginAsync(user, pass, "m-log", Protocol.RegisterLevel, "zxms", 8000, "b0.4.0");
            r.Add("登录测试账号", logged, probe.LastAck ?? "");
        }   //probe 释放 → 连接断开,再打一条带账号的「断开」日志

        var logDir = Path.Combine(dir, "logs");
        var hit = WaitFor(() => LogContains(logDir, "[账号#"), 6000);
        r.Add("登录后日志里 IP 旁带 [账号#ID/名称]", hit, hit ? "" : "6 秒内没在日志里看到 [账号#");

        var named = WaitFor(() => LogContains(logDir, user), 3000);
        r.Add("日志里带上了账号名称", named, named ? "" : "没看到 " + user);

        // 网络层自己那条「客户端断开」也要带账号(它在注销登记表之前打)
        var netLine = WaitFor(() => LogHasLine(logDir, "[Network] 客户端断开", "[账号#"), 4000);
        r.Add("网络层的断开日志也带账号", netLine, netLine ? "" : "没在 [Network] 客户端断开 那行看到 [账号#");
    }

    /// <summary>日志里有没有一行同时包含两段话(服务端随时在写,必须共享读)。</summary>
    private static bool LogHasLine(string logDir, string first, string second)
    {
        if (!Directory.Exists(logDir))
        {
            return false;
        }

        foreach (var file in Directory.GetFiles(logDir, "*.log", SearchOption.AllDirectories))
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Contains(first, StringComparison.Ordinal) && line.Contains(second, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch (IOException)
            {
                //正在写,下一轮再看
            }
        }

        return false;
    }

    /// <summary>日志目录里有没有哪个文件包含这段话(服务端随时在写,必须共享读)。</summary>
    private static bool LogContains(string logDir, string text)
    {
        if (!Directory.Exists(logDir))
        {
            return false;
        }

        foreach (var file in Directory.GetFiles(logDir, "*.log", SearchOption.AllDirectories))
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                if (reader.ReadToEnd().Contains(text, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                //正在写,下一轮再看
            }
        }

        return false;
    }

    /// <summary>
    /// 「每次都要确认」开着启动:持久登录表要被清空,让所有人下次进服都重新点一次。
    /// 另起一个实例单测这一条,免得影响上面那个已经跑起来的服务端。
    /// </summary>
    private static void StartupClearsPersistent(SuiteResult r)
    {
        var clearPort = Port + 10;
        string dir2;
        try
        {
            dir2 = Svc.PrepareBin(DebugMs, Path.Combine(Ws, "_zxt", "persist-clear"));
            File.WriteAllLines(Path.Combine(dir2, "PersistentLogin.txt"), ["老玩家|1.2.3.4|2026/01/01 00:00:00"]);
        }
        catch (Exception ex)
        {
            r.Add("准备清空测试目录", false, ex.Message);
            return;
        }

        using var ms2 = Svc.Start(Path.Combine(dir2, "ZincXManagerServer.exe"), dir2, new Dictionary<string, string>
        {
            ["configver"] = "2",
            ["connectOS"] = "0",
            ["linkname"] = "0",
            ["name"] = "CSharp-清空服",
            ["czport"] = clearPort.ToString(),
            ["confirmalways"] = "1",
        });

        if (!ms2.WaitPort(clearPort))
        {
            r.Add("清空测试实例起来了", false, "15 秒没监听 " + clearPort);
            return;
        }

        var file = Path.Combine(dir2, "PersistentLogin.txt");
        var cleared = WaitFor(() => ReadAll(file).Trim().Length == 0, 6000);
        r.Add("启动时 confirmalways=1 清空了持久登录表", cleared, cleared ? "" : "文件里还有:" + ReadAll(file));
        // 这条走 LoginStore.Log,落的是 loginlog.log,不在控制台输出里
        var logged = WaitFor(() => ReadAll(Path.Combine(dir2, LoginStore.LogFile)).Contains("已清空持久登录表", StringComparison.Ordinal), 4000);
        r.Add("清空时打了日志", logged, "没看到日志");
    }

    /// <summary>整文件读成字符串(服务端可能正在写,必须共享读)。</summary>
    private static string ReadAll(string file)
    {
        if (!File.Exists(file))
        {
            return "";
        }

        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    /// <summary>轮询等一个条件成立(控制台输出与文件落盘都不是即时的)。</summary>
    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (condition()) { return true; }
            System.Threading.Thread.Sleep(120);
        }
        return condition();
    }

    /// <summary>读 config.ini 里一个键的原值(没有就返回空串)。</summary>
    /// <remarks>服务端随时可能在写这个文件,必须用 FileShare.ReadWrite 打开,否则会撞锁。</remarks>
    private static string ReadIni(string file, string key)
    {
        if (!File.Exists(file)) { return ""; }
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                var i = line.IndexOf('=');
                if (i <= 0) { continue; }
                if (line[..i].Trim() == key) { return line[(i + 1)..].Trim(); }
            }
        }
        catch (IOException)
        {
            //服务端正写在中途,让调用方下一轮再读
        }
        return "";
    }

    /// <summary>取第一行非空内容,失败详情里用。</summary>
    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().Length > 0) { return line.Trim(); }
        }
        return "";
    }
}
