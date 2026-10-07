using System.Net;
using System.Net.Sockets;
using ZincXManagerShared.Logging;

// BCL 的连接对象与本类同名,文件内部统一用 SocketClient 指代它
using SocketClient = System.Net.Sockets.TcpClient;

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
    private readonly IPAddress _ipAddress;
    private readonly ushort _port;
    private readonly SocketClient _client = new();

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

    public TcpClient(IPAddress ipAddress, ushort port)
    {
        _ipAddress = ipAddress;
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

        await _client.ConnectAsync(_ipAddress, _port);
        _stream = _client.GetStream();
        _receiveTask = ReceiveLoopAsync();

        Logger.Log(LogLevel.Info, $"[Network] 已连接服务端 {_ipAddress}:{_port}");
    }

    /// <summary>接收循环:解析完整包并交给处理器队列;各种断开都只记日志,不往外抛。</summary>
    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var packet = await Packet.ReadAsync(_stream!, _cts.Token);
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
            await _stream.WriteAsync(packet.ToBytes(), cancellationToken);
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
