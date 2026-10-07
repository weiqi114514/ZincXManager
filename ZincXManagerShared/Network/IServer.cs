namespace ZincXManagerShared.Network;

/// <summary>
/// 服务端接口(ZXMS / ZXMOS 监听客户端用),由 <see cref="TcpServer"/> 实现。
///
/// <code>
/// var server = new TcpServer(IPAddress.Any, 21000);
/// server.Connected += conn => { /* 有人接入 */ };
/// server.AddHandler(async packet => { /* 收包,回包用 packet.WriteAsync */ });
/// server.Open();                    // 开始监听(每个连接一条独立接收循环)
/// await server.BroadcastAsync(...); // 群发
/// await server.CloseAsync();        // 关闭(幂等)
/// </code>
/// </summary>
public interface IServer
{
    /// <summary>开始监听;立即返回,接受连接在后台进行,端口占用等错误直接抛异常。</summary>
    void Open();

    /// <summary>注册收包处理器(所有连接共用);需要回包时用参数里的 <see cref="ServerboundPacket"/>。</summary>
    void AddHandler(Func<ServerboundPacket, Task> handler);

    /// <summary>停止监听、关闭所有连接并释放资源;幂等。</summary>
    Task CloseAsync();

    /// <summary>当前在线连接(每次访问返回快照)。</summary>
    IReadOnlyCollection<TcpConnection> Connections { get; }

    /// <summary>客户端接入时触发。</summary>
    event Action<TcpConnection>? Connected;

    /// <summary>客户端断开时触发。</summary>
    event Action<TcpConnection>? Disconnected;

    /// <summary>向所有在线客户端广播一个包;单条失败只记日志。</summary>
    Task BroadcastAsync(Packet packet, CancellationToken cancellationToken = default);
}
