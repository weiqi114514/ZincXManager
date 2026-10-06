namespace ZincXManagerShared.Network;

/// <summary>
/// 客户端接口(ZXMC 连 ZXMS / ZXMOS 时使用)。由 <see cref="TcpClient"/> 实现。
///
/// <para><b>完整用法(每段都标了它的作用):</b></para>
/// <code>
/// // ① 建实例:只记住目标地址和端口,此时【没有】建立任何连接,不占网络资源
/// var client = new TcpClient(IPAddress.Parse("127.0.0.1"), 25565);
///
/// // ② 注册收包处理器:连接成功后每收到一个完整包,都会依次调用这里(可注册多个)
/// //    作用:把"收到什么包该做什么"告诉网络层,业务处理都写在这里面
/// client.AddHandler(async packet =>
/// {
///     // packet.Id    对端给的包序号(回包时原样带回,用来配对请求与响应)
///     // packet.Type  业务类型,决定怎么解释负载
///     // packet.Flags 标志位(留给压缩/加密等扩展),默认 0
///     // packet.Data  负载字节;文本用 Encoding.UTF8.GetString(packet.Data) 还原
///     if (packet.Type == 101)
///     {
///         Console.WriteLine(Encoding.UTF8.GetString(packet.Data));
///     }
///
///     // 可以 await 耗时操作(查库、写文件):处理器与 socket 读取已解耦,不会卡住收包
///     await Task.CompletedTask;
/// });
///
/// // ③ 建立连接:内部拿到网络流并启动后台接收循环(从这里开始才会真正收包)
/// await client.ConnectAsync();
///
/// // ④ 发包:构造 Packet(包头自动拼好)→ 抢写锁 → 写进网络流
/// //    Id / Type 由业务约定;Data 是负载。写锁保证多线程并发发也不会字节交错
/// await client.WriteAsync(new Packet(1, 100, Encoding.UTF8.GetBytes("hello")));
///
/// // ⑤ 关闭:取消接收循环 → 关流 → 等接收循环与处理队列退出 → 释放资源
/// //    幂等(重复调用安全);关闭后实例不可复用,要重连请新建
/// await client.CloseAsync();
/// </code>
///
/// <para><b>收包链路(每个包都走这条流水线):</b></para>
/// <code>
/// 网络流 → Packet.ReadAsync 先读满 16 字节包头 → 按包头里的 Length 读满负载 → 组装 Packet
///        → 交给 PacketDispatcher 入队(队列上限 256;写满会让读取等待 = 背压)
///        → 处理任务按【入队顺序】依次 await 每一个处理器
/// 结论:处理器慢只会堆积队列,不会卡住 socket 读取;队列也满时才反过来压住读取。
/// </code>
///
/// <para><b>发包链路:</b></para>
/// <code>
/// Packet.ToBytes()(拼 16 字节包头 + 负载)
///        → 抢写锁 SemaphoreSlim(保证同一条连接上的包不会字节交错)
///        → NetworkStream.WriteAsync
/// 注意:方法返回只代表"已写进流的缓冲区",不代表对端已经收到。
/// </code>
/// </summary>
public interface IClient
{
    /// <summary>
    /// 连接到服务端并开始接收数据。
    /// <para>作用:建立 TCP 连接 → 拿到网络流 → 启动后台接收循环(之后才会调用 <see cref="AddHandler"/> 注册的处理器)。</para>
    /// <para>连接失败会抛异常,由调用方决定重试策略;已关闭的实例调用会抛 <see cref="ObjectDisposedException"/>。</para>
    /// </summary>
    Task ConnectAsync();

    /// <summary>
    /// 发送一个包(发包入口)。
    /// <para>作用:把 <see cref="Packet"/> 序列化后写进网络流;内部有写锁,多线程并发调用不会把包写花。</para>
    /// <para>什么时候用:登录、心跳、上报数据、响应服务端下发的指令。</para>
    /// </summary>
    /// <param name="packet">要发送的包(Id 自己定,Type 按业务协议约定)</param>
    /// <param name="cancellationToken">取消令牌(取消时本次发送中断)</param>
    /// <exception cref="InvalidOperationException">还没有调用 <see cref="ConnectAsync"/> 时抛出</exception>
    Task WriteAsync(Packet packet, CancellationToken cancellationToken = default);

    /// <summary>
    /// 注册收包处理器(收包入口)。
    /// <para>作用:声明"收到包之后做什么";可注册多个,按注册顺序依次执行。单个处理器抛异常只写日志,不影响其它处理器和后续包。</para>
    /// <para>好处:处理器与 socket 读取解耦,里面 await 耗时操作不会卡住收包。</para>
    /// </summary>
    /// <param name="handler">收到包时执行的回调(参数就是解析好的包)</param>
    void AddHandler(Func<Packet, Task> handler);

    /// <summary>
    /// 关闭连接并清理资源。
    /// <para>作用:取消接收循环 → 关闭网络流 → 停止处理队列 → 释放 socket。</para>
    /// <para>幂等:重复调用安全;关闭后本实例不可复用。</para>
    /// </summary>
    Task CloseAsync();
}
