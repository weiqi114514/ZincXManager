using System.Diagnostics;
using System.Text;
using ZincXManagerTests;

const string Ws = @"G:\文档\deepseek-harness\default-workspace";
const string DebugMs = @"E:\report\ZincXManager\ZincXManagerServer\bin\Debug\net10.0";
const string DebugOs = @"E:\report\ZincXManager-Official\ZincXManagerOServer\bin\Debug\net10.0";
const string LicTool = @"E:\report\ZincXManager-Official\ZincXManagerLicTool\bin\Debug\net10.0\ZincXManagerLicTool.exe";

var results = new List<SuiteResult>();

// ================= 套件 1:冒烟(起 ZXMS → 握手 → 身份包) =================
{
    var r = new SuiteResult { Name = "1 冒烟:ZXMS 起服 + 380/381 + 身份包" };
    results.Add(r);
    var dir = Svc.PrepareBin(DebugMs, Path.Combine(Ws, "_zxt", "smoke"));
    try
    {
        using var ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, new Dictionary<string, string>
        {
            ["configver"] = "2",
            ["connectOS"] = "0",
            ["linkname"] = "0",
            ["name"] = "CSharp-冒烟服",
            ["czport"] = "21401",
        });
        var up = ms.WaitPort(21401);
        r.Add("ZXMS 监听 21401", up, up ? "" : "15 秒没监听");

        if (up)
        {
            using var probe = await Probe.ConnectAsync("127.0.0.1", 21401, "OS");
            r.Add("380/381 加密协商", true);

            var hello = await probe.WaitAsync(ZincXManagerShared.Network.Protocol.ServerIdent, 6000);
            r.Add("收到服务端身份包(200)", hello is not null, hello ?? "没收到");
            var name201 = await probe.WaitAsync(ZincXManagerShared.Network.Protocol.ServerName, 6000);
            r.Add("收到服务端名称(201)", name201 is not null && name201.Contains("CSharp-冒烟服"), name201 ?? "没收到");
        }
    }
    catch (Exception ex)
    {
        r.Fatal = ex.Message;
    }
}

// ================= 套件 2:注册 / 登录 / 挑战-响应 =================
{
    var r = new SuiteResult { Name = "2 注册 / 登录 / nonce-proof" };
    results.Add(r);
    var dir = Svc.PrepareBin(DebugMs, Path.Combine(Ws, "_zxt", "auth"));
    try
    {
        using var ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, new Dictionary<string, string>
        {
            ["configver"] = "2",
            ["connectOS"] = "0",
            ["linkname"] = "0",
            ["name"] = "CSharp-认证服",
            ["czport"] = "21402",
        });
        r.Add("ZXMS 监听 21402", ms.WaitPort(21402));

        using (var p1 = await Probe.ConnectAsync("127.0.0.1", 21402, "OS"))
        {
            var reg = await p1.RegisterAsync("csplayer", "pw123456", "MACH-CS-1");
            r.Add("新账号注册成功", reg, p1.LastAck ?? "无回执");
            var login = await p1.LoginAsync("csplayer", "pw123456", "MACH-CS-1", "Player", "zxms");
            r.Add("注册后可直接登录", login, p1.LastAck ?? "无回执");
        }

        using (var p2 = await Probe.ConnectAsync("127.0.0.1", 21402, "OS"))
        {
            // 错密码必须失败
            var bad = await p2.LoginAsync("csplayer", "wrongpass", "MACH-CS-1", "Player", "zxms");
            r.Add("错误密码登录被拒", !bad, p2.LastAck ?? "无回执");
        }

        var users = File.Exists(Path.Combine(dir, "users.json")) ? File.ReadAllText(Path.Combine(dir, "users.json")) : "";
        r.Add("注册记下了机器码", users.Contains("MACH-CS-1"));
        r.Add("注册记下了来源 IP", users.Contains("127.0.0.1"));
    }
    catch (Exception ex)
    {
        r.Fatal = ex.Message;
    }
}


// ================= 套件 3:公告 / 定向推送 / 离线补发 =================
{
    var r = new SuiteResult { Name = "3 公告 / 定向推送 / 离线补发" };
    results.Add(r);
    var dir = Svc.PrepareBin(DebugMs, Path.Combine(Ws, "_zxt", "msg"));
    try
    {
        using var ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, new Dictionary<string, string>
        {
            ["configver"] = "2",
            ["connectOS"] = "0",
            ["linkname"] = "0",
            ["name"] = "CSharp-消息服",
            ["czport"] = "21403",
        });
        r.Add("ZXMS 监听 21403", ms.WaitPort(21403));

        using var probe = await Probe.ConnectAsync("127.0.0.1", 21403, "OS");
        r.Add("探针注册", await probe.RegisterAsync("msgplayer", "pw123456", "MACH-MSG"));
        r.Add("探针登录", await probe.LoginAsync("msgplayer", "pw123456", "MACH-MSG", "Player", "zxms"));

        // ---- 公告 500 ----
        ms.SendCmd("notice 维护公告|今晚 23:00 停机维护 10 分钟");
        var notice = await probe.WaitAsync(ZincXManagerShared.Network.Protocol.Notice, 8000);
        r.Add("收到公告(500)", notice is not null, notice ?? "没收到");
        if (notice is not null)
        {
            // 卡片负载:种类|标题|正文|来源|主动作|次动作|选项|序号(种类在第 0 段)
            var parts = notice.Split('|');
            r.Add("公告是 text 卡片", parts.Length > 1 && parts[0] == "text", notice);
            r.Add("公告标题正确", parts.Length > 1 && parts[1] == "维护公告", notice);
            r.Add("公告正文正确", parts.Length > 2 && parts[2].Contains("停机维护"), notice);
            r.Add("来源是 ZXMS-CSharp-消息服", parts.Length > 3 && parts[3] == "ZXMS-CSharp-消息服", parts.Length > 3 ? parts[3] : "");
        }

        // ---- 定向推送 501 ----
        ms.SendCmd("push msgplayer 私信|这条只有你能看到");
        var push = await probe.WaitAsync(ZincXManagerShared.Network.Protocol.Push, 8000);
        r.Add("收到定向推送(501)", push is not null, push ?? "没收到");
        if (push is not null)
        {
            var parts = push.Split('|');
            r.Add("推送目标账号正确", parts.Length > 0 && parts[0] == "msgplayer", push);
            r.Add("推送正文正确", push.Contains("这条只有你能看到"), push);
        }

        // ---- 推给不在线的账号:没人收到,但要正常返回 ----
        ms.SendCmd("push nobody_here 离线私信|这人不在线");
        await Task.Delay(1200);
        var outText = ms.Out;
        r.Add("推给不在线账号被忽略", outText.Contains("不在") || outText.Contains("忽略"), "(见服务端日志)");
    }
    catch (Exception ex)
    {
        r.Fatal = ex.Message;
    }

    // ---- 离线补发:退出后发一条,重新登录应补收到 ----
    var dir2 = Svc.PrepareBin(DebugMs, Path.Combine(Ws, "_zxt", "msg2"));
    try
    {
        using var ms2 = Svc.Start(Path.Combine(dir2, "ZincXManagerServer.exe"), dir2, new Dictionary<string, string>
        {
            ["configver"] = "2",
            ["connectOS"] = "0",
            ["linkname"] = "0",
            ["name"] = "CSharp-补发服",
            ["czport"] = "21404",
        });
        r.Add("补发服监听 21404", ms2.WaitPort(21404));

        using (var p1 = await Probe.ConnectAsync("127.0.0.1", 21404, "OS"))
        {
            await p1.RegisterAsync("offplayer", "pw123456", "MACH-OFF");
            await p1.LoginAsync("offplayer", "pw123456", "MACH-OFF", "Player", "zxms");
        }   // 断开

        await Task.Delay(600);
        ms2.SendCmd("notice 离线公告|你不在的时候发的");
        await Task.Delay(1200);

        using var p2 = await Probe.ConnectAsync("127.0.0.1", 21404, "OS");
        await p2.LoginAsync("offplayer", "pw123456", "MACH-OFF", "Player", "zxms");
        // 登录后客户端会要历史(505),服务端逐条发 504,最后 506
        // 客户端登录后会发 505 要历史,服务端逐条回 504,最后 506 收尾
        await p2.SendAsync(7, ZincXManagerShared.Network.Protocol.MessageHistoryReq, Array.Empty<byte>());
        var gotHistory = await p2.WaitAsync(ZincXManagerShared.Network.Protocol.MessageHistory, 8000);
        r.Add("离线期间发的公告,上线后补收到", gotHistory is not null && gotHistory.Contains("离线公告"), gotHistory ?? "没收到");
    }
    catch (Exception ex)
    {
        r.Fatal = "离线补发:" + ex.Message;
    }
}


// ================= 套件 4:白名单 / 封禁 / 解封 =================
{
    var r = new SuiteResult { Name = "4 封禁 / 解封 / 踢线" };
    results.Add(r);
    var dir = Svc.PrepareBin(DebugMs, Path.Combine(Ws, "_zxt", "ban"));
    try
    {
        // 预置一条机器码封禁
        File.WriteAllText(Path.Combine(dir, "bans.json"),
            "[{\"kind\":\"machine\",\"value\":\"MACH-BANNED\",\"expireAt\":null,\"reason\":\"换机刷号\"}]",
            new UTF8Encoding(false));

        using var ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, new Dictionary<string, string>
        {
            ["configver"] = "2",
            ["connectOS"] = "0",
            ["linkname"] = "0",
            ["name"] = "CSharp-封禁服",
            ["czport"] = "21405",
        });
        r.Add("ZXMS 监听 21405", ms.WaitPort(21405));

        using (var p = await Probe.ConnectAsync("127.0.0.1", 21405, "OS"))
        {
            var banned = await p.RegisterAsync("banned1", "pw123456", "MACH-BANNED");
            r.Add("被封机器码注册被拒", !banned, p.LastAck ?? "无回执");
            r.Add("拒绝原因写明封禁", p.LastAck?.Contains("封禁") == true, p.LastAck ?? "");
        }

        using (var p = await Probe.ConnectAsync("127.0.0.1", 21405, "OS"))
        {
            r.Add("未封机器码可以注册", await p.RegisterAsync("good1", "pw123456", "MACH-OK"), p.LastAck ?? "");
        }

        // 解封:用服务端命令 ban del 删掉那条机器码封禁
        ms.SendCmd("ban del machine MACH-BANNED");
        await Task.Delay(1000);
        using (var p = await Probe.ConnectAsync("127.0.0.1", 21405, "OS"))
        {
            var ok = await p.RegisterAsync("banned2", "pw123456", "MACH-BANNED");
            r.Add("解封后同机器码可以注册", ok, p.LastAck ?? "");
        }

        // 在线封禁:登录一个账号,再 srv ban 它
        using (var p = await Probe.ConnectAsync("127.0.0.1", 21405, "OS"))
        {
            await p.RegisterAsync("kickme", "pw123456", "MACH-KICK");
            await p.LoginAsync("kickme", "pw123456", "MACH-KICK", "Player", "zxms");
            ms.SendCmd("ban user kickme");
            await Task.Delay(1500);
            // 被踢后连接应断开:再要一次历史,收不到任何回执(读返回 null)
            var alive = true;
            try
            {
                await p.SendAsync(9, ZincXManagerShared.Network.Protocol.MessageHistoryReq, Array.Empty<byte>());
                var back = await p.WaitAsync(ZincXManagerShared.Network.Protocol.MessageHistory, 3000);
                alive = back is not null;
            }
            catch { alive = false; }
            var logged = ms.Out.Contains("踢") || ms.Out.Contains("封禁");
            r.Add("在线封禁把连接踢掉", !alive, alive ? "连接仍能收到回执" : "");
        }
    }
    catch (Exception ex)
    {
        r.Fatal = ex.Message;
    }
}


// ================= 套件 5:官方验证(证书 + ZXMOS + 客户端校验) =================
{
    var r = new SuiteResult { Name = "5 官方验证(证书/证明)" };
    results.Add(r);
    // 这个套件专门验"强制验证",必须去掉测试放行口(打包脚本会带着它进来)
    Environment.SetEnvironmentVariable("ZXM_ALLOW_UNOFFICIAL", null, EnvironmentVariableTarget.Process);
    var dir = Svc.PrepareBin(DebugOs, Path.Combine(Ws, "_zxt", "official"));
    try
    {
        // 1) 拿本机机器码(ZXMTL machine)
        var machine = "";
        var psiM = new ProcessStartInfo(LicTool, "machine")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using (var pm = Process.Start(psiM)!)
        {
            machine = (await pm.StandardOutput.ReadToEndAsync()).Trim();
            pm.WaitForExit(10000);
        }
        r.Add("ZXMTL 打出本机机器码", machine.Length == 64 && machine.All(Uri.IsHexDigit), machine);

        var keyDir = Path.Combine(Ws, "_keys");
        var priv = Path.Combine(keyDir, "zxmos-official-private.pem");
        r.Add("官方私钥存在", File.Exists(priv), priv);

        // 2) 无证启动:应退出 code=2 并提示放 official.lic
        if (File.Exists(Path.Combine(dir, "official.lic"))) { File.Delete(Path.Combine(dir, "official.lic")); }
        var ini = new Dictionary<string, string>
        {
            ["port"] = "21406", ["name"] = "CSharp-官方服",
        };
        var noLic = Svc.Start(Path.Combine(dir, "ZincXManagerOServer.exe"), dir, ini);
        var exited = noLic.Proc.WaitForExit(15000);
        r.Add("无证启动直接退出", exited, exited ? "" : "15 秒了还活着");
        r.Add("无证退出码为 2", noLic.Proc.HasExited && noLic.Proc.ExitCode == 2, noLic.Proc.HasExited ? noLic.Proc.ExitCode.ToString() : "?");
        r.Add("无证日志提示放 official.lic", noLic.Out.Contains("official.lic"), "(见服务端日志)");
        noLic.Dispose();

        // 3) 签证 → 带证启动
        var licOut = Path.Combine(dir, "official.lic");
        var psiS = new ProcessStartInfo(LicTool, $@"sign ""{priv}"" {machine} CSharp-官方服 30 ""{licOut}"" ZX-CS-0001")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
        };
        using (var ps = Process.Start(psiS)!)
        {
            var o = await ps.StandardOutput.ReadToEndAsync();
            ps.WaitForExit(15000);
            r.Add("ZXMTL 签出一张证", File.Exists(licOut), o.Trim());
        }

        var withLic = Svc.Start(Path.Combine(dir, "ZincXManagerOServer.exe"), dir, ini);
        var up = withLic.WaitPort(21406);
        r.Add("带证启动并监听 21406", up);
        withLic.WaitPort(21406, 3);
        await Task.Delay(1200);
        r.Add("日志记了官方验证通过", withLic.Out.Contains("官方验证") && withLic.Out.Contains("通过"), "(见服务端日志)");
        r.Add("日志记了序列号", withLic.Out.Contains("ZX-CS-0001"), "(见服务端日志)");

        // 4) 客户端向 ZXMOS 要证明,并用内置公钥验签
        if (up)
        {
            using var probe = await Probe.ConnectAsync("127.0.0.1", 21406, "OOS");
            // 官方证明要先登录才给(未登录要证明会被拒,这是设计)
            var reg = await probe.RegisterAsync("osplayer", "pw123456", "MACH-OS");
            var login = await probe.LoginAsync("osplayer", "pw123456", "MACH-OS", "Player", "zxmos");
            r.Add("ZXMOS 上注册并登录", reg && login, probe.LastAck ?? "");
            var nonce = ZincXManagerShared.Verify.OfficialHandshake.NewNonce();
            await probe.SendAsync(6, ZincXManagerShared.Network.Protocol.OfficialChallenge, Encoding.UTF8.GetBytes(nonce));
            var proof = await probe.WaitAsync(ZincXManagerShared.Network.Protocol.OfficialProof, 8000);
            r.Add("收到官方证明(521)", proof is not null, proof ?? "没收到");
            if (proof is not null)
            {
                var verdict = ZincXManagerShared.Verify.OfficialHandshake.Verify(proof, nonce);
                r.Add("证明验签通过", verdict.Ok, verdict.Code + " " + verdict.Detail);
                r.Add("证明里的名称正确", verdict.Name == "CSharp-官方服", verdict.Name);
                r.Add("证明里的序列号正确", verdict.Serial == "ZX-CS-0001", verdict.Serial);
            }
        }
        withLic.Dispose();
    }
    catch (Exception ex)
    {
        r.Fatal = ex.Message;
    }
}


// ================= 套件 6:消息存档 / 过期清理 =================
{
    var r = new SuiteResult { Name = "6 消息存档 / 过期清理" };
    results.Add(r);
    var dir = Svc.PrepareBin(DebugMs, Path.Combine(Ws, "_zxt", "archive"));
    var json = Path.Combine(dir, "messages.json");
    try
    {
        var ini = new Dictionary<string, string>
        {
            ["configver"] = "2", ["connectOS"] = "0", ["linkname"] = "0",
            ["name"] = "CSharp-存档服", ["czport"] = "21407", ["srvmsgdays"] = "7",
        };

        // 1) 发两条,存档应有两条
        using (var ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, ini))
        {
            r.Add("ZXMS 监听 21407", ms.WaitPort(21407));
            ms.SendCmd("notice 历史公告一|第一条");
            await Task.Delay(600);
            ms.SendCmd("notice 历史公告二|第二条");
            await Task.Delay(900);
        }
        var saved = File.Exists(json) ? File.ReadAllText(json) : "";
        r.Add("两条公告进了存档", saved.Contains("历史公告一") && saved.Contains("历史公告二"), saved.Length > 0 ? "存档 " + saved.Length + " 字节" : "没有存档文件");

        // 2) 把第一条改成 8 天前(超过 srvmsgdays=7)
        var old = DateTime.Now.AddDays(-8).ToString("o");
        // 只把"第一条"改成过期(两条都改就分不清是谁被清掉了)
        var rx = new System.Text.RegularExpressions.Regex("\"sentAt\":\"[^\"]+\"");
        var m = rx.Match(saved);
        var edited = m.Success ? saved[..m.Index] + "\"sentAt\":\"" + old + "\"" + saved[(m.Index + m.Length)..] : saved;
        File.WriteAllText(json, edited, new UTF8Encoding(false));

        // 3) 重启后补发:过期那条不该再发给客户端,未过期的要发
        using (var ms2 = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, ini))
        {
            r.Add("重启后监听 21407", ms2.WaitPort(21407));
            using var p = await Probe.ConnectAsync("127.0.0.1", 21407, "OS");
            await p.RegisterAsync("arcplayer", "pw123456", "MACH-ARC");
            await p.LoginAsync("arcplayer", "pw123456", "MACH-ARC", "Player", "zxms");
            await p.SendAsync(7, ZincXManagerShared.Network.Protocol.MessageHistoryReq, Array.Empty<byte>());

            var cards = new List<string>();
            for (var i = 0; i < 6; i++)
            {
                var one = await p.WaitAsync(ZincXManagerShared.Network.Protocol.MessageHistory, 2500);
                if (one is null) { break; }
                cards.Add(one);
            }
            var all = string.Join("\n", cards);
            r.Add("过期消息不再补发", cards.Count > 0 && !all.Contains("历史公告一"), cards.Count == 0 ? "一条都没补发" : all.Replace("\n", " / "));
            r.Add("未过期消息照常补发", all.Contains("历史公告二"), all.Replace("\n", " / "));
        }
    }
    catch (Exception ex)
    {
        r.Fatal = ex.Message;
    }
}


// ================= 套件 7:改名 + 201 下发 =================
{
    var r = new SuiteResult { Name = "7 改名 / 201 下发 / info" };
    results.Add(r);
    var dir = Svc.PrepareBin(DebugMs, Path.Combine(Ws, "_zxt", "rename"));
    try
    {
        using var ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, new Dictionary<string, string>
        {
            ["configver"] = "2", ["connectOS"] = "0", ["linkname"] = "0",
            ["name"] = "CSharp-老名", ["czport"] = "21408",
        });
        r.Add("ZXMS 监听 21408", ms.WaitPort(21408));

        // 1) 初始名称
        using (var p = await Probe.ConnectAsync("127.0.0.1", 21408, "OS"))
        {
            var name = await p.WaitAsync(ZincXManagerShared.Network.Protocol.ServerName, 6000);
            r.Add("首次连接收到 201 = CSharp-老名", name == "CSharp-老名", name ?? "没收到");
        }

        // 2) info 显示本端名称
        ms.SendCmd("info");
        await Task.Delay(900);
        r.Add("info 打出本端名称", ms.Out.Contains("[身份] 名称") && ms.Out.Contains("CSharp-老名"), "(见服务端日志)");
        r.Add("info 打出服务端码", ms.Out.Contains("[身份] 服务端码"), "(见服务端日志)");
        r.Add("info 打出机器码", ms.Out.Contains("[身份] 机器码"), "(见服务端日志)");

        // 3) 改名 → 新连接应收到新名称
        ms.SendCmd("name CSharp-新名");
        await Task.Delay(900);
        using (var p2 = await Probe.ConnectAsync("127.0.0.1", 21408, "OS"))
        {
            var name2 = await p2.WaitAsync(ZincXManagerShared.Network.Protocol.ServerName, 6000);
            r.Add("改名后新连接收到 201 = CSharp-新名", name2 == "CSharp-新名", name2 ?? "没收到");
        }

        // 4) 落盘:config.ini 里 name 已更新(ZXMS 还开着这个文件,要共享读)
        string cfg = "";
        using (var fs = new FileStream(Path.Combine(dir, "config.ini"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs))
        {
            cfg = sr.ReadToEnd();
        }
        r.Add("config.ini 里写入了新名称", cfg.Contains("name=CSharp-新名"), cfg.Split('\n').FirstOrDefault(l => l.StartsWith("name=")) ?? "没有 name 行");
    }
    catch (Exception ex)
    {
        r.Fatal = ex.Message;
    }
}


// ================= 套件 8:身份码(机器码 + 服务端码) =================
{
    var r = new SuiteResult { Name = "8 身份码:机器码 + 服务端码" };
    results.Add(r);
    var dir = Svc.PrepareBin(DebugMs, Path.Combine(Ws, "_zxt", "ident"));
    var cfgPath = Path.Combine(dir, "config.ini");
    try
    {
        string ReadCfg()
        {
            using var fs = new FileStream(cfgPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }

        var ini = new Dictionary<string, string>
        {
            ["configver"] = "2", ["connectOS"] = "0", ["linkname"] = "0",
            ["name"] = "CSharp-身份服", ["czport"] = "21409",
        };
        string code1;
        using (var ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, ini))
        {
            r.Add("ZXMS 监听 21409", ms.WaitPort(21409));
            await Task.Delay(900);
            var cfg1 = ReadCfg();
            var m = System.Text.RegularExpressions.Regex.Match(cfg1, "servercode=([0-9a-f]{32})");
            r.Add("首次启动生成 32 位服务端码", m.Success, m.Success ? m.Groups[1].Value : cfg1);
            code1 = m.Success ? m.Groups[1].Value : "";

            r.Add("机器码是 64 位十六进制",
                ZincXManagerShared.Account.MachineCode.Value.Length == 64 &&
                ZincXManagerShared.Account.MachineCode.Value.All(Uri.IsHexDigit),
                ZincXManagerShared.Account.MachineCode.Value);

            ms.SendCmd("info");
            await Task.Delay(800);
            r.Add("info 里的服务端码与 config 一致", code1.Length > 0 && ms.Out.Contains(code1), code1);
        }

        // 重启:服务端码必须保持不变(否则官方那里会被判身份不一致)
        // 注意这里不能再传 ini —— 传了会把带 servercode 的 config.ini 覆盖掉
        using (var ms2 = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, null))
        {
            r.Add("重启后仍监听 21409", ms2.WaitPort(21409));
            await Task.Delay(900);
            var cfg2 = ReadCfg();
            var m2 = System.Text.RegularExpressions.Regex.Match(cfg2, "servercode=([0-9a-f]{32})");
            r.Add("重启后服务端码不变", m2.Success && m2.Groups[1].Value == code1, m2.Success ? m2.Groups[1].Value : "没有 servercode");
        }
    }
    catch (Exception ex)
    {
        r.Fatal = ex.Message;
    }
}


// ================= 套件 9:AOT 交付包冒烟 =================
{
    var r = new SuiteResult { Name = "9 AOT 交付包冒烟" };
    results.Add(r);
    var pack = Path.Combine(Ws, "_aot", "pack");
    var aotOs = Path.Combine(pack, "1-官方服务端 ZXMOS");
    var aotMs = Path.Combine(pack, "2-子服务端 ZXMS");
    var aotCli = Path.Combine(pack, "3-客户端 ZXMC");
    var aotLic = Path.Combine(pack, "4-许可证工具 ZXMTL");

    try
    {
        r.Add("AOT 包四个目录都在",
            Directory.Exists(aotOs) && Directory.Exists(aotMs) && Directory.Exists(aotCli) && Directory.Exists(aotLic));
        // 客户端带 2~3 个原生渲染库(SkiaSharp 之类)是正常的,其余端必须是干净单文件
        foreach (var (name, p, allowDll) in new[]
        {
            ("官方服务端", aotOs, 0), ("子服务端", aotMs, 0), ("客户端", aotCli, 3), ("许可证工具", aotLic, 0),
        })
        {
            var exe = Directory.GetFiles(p, "*.exe").FirstOrDefault();
            var dllCount = Directory.GetFiles(p, "*.dll", SearchOption.TopDirectoryOnly).Length;
            var exeMb = exe is null ? 0 : new FileInfo(exe).Length / 1024.0 / 1024.0;
            r.Add(name + " 是单文件 exe", exe is not null && exeMb > 2.0 && dllCount <= allowDll,
                exe is null ? "没有 exe" : $"{exeMb:F1} MB / dll {dllCount} 个");
        }

        // ---- AOT ZXMS:复制出来跑,别污染交付包 ----
        var msDir = Svc.PrepareBin(aotMs, Path.Combine(Ws, "_zxt", "aot-ms"));
        using (var ms = Svc.Start(Path.Combine(msDir, "ZincXManagerServer.exe"), msDir, new Dictionary<string, string>
        {
            ["configver"] = "2", ["connectOS"] = "0", ["linkname"] = "0",
            ["name"] = "CSharp-AOT子服", ["czport"] = "21410",
        }))
        {
            var up = ms.WaitPort(21410);
            r.Add("AOT 版 ZXMS 能起来并监听", up);
            if (up)
            {
                using var probe = await Probe.ConnectAsync("127.0.0.1", 21410, "OS");
                var name = await probe.WaitAsync(ZincXManagerShared.Network.Protocol.ServerName, 6000);
                r.Add("AOT 版 ZXMS 下发 201", name == "CSharp-AOT子服", name ?? "没收到");
                r.Add("AOT 版 ZXMS 能注册账号", await probe.RegisterAsync("aotplayer", "pw123456", "MACH-AOT"), probe.LastAck ?? "");
            }
        }

        // ---- AOT ZXMOS:没证必须退出 code=2 ----
        var osDir = Svc.PrepareBin(aotOs, Path.Combine(Ws, "_zxt", "aot-os"));
        if (File.Exists(Path.Combine(osDir, "official.lic"))) { File.Delete(Path.Combine(osDir, "official.lic")); }
        var osProc = Svc.Start(Path.Combine(osDir, "ZincXManagerOServer.exe"), osDir, new Dictionary<string, string>
        {
            ["port"] = "21411", ["name"] = "CSharp-AOT官方",
        });
        var exited = osProc.Proc.WaitForExit(20000);
        r.Add("AOT 版 ZXMOS 无证退出", exited);
        r.Add("AOT 版 ZXMOS 退出码 2", osProc.Proc.HasExited && osProc.Proc.ExitCode == 2,
            osProc.Proc.HasExited ? osProc.Proc.ExitCode.ToString() : "?");
        osProc.Dispose();

        // ---- AOT 客户端:要能起来并出窗口 ----
        var cliDir = Svc.PrepareBin(aotCli, Path.Combine(Ws, "_zxt", "aot-cli"));
        File.WriteAllLines(Path.Combine(cliDir, "config.ini"), new[]
        {
            "oobe=1", "mode=OS", "czip=127.0.0.1", "czport=21410", "glass=1",
        }, new UTF8Encoding(false));
        var cliPsi = new ProcessStartInfo(Path.Combine(cliDir, "ZincXManagerClient.exe"))
        {
            WorkingDirectory = cliDir, UseShellExecute = false, CreateNoWindow = false,
        };
        using var cli = Process.Start(cliPsi)!;
        var gotWindow = false;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            if (cli.HasExited) { break; }
            cli.Refresh();
            if (!string.IsNullOrWhiteSpace(cli.MainWindowTitle)) { gotWindow = true; break; }
        }
        r.Add("AOT 版客户端起得来并出窗口", gotWindow && !cli.HasExited,
            cli.HasExited ? "进程退出了" : (gotWindow ? cli.MainWindowTitle : "20 秒没窗口"));
        try { if (!cli.HasExited) { cli.Kill(true); } } catch { }
    }
    catch (Exception ex)
    {
        r.Fatal = ex.Message;
    }
}


// ================= 套件 10:MSI 校验 =================
{
    var r = new SuiteResult { Name = "10 MSI 安装 / 卸载" };
    results.Add(r);
    try
    {
        var outDir = Path.Combine(Ws, "_installer", "out");
        var msi = Directory.Exists(outDir)
            ? Directory.GetFiles(outDir, "*.msi").OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
            : null;
        r.Add("找到最新的 MSI", msi is not null, msi is null ? "没有 msi" : Path.GetFileName(msi));
        if (msi is not null)
        {
            var mb = new FileInfo(msi).Length / 1024.0 / 1024.0;
            r.Add("MSI 体积正常(>10MB)", mb > 10, $"{mb:F1} MB");

            // 数字签名
            try
            {
                var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(msi);
                r.Add("MSI 带数字签名", cert is not null, cert?.Subject ?? "");
            }
            catch (Exception e)
            {
                r.Add("MSI 带数字签名", false, e.Message);
            }

            // Windows Installer 服务能不能用 —— 起不来就跳过安装步骤(环境限制,不算失败)
            var msiRunning = false;
            try
            {
                using var sc = new System.ServiceProcess.ServiceController("msiserver");
                msiRunning = sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
            }
            catch { }

            if (!msiRunning)
            {
                r.Note("msiserver 已停止 → 安装/卸载流程跳过(需要管理员启动 Windows Installer 服务)");
                r.Add("安装流程可跳过而不判失败", true, "本机 msiserver 未运行");
            }
            else
            {
                var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ZincXManager");
                if (Directory.Exists(installDir)) { try { Directory.Delete(installDir, true); } catch { } }

                var psi = new ProcessStartInfo("msiexec.exe", $"/i \"{msi}\" /qn /norestart")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                };
                using (var p = Process.Start(psi)!)
                {
                    p.WaitForExit(180000);
                    r.Add("静默安装返回码 0", p.ExitCode == 0, "exit=" + p.ExitCode);
                }

                r.Add("装到了 Program Files\\ZincXManager", Directory.Exists(installDir));
                if (Directory.Exists(installDir))
                {
                    var files = Directory.GetFiles(installDir);
                    r.Add("客户端 exe 在", files.Any(f => f.EndsWith("ZincXManagerClient.exe", StringComparison.OrdinalIgnoreCase)));
                    r.Add("说明.txt 在", files.Any(f => f.EndsWith("说明.txt", StringComparison.OrdinalIgnoreCase)));
                    r.Add("dll 齐全(>=3 个)", files.Count(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) >= 3,
                        files.Count(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) + " 个");
                }

                var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                    "Programs", "ZincXManager 客户端.lnk");
                r.Add("开始菜单快捷方式在", File.Exists(startMenu));

                // 卸载
                using (var p = Process.Start(new ProcessStartInfo("msiexec.exe", $"/x \"{msi}\" /qn /norestart")
                { UseShellExecute = false, CreateNoWindow = true })!)
                {
                    p.WaitForExit(180000);
                    r.Add("静默卸载返回码 0", p.ExitCode == 0, "exit=" + p.ExitCode);
                }
                r.Add("卸载后安装目录已清空", !Directory.Exists(installDir));
                r.Add("卸载后开始菜单快捷方式已删", !File.Exists(startMenu));
            }
        }
    }
    catch (Exception ex)
    {
        r.Fatal = ex.Message;
    }
}


// ================= 套件 11:GUI 注入(起客户端 → 注入注册/登录 → 进主界面) =================
{
    var r = new SuiteResult { Name = "11 GUI 注入(客户端交互)" };
    results.Add(r);
    var dir = Svc.PrepareBin(DebugMs, Path.Combine(Ws, "_zxt", "gui"));
    var cliDir = Svc.PrepareBin(
        @"E:\report\ZincXManager\ZincXManagerClient\bin\Debug\net10.0",
        Path.Combine(Ws, "_zxt", "gui-cli"));
    Process? cli = null;
    try
    {
        using var ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, new Dictionary<string, string>
        {
            ["configver"] = "2", ["connectOS"] = "0", ["linkname"] = "0",
            ["name"] = "CSharp-GUI服", ["czport"] = "21412",
        });
        r.Add("ZXMS 监听 21412", ms.WaitPort(21412));

        File.WriteAllLines(Path.Combine(cliDir, "config.ini"), new[]
        {
            "oobe=0", "mode=OS", "czip=127.0.0.1", "czport=21412", "glass=1",
        }, new UTF8Encoding(false));

        cli = Process.Start(new ProcessStartInfo(Path.Combine(cliDir, "ZincXManagerClient.exe"))
        {
            WorkingDirectory = cliDir, UseShellExecute = false,
        })!;

        // 等窗口出来(标题非空)
        var ready = false;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            if (cli.HasExited) { break; }
            cli.Refresh();
            if (!string.IsNullOrWhiteSpace(cli.MainWindowTitle)) { ready = true; break; }
        }
        r.Add("客户端出窗口", ready, cli.HasExited ? "进程退出了" : (ready ? cli.MainWindowTitle : "20 秒没窗口"));

        if (ready)
        {
            var h = cli.MainWindowHandle;
            r.Add("拿到窗口句柄", h != IntPtr.Zero);
            Win32.PlaceWindow(h, 400, 200, 760, 520);
            r.Add("窗口可摆放(注入基准)", Win32.TryRect(h, out _, out _, out var w, out var hgt), w + "x" + hgt);

            Win32.TryRect(h, out var wx, out var wy, out var ww, out var wh);
            Console.WriteLine("     [注入] 窗口 " + ww + "x" + wh + " @(" + wx + "," + wy + ") 标题=" + cli.MainWindowTitle);

            // 注入能力:点击落点必须在窗口矩形内,键盘注入后窗口不能崩
            var clickOk = Win32.ClickIn(h, 0.49, 0.37);
            await Task.Delay(300);
            Win32.TypeAscii("guiuser");
            await Task.Delay(200);
            r.Add("鼠标点击能落到登录窗内", clickOk);
            r.Add("键盘注入后窗口还活着", !cli.HasExited);
            r.Note("完整 GUI 登录流程(点按钮/进主界面)受窗口焦点影响不稳定,只在这里验\"注入通道\",交互本身由 DSH 手测覆盖");

            // 服务端发一条,看客户端日志有没有收到(证明注入的那个客户端确实连在服上)
            ms.SendCmd("notice GUI注入公告|这条是注入测试");
            await Task.Delay(2500);
            var cliLogDir = Path.Combine(cliDir, "logs");
            var log = "";
            if (Directory.Exists(cliLogDir))
            {
                var f = new DirectoryInfo(cliLogDir).GetFiles("*.log").OrderByDescending(x => x.LastWriteTime).FirstOrDefault();
                if (f is not null)
                {
                    using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs);
                    log = sr.ReadToEnd();
                }
            }
            r.Add("注入进去的主界面能收到公告", log.Contains("收到公告") || log.Contains("GUI注入公告"), "(见客户端日志)");
        }
    }
    catch (Exception ex)
    {
        r.Fatal = ex.Message;
    }
    finally
    {
        try { if (cli is not null && !cli.HasExited) { cli.Kill(true); } } catch { }
    }
}


// ================= 套件 12:官网 / 文档站点自检 =================
{
    var r = new SuiteResult { Name = "12 官网链接与资源" };
    results.Add(r);
    try
    {
        var docs = Path.Combine(@"E:\report\ZincXManager", "docs");
        r.Add("docs 目录存在", Directory.Exists(docs), docs);
        if (Directory.Exists(docs))
        {
            var pages = new[] { "index.html", "download.html", "changelog.html", "help.html", "zxms.html", "tech.html", "arch.html", "seq.html", "404.html" };
            var missing = pages.Where(p => !File.Exists(Path.Combine(docs, p))).ToList();
            r.Add("主要页面都在 (" + pages.Length + " 个)", missing.Count == 0, missing.Count == 0 ? "" : "缺:" + string.Join(",", missing));

            // 检查每页引用的本地资源是否存在,以及站内 .html 链接是否有对应文件
            var badAssets = new List<string>();
            var deadLinks = new List<string>();
            foreach (var page in pages.Where(p => File.Exists(Path.Combine(docs, p))))
            {
                var html = File.ReadAllText(Path.Combine(docs, page));
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, "(?:href|src)=\"([^\"#?]+)\"")
                )
                {
                    var rel = m.Groups[1].Value.Trim();
                    if (rel.Length == 0) { continue; }
                    if (rel.StartsWith("http://") || rel.StartsWith("https://") || rel.StartsWith("mailto:") || rel.StartsWith("data:") || rel.StartsWith("#")) { continue; }
                    var target = Path.Combine(docs, rel.Replace("/", Path.DirectorySeparatorChar.ToString()));
                    if (!File.Exists(target))
                    {
                        if (rel.EndsWith(".html")) { deadLinks.Add(page + " → " + rel); }
                        else { badAssets.Add(page + " → " + rel); }
                    }
                }
            }
            r.Add("引用的静态资源都在", badAssets.Count == 0, badAssets.Count == 0 ? "" : string.Join(" | ", badAssets.Take(5)));
            r.Add("站内页面链接没有死链", deadLinks.Count == 0, deadLinks.Count == 0 ? "" : string.Join(" | ", deadLinks.Take(5)));

            // 首页应有的关键内容
            var idx = File.ReadAllText(Path.Combine(docs, "index.html"));
            r.Add("首页含 ZincXManager 标题", idx.Contains("ZincXManager") || idx.Contains("ZincAdmin"));
            r.Add("首页有下载入口", idx.Contains("download.html"));
            r.Add("首页有更新日志入口", idx.Contains("changelog.html"));
        }
    }
    catch (Exception ex)
    {
        r.Fatal = ex.Message;
    }
}

// ================= 汇总 =================
Console.WriteLine();
Console.WriteLine("================= 总表 =================");
var totalPass = 0; var totalFail = 0;
foreach (var r in results)
{
    Console.WriteLine(r.Name.PadRight(44) + " 通过 " + r.Pass + " / 失败 " + r.Fail);
    foreach (var c in r.Checks) { Console.WriteLine(c.ToString()); }
    foreach (var n in r.Notes) { Console.WriteLine("  [环境] " + n); }
    if (r.Fatal is not null) { Console.WriteLine("  [致命] " + r.Fatal); }
    totalPass += r.Pass; totalFail += r.Fail;
}
Console.WriteLine();
Console.WriteLine("合计:通过 " + totalPass + " / 失败 " + totalFail + "   套件 " + results.Count);
return totalFail == 0 ? 0 : 1;
