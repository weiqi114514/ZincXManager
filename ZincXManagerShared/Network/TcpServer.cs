using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ZincXManagerShared.Logging;

namespace ZincXManagerShared.Network;

/// <summary>
/// TCP 服务端实现(<see cref="IServer"/>)。ZXMS / ZXMOS 用它监听客户端。
///
/// <para><b>完整用法(每段都标了作用):</b></para>
/// <code>
/// // ① 建实例:记下监听地址与端口,此时还没监听
/// var server = new TcpServer(IPAddress.Any, 25565);
///
/// // ② 订阅上下线:维护在线表 / 发欢迎语 / 清理会话
/// server.Connected    += conn => Console.WriteLine("上线 " + conn.RemoteEndPoint);
/// server.Disconnected += conn => Console.WriteLine("下线 " + conn.RemoteEndPoint);
///
/// // ③ 注册收包处理器:所有连接的包都进这里
/// server.AddHandler(async packet =>
/// {
///     if (packet.Type == 100)
///         await packet.WriteAsync(new Packet(packet.Id, 101, Encoding.UTF8.GetBytes("ok")));   // 回包
/// });
///
/// // ④ 开始监听:立即返回,接受连接在后台进行
/// server.Open();
///
/// // ⑤ 主动下发:广播给所有人,或按 id 找一条连接单独推
/// await server.BroadcastAsync(new Packet(0, 999, Encoding.UTF8.GetBytes("维护公告")));
///
/// // ⑥ 关闭:取消 → 停监听 → 等连接循环退出 → 关连接 → 释放(幂等)
/// await server.CloseAsync();
/// </code>
///
/// <para><b>并发模型(一个客户端一套):</b>接受连接(1 个后台循环)→
/// 每条连接 1 条独立接收循环(互不阻塞)→ 解析出的包统一进
/// <see cref="PacketDispatcher{TPacket}"/> 队列 → 处理任务按顺序执行处理器。</para>
///
/// <para><b>相比早期版本补齐的点:</b>连接可管理(<see cref="Connections"/>)、
/// 上下线事件、群发(<see cref="BroadcastAsync"/>)、回包写锁(<see cref="TcpConnection"/>)、
/// 包长校验(<see cref="Packet.ReadAsync"/>)、<see cref="CloseAsync"/> 幂等。</para>
/// </summary>
public class TcpServer : IServer
{
    /// <summary>监听的地址(常用 <see cref="IPAddress.Any"/> 表示所有网卡)。</summary>
    private readonly IPAddress _ipAddress;

    /// <summary>监听的端口。</summary>
    private readonly ushort _port;

    /// <summary>底层监听器,负责绑定端口与接受连接。</summary>
    private readonly TcpListener _listener;

    /// <summary>取消源:<see cref="CloseAsync"/> 时触发,终止接受循环与所有连接循环。</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>当前在线连接:连接 id → 连接(用于按 id 定位、群发、统计在线数)。</summary>
    private readonly ConcurrentDictionary<Guid, TcpConnection> _connections = new();

    /// <summary>每条连接的接收循环任务,关闭时等它们退出。</summary>
    private readonly ConcurrentDictionary<Guid, Task> _clientTasks = new();

    /// <summary>收包处理器队列(所有连接共用;读 socket 与执行 handler 解耦)。</summary>
    private readonly PacketDispatcher<ServerboundPacket> _dispatcher;

    /// <summary>接受连接的后台循环任务。</summary>
    private Task? _acceptTask;

    /// <summary>0 = 未关闭,1 = 已关闭(让 <see cref="CloseAsync"/> 幂等)。</summary>
    private int _closed;

    /// <summary>创建服务端实例(此时还没有开始监听)。</summary>
    /// <param name="ipAddress">监听地址</param>
    /// <param name="port">监听端口</param>
    public TcpServer(IPAddress ipAddress, ushort port)
    {
        _ipAddress = ipAddress;
        _port = port;
        _listener = new TcpListener(_ipAddress, port);
        _dispatcher = new PacketDispatcher<ServerboundPacket>(_cts.Token);
    }

    /// <summary>客户端接入时触发(参数是刚建立的连接)。</summary>
    public event Action<TcpConnection>? Connected;

    /// <summary>客户端断开时触发(参数是已关闭的连接)。</summary>
    public event Action<TcpConnection>? Disconnected;

    /// <summary>当前所有在线连接(每次访问返回一份快照,遍历时不用担心被并发修改)。</summary>
    public IReadOnlyCollection<TcpConnection> Connections => _connections.Values.ToArray();

    /// <summary>
    /// 开始监听端口。
    /// <para>作用:绑定端口 → 启动接受连接的后台循环(每个客户端一条连接 + 一条接收循环)。</para>
    /// <para>立即返回;端口被占用等错误会直接抛出。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">重复调用 <see cref="Open"/></exception>
    public void Open()
    {
        if (_acceptTask is not null)
        {
            throw new InvalidOperationException("服务端已经启动");
        }

        _listener.Start();
        _acceptTask = AcceptLoopAsync();

        Logger.Log(LogLevel.Info, $"[Network] 服务端已启动,监听 {_ipAddress}:{_port}");
    }

    /// <summary>
    /// 注册收包处理器(收包入口)。
    /// <para>作用:声明"收到任意客户端的包之后做什么";所有连接共用,按注册顺序依次 await 执行。</para>
    /// <para>处理器与 socket 读取已解耦,耗时操作不会卡住收包。</para>
    /// </summary>
    public void AddHandler(Func<ServerboundPacket, Task> handler)
    {
        _dispatcher.AddHandler(handler);
    }

    /// <summary>
    /// 向所有在线客户端广播一个包(主动下发)。
    /// <para>作用:发公告、同步状态这类"一对多"的消息;单条连接写失败只记日志,不影响其它连接。</para>
    /// </summary>
    public async Task BroadcastAsync(Packet packet, CancellationToken cancellationToken = default)
    {
        foreach (var connection in _connections.Values.ToArray())
        {
            try
            {
                await connection.WriteAsync(packet, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, $"[Network] 广播到 {connection.RemoteEndPoint} 失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 接受连接循环(后台任务)。
    /// <para>作用:每来一个客户端就建一条 <see cref="TcpConnection"/>、登记进在线表、
    /// 起一条独立的处理循环(互不等待),并触发 <see cref="Connected"/>。</para>
    /// </summary>
    private async Task AcceptLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            System.Net.Sockets.TcpClient socket;
            try
            {
                socket = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break; // 监听器已被 Stop
            }
            catch (SocketException ex)
            {
                Logger.Log(LogLevel.Error, "[Network] 接受连接失败: " + ex.Message);
                continue;
            }

            var connection = new TcpConnection(socket);
            _connections[connection.Id] = connection;
            _clientTasks[connection.Id] = HandleClientAsync(connection);

            Logger.Log(LogLevel.Info, $"[Network] 客户端接入 {connection.RemoteEndPoint} (id={connection.Id})");
            SafeInvoke(Connected, connection);
        }
    }

    /// <summary>
    /// 单条连接的接收循环(每个客户端一条)。
    /// <para>作用:解析完整包 → 包成 <see cref="ServerboundPacket"/>(绑上连接,便于回包)→ 入队。</para>
    /// <para>收尾:对端断开 / 取消 / 非法包都走 finally —— 从在线表移除、关闭连接、触发
    /// <see cref="Disconnected"/>。</para>
    /// </summary>
    private async Task HandleClientAsync(TcpConnection connection)
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var packet = await Packet.ReadAsync(connection.Stream, _cts.Token);
                await _dispatcher.EnqueueAsync(new ServerboundPacket(packet, connection), _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 服务端关闭,正常退出
        }
        catch (EndOfStreamException)
        {
            // 客户端断开,正常退出
        }
        catch (IOException ex)
        {
            Logger.Log(LogLevel.Warning, $"[Network] 连接 {connection.RemoteEndPoint} 中断: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            Logger.Log(LogLevel.Warning, $"[Network] 连接 {connection.RemoteEndPoint} 发送非法包,已断开: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, $"[Network] 连接 {connection.RemoteEndPoint} 处理异常: {ex}");
        }
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            _clientTasks.TryRemove(connection.Id, out _);
            await connection.DisposeAsync();

            Logger.Log(LogLevel.Info, $"[Network] 客户端断开 {connection.RemoteEndPoint} (id={connection.Id})");
            SafeInvoke(Disconnected, connection);
        }
    }

    /// <summary>触发上下线事件时的兜底:外部回调抛异常不影响网络循环。</summary>
    private static void SafeInvoke(Action<TcpConnection>? handler, TcpConnection connection)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(connection);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[Network] 连接事件回调异常: " + ex.Message);
        }
    }

    /// <summary>
    /// 关闭服务端并清理资源。
    /// <para>作用:取消令牌 → 停止监听 → 等接受循环与所有连接循环退出 → 关闭所有连接 → 停止处理队列 → 释放资源。</para>
    /// <para>幂等:重复调用安全。</para>
    /// </summary>
    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        _listener.Stop();

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask;
            }
            catch (IOException)
            {
                // 监听器已停止,忽略
            }
        }

        var tasks = _clientTasks.Values.ToArray();
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
                // 各连接循环内部已各自处理异常,这里只等它们结束
            }
        }

        foreach (var connection in _connections.Values.ToArray())
        {
            await connection.DisposeAsync();
        }
        _connections.Clear();

        await _dispatcher.DisposeAsync();
        _cts.Dispose();

        Logger.Log(LogLevel.Info, "[Network] 服务端已关闭");
    }
}
