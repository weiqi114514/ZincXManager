using System.Net;
using System.Net.Sockets;
using ZincXManagerShared.Logging;

// BCL 的连接对象与本类同名,这里起别名:文件内部统一用 SocketClient 指代 BCL 的实现
using SocketClient = System.Net.Sockets.TcpClient;

namespace ZincXManagerShared.Network;

/// <summary>
/// TCP 客户端实现(<see cref="IClient"/>)。ZXMC 用它连 ZXMS / ZXMOS。
///
/// <para><b>完整用法(每段都标了作用):</b></para>
/// <code>
/// // 需要时给 BCL 那个同名类型起别名,避免二义性
/// using TcpClient = ZincXManagerShared.Network.TcpClient;
///
/// // ① 建实例:只记地址端口,不建立连接
/// var client = new TcpClient(IPAddress.Parse("127.0.0.1"), 25565);
///
/// // ② 注册收包处理器:收到包后做什么,业务逻辑写这里
/// client.AddHandler(async packet =>
/// {
///     if (packet.Type == 101)
///         Console.WriteLine(Encoding.UTF8.GetString(packet.Data));   // 取负载
/// });
///
/// // ③ 连接:建立 TCP + 拿流 + 启动后台接收循环
/// await client.ConnectAsync();
///
/// // ④ 发包:构造包 → 写锁 → 写流;Id/Type 按业务协议约定
/// await client.WriteAsync(new Packet(1, 100, Encoding.UTF8.GetBytes("hello")));
///
/// // ⑤ 关闭:取消接收循环 → 关流 → 等收尾 → 释放资源(幂等)
/// await client.CloseAsync();
/// </code>
///
/// <para><b>收包链路(每个包):</b><c>ReceiveLoopAsync</c> 用 <see cref="Packet.ReadAsync"/> 解析完整包
/// → 交给 <see cref="PacketDispatcher{TPacket}"/> 入队 → 处理任务按顺序执行所有 handler。
/// 所以 handler 慢不会卡住收包,包的处理顺序与到达顺序一致。</para>
///
/// <para><b>发包链路:</b><see cref="WriteAsync"/> 抢写锁 → <c>Packet.ToBytes()</c> → 写网络流。</para>
///
/// <para><b>注意:</b>类名与 <c>System.Net.Sockets.TcpClient</c> 重名,同时 using 两个命名空间时要写别名。</para>
/// </summary>
public class TcpClient : IClient
{
    /// <summary>要连接的服务端地址。</summary>
    private readonly IPAddress _ipAddress;

    /// <summary>要连接的服务端端口。</summary>
    private readonly ushort _port;

    /// <summary>底层 Socket 客户端(BCL 实现),负责真正的 TCP 连接。</summary>
    private readonly SocketClient _client = new();

    /// <summary>连接建立后拿到的网络流;未连接时是 null。</summary>
    private NetworkStream? _stream;

    /// <summary>取消源:<see cref="CloseAsync"/> 时触发,用来终止接收循环与处理任务。</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>收包处理器队列(读 socket 与执行 handler 解耦)。</summary>
    private readonly PacketDispatcher<Packet> _dispatcher;

    /// <summary>写锁:保证并发发包时不会把两个包写花。</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>后台接收循环任务,关闭时等它退出。</summary>
    private Task? _receiveTask;

    /// <summary>0 = 未关闭,1 = 已关闭(让 <see cref="CloseAsync"/> 幂等)。</summary>
    private int _closed;

    /// <summary>创建客户端实例(此时还没有建立连接)。</summary>
    /// <param name="ipAddress">服务端 IP</param>
    /// <param name="port">服务端端口</param>
    public TcpClient(IPAddress ipAddress, ushort port)
    {
        _ipAddress = ipAddress;
        _port = port;
        _dispatcher = new PacketDispatcher<Packet>(_cts.Token);
    }

    /// <summary>
    /// 注册收包处理器。
    /// <para>作用:声明"收到包之后做什么";可注册多个,按注册顺序依次 await 执行。</para>
    /// <para>处理器与 socket 读取已解耦,里面做耗时操作不会卡住收包。</para>
    /// </summary>
    public void AddHandler(Func<Packet, Task> handler)
    {
        _dispatcher.AddHandler(handler);
    }

    /// <summary>
    /// 连接服务端并启动接收循环。
    /// <para>作用:三次握手建立连接 → 取网络流 → 起后台接收循环(之后才开始真正收包)。</para>
    /// <para>连接失败会抛异常,由调用方决定重试策略。</para>
    /// </summary>
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

    /// <summary>
    /// 接收循环(后台任务):不断解析出完整包并交给处理器队列。
    /// <para>作用:收包链路的源头 —— 只有它在读 socket。</para>
    /// <para>结束条件:主动关闭(取消)、对端断开、连接中断、收到非法包;都只记录日志,不往外抛。</para>
    /// </summary>
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
    }

    /// <summary>
    /// 发送一个包(发包入口)。
    /// <para>作用:把包写进网络流;内部用写锁串行化,可安全并发调用。</para>
    /// <para>返回只代表"已写入流缓冲",不代表对端已收到。</para>
    /// </summary>
    /// <param name="packet">要发送的包</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="InvalidOperationException">尚未调用 <see cref="ConnectAsync"/> 时抛出</exception>
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

    /// <summary>
    /// 关闭连接并清理资源。
    /// <para>作用:取消接收循环 → 关闭网络流 → 等接收循环与处理队列退出 → 释放 socket 与信号量。</para>
    /// <para>幂等:重复调用安全;关闭后实例不可复用,要重连请新建。</para>
    /// </summary>
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
