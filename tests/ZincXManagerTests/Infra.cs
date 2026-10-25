using System.Diagnostics;  // 进程 / 调试
using System.Net.Sockets;  // TCP 套接字
using System.Text;  // 文本编码与 StringBuilder
using ZincXManagerShared.Network;  // 共享库
using ZincXManagerShared.Security;  // 共享库
using ZincXManagerShared.Account;  // 共享库
using SocketClient = System.Net.Sockets.TcpClient;

namespace ZincXManagerTests;

/// <summary>一条断言结果。</summary>
public sealed record Check(string Name, bool Ok, string Detail = "")
{
    public override string ToString() => (Ok ? "  [通过] " : "  [失败] ") + Name + (Ok || Detail.Length == 0 ? "" : " —— " + Detail);
}

/// <summary>一个套件的运行结果。</summary>
public sealed class SuiteResult
{
    public string Name = "";
    public readonly List<Check> Checks = new();
    public string? Fatal;
    /// <summary>环境信息(不计入通过/失败,比如"本机服务没启动,这一步跳过")。</summary>
    public readonly List<string> Notes = new();
    public int Pass => Checks.Count(c => c.Ok);
    public int Fail => Checks.Count(c => !c.Ok) + (Fatal is null ? 0 : 1);
    public void Note(string text) => Notes.Add(text);
    public void Add(string name, bool ok, string detail = "") => Checks.Add(new Check(name, ok, detail));
    public void AddTrue(string name, bool ok, string detail = "") => Checks.Add(new Check(name, ok, detail));
}

/// <summary>服务端/客户端进程包装:起、等端口、停、读日志。</summary>
public sealed class Svc : IDisposable
{
    /// <summary>控制台输出编码(ZXMS 用 GBK 读写控制台)。字段初始化早于静态构造,所以这里用属性懒加载。</summary>
    public static Encoding Gbk
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936);
        }
    }

    public Process Proc { get; }
    public string Dir { get; }
    private readonly StringBuilder _out = new();

    /// <summary>服务端/客户端进程包装:起进程、等端口、发控制台命令、收集输出。</summary>
    private Svc(Process p, string dir)
    {
        Proc = p;
        Dir = dir;
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (_out) { _out.AppendLine(e.Data); } } };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (_out) { _out.AppendLine(e.Data); } } };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
    }

    /// <summary>
    /// 把某个程序 bin 目录整个复制到临时目录(服务端把数据目录取在 exe 同目录,
    /// 所以要连 config.ini 一起摆到那边,不能只改工作目录)。
    /// </summary>
    public static string PrepareBin(string binDir, string targetDir)
    {
        if (Directory.Exists(targetDir)) { Directory.Delete(targetDir, true); }
        Directory.CreateDirectory(targetDir);
        foreach (var f in Directory.GetFiles(binDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(binDir, f);
            var dst = Path.Combine(targetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, true);
        }
        var logs = Path.Combine(targetDir, "logs");
        if (Directory.Exists(logs)) { Directory.Delete(logs, true); }
        return targetDir;
    }

    /// <summary>启动一个进程(可顺带写 config.ini),输出与错误都异步收集起来。</summary>
    public static Svc Start(string exe, string dir, IDictionary<string, string>? ini = null)
    {
        Directory.CreateDirectory(dir);
        if (ini is not null)
        {
            File.WriteAllLines(Path.Combine(dir, "config.ini"), ini.Select(kv => kv.Key + "=" + kv.Value), new UTF8Encoding(false));
        }
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // ZXMS 控制台按 GBK(936)读 stdin,写 UTF-8 过去会变乱码
            StandardInputEncoding = Gbk,
            StandardOutputEncoding = Gbk,
            StandardErrorEncoding = Gbk,
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("进程没起来:" + exe);
        return new Svc(p, dir);
    }

    /// <summary>一条断言的可读文本(通过/失败 + 细节)。</summary>
    public string Out { get { lock (_out) { return _out.ToString(); } } }

    /// <summary>往服务端控制台敲一条命令(notice / push / info 这类)。</summary>
    public void SendCmd(string line)
    {
        Proc.StandardInput.WriteLine(line);
        Proc.StandardInput.Flush();
    }

    /// <summary>等端口被监听(返回 true 表示起来了)。</summary>
    public bool WaitPort(int port, int seconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (Proc.HasExited) { return false; }
            try
            {
                using var c = new SocketClient();
                var t = c.ConnectAsync("127.0.0.1", port);
                if (t.Wait(400) && c.Connected) { return true; }
            }
            catch { }
            Thread.Sleep(200);
        }
        return false;
    }

    /// <summary>结束进程并释放句柄。</summary>
    public void Dispose()
    {
        try { if (!Proc.HasExited) { Proc.Kill(true); } } catch { }
        try { Proc.Dispose(); } catch { }
    }
}

/// <summary>客户端探针:握手 + 注册/登录 + 收包(纯 C#,不再调外部 exe)。</summary>
public sealed class Probe : IDisposable
{
    private readonly SocketClient _tcp = new();
    private NetworkStream _ns = default!;
    private SessionCrypto? _crypto;
    private readonly List<(ushort Type, string Text)> _got = new();
    private readonly object _lock = new();
    private Task? _reader;
    private readonly SemaphoreSlim _signal = new(0);

    public string Name { get; }
    public string? LastAck { get; private set; }

    /// <summary>纯 C# 客户端探针:自己走握手、登录、收包,不依赖 ZXMC 界面。</summary>
    private Probe(string name) { Name = name; }

    /// <summary>连上服务端并完成加密协商(380/381),之后所有包都加密。</summary>
    public static async Task<Probe> ConnectAsync(string host, int port, string connMode, int timeoutMs = 8000)
    {
        var p = new Probe($"{host}:{port}");
        await p._tcp.ConnectAsync(host, port);
        p._ns = p._tcp.GetStream();

        // 1) 会话加密(380/381):服务端连上后会先主动发身份包(200/201/202),
        //    所以这里要一直读到 381 为止,别假设第一包就是它。
        var hs = new SessionHandshake();
        await p.SendAsync(0, Protocol.SessionHello, hs.PublicKey);
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        (ushort Type, byte[] Data)? ack = null;
        while (DateTime.UtcNow < end)
        {
            var left = (int)Math.Max(300, (end - DateTime.UtcNow).TotalMilliseconds);
            var one = await p.ReadOneAsync(left);
            if (one is null) { break; }
            // 握手前服务端会先推身份包(200/201/202),先存起来,别丢
            lock (p._lock) { p._got.Add((one.Value.Type, Encoding.UTF8.GetString(one.Value.Data))); }
            if (one.Value.Type == Protocol.SessionHelloAck) { ack = one; break; }
        }
        if (ack is null)
        {
            throw new InvalidOperationException("没收到 381(加密协商失败)");
        }
        p._crypto = hs.Complete(ack.Value.Data, isServer: false);

        // 服务端发完 381 才装会话密钥,这里让出一点时间,别抢在它前面把加密包塞过去
        await Task.Delay(150);

        // 2) 上线握手
        await p.SendAsync(1, Protocol.Conline, Encoding.UTF8.GetBytes("ZXMC:" + connMode));

        return p;
    }

    /// <summary>按协议头(Id/Type/Flags/Length,大端)组包并发送,自动加密。</summary>
    public async Task SendAsync(ushort packetId, ushort type, byte[] payload)
    {
        var body = payload;
        ushort flags = 0;
        if (_crypto is not null && type != Protocol.SessionHello && type != Protocol.SessionHelloAck)
        {
            body = _crypto.Encrypt(payload);
            flags = SessionCrypto.FlagEncrypted;
        }
        var pkt = new byte[16 + body.Length];
        // 头:Id(8 字节) + Type(2) + Flags(2) + Length(4),全部大端
        BitConverter.GetBytes((ulong)packetId).Reverse().ToArray().CopyTo(pkt, 0);
        BitConverter.GetBytes(type).Reverse().ToArray().CopyTo(pkt, 8);
        BitConverter.GetBytes(flags).Reverse().ToArray().CopyTo(pkt, 10);
        BitConverter.GetBytes(body.Length).Reverse().ToArray().CopyTo(pkt, 12);
        body.CopyTo(pkt, 16);
        if (Environment.GetEnvironmentVariable("ZXT_DEBUG") == "1")
        {
            Console.WriteLine("  → 发送 type=" + type + " flags=" + flags + " len=" + body.Length + " 头=" + Convert.ToHexString(pkt, 0, 16));
        }
        await _ns.WriteAsync(pkt);
        await _ns.FlushAsync();
    }

    /// <summary>读一个完整包(16 字节头 + 负载),带超时。</summary>
    private async Task<(ushort Type, byte[] Data)?> ReadOneAsync(int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var hdr = new byte[16];
        var read = 0;
        while (read < 16)
        {
            var n = await _ns.ReadAsync(hdr.AsMemory(read, 16 - read), cts.Token);
            if (n <= 0) { return null; }
            read += n;
        }
        var type = (ushort)((hdr[8] << 8) | hdr[9]);
        var flags = (ushort)((hdr[10] << 8) | hdr[11]);
        var len = (hdr[12] << 24) | (hdr[13] << 16) | (hdr[14] << 8) | hdr[15];
        var body = new byte[len];
        var got = 0;
        while (got < len)
        {
            var n = await _ns.ReadAsync(body.AsMemory(got, len - got), cts.Token);
            if (n <= 0) { return null; }
            got += n;
        }
        if ((flags & SessionCrypto.FlagEncrypted) != 0 && _crypto is not null)
        {
            body = _crypto.Decrypt(body);
        }
        return (type, body);
    }

    /// <summary>收到指定类型的包就返回它;其它包跳过着继续等。</summary>
    public async Task<string?> WaitAsync(ushort type, int timeoutMs = 8000)
    {
        // 先看已经收到的(握手阶段存下来的 200/201/202 就在这儿)
        lock (_lock)
        {
            var hit = _got.FirstOrDefault(g => g.Type == type);
            if (hit.Text is not null || _got.Any(g => g.Type == type)) { return hit.Text; }
        }

        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            var left = (int)Math.Max(200, (end - DateTime.UtcNow).TotalMilliseconds);
            var p = await ReadOneAsync(left);
            if (p is null) { return null; }
            var text = Encoding.UTF8.GetString(p.Value.Data);
            lock (_lock) { _got.Add((p.Value.Type, text)); }
            if (p.Value.Type == type) { return text; }
        }
        return null;
    }

    /// <summary>拿走一条指定类型的包(取过就不再返回),用来验证"第二次还会不会来"。</summary>
    public async Task<string?> TakeAsync(ushort type, int timeoutMs = 8000)
    {
        lock (_lock)
        {
            var index = _got.FindIndex(g => g.Type == type);
            if (index >= 0)
            {
                var hit = _got[index].Text;
                _got.RemoveAt(index);
                return hit;
            }
        }

        // 短读轮询:直接拿大超时去取消会把底层 socket 读断,后面还要继续用这条连接
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            (ushort Type, byte[] Data)? p;
            try
            {
                p = await ReadOneAsync(300);
            }
            catch (OperationCanceledException)
            {
                p = null;
            }

            if (p is null)
            {
                await Task.Delay(50);
                continue;
            }

            var text = Encoding.UTF8.GetString(p.Value.Data);
            if (p.Value.Type == type) { return text; }
            lock (_lock) { _got.Add((p.Value.Type, text)); }
        }
        return null;
    }

    /// <summary>起后台读包线程:之后请用 <see cref="TakeQueuedAsync"/> 取包,别再混用 WaitAsync。</summary>
    public void StartReader()
    {
        _reader ??= Task.Run(async () =>
        {
            while (true)
            {
                (ushort Type, byte[] Data)? packet;
                try
                {
                    packet = await ReadOneAsync(600000);
                }
                catch
                {
                    return;
                }

                if (packet is null)
                {
                    return;
                }

                lock (_lock) { _got.Add((packet.Value.Type, Encoding.UTF8.GetString(packet.Value.Data))); }
                try { _signal.Release(); } catch { /* 已释放 */ }
            }
        });
    }

    /// <summary>从后台读线程攒的队列里取一条指定类型的包(取过就没)。</summary>
    public async Task<string?> TakeQueuedAsync(ushort type, int timeoutMs = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            lock (_lock)
            {
                var index = _got.FindIndex(g => g.Type == type);
                if (index >= 0)
                {
                    var hit = _got[index].Text;
                    _got.RemoveAt(index);
                    return hit;
                }
            }

            var left = Math.Max(50, (int)(end - DateTime.UtcNow).TotalMilliseconds);
            try { await _signal.WaitAsync(Math.Min(left, 200)); } catch { /* 超时继续轮 */ }
        }

        return null;
    }

    /// <summary>队列里是不是已经有这种包(不消费、不读网络)。</summary>
    public bool HasQueued(ushort type)
    {
        lock (_lock) { return _got.Any(g => g.Type == type); }
    }

    /// <summary>注册一个账号,返回服务端是否回 ok|。</summary>
    public async Task<bool> RegisterAsync(string user, string pass, string machine, int timeoutMs = 8000)
    {
        var hash = PasswordHash.Hash(user, pass);
        await SendAsync(2, Protocol.AccountRegister, Encoding.UTF8.GetBytes($"{user}|{hash}|{machine}"));
        var ack = await WaitAsync(Protocol.AccountRegisterAck, timeoutMs);
        LastAck = ack;
        return ack?.StartsWith("ok|") == true;
    }

    /// <summary>登录:带 nonce/proof 走挑战-响应,返回是否回 ok|。</summary>
    public async Task<bool> LoginAsync(string user, string pass, string machine, string level, string side, int timeoutMs = 8000)
    {
        var hash = PasswordHash.Hash(user, pass);
        var nonce = LoginProof.NewNonce();
        var proof = LoginProof.Compute(hash, nonce);
        await SendAsync(2, Protocol.AccountLogin, Encoding.UTF8.GetBytes($"{user}|{hash}|{level}|{side}|{machine}||{nonce}|{proof}"));
        var ack = await WaitAsync(Protocol.AccountLoginAck, timeoutMs);
        LastAck = ack;
        return ack?.StartsWith("ok|") == true;
    }

    /// <summary>结束进程并释放句柄。</summary>
    public void Dispose()
    {
        try { _ns?.DisposeAsync(); } catch { }
        try { _tcp.Dispose(); } catch { }
    }
}
