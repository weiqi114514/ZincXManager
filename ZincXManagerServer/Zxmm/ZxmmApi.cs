using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZincXManagerServer.Zxmm;  // ZXMS 侧:与 ZXMM 的专用长连接

/// <summary>
/// 与 ZXMM 的**专用长连接**(ZXMS 连 ZXMM 的 21200,一直保持,断了自动重连)。
///
/// <p>一条连接双向跑：</p>
/// <pre>
/// ZXMS → ZXMM   hello|令牌                        (模组配了 api.token 才有这一步)
/// ZXMM → ZXMS   evt|joined|玩家名|IP              (触发 <see cref="PlayerJoined"/>)
/// ZXMS → ZXMM   req|id|方法|参数…                 (调 <see cref="KickAsync"/> 等方法)
/// ZXMM → ZXMS   res|id|ok|结果 / res|id|err|原因   (方法返回)
/// </pre>
///
/// <para>ZXMM 那边只是执行器,所有流程/判断都在 ZXMS。
/// 30 秒一次 ping 探活:半开连接不探就永远发现不了(踢人会一直等 10 秒超时)。</para>
/// </summary>
public sealed class ZxmmApi : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _token;

    private readonly ConcurrentDictionary<long, TaskCompletionSource<(bool Ok, string Result)>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private TcpClient? _client;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private long _seq;

    // 重复进服事件的去重窗口(模组侧重连重叠时同一条事件可能来两次)
    private string _lastJoinKey = "";
    private DateTime _lastJoinAt = DateTime.MinValue;

    /// <summary>ZXMM 推来"玩家进服"事件(玩家名, IP)。</summary>
    public event Action<string, string>? PlayerJoined;

    /// <summary>专用通道当前有没有连上。</summary>
    public bool Connected => _client?.Connected == true;

    /// <summary>建一个指向某个 ZXMM 的连接。</summary>
    /// <param name="host">ZXMM 所在主机</param>
    /// <param name="port">ZXMM 的专用通道端口</param>
    /// <param name="token">共享令牌,空 = 不校验(模组那边也要留空)</param>
    public ZxmmApi(string host = "127.0.0.1", int port = 21200, string token = "")
    {
        _host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        _port = port <= 0 ? 21200 : port;
        _token = token?.Trim() ?? "";
    }

    /// <summary>开始连(后台循环,断了自动重连),顺带起 30 秒一次的探活。</summary>
    public void Start()
    {
        _cts ??= new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
        _ = HeartbeatAsync(_cts.Token);
    }

    /// <summary>连接 + 读循环。</summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
                client.NoDelay = true;

                // 对端进程被强杀时,没有 keepalive 的 socket 会一直"看起来还连着"
                try { client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); }
                catch { /* 部分平台不支持,忽略 */ }

                _client = client;
                _writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };

                // 模组配了令牌就必须先自报身份,否则它会直接把连接关掉
                if (_token.Length > 0)
                {
                    await _writer.WriteLineAsync("hello|" + _token).ConfigureAwait(false);
                    await _writer.FlushAsync().ConfigureAwait(false);
                }

                Log($"[ZXMM] 专用通道已连上 {_host}:{_port}");

                using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                {
                    HandleLine(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"[ZXMM] 专用通道断开: {ex.Message}");
            }
            finally
            {
                _client?.Dispose();
                _client = null;
                _writer = null;

                // 断开时把等回执的请求全部放掉,别让它们各自等到 10 秒超时
                foreach (var (_, waiter) in _pending)
                {
                    waiter.TrySetResult((false, "专用通道已断开"));
                }
            }

            await Task.Delay(3000, ct).ConfigureAwait(false);
        }
    }

    /// <summary>30 秒一次探活:没回 pong 就把连接扔掉,让读循环重连。</summary>
    private async Task HeartbeatAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_writer is null)
            {
                continue;
            }

            var (ok, result) = await CallAsync("ping", ct).ConfigureAwait(false);
            if (ok && result == "pong")
            {
                continue;
            }

            Log($"[ZXMM] 探活失败,主动断开准备重连: {result}");
            try { _client?.Dispose(); } catch { /* 已经断了 */ }
        }
    }

    /// <summary>处理一行:优先看是不是任务结果,不是就当事件。</summary>
    private void HandleLine(string line)
    {
        var parts = line.Split('|');   //拆分结果

        // 任务结果 res|id|ok|结果
        if (parts.Length >= 3 && parts[0] == "res" && long.TryParse(parts[1], out var id))
        {
            if (_pending.TryRemove(id, out var waiter))
            {
                waiter.TrySetResult((parts[2] == "ok", parts.Length > 3 ? parts[3] : ""));
            }

            return;
        }

        // 事件 evt|joined|玩家|IP
        if (parts.Length >= 4 && parts[0] == "evt" && parts[1] == "joined")
        {
            var name = parts[2];
            var ip = parts[3];   //玩家IP

            // 重连重叠期同一条事件可能收到两遍,2 秒内同样的名 + IP 只算一次
            var key = name + "|" + ip;
            var now = DateTime.UtcNow;
            if (key == _lastJoinKey && (now - _lastJoinAt).TotalSeconds < 2)
            {
                Log($"[ZXMM] 重复的进服事件已忽略: {key}");
                return;
            }

            _lastJoinKey = key;
            _lastJoinAt = now;

            Log($"[ZXMM] 收到进服事件: {name} ({ip})");
            try
            {
                PlayerJoined?.Invoke(name, ip);
            }
            catch (Exception ex)
            {
                Log($"[ZXMM] 处理进服事件异常: {ex.Message}");
            }
        }
    }

    /// <summary>踢出玩家。返回"ZXMM 到底做了什么":Ok=false 表示没真的踢掉(人不在线/没连上)。</summary>
    public async Task<(bool Ok, string Result)> KickAsync(string player, string reason, CancellationToken ct = default)
        => await CallAsync("kick", ct, player, reason).ConfigureAwait(false);

    /// <summary>在线玩家名列表。</summary>
    public async Task<string[]> ListPlayersAsync(CancellationToken ct = default)
    {
        var (ok, result) = await CallAsync("list", ct).ConfigureAwait(false);
        return ok && result.Length > 0
            ? result.Split(',', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
    }

    /// <summary>玩家信息：名|维度|X|Y|Z；不在线返回 null。</summary>
    public async Task<string?> InfoAsync(string player, CancellationToken ct = default)
    {
        var (ok, result) = await CallAsync("info", ct, player).ConfigureAwait(false);
        return ok && result.Length > 0 ? result : null;
    }

    /// <summary>玩家在不在线。</summary>
    public async Task<bool> OnlineAsync(string player, CancellationToken ct = default)
    {
        var (ok, result) = await CallAsync("online", ct, player).ConfigureAwait(false);
        return ok && result == "1";
    }

    /// <summary>全服广播。</summary>
    public async Task<bool> SayAsync(string text, CancellationToken ct = default)
        => (await CallAsync("say", ct, text).ConfigureAwait(false)).Ok;

    /// <summary>探活。</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        var (ok, result) = await CallAsync("ping", ct).ConfigureAwait(false);
        return ok && result == "pong";
    }

    /// <summary>在长连接上发一条任务并等结果。</summary>
    private async Task<(bool Ok, string Result)> CallAsync(string method, CancellationToken ct, params string[] args)
    {
        var writer = _writer;
        if (writer is null)
        {
            return (false, "专用通道没连上");
        }

        var id = Interlocked.Increment(ref _seq);
        var waiter = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        var line = new StringBuilder().Append("req|").Append(id).Append('|').Append(method);
        foreach (var arg in args)
        {
            line.Append('|').Append((arg ?? "").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' '));
        }

        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(line.ToString()).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            return await waiter.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>打日志(进 ZXMS 的日志文件)。</summary>
    private static void Log(string text)
        => ZincXManagerShared.Logging.Logger.Log(ZincXManagerShared.Logging.LogLevel.Info, text);

    /// <summary>关掉连接。</summary>
    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _client?.Dispose();

        foreach (var (_, waiter) in _pending)
        {
            waiter.TrySetResult((false, "连接已关闭"));
        }

        _pending.Clear();
        await Task.CompletedTask;
    }
}
