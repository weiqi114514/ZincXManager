using System.Text;  // UTF-8
using System.Text.RegularExpressions;  // 绑定码提取
using ZincXManagerShared.Account;  // PasswordHash
using ZincXManagerShared.Network;  // 协议常量

namespace ZincXManagerTests;

/// <summary>
/// 套件 15:官方账号完整链路(ZXMOS 注册/登录 → ZXMS 壳账号镜像 → 绑定 → 621 在线同意 → 改绑踢旧玩家)。
/// <para>和套件 13 的差别:账号不在 ZXMS 本地,而是经 ZXMS 中转到 ZXMOS 校验,
/// 绑定表里记的是 ZXMS 按需镜像出来的壳账号 ID。</para>
/// </summary>
public static class OfficialBindChain
{
    /// <summary>跑一整套官方账号链路测试。</summary>
    public static async Task<SuiteResult> RunAsync(string ws, string debugMs, string debugOs)
    {
        var r = new SuiteResult { Name = "15 官方账号绑定 + 621 在线同意 + 改绑踢旧玩家" };

        const int ozport = 21470;   // 官方服务端测试端口
        const int czport = 21471;   // ZXMS 客户端接入端口
        const int zxmmPort = 21472; // 假 ZXMM 端口

        // 两个进程都靠这个放行口跳过 official.lic;退出时恢复原值,别影响别的套件
        var previousBypass = Environment.GetEnvironmentVariable("ZXM_ALLOW_UNOFFICIAL");
        Environment.SetEnvironmentVariable("ZXM_ALLOW_UNOFFICIAL", "1", EnvironmentVariableTarget.Process);

        try
        {
            // ---------- 官方服务端 ----------
            var osDir = Svc.PrepareBin(debugOs, Path.Combine(ws, "_zxt", "official-bind-os"));

            // 模板目录里带着开发机的账号 / 封禁数据,同名账号会让注册直接失败
            foreach (var staleDir in new[] { "User", "Data" })
            {
                var path = Path.Combine(osDir, staleDir);
                if (Directory.Exists(path)) { Directory.Delete(path, true); }
            }

            var staleLic = Path.Combine(osDir, "official.lic");
            if (File.Exists(staleLic)) { File.Delete(staleLic); }

            using var os = Svc.Start(Path.Combine(osDir, "ZincXManagerOServer.exe"), osDir, new Dictionary<string, string>
            {
                ["port"] = ozport.ToString(),
                ["name"] = "CSharp-官方绑定服",
            });

            r.Add("ZXMOS 监听 " + ozport, os.WaitPort(ozport, 25), LastLines(os, 2));
            r.Add("ZXMOS 走测试放行口启动", await WaitOutAsync(os, "本次放行", 8000), LastLines(os, 3));

            // ---------- ZXMS(连接官方端) ----------
            var msDir = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "official-bind-ms"));

            // 链路测试要干净起点:持久登录 / 绑定表 / 本地账号都不能带着开发机的数据
            foreach (var stale in new[] { "PersistentLogin.txt", "loginlog.log", "BindUser.txt" })
            {
                var path = Path.Combine(msDir, stale);
                if (File.Exists(path)) { File.Delete(path); }
            }

            foreach (var staleDir in new[] { "Data", "User" })
            {
                var path = Path.Combine(msDir, staleDir);
                if (Directory.Exists(path)) { Directory.Delete(path, true); }
            }

            var bindPath = Path.Combine(msDir, "Data", "BindUser.txt");   // 绑定表(Relative 到 ZXMS 运行目录)
            var persistentPath = Path.Combine(msDir, "PersistentLogin.txt");   // 持久登录表
            var loginlogPath = Path.Combine(msDir, "loginlog.log");   // 登录审计

            using var fake = new FakeZxmm(zxmmPort);
            fake.Start();

            const string osUser = "ZXMS-Official-Bind";   // ZXMS 在官方端的服务端账号名
            var osHash = PasswordHash.Hash(osUser, "pw123456");   // 服务端账号密文
            var serverCode = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
                .ToLowerInvariant();   // 本实例服务端码

            using var ms = Svc.Start(Path.Combine(msDir, "ZincXManagerServer.exe"), msDir, new Dictionary<string, string>
            {
                ["configver"] = "2",
                ["connectOS"] = "1",
                ["linkname"] = "0",
                ["name"] = "CSharp-官方绑定子服",
                ["czport"] = czport.ToString(),
                ["ozip"] = "127.0.0.1",
                ["ozport"] = ozport.ToString(),
                ["osuser"] = osUser,
                ["oshash"] = osHash,
                ["oslevel"] = "Admin",
                ["servercode"] = serverCode,
                ["zxmmapi"] = "127.0.0.1:" + zxmmPort,
            });

            r.Add("ZXMS 监听 " + czport, ms.WaitPort(czport, 25), LastLines(ms, 2));
            r.Add("ZXMS 连上假 ZXMM", await fake.WaitConnectedAsync(15000), fake.Dump());
            r.Add("ZXMS 在 ZXMOS 上没有账号时自动注册",
                await WaitOutAsync(ms, "还没注册过,正在注册", 15000) ||
                await WaitOutAsync(ms, "[ZXMOS] 账号认证通过", 1000), LastLines(ms, 3));
            r.Add("ZXMS 服务端账号在官方端认证通过", await WaitOutAsync(ms, "[ZXMOS] 账号认证通过", 20000), LastLines(ms, 3));
            r.Add("ZXMOS 记了子服务端账号注册", await WaitOutAsync(os, "[子服务端账号]", 8000), LastLines(os, 2));

            // ---------- 官方账号(经 ZXMS 中转)注册 + 登录 ----------
            using var probe = await Probe.ConnectAsync("127.0.0.1", czport, "OS");
            var regOk = await probe.RegisterAsync("osbind", "pw123456", "MACH-OFFICIAL");   // 官方账号注册
            r.Add("官方账号经 ZXMS 转发注册成功", regOk, probe.LastAck ?? "");
            r.Add("官方账号注册等级是 Player", probe.LastAck?.EndsWith("|Player") == true, probe.LastAck ?? "");

            var loginOk = await probe.LoginAsync("osbind", "pw123456", "MACH-OFFICIAL", "Player", "zxmos");   // 官方账号登录
            r.Add("官方账号经 ZXMS 转发登录成功", loginOk, probe.LastAck ?? "");
            probe.StartReader();

            r.Add("官方账号按需镜像成本地壳账号", await WaitOutAsync(ms, "官方账号 osbind 按需镜像到本地", 10000), LastLines(ms, 2));
            r.Add("ZXMOS 记了替玩家注册", await WaitOutAsync(os, "注册成功 osbind", 8000), LastLines(os, 2));
            r.Add("ZXMOS 记了替玩家登录", await WaitOutAsync(os, "登录成功 osbind", 8000), LastLines(os, 2));

            // 第二个官方账号在线:用来证明 621 只发给绑定账号本人
            using var other = await Probe.ConnectAsync("127.0.0.1", czport, "OS");
            var otherReg = await other.RegisterAsync("osbind2", "pw123456", "MACH-OFFICIAL2");
            var otherLogin = await other.LoginAsync("osbind2", "pw123456", "MACH-OFFICIAL2", "Player", "zxmos");
            other.StartReader();
            r.Add("第二个官方账号在线", otherReg && otherLogin, other.LastAck ?? "");

            // ---------- ① 未绑定官方玩家进服 → 拿绑定码 ----------
            fake.PushJoining("osplayer", "/198.51.100.7:50001");
            var kick1 = await fake.TakeAsync("|kick|osplayer|", 12000);
            r.Add("未绑定的官方玩家进服被踢去绑定", kick1 is not null, kick1 ?? "没收到:" + fake.Dump());
            if (kick1 is not null) { await fake.ReplyAsync(kick1, "err", "没有这个在线玩家"); }

            var code1 = Regex.Match(kick1 ?? "", "绑定码：([A-Z2-9]{6})").Groups[1].Value;
            r.Add("kick 理由带 6 位绑定码", code1.Length == 6, kick1 ?? "");

            // ---------- ② 用绑定码绑到官方账号(626 → pending,631 → 落盘) ----------
            var bindToken = "";
            if (code1.Length == 6)
            {
                await probe.SendAsync(11, Protocol.BindApply, Encoding.UTF8.GetBytes(code1));
                var pending = await probe.TakeQueuedAsync(Protocol.BindAck, 10000);
                r.Add("官方账号提交绑定码只校验、先回 pending", pending?.StartsWith("pending|") == true, pending ?? "没收到");
                r.Add("pending 里带玩家名", pending?.EndsWith("|osplayer") == true, pending ?? "");
                r.Add("确认前不写绑定表", !ReadShared(bindPath).Contains("osplayer"), ReadShared(bindPath).Trim());
                bindToken = pending?.Split('|') is { Length: > 1 } parts ? parts[1] : "";
            }

            if (bindToken.Length > 0)
            {
                await probe.SendAsync(15, Protocol.BindConfirm, Encoding.UTF8.GetBytes(bindToken));
                var confirm = await probe.TakeQueuedAsync(Protocol.BindAck, 10000);
                r.Add("官方账号二次确认后绑定成功", confirm?.StartsWith("ok|osplayer") == true, confirm ?? "没收到");
            }

            var bindText = ReadShared(bindPath);   // 绑定表全文
            var bindLine = bindText.Split('\n')
                .Select(l => l.Trim().TrimStart('\uFEFF'))
                .FirstOrDefault(l => l.EndsWith("=osplayer", StringComparison.OrdinalIgnoreCase)) ?? "";
            var bindId = bindLine.Split('=')[0].Trim();   // 绑定表里的账号ID
            r.Add("绑定表记的是镜像账号 ID(不是 0)", bindId.Length > 0 && bindId != "0", bindText.Trim());

            var shellFile = bindId.Length > 0 ? Path.Combine(msDir, "User", "Data", bindId + ".json") : "";
            var shellJson = shellFile.Length > 0 && File.Exists(shellFile) ? ReadShared(shellFile) : "";
            r.Add("绑定 ID 对应官方账号的壳账号(名称/等级都对)",
                shellJson.Contains("\"name\":\"osbind\"") && shellJson.Contains("\"level\":\"Player\""), shellJson.Trim());

            // ---------- ③ 已绑定官方玩家再进服 → 621 发给账号主人 → 玩家被踢 ----------
            fake.PushJoining("osplayer", "/198.51.100.7:50002");
            var ask = await probe.TakeQueuedAsync(Protocol.LoginAsk, 12000);
            r.Add("官方账号主人收到 621 登录请求", ask is not null, ask ?? "没收到");
            var askParts = (ask ?? "").Split('|');   //请求ID|玩家|IP|归属地
            r.Add("621 里的 IP 是纯地址(无端口/无斜杠)",
                askParts.Length > 2 && askParts[2] == "198.51.100.7", ask ?? "");
            r.Add("621 只发给绑定账号(第二条官方账号连接没收到)",
                !other.HasQueued(Protocol.LoginAsk), "另一条官方账号连接也收到了 621");

            var kickAsk = await fake.TakeAsync("|kick|osplayer|", 12000);
            r.Add("发完 621 把玩家踢走(点同意后重进)",
                kickAsk is not null && kickAsk.Contains("点「同意」后重新进入"), kickAsk ?? "没收到:" + fake.Dump());
            if (kickAsk is not null) { await fake.ReplyAsync(kickAsk, "err", "没有这个在线玩家"); }

            // ---------- ④ 账号主人点同意(622) → 登录数组 + 持久登录表 ----------
            if (askParts.Length > 0 && askParts[0].Length > 0)
            {
                await probe.SendAsync(12, Protocol.LoginAnswer, Encoding.UTF8.GetBytes(askParts[0] + "|yes"));
                r.Add("同意后记 [登录通过]", await WaitFileAsync(loginlogPath, "[登录通过] 玩家 osplayer", 12000), "");
            }

            // 登录日志先落盘、持久表随后写,这里直接等表本身,避免抢在写入前读到空表
            r.Add("同意后写持久登录表(官方账号路径)",
                await WaitFileAsync(persistentPath, "osplayer|198.51.100.7|", 12000), ReadShared(persistentPath).Trim());

            // ---------- ⑤ 同意后重进 → 直接放行,不再弹窗 ----------
            fake.PushJoining("osplayer", "/198.51.100.7:50003");
            r.Add("同意后重进命中登录数组直接放行",
                await WaitFileAsync(loginlogPath, "[配置阶段] 玩家 osplayer(198.51.100.7) 在登录数组里", 12000), "");
            r.Add("放行时没有再弹 621", !probe.HasQueued(Protocol.LoginAsk), "队列里出现了 621");
            r.Add("放行时没有再下发 kick", await fake.TakeAsync("|kick|osplayer|", 2500) is null, "又收到一条 kick");

            // ---------- ⑥ 改绑:同一个官方账号改绑新玩家 → 旧玩家免登录清掉并踢下线 ----------
            fake.PushJoining("osplayer2", "/198.51.100.8:50011");
            var kick2 = await fake.TakeAsync("|kick|osplayer2|", 12000);
            r.Add("改绑用的新玩家拿到绑定码", kick2 is not null, kick2 ?? "没收到:" + fake.Dump());
            if (kick2 is not null) { await fake.ReplyAsync(kick2, "err", "没有这个在线玩家"); }

            var code2 = Regex.Match(kick2 ?? "", "绑定码：([A-Z2-9]{6})").Groups[1].Value;
            if (code2.Length == 6)
            {
                await probe.SendAsync(14, Protocol.BindApply, Encoding.UTF8.GetBytes(code2));
                var pending2 = await probe.TakeQueuedAsync(Protocol.BindAck, 10000);
                r.Add("改绑也要先 pending", pending2?.StartsWith("pending|") == true, pending2 ?? "没收到");

                var token2 = pending2?.Split('|') is { Length: > 1 } parts2 ? parts2[1] : "";
                await probe.SendAsync(17, Protocol.BindConfirm, Encoding.UTF8.GetBytes(token2));
                var rebind = await probe.TakeQueuedAsync(Protocol.BindAck, 10000);
                r.Add("改绑到新玩家成功(同一个官方账号)", rebind?.StartsWith("ok|osplayer2") == true, rebind ?? "没收到");
            }

            r.Add("改绑记了 [改绑] 审计", await WaitFileAsync(loginlogPath, "从 osplayer 改绑到 osplayer2", 10000), "");

            var persistentAfter = ReadShared(persistentPath);   // 改绑后的持久登录表
            r.Add("改绑后旧玩家持久登录被清掉", !persistentAfter.Contains("osplayer|"), persistentAfter.Trim());

            var kickOld = await fake.TakeAsync("|kick|osplayer|", 12000);
            r.Add("改绑后旧玩家被踢(理由:账号已改绑到别的玩家)",
                kickOld is not null && kickOld.Contains("账号已改绑到别的玩家"), kickOld ?? "没收到:" + fake.Dump());
            if (kickOld is not null) { await fake.ReplyAsync(kickOld, "err", "没有这个在线玩家"); }

            // ---------- ⑦ 旧玩家再进服 → 已经不在任何账号名下,必须重新绑定 ----------
            fake.PushJoining("osplayer", "/198.51.100.7:50004");
            var kick3 = await fake.TakeAsync("|kick|osplayer|", 12000);
            var code3 = Regex.Match(kick3 ?? "", "绑定码：([A-Z2-9]{6})").Groups[1].Value;
            r.Add("改绑后旧玩家再进服要重新绑定", code3.Length == 6, kick3 ?? "没收到:" + fake.Dump());
            r.Add("重新拿到的绑定码和旧的不同", code3.Length == 6 && code3 != code1, code1 + " → " + code3);
            if (kick3 is not null) { await fake.ReplyAsync(kick3, "err", "没有这个在线玩家"); }
        }
        catch (Exception ex)
        {
            r.Fatal = ex.Message;
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZXM_ALLOW_UNOFFICIAL", previousBypass, EnvironmentVariableTarget.Process);
        }

        return r;
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
