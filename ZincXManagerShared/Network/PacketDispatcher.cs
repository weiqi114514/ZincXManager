using System.Threading.Channels;
using ZincXManagerShared.Logging;

namespace ZincXManagerShared.Network;

/// <summary>
/// 收包处理器队列:把"读 socket"和"执行 handler"分开(收包链路的中转站)。
///
/// 没有它:handler 直接在接收循环里 await,一个慢 handler(查库、写文件)就把整条连接的收包卡住。
/// 有了它:接收循环只负责入队(极快),另一个任务按顺序取出并执行 handler —— 顺序不乱、互不拖累,
/// 队列满(默认 256)时入队方会等待(背压),不会把内存吃光。
///
/// 包类型参数:客户端用 <see cref="Packet"/>,服务端用 <see cref="ServerboundPacket"/>。
/// </summary>
internal sealed class PacketDispatcher<TPacket>
{
    private readonly Channel<TPacket> _queue;
    private readonly CancellationToken _cancellationToken;

    /// <summary>处理器列表;用"整体替换数组"更新,读侧不用加锁。</summary>
    private Func<TPacket, Task>[] _handlers = [];

    private readonly Task _worker;

    /// <param name="cancellationToken">所属连接 / 服务端的取消令牌</param>
    /// <param name="capacity">队列容量(背压阈值),默认 256 个包</param>
    public PacketDispatcher(CancellationToken cancellationToken, int capacity = 256)
    {
        _cancellationToken = cancellationToken;
        _queue = Channel.CreateBounded<TPacket>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait   // 满了让写入方等 = 背压
        });
        _worker = WorkerAsync();
    }

    /// <summary>注册收包处理器;可注册多个,按注册顺序依次 await 执行。</summary>
    public void AddHandler(Func<TPacket, Task> handler)
    {
        var current = Volatile.Read(ref _handlers);
        var next = new Func<TPacket, Task>[current.Length + 1];
        current.CopyTo(next, 0);
        next[^1] = handler;
        Volatile.Write(ref _handlers, next);
    }

    /// <summary>入队一个待处理的包(接收循环调用);队列满时等待。</summary>
    public ValueTask EnqueueAsync(TPacket packet, CancellationToken cancellationToken = default)
        => _queue.Writer.WriteAsync(packet, cancellationToken);

    /// <summary>处理任务:按 FIFO 取出包,依次 await 所有处理器;单个处理器抛异常只记日志。</summary>
    private async Task WorkerAsync()
    {
        try
        {
            await foreach (var packet in _queue.Reader.ReadAllAsync(_cancellationToken))
            {
                foreach (var handler in Volatile.Read(ref _handlers))
                {
                    try
                    {
                        await handler(packet);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(LogLevel.Error, "[Network] 收包处理器异常: " + ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 连接 / 服务端关闭,正常退出
        }
    }

    /// <summary>停止入队并等处理任务退出;幂等。</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try
        {
            await _worker;
        }
        catch (OperationCanceledException)
        {
            // 忽略
        }
    }
}
