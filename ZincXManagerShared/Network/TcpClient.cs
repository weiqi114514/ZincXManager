using System.Net;
using System.Net.Sockets;
using ZincXManagerShared.Logging;

// BCL 的连接对象与本类同名,文件内部统一用 SocketClient 指代它
using SocketClient = System.Net.Sockets.TcpClient;

using ZincXManagerShared.Security;

namespace ZincXManagerShared.Network;

/// <summary>
/// TCP 客户端实现(<see cref="IClient"/>)。ZXMC 用它连 ZXMS / ZXMOS。
///
/// 收包链路:后台接收循环用 <see cref="Packet.ReadAsync"/> 解析完整包 → 交给 <see cref="PacketDispatcher{TPacket}"/>
/// 入队 → 处理任务按顺序执行 handler(所以 handler 慢不会卡住收包)。
/// 发包链路:<see cref="WriteAsync"/> 抢写锁 → <c>Packet.ToBytes()</c> → 写网络流。
///
/// 注意:类名与 <c>System.Net.Sockets.TcpClient</c> 重名,同时 using 两个命名空间时要写别名。
/// </summary>
public class TcpClient : IClient
{
    private readonly IPAddress? _ipAddress;
    private readonly string? _host;
    private readonly ushort _port;

    // 域名解析出多个地址时要换一个重试,所以不能是 readonly
    private SocketClient _client = new();

    /// <summary>连接成功后的网络流;未连接时是 null。</summary>
    private NetworkStream? _stream;

    private readonly CancellationTokenSource _cts = new();
    private readonly PacketDispatcher<Packet> _dispatcher;

    /// <summary>写锁:并发发包不会写花。</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private Task? _receiveTask;

    /// <summary>0 = 未关闭,1 = 已关闭(让 <see cref="CloseAsync"/> 幂等)。</summary>
    private int _closed;

    /// <summary>接收循环是否已退出(对端断开 / 连接中断时置位)。</summary>
    private volatile bool _disconnected;

    /// <summary>是否仍处于已连接状态(连上了、接收循环还在跑、还没关闭);命令和界面显示用。</summary>
    public bool IsConnected => _stream is not null && _closed == 0 && !_disconnected;

    /// <summary>会话加密器;协商完成后非 null,之后收发的包都会加解密。</summary>
    public SessionCrypto? Crypto { get; set; }

    /// <summary>连的是谁(日志/报错用)。</summary>
    public string Target => _ipAddress?.ToString() ?? _host ?? "?";

    /// <summary>按 IP 连(老写法)。</summary>
    public TcpClient(IPAddress ipAddress, ushort port)
    {
        _ipAddress = ipAddress;
        _port = port;
        _dispatcher = new PacketDispatcher<Packet>(_cts.Token);
    }

    /// <summary>
    /// 按主机名(域名)连 —— 真正连的时候才做 DNS 解析,所以 config 里写域名是可以的。
    /// </summary>
    public TcpClient(string host, ushort port)
    {
        _host = HostText.Normalize(host);
        _port = port;
        _dispatcher = new PacketDispatcher<Packet>(_cts.Token);
    }

    /// <summary>注册收包处理器;可多个,按注册顺序执行。</summary>
    public void AddHandler(Func<Packet, Task> handler) => _dispatcher.AddHandler(handler);

    /// <summary>连接服务端并启动接收循环。</summary>
    /// <exception cref="ObjectDisposedException">实例已关闭</exception>
    public async Task ConnectAsync()
    {
        if (_closed != 0)
        {
            throw new ObjectDisposedException(nameof(TcpClient));
        }

        if (_ipAddress is not null)
        {
            await _client.ConnectAsync(_ipAddress, _port);
        }
        else
        {
            // 域名:交给系统 DNS。解析出来可能有多个地址(常见 IPv6 + IPv4),
            // 一个连不上就试下一个 —— 别因为 IPv6 那条路不通就把整个连接判死。
            var addresses = await Dns.GetHostAddressesAsync(_host!);
            if (addresses.Length == 0)
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }

            Exception? lastError = null;
            foreach (var address in addresses)
            {
                try
                {
                    await _client.ConnectAsync(address, _port);
                    lastError = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _client.Dispose();
                    _client = new SocketClient();
                }
            }

            if (lastError is not null)
            {
                throw lastError;
            }
        }

        _stream = _client.GetStream();
        _receiveTask = ReceiveLoopAsync();

        Logger.Log(LogLevel.Info, $"[Network] 已连接服务端 {Target}:{_port}");
    }

    /// <summary>接收循环:解析完整包并交给处理器队列;各种断开都只记日志,不往外抛。</summary>
    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var packet = await Packet.ReadAsync(_stream!, _cts.Token);

                if ((packet.Flags & SessionCrypto.FlagEncrypted) != 0)
                {
                    if (Crypto is null)
                    {
                        throw new InvalidDataException("收到加密包,但会话密钥尚未协商");
                    }

                    packet = new Packet(packet.Id, packet.Type, Crypto.Decrypt(packet.Data),
                        (ushort)(packet.Flags & ~SessionCrypto.FlagEncrypted));
                }

                await _dispatcher.EnqueueAsync(packet, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 自己调用了 CloseAsync,正常退出
        }
        catch (EndOfStreamException)
        {
            Logger.Log(LogLevel.Info, "[Network] 服务端已关闭连接");
        }
        catch (IOException ex)
        {
            Logger.Log(LogLevel.Warning, "[Network] 连接中断: " + ex.Message);
        }
        catch (InvalidDataException ex)
        {
            Logger.Log(LogLevel.Warning, "[Network] 收到非法包,连接关闭: " + ex.Message);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[Network] 接收循环异常: " + ex.Message);
        }
        finally
        {
            _disconnected = true;   // 循环退出就不再是"已连接"
        }
    }

    /// <summary>发一个包;内部写锁串行化,可安全并发调用。返回只代表写进流缓冲,不代表对端已收到。</summary>
    /// <exception cref="InvalidOperationException">尚未连接</exception>
    public async Task WriteAsync(Packet packet, CancellationToken cancellationToken = default)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Client is not connected");
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var outgoing = packet;
            if (Crypto is not null)
            {
                var encrypted = Crypto.Encrypt(packet.Data);
                outgoing = new Packet(packet.Id, packet.Type, encrypted,
                    (ushort)(packet.Flags | SessionCrypto.FlagEncrypted));
            }

            await _stream.WriteAsync(outgoing.ToBytes(), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>关闭连接并释放资源;幂等,关闭后实例不可复用。</summary>
    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();

        if (_stream is not null)
        {
            try
            {
                await _stream.DisposeAsync();
            }
            catch (IOException)
            {
                // 流已不可用,忽略
            }
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask;
            }
            catch (IOException)
            {
                // 流已释放,忽略
            }
        }

        await _dispatcher.DisposeAsync();

        _client.Dispose();
        _cts.Dispose();
        _writeLock.Dispose();

        Logger.Log(LogLevel.Info, "[Network] 客户端连接已关闭");
    }
}
