using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ZincXManagerShared.Logging;

namespace ZincXManagerShared.Network;

/// <summary>
/// TCP 服务端实现(<see cref="IServer"/>)。ZXMS / ZXMOS 用它监听。
///
/// 并发模型:1 个接受连接的后台循环 → 每个客户端一条独立接收循环(互不阻塞)→ 解析出的包统一进
/// <see cref="PacketDispatcher{TPacket}"/> 队列 → 处理任务按顺序执行 handler。
///
/// 相比最初版本补齐:连接可管理(<see cref="Connections"/>)、上下线事件、群发、
/// 回包写锁(<see cref="TcpConnection"/>)、包长校验、<see cref="CloseAsync"/> 幂等。
/// </summary>
public class TcpServer : IServer
{
    private readonly IPAddress _ipAddress;
    private readonly ushort _port;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>在线连接:连接 id → 连接。</summary>
    private readonly ConcurrentDictionary<Guid, TcpConnection> _connections = new();

    /// <summary>每条连接的接收循环任务,关闭时等它们退出。</summary>
    private readonly ConcurrentDictionary<Guid, Task> _clientTasks = new();

    private readonly PacketDispatcher<ServerboundPacket> _dispatcher;
    private Task? _acceptTask;

    /// <summary>0 = 未关闭,1 = 已关闭(让 <see cref="CloseAsync"/> 幂等)。</summary>
    private int _closed;

    public TcpServer(IPAddress ipAddress, ushort port)
    {
        _ipAddress = ipAddress;
        _port = port;
        _listener = new TcpListener(_ipAddress, port);
        _dispatcher = new PacketDispatcher<ServerboundPacket>(_cts.Token);
    }

    /// <summary>客户端接入时触发。</summary>
    public event Action<TcpConnection>? Connected;

    /// <summary>客户端断开时触发。</summary>
    public event Action<TcpConnection>? Disconnected;

    /// <summary>当前所有在线连接(每次访问一份快照)。</summary>
    public IReadOnlyCollection<TcpConnection> Connections => _connections.Values.ToArray();

    /// <summary>连接登记表:自动登记接入、注销断开,收包计数;身份 / 模式由业务侧 SetRole 补上。</summary>
    public ClientRegistry Registry { get; } = new();

    /// <summary>开始监听;立即返回,端口占用等错误直接抛出。</summary>
    /// <exception cref="InvalidOperationException">重复调用</exception>
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

    /// <summary>注册收包处理器(所有连接共用)。</summary>
    public void AddHandler(Func<ServerboundPacket, Task> handler) => _dispatcher.AddHandler(handler);

    /// <summary>向所有在线客户端广播一个包;单条连接写失败只记日志。</summary>
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

    /// <summary>接受连接循环:每来一个客户端就建连接、登记在线表、起一条独立的处理循环。</summary>
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

            // 先登记,再启动接收循环 —— 否则客户端如果立刻发来包,handler 里的 SetRole
            // 会作用在"还没登记"的条目上,随后 Add 又把身份覆盖成"未识别"
            Registry.Add(connection);

            _clientTasks[connection.Id] = HandleClientAsync(connection);

            Logger.Log(LogLevel.Info, $"[Network] 客户端接入 {connection.RemoteEndPoint} (id={connection.Id})");
            SafeInvoke(Connected, connection);
        }
    }

    /// <summary>单条连接的接收循环:解析包 → 包成带连接的 ServerboundPacket 入队;结束时清理并通知断开。</summary>
    private async Task HandleClientAsync(TcpConnection connection)
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var packet = await Packet.ReadAsync(connection.Stream, _cts.Token);
                Registry.Touch(connection.Id);
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
            // 先通知断开(此时登记表里还留着这条数据,调用方能读到身份/模式/在线时长),再注销
            SafeInvoke(Disconnected, connection);

            _connections.TryRemove(connection.Id, out _);
            _clientTasks.TryRemove(connection.Id, out _);
            Registry.Remove(connection.Id);
            await connection.DisposeAsync();

            Logger.Log(LogLevel.Info, $"[Network] 客户端断开 {connection.RemoteEndPoint} (id={connection.Id})");
        }
    }

    /// <summary>触发事件时兜底:外部回调抛异常不影响网络循环。</summary>
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

    /// <summary>停止监听、关闭所有连接、释放资源;幂等。</summary>
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
