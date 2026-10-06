using System.Threading.Channels;
using ZincXManagerShared.Logging;

namespace ZincXManagerShared.Network;

/// <summary>
/// 收包处理器队列:把「读 socket」和「执行 handler」解耦(收包链路的中转站)。
///
/// <para><b>没有它的时候会怎样:</b>handler 直接在接收循环里 await 执行,
/// 一旦某个 handler 去查数据库 / 写文件(几百毫秒),接收循环就停在那里,
/// socket 缓冲区满了以后对端也发不动了 —— 一个慢 handler 拖住整条连接。</para>
///
/// <para><b>有了它之后:</b></para>
/// <code>
/// 接收循环:解析出包 → 入队(极快,只做一次内存写入)→ 立刻回去读下一个包
/// 处理任务:从队列按【先进先出】取出 → 依次 await 所有 handler
///
/// ① 顺序不乱:FIFO + 单消费者,包的处理顺序与到达顺序一致
/// ② 互不拖累:handler 慢只堆积队列,不阻塞 socket 读取
/// ③ 内存有界:队列容量默认 256,写满时入队方 await(背压),
///    压力最终传导给对端,而不是把内存吃光
/// ④ 出错隔离:单个 handler 抛异常只写日志,不影响其它 handler 与后续包
/// </code>
///
/// <para>包类型参数 <c>TPacket</c>:客户端用 <see cref="Packet"/>,服务端用 <see cref="ServerboundPacket"/>。</para>
/// </summary>
/// <typeparam name="TPacket">包类型</typeparam>
internal sealed class PacketDispatcher<TPacket>
{
    /// <summary>待处理包队列(有界、单读单写)。</summary>
    private readonly Channel<TPacket> _queue;

    /// <summary>所属连接 / 服务端的取消令牌:取消后处理任务退出。</summary>
    private readonly CancellationToken _cancellationToken;

    /// <summary>处理器列表。用"整体替换数组"的方式更新,读侧不需要加锁。</summary>
    private Func<TPacket, Task>[] _handlers = [];

    /// <summary>处理任务(构造时启动,一直在后台等队列)。</summary>
    private readonly Task _worker;

    /// <param name="cancellationToken">所属连接 / 服务端的取消令牌</param>
    /// <param name="capacity">队列容量(背压阈值),默认 256 个包</param>
    public PacketDispatcher(CancellationToken cancellationToken, int capacity = 256)
    {
        _cancellationToken = cancellationToken;
        _queue = Channel.CreateBounded<TPacket>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,   // 只有一个处理任务在读
            SingleWriter = true,   // 只有接收循环在写
            FullMode = BoundedChannelFullMode.Wait   // 满了就让写入方等 = 背压
        });
        _worker = WorkerAsync();
    }

    /// <summary>
    /// 注册一个收包处理器。
    /// <para>作用:告诉队列"包取出来之后交给谁处理";可以随时注册,按注册顺序依次 await 执行。</para>
    /// </summary>
    public void AddHandler(Func<TPacket, Task> handler)
    {
        var current = Volatile.Read(ref _handlers);
        var next = new Func<TPacket, Task>[current.Length + 1];
        current.CopyTo(next, 0);
        next[^1] = handler;
        Volatile.Write(ref _handlers, next);
    }

    /// <summary>
    /// 入队一个待处理的包(接收循环调用)。
    /// <para>作用:把包从"读取线程"交给"处理任务";队列满时会等待(背压)。</para>
    /// </summary>
    public ValueTask EnqueueAsync(TPacket packet, CancellationToken cancellationToken = default)
        => _queue.Writer.WriteAsync(packet, cancellationToken);

    /// <summary>
    /// 处理任务:按 FIFO 取出包,依次 await 所有处理器。
    /// <para>作用:真正执行 handler 的地方;单个处理器抛异常只记录日志,不影响后续。</para>
    /// </summary>
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

    /// <summary>
    /// 停止入队并等待处理任务退出。
    /// <para>作用:关闭连接/服务端时调用,先把写入端标记完成,再等处理任务收尾;幂等。</para>
    /// </summary>
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
