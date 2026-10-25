using System.Net;  // IPAddress
using System.Net.Sockets;  // TcpListener
using System.Text;  // UTF-8
using System.Text.RegularExpressions;  // 绑定码
using ZincXManagerShared.Network;  // 协议常量
using SocketClient = System.Net.Sockets.TcpClient;  // 和共享库的 TcpClient 区分开

namespace ZincXManagerTests;

/// <summary>
/// 假 ZXMM：只做测试用的"执行器"——ZXMS 连上它，它推进服事件、收踢人任务并回执。
/// <para>真模组在 MC 服里，测试里不启动 MC，所以用这个替身验证 ZXMS 侧的判定链路。</para>
/// </summary>
public sealed class FakeZxmm : IDisposable
{
    private readonly TcpListener _listener;
    private readonly List<string> _lines = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private SocketClient? _client;
    private StreamWriter? _writer;

    public int Port { get; }

    /// <summary>在指定端口起一个假 ZXMM，等 ZXMS 连进来。</summary>
    public FakeZxmm(int port)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    /// <summary>开始监听(后台 accept)。</summary>
    public void Start()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            _client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            var stream = _client.GetStream();
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false)) != null)
            {
                lock (_lock) { _lines.Add(line); }
            }
        }
        catch
        {
            // 测试结束关连接会走到这里，正常
        }
    }

    /// <summary>ZXS 连上来了没有。</summary>
    public async Task<bool> WaitConnectedAsync(int timeoutMs = 10000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (_writer is not null) { return true; }
            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>推一条进服事件(IP 故意保持 ZXMM 老格式 /ip:port)。</summary>
    public void PushJoined(string player, string ip)
    {
        var w = _writer ?? throw new InvalidOperationException("ZXMS 还没连上假 ZXMM");
        w.WriteLine($"evt|joined|{player}|{ip}");
    }

    /// <summary>是不是收到了含 key 的请求(看一眼，不消费)。</summary>
    public bool Contains(string key)
    {
        lock (_lock) { return _lines.Any(l => l.Contains(key)); }
    }

    /// <summary>取走第一条含 key 的请求(取过就没了，能验"第二次还会不会来")。</summary>
    public async Task<string?> TakeAsync(string key, int timeoutMs = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            lock (_lock)
            {
                var index = _lines.FindIndex(l => l.Contains(key));
                if (index >= 0)
                {
                    var hit = _lines[index];
                    _lines.RemoveAt(index);
                    return hit;
                }
            }

            await Task.Delay(100);
        }

        return null;
    }

    /// <summary>回一条任务结果(把 req 里的 id 原样带回去)。</summary>
    public async Task ReplyAsync(string request, string status, string result)
    {
        var w = _writer ?? throw new InvalidOperationException("ZXMS 还没连上假 ZXMM");
        var parts = request.Split('|');
        await w.WriteLineAsync($"res|{parts[1]}|{status}|{result}");
    }

    /// <summary>收到的原始行(排障用)。</summary>
    public string Dump()
    {
        lock (_lock) { return string.Join(" / ", _lines); }
    }

    /// <summary>关掉监听和连接。</summary>
    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _client?.Dispose(); } catch { }
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}

/// <summary>
/// 套件 13：ZXMM ↔ ZXMS 的判定链路(未绑定踢人 / 绑码 / 621-622 / 持久登录 / 解绑复验 / 令牌)。
/// </summary>
public static class ZxmmChain
{
    /// <summary>跑一整套链路测试。</summary>
    public static async Task<SuiteResult> RunAsync(string ws, string debugMs)
    {
        var r = new SuiteResult { Name = "13 ZXMM 链路(踢人/绑定/登录/解绑/令牌)" };

        const int czport = 21414;
        const int zxmmPort = 21455;

        try
        {
            var dir = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "zxmm"));

            // 模板 bin 里带着开发机的真实数据(持久登录表 / 绑定表 / 账号),链路测试要干净起点
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

            using var fake = new FakeZxmm(zxmmPort);
            fake.Start();

            using var ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, new Dictionary<string, string>
            {
                ["configver"] = "2",
                ["connectOS"] = "0",
                ["linkname"] = "0",
                ["name"] = "CSharp-ZXMM服",
                ["czport"] = czport.ToString(),
                ["zxmmapi"] = "127.0.0.1:" + zxmmPort,
            });

            r.Add("ZXMS 监听 " + czport, ms.WaitPort(czport));
            r.Add("ZXMS 连上假 ZXMM(21200)", await fake.WaitConnectedAsync());
            r.Add("日志打出专用通道已连上", await WaitOutAsync(ms, "专用通道已连上", 8000), LastLines(ms, 3));

            using var probe = await Probe.ConnectAsync("127.0.0.1", czport, "OS");
            r.Add("探针注册账号", await probe.RegisterAsync("bindplayer", "pw123456", "MACH-ZXMM"), probe.LastAck ?? "");
            r.Add("探针登录账号", await probe.LoginAsync("bindplayer", "pw123456", "MACH-ZXMM", "Player", "zxms"), probe.LastAck ?? "");

            // 注册/登录走的是按需读包，等它们做完再起后台读线程，否则两个读者会抢同一个流
            probe.StartReader();

            // ① 未绑定玩家进服(IP 用老格式 /ip:port)→ 应该被要求踢掉，理由里带 6 位绑定码
            fake.PushJoined("mcplayer", "/203.0.113.9:51515");
            var kick1 = await fake.TakeAsync("|kick|mcplayer");
            r.Add("未绑定 → 下发 kick", kick1 is not null, kick1 ?? "没收到:" + fake.Dump());

            var code1 = Regex.Match(kick1 ?? "", "绑定码：([A-Z2-9]{6})").Groups[1].Value;
            r.Add("kick 理由带 6 位绑定码", code1.Length == 6, code1.Length > 0 ? code1 : kick1 ?? "");

            if (kick1 is not null)
            {
                // 真模组这时会回 err(Z 服里根本没这个人)
                await fake.ReplyAsync(kick1, "err", "没有这个在线玩家");
                r.Add("踢人失败被如实记录(err 不当成功)",
                    await WaitOutAsync(ms, "下发踢人任务 mcplayer → 没有这个在线玩家", 6000), LastLines(ms, 2));
            }

            // ② 用绑定码绑定
            if (code1.Length == 6)
            {
                await probe.SendAsync(11, Protocol.BindApply, Encoding.UTF8.GetBytes(code1));
                var bindAck = await probe.TakeQueuedAsync(Protocol.BindAck, 8000);
                r.Add("绑定码绑定成功", bindAck?.StartsWith("ok|") == true, bindAck ?? "没收到回执");
            }

            // ③ 已绑定再进服 → 621 弹窗，IP 必须是纯地址(证明 /ip:port 被压平了)
            fake.PushJoined("mcplayer", "/203.0.113.9:51516");
            var ask = await probe.TakeQueuedAsync(Protocol.LoginAsk, 8000);
            r.Add("已绑定 → 收到 621 登录请求", ask is not null, ask ?? "没收到");
            r.Add("621 里的 IP 是纯地址(无端口/无斜杠)",
                ask is not null && ask.Split('|').Length > 2 && ask.Split('|')[2] == "203.0.113.9", ask ?? "");

            var askId = ask is null ? "" : ask.Split('|')[0];

            // ③a 重复上报:两层拦截
            // 同一玩家 + 同一个 IP(2 秒内) → ZXMS 的连接层去重先拦下
            fake.PushJoined("mcplayer", "/203.0.113.9:51516");
            r.Add("同 IP 重复上报被 2 秒去重拦下", await WaitOutAsync(ms, "重复的进服事件已忽略", 4000), LastLines(ms, 2));

            // 换个端口再来一次(去重键不同) → 待确认请求层不许再弹第二个窗
            fake.PushJoined("mcplayer", "/203.0.113.9:59999");
            r.Add("换端口重复上报被待确认去重拦下",
                await WaitFileAsync(Path.Combine(dir, "loginlog.log"), "忽略这次重复上报", 5000), "");
            r.Add("重复上报没有再弹 621", !probe.HasQueued(Protocol.LoginAsk), "队列里多了一条 621");

            // ③b 越权回答:另一个账号抢答 622 必须无效,而且不能把请求吃掉
            if (askId.Length > 0)
            {
                using (var intruder = await Probe.ConnectAsync("127.0.0.1", czport, "OS"))
                {
                    var regOk = await intruder.RegisterAsync("intruder", "pw123456", "MACH-ZXMM");
                    var loginOk = await intruder.LoginAsync("intruder", "pw123456", "MACH-ZXMM", "Player", "zxms");
                    r.Add("第二个账号能注册登录", regOk && loginOk, intruder.LastAck ?? "");

                    await intruder.SendAsync(21, Protocol.LoginAnswer, Encoding.UTF8.GetBytes(askId + "|yes"));
                    r.Add("越权回答 622 被拒并记日志", await WaitOutAsync(ms, "越权回答登录请求", 5000), LastLines(ms, 2));
                }

                await Task.Delay(300);
                var leakedPath = Path.Combine(dir, "PersistentLogin.txt");
                var leaked = File.Exists(leakedPath) ? ReadShared(leakedPath) : "";
                r.Add("越权回答没有写进持久登录表", !leaked.Contains("mcplayer"), leaked.Trim());
            }

            if (askId.Length > 0)
            {
                await probe.SendAsync(12, Protocol.LoginAnswer, Encoding.UTF8.GetBytes(askId + "|yes"));
                await Task.Delay(700);
            }

            var persistentPath = Path.Combine(dir, "PersistentLogin.txt");
            var persistent = File.Exists(persistentPath) ? ReadShared(persistentPath) : "";
            r.Add("同意后写持久登录表", persistent.Contains("mcplayer"), persistent.Trim());
            r.Add("持久登录表里记的是纯 IP", persistent.Contains("mcplayer|203.0.113.9|"), persistent.Trim());
            r.Add("同一玩家只留一行(不再无限追加)", CountOf(persistent, "mcplayer") == 1, persistent.Trim());

            // ④ 再进服 → 登录数组/持久表命中，不再弹窗
            fake.PushJoined("mcplayer", "/203.0.113.9:51517");
            var passed = await WaitFileAsync(Path.Combine(dir, "loginlog.log"), "在登录数组里", 6000);
            r.Add("持久登录命中走的是放行分支", passed, "");
            r.Add("持久登录命中 → 不再弹窗", !probe.HasQueued(Protocol.LoginAsk), "队列里出现了 621");

            // ⑤ 改绑到另一个玩家 → 旧玩家的免登录必须立刻失效(账号名下已经没他了)
            fake.PushJoined("mcplayer2", "/203.0.113.10:51519");
            var kickP2 = await fake.TakeAsync("|kick|mcplayer2", 8000);
            var codeP2 = Regex.Match(kickP2 ?? "", "绑定码：([A-Z2-9]{6})").Groups[1].Value;
            r.Add("改绑用的新玩家拿到绑定码", codeP2.Length == 6, codeP2.Length > 0 ? codeP2 : kickP2 ?? "");

            if (codeP2.Length == 6)
            {
                await probe.SendAsync(14, Protocol.BindApply, Encoding.UTF8.GetBytes(codeP2));
                var rebindAck = await probe.TakeQueuedAsync(Protocol.BindAck, 8000);
                r.Add("改绑到新玩家成功", rebindAck?.StartsWith("ok|mcplayer2") == true, rebindAck ?? "没收到回执");
            }

            await Task.Delay(400);
            var persistentRebind = File.Exists(persistentPath) ? ReadShared(persistentPath) : "";
            r.Add("改绑后旧玩家免登录已清掉", CountOf(persistentRebind, "mcplayer") == 0, persistentRebind.Trim());

            var rebindKick = await fake.TakeAsync("|kick|mcplayer", 6000);
            r.Add("改绑后旧玩家被要求重新绑定",
                rebindKick is not null && rebindKick.Contains("账号已改绑到别的玩家"), rebindKick ?? "没收到:" + fake.Dump());

            // ⑥ 解绑(现在账号绑的是 mcplayer2) → 立刻失效：回执 ok + 踢人 + 持久表清干净
            await probe.SendAsync(13, Protocol.Unbind, Array.Empty<byte>());
            var unbindAck = await probe.TakeQueuedAsync(Protocol.BindAck, 8000);
            r.Add("解绑回执 ok", unbindAck?.StartsWith("ok|") == true, unbindAck ?? "没收到回执");

            var kick2 = await fake.TakeAsync("|kick|mcplayer2", 6000);
            r.Add("解绑后下发踢人", kick2 is not null, kick2 ?? "没收到:" + fake.Dump());

            await Task.Delay(400);
            var persistentAfter = File.Exists(persistentPath) ? ReadShared(persistentPath) : "";
            r.Add("解绑后持久登录表已清空(历史 bug 复验)",
                CountOf(persistentAfter, "mcplayer") + CountOf(persistentAfter, "mcplayer2") == 0, persistentAfter.Trim());

            // ⑦ 解绑后再进服 → 回到"未绑定"，必须重新给绑定码
            fake.PushJoined("mcplayer", "/203.0.113.9:51518");
            var kick3 = await fake.TakeAsync("|kick|mcplayer", 8000);
            var code2 = Regex.Match(kick3 ?? "", "绑定码：([A-Z2-9]{6})").Groups[1].Value;
            r.Add("解绑后再进服要重新绑定", code2.Length == 6, kick3 ?? "没收到:" + fake.Dump());
            r.Add("新绑定码与旧的不同", code2.Length == 6 && code2 != code1, code1 + " → " + code2);
        }
        catch (Exception ex)
        {
            r.Fatal = ex.Message;
        }

        // ⑦ 令牌校验：配了 zxmmtoken 就必须先发 hello|令牌
        try
        {
            const int czport2 = 21415;
            const int zxmmPort2 = 21456;
            var dir2 = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "zxmm-token"));

            using var fake2 = new FakeZxmm(zxmmPort2);
            fake2.Start();

            using var ms2 = Svc.Start(Path.Combine(dir2, "ZincXManagerServer.exe"), dir2, new Dictionary<string, string>
            {
                ["configver"] = "2",
                ["connectOS"] = "0",
                ["linkname"] = "0",
                ["name"] = "CSharp-令牌服",
                ["czport"] = czport2.ToString(),
                ["zxmmapi"] = "127.0.0.1:" + zxmmPort2,
                ["zxmmtoken"] = "TEST-TOKEN-123",
            });

            r.Add("令牌服监听 " + czport2, ms2.WaitPort(czport2));
            r.Add("令牌服连上假 ZXMM", await fake2.WaitConnectedAsync());
            r.Add("配置令牌后先发 hello|令牌", await fake2.TakeAsync("hello|TEST-TOKEN-123", 8000) is not null, fake2.Dump());
        }
        catch (Exception ex)
        {
            r.Add("令牌链路没跑起来", false, ex.Message);
        }

        return r;
    }

    /// <summary>数一下表里有几行属于这个玩家(一行 = 一条记录)。</summary>
    private static int CountOf(string text, string player)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.TrimStart('\uFEFF').StartsWith(player + "|", StringComparison.OrdinalIgnoreCase));

    /// <summary>读服务端正在写的文件:用 FileShare.ReadWrite 打开,免得把服务端的写入顶掉。</summary>
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

    /// <summary>取输出最后几行，失败时当证据。</summary>
    private static string LastLines(Svc svc, int count)
    {
        var lines = svc.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", lines.TakeLast(count).Select(l => l.Trim()));
    }
}


