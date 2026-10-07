namespace ZincXManagerShared.Network;

/// <summary>
/// 客户端接口(ZXMC 连 ZXMS / ZXMOS 用),由 <see cref="TcpClient"/> 实现。
///
/// <code>
/// var client = new TcpClient(IPAddress.Parse("127.0.0.1"), 21000);
/// client.AddHandler(p => { /* 收到包 */ return Task.CompletedTask; });   // 收
/// await client.ConnectAsync();                                          // 连
/// await client.WriteAsync(new Packet(1, 300, "ZXMC online"u8.ToArray()));// 发
/// await client.CloseAsync();                                            // 关(幂等)
/// </code>
/// </summary>
public interface IClient
{
    /// <summary>连接服务端并启动后台接收循环;失败抛异常,由调用方决定重试。</summary>
    Task ConnectAsync();

    /// <summary>发一个包(内部有写锁,可并发调用);未连接时抛 <see cref="InvalidOperationException"/>。</summary>
    Task WriteAsync(Packet packet, CancellationToken cancellationToken = default);

    /// <summary>注册收包处理器;可多个,按顺序执行,与 socket 读取解耦(里面可以做耗时操作)。</summary>
    void AddHandler(Func<Packet, Task> handler);

    /// <summary>关闭连接并释放资源;幂等,关闭后实例不可复用。</summary>
    Task CloseAsync();
}
