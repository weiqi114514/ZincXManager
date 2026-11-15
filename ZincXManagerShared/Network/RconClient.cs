using System.Buffers.Binary;  // 小端读写
using System.Net.Sockets;  // TCP 套接字
using System.Text;  // 命令与回包的编码

// BCL 的连接对象与本命名空间的 TcpClient 同名,文件内部统一用 SocketClient 指代它
using SocketClient = System.Net.Sockets.TcpClient;

namespace ZincXManagerShared.Network;

/// <summary>RCON 密码不对:服务端对认证包回了 -1。</summary>
public sealed class RconAuthException(string message) : Exception(message);

/// <summary>RCON 命令等回包超时(服务端没响应,不是网络断)。</summary>
public sealed class RconTimeoutException(string message) : Exception(message);

/// <summary>
/// MC 服务端的 RCON 客户端(RFC 式一问一答,只实现认证 + 执行命令两种包)。
///
/// 包结构全是小端:<c>长度(4) | 请求ID(4) | 类型(4) | 负载 | 0x00 0x00</c>。
/// 长度字段 = 请求ID + 类型 + 负载 + 两个结尾空字节,不含它自己。
///
/// 一条连接同一时刻只能跑一问一答,所以 <see cref="SendAsync"/> 用信号量串行,
/// 并发进来的命令排队而不是把包写花。
/// </summary>
public sealed class RconClient : IAsyncDisposable
{
    private const int TypeResponse = 0;      // 服务端回的执行结果
    private const int TypeCommand = 2;       // 客户端发的执行命令(也是服务端回的认证结果)
    private const int TypeAuth = 3;          // 客户端发的密码

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 一次命令最多扫描多少个帧去找自己的回包。
    /// 除了自己的分片，还要容得下上一条命令的残留帧和乱序帧，所以给得比较宽。
    /// </summary>
    private const int MaxFrames = 64;

    /// <summary>
    /// 负载到了这么长就按「可能还是分片」对待，读完要多等一会后续帧。
    /// 会按块切包的服务端就是这个量级(4 KB / 64 KB 都落在上面)，小到几百字节的回包不可能是分片。
    /// </summary>
    private const int FragmentThreshold = 4096;

    /// <summary>满帧之后最多再等多久(毫秒)看有没有后续帧，等的是分片之间的网络延迟，不是整条命令。</summary>
    private const int FragmentGraceMs = 200;

    /// <summary>短帧之后只可能是服务端拆成两个小包，给一拍的宽容(毫秒)就够，别让每条命令都多等。</summary>
    private const int ShortGraceMs = 20;

    /// <summary>等后续帧时的轮询间隔(毫秒)。</summary>
    private const int PendingPollMs = 5;

    private readonly string _host;
    private readonly int _port;
    private readonly string _password;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private SocketClient? _client;
    private NetworkStream? _stream;

    /// <summary>请求 ID 自增(服务端会原样回带,用来对包)。</summary>
    private int _seq;

    /// <summary>建一个客户端,不会自动连;先 <see cref="ConnectAsync"/>。</summary>
    public RconClient(string host, int port, string password)
    {
        _host = host;
        _port = port;
        _password = password;
    }

    /// <summary>当前是否连着(只看 socket 状态,不代表服务端还认这条连接)。</summary>
    public bool Connected => _client?.Connected == true;

    /// <summary>
    /// 最近一次成功往返的时间(UTC):认证通过或命令拿到回包时刷新。
    /// 对端进程被杀后 <see cref="Connected"/> 还会假报 true,判在线只能靠这个时间差。
    /// </summary>
    public DateTime LastOkAt { get; private set; } = DateTime.MinValue;

    /// <summary>连上去并完成密码认证;密码不对抛 <see cref="RconAuthException"/>。</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await CloseAsync().ConfigureAwait(false);

        var client = new SocketClient();   //未连接的套接字,下面自己控制超时
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);

            _client = client;
            _stream = client.GetStream();

            var id = NextId();
            await WriteFrameAsync(TypeAuth, _password, id, ct).ConfigureAwait(false);

            // 有的实现会先发一个空的 RESPONSE_VALUE(id=0) 再发认证结果,要把这种包跳过
            for (var i = 0; i < MaxFrames; i++)
            {
                var (frameId, _, body) = await ReadFrameAsync(ct).ConfigureAwait(false);
                if (frameId == -1)
                {
                    throw new RconAuthException("RCON 密码不对");
                }

                if (frameId == id)
                {
                    // 认证也是一次成功的往返:IsOnline 不必等到下一条命令才算新鲜
                    LastOkAt = DateTime.UtcNow;
                    return;
                }

                // frameId 既不是 -1 也不是我们发的 id:是服务端多塞的包(常见的是认证前的空响应),继续读
                _ = body;
            }

            throw new RconAuthException("RCON 认证没有回包");
        }
        catch
        {
            await CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>执行一条命令并拿回文本结果。没连着就抛 <see cref="InvalidOperationException"/>。</summary>
    public async Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("RCON 没连上");
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = NextId();
            try
            {
                await WriteFrameAsync(TypeCommand, command, id, ct).ConfigureAwait(false);
                var body = await ReadResponseAsync(id, ct).ConfigureAwait(false);
                LastOkAt = DateTime.UtcNow;   //拿到回包才算这条命令成功,IsOnline 靠它判新鲜
                return body;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 令牌里只有我们自己的 CommandTimeout 和调用方的取消;调用方没撤,那就一定是等回包超时。
                // 单独一个异常类型,免得上层只能靠 ct.IsCancellationRequested 猜是超时还是取消。
                throw new RconTimeoutException("RCON 命令超时:服务端没回包");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 读到本命令的完整回包:同 ID 的连续帧拼起来;别的 ID(上一条命令的残留、乱序)丢掉继续等。
    /// </summary>
    private async Task<string> ReadResponseAsync(int id, CancellationToken ct)
    {
        var parts = new StringBuilder();   //同一 ID 的多帧拼在这里
        var lastLength = -1;               //上一帧负载长度;-1 表示还没拿到过自己的帧

        for (var i = 0; i < MaxFrames; i++)
        {
            // 已经收到过自己的帧、套接字里又没有新帧 = 单帧回包,立刻收工(不能干等到 CommandTimeout)
            if (lastLength >= 0 && !await HasPendingFrameAsync(lastLength, ct).ConfigureAwait(false))
            {
                break;
            }

            var (frameId, _, body) = await ReadFrameAsync(ct).ConfigureAwait(false);

            // 认证过期 / 服务端认不出我们:不要当成功
            if (frameId == -1)
            {
                throw new RconAuthException("RCON 会话失效,需要重新认证");
            }

            // MC 的服务端会原样回带请求 ID,对不上说明是上一条命令的残留或乱序帧:
            // 丢掉它继续等自己的(这里不能 break,否则本命令的回包会被留在套接字里串到下一条命令)
            if (frameId != id)
            {
                lastLength = -1;
                continue;
            }

            parts.Append(body);
            lastLength = body.Length;
        }

        if (lastLength < 0)
        {
            throw new InvalidDataException("RCON 没有回这条命令的结果");
        }

        return parts.ToString();
    }

    /// <summary>
    /// 套接字里还有没有下一帧可读:服务端可能把一条响应拆成多个包(同 ID),读完一帧要先看一眼。
    /// 只看「已有数据」,不真去读——读不到就会挂到 CommandTimeout,把正常单帧命令也拖成 5 秒。
    /// </summary>
    private async Task<bool> HasPendingFrameAsync(int lastLength, CancellationToken ct)
    {
        var graceMs = lastLength >= FragmentThreshold ? FragmentGraceMs : ShortGraceMs;
        var deadline = DateTime.UtcNow.AddMilliseconds(graceMs);

        while (true)
        {
            if (_stream!.DataAvailable)
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(PendingPollMs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>断开连接(不抛异常)。</summary>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _lock.Dispose();
    }

    private async Task CloseAsync()
    {
        var stream = _stream;   //先摘引用再关,别让别的线程拿到半死状态
        var client = _client;
        _stream = null;
        _client = null;

        try
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // 已经断了,关不掉无所谓
        }

        client?.Dispose();
    }

    private int NextId()
    {
        var id = Interlocked.Increment(ref _seq);
        return id == -1 ? Interlocked.Increment(ref _seq) : id;   //-1 被服务端用作"认证失败",别撞上
    }

    private async Task WriteFrameAsync(int type, string body, int id, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(body);   //命令与回包都按 UTF-8,MC 侧也是
        var frame = new byte[14 + payload.Length];    // 4 长度 + 4 编号 + 4 类型 + 负载 + 2 结尾

        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), 10 + payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), id);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8, 4), type);
        payload.CopyTo(frame, 12);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);
        await _stream!.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
        await _stream.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    private async Task<(int Id, int Type, string Body)> ReadFrameAsync(CancellationToken ct)
    {
        var head = new byte[4];   //长度
        await ReadExactlyAsync(head, ct).ConfigureAwait(false);

        var size = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (size < 10 || size > 1024 * 1024)
        {
            throw new InvalidDataException($"RCON 回包长度非法:{size}");
        }

        var rest = new byte[size];   //编号 + 类型 + 负载 + 两个空字节
        await ReadExactlyAsync(rest, ct).ConfigureAwait(false);

        var id = BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(0, 4));
        var type = BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(4, 4));
        var bodyLength = size - 10;   //去掉编号、类型、两个结尾空字节
        var body = bodyLength <= 0 ? "" : Encoding.UTF8.GetString(rest, 8, bodyLength);

        return (id, type, body);
    }

    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);

        var read = 0;   //已经读了多少字节
        while (read < buffer.Length)
        {
            var n = await _stream!.ReadAsync(buffer.AsMemory(read), timeout.Token).ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException("RCON 连接被对端关闭");
            }

            read += n;
        }
    }
}
