namespace ZincXManagerShared.Network;

/// <summary>
/// 服务端接口(ZXMS / ZXMOS 监听客户端连接时使用)。由 <see cref="TcpServer"/> 实现。
///
/// <para><b>完整用法(每段都标了它的作用):</b></para>
/// <code>
/// // ① 建实例:只记下监听地址与端口,此时【还没有】开始监听
/// //    IPAddress.Any = 监听本机所有网卡;端口被占用要到 Open() 才会报错
/// var server = new TcpServer(IPAddress.Any, 25565);
///
/// // ② 订阅连接事件:每个客户端对应一个 TcpConnection(带 id、对端地址、写锁)
/// //    作用:维护会话表、上线发欢迎语、下线清理状态
/// server.Connected    += conn => Console.WriteLine("上线 " + conn.RemoteEndPoint + " id=" + conn.Id);
/// server.Disconnected += conn => Console.WriteLine("下线 " + conn.RemoteEndPoint + " id=" + conn.Id);
///
/// // ③ 注册收包处理器:所有连接的包都会进这里,可以注册多个(按注册顺序执行)
/// server.AddHandler(async packet =>
/// {
///     // packet.Connection          这个包来自哪条连接(能拿对端地址、连接 id,也能主动推送)
///     // packet.Id/Type/Flags/Data  与客户端侧一致,含义由业务约定
///     if (packet.Type == 100)
///     {
///         // ④ 回包:只回给"发来这个包的那条连接",其它客户端收不到
///         //    内部走该连接的写锁,并发回包不会字节交错
///         await packet.WriteAsync(new Packet(packet.Id, 101, Encoding.UTF8.GetBytes("ok")));
///     }
/// });
///
/// // ⑤ 开始监听:立即返回,接受连接在后台循环里进行(端口占用会抛 SocketException)
/// server.Open();
///
/// // ⑥ 主动下发(不是被动回包):
/// //    · 广播给所有在线客户端
/// await server.BroadcastAsync(new Packet(0, 999, Encoding.UTF8.GetBytes("维护公告")));
/// //    · 或只推给某一条(例如通知某玩家"白名单审核通过")
/// foreach (var conn in server.Connections)
/// {
///     await conn.WriteAsync(new Packet(0, 998, Encoding.UTF8.GetBytes("hello")));
/// }
///
/// // ⑦ 关闭:取消令牌 → 停止监听 → 等所有连接循环退出 → 关闭所有连接 → 释放资源(幂等)
/// await server.CloseAsync();
/// </code>
///
/// <para><b>服务端的收包链路:</b></para>
/// <code>
/// 每个客户端一条 TcpConnection + 一条独立接收循环(互不阻塞)
///        → Packet.ReadAsync 解析出完整包(包长超过上限则断开该连接)
///        → 包成 ServerboundPacket(绑上连接,便于回包)入队
///        → 处理任务按顺序执行所有处理器
/// </code>
/// </summary>
public interface IServer
{
    /// <summary>
    /// 开始监听端口。
    /// <para>作用:绑定端口并启动"接受连接"的后台循环;每来一个客户端就建立一条连接与一条独立接收循环。</para>
    /// <para>立即返回;端口被占用等错误会直接抛异常。</para>
    /// </summary>
    void Open();

    /// <summary>
    /// 注册收包处理器(服务端收包入口)。
    /// <para>作用:声明"收到任意客户端的包之后做什么";需要回包时用参数里的 <see cref="ServerboundPacket"/>。</para>
    /// <para>所有连接共用同一份处理器列表,按注册顺序依次执行;某个处理器抛异常只写日志,不影响其它包。</para>
    /// </summary>
    /// <param name="handler">收到包时执行的回调(包上带着来源连接)</param>
    void AddHandler(Func<ServerboundPacket, Task> handler);

    /// <summary>
    /// 停止服务并释放资源。
    /// <para>作用:取消所有循环、停止监听、关闭所有连接、停止处理队列。</para>
    /// <para>幂等:重复调用安全。</para>
    /// </summary>
    Task CloseAsync();

    /// <summary>当前所有在线连接(每次访问返回一份快照,可安全遍历;用于"按 id 找连接并推送")。</summary>
    IReadOnlyCollection<TcpConnection> Connections { get; }

    /// <summary>客户端接入时触发;参数是刚建立的连接(可用于记录在线表 / 发欢迎语)。</summary>
    event Action<TcpConnection>? Connected;

    /// <summary>客户端断开时触发;参数是已关闭的连接(可用于清理会话状态)。</summary>
    event Action<TcpConnection>? Disconnected;

    /// <summary>
    /// 向所有在线客户端广播一个包(主动下发,不是回包)。
    /// <para>单条连接写失败只记日志,不影响其它连接。</para>
    /// </summary>
    /// <param name="packet">要广播的包</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task BroadcastAsync(Packet packet, CancellationToken cancellationToken = default);
}
