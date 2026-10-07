namespace ZincXManagerShared.Network;

/// <summary>
/// 服务端收到的包:比 <see cref="Packet"/> 多带一条 <see cref="TcpConnection"/>,
/// 好处是 handler 里能直接回包给"发来这个包的那个客户端",也能知道是谁发的。
///
/// <code>
/// server.AddHandler(async packet =>
/// {
///     if (packet.Type == 100)
///         await packet.WriteAsync(new Packet(packet.Id, 101, Encoding.UTF8.GetBytes("ok")));
/// });
/// </code>
/// </summary>
public class ServerboundPacket : Packet
{
    private readonly TcpConnection _connection;

    /// <summary>由"已解析好的包 + 所属连接"构造(服务端接收循环用的就是这个)。</summary>
    public ServerboundPacket(Packet packet, TcpConnection connection)
        : base(packet.Id, packet.Type, packet.Data, packet.Flags)
    {
        _connection = connection;
    }

    /// <summary>构造一个不带标志位的包。</summary>
    public ServerboundPacket(ulong id, ushort type, byte[] data, TcpConnection connection) : base(id, type, data)
    {
        _connection = connection;
    }

    /// <summary>构造一个带标志位的包。</summary>
    public ServerboundPacket(ulong id, ushort type, byte[] data, ushort flags, TcpConnection connection) : base(id, type, data, flags)
    {
        _connection = connection;
    }

    /// <summary>该包来自哪条连接(可读对端地址 / 连接 id,也可用来主动推送)。</summary>
    public TcpConnection Connection => _connection;

    /// <summary>把包回给本连接(内部走连接的写锁,线程安全)。</summary>
    public Task WriteAsync(Packet packet, CancellationToken cancellationToken = default)
        => _connection.WriteAsync(packet, cancellationToken);
}
