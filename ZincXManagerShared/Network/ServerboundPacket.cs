namespace ZincXManagerShared.Network;

/// <summary>
/// 服务端收到的包 —— 在 <see cref="Packet"/> 基础上绑定"哪条连接发来的",
/// 目的是让服务端的 handler 能**直接回包给发来请求的那个客户端**。
///
/// <para><b>用法(每段都标了作用):</b></para>
/// <code>
/// server.AddHandler(async packet =>
/// {
///     // ① 判断业务类型(和客户端约定好的)
///     if (packet.Type == 100)
///     {
///         // ② 回包:WriteAsync 只写给"发来这个包的那条连接",其它客户端收不到
///         //    内部走该连接的写锁 —— 多个 handler/后台任务同时回包也不会字节交错
///         await packet.WriteAsync(new Packet(packet.Id, 101, Encoding.UTF8.GetBytes("ok")));
///
///         // ③ 需要识别/记录来源时用 Connection:对端地址、连接 id 都在上面
///         Console.WriteLine("来自 " + packet.Connection.RemoteEndPoint + " 连接 " + packet.Connection.Id);
///     }
/// });
/// </code>
///
/// <para><b>和 <see cref="Packet"/> 的区别:</b>只是多带一个 <see cref="TcpConnection"/>;
/// 从网络层出来时由服务端接收循环包好,业务侧负责构造的包用 <see cref="Packet"/> 就行。</para>
/// </summary>
public class ServerboundPacket : Packet
{
    /// <summary>该包所属的连接(含网络流与写锁)。</summary>
    private readonly TcpConnection _connection;

    /// <summary>由已解析好的包 + 所属连接构造(服务端接收循环用的就是这个)。</summary>
    /// <param name="packet">接收循环解析出来的包</param>
    /// <param name="connection">该包所属的连接</param>
    public ServerboundPacket(Packet packet, TcpConnection connection)
        : base(packet.Id, packet.Type, packet.Data, packet.Flags)
    {
        _connection = connection;
    }

    /// <summary>构造一个不带标志位的包(flags 取默认值 0)。</summary>
    /// <param name="id">包序号 / 消息 id</param>
    /// <param name="type">消息类型</param>
    /// <param name="data">负载数据</param>
    /// <param name="connection">该包所属的连接</param>
    public ServerboundPacket(ulong id, ushort type, byte[] data, TcpConnection connection) : base(id, type, data)
    {
        _connection = connection;
    }

    /// <summary>构造一个带标志位的包。</summary>
    /// <param name="id">包序号 / 消息 id</param>
    /// <param name="type">消息类型</param>
    /// <param name="data">负载数据</param>
    /// <param name="flags">标志位</param>
    /// <param name="connection">该包所属的连接</param>
    public ServerboundPacket(ulong id, ushort type, byte[] data, ushort flags, TcpConnection connection) : base(id, type, data, flags)
    {
        _connection = connection;
    }

    /// <summary>该包来自哪条连接(可读对端地址 / 连接 id,也可用来主动推送)。</summary>
    public TcpConnection Connection => _connection;

    /// <summary>
    /// 把指定包写回本包所属的连接(回包入口)。
    /// <para>作用:在 handler 里直接回复发起请求的客户端;线程安全(连接级写锁)。</para>
    /// </summary>
    /// <param name="packet">要回给客户端的包(通常沿用请求的 Id,便于对方配对)</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task WriteAsync(Packet packet, CancellationToken cancellationToken = default)
        => _connection.WriteAsync(packet, cancellationToken);
}
