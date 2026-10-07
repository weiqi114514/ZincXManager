using System.Net;
using System.Net.Sockets;

namespace ZincXManagerShared.Network;

/// <summary>
/// 服务端侧的一条客户端连接:socket + 网络流 + 写锁。
///
/// 为什么要它:① 回包要对准人(handler 拿到的包绑着连接);② 同一条连接可能被多个任务同时写
/// (回包 + 广播 + 主动推送),这里的写锁保证包不会被写花;③ 用 Id / RemoteEndPoint 区分是谁。
/// </summary>
public sealed class TcpConnection : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpClient _client;

    /// <summary>写锁:同一条连接的并发写串行化。</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>0 = 未关闭,1 = 已关闭(保证只释放一次)。</summary>
    private int _disposed;

    internal TcpConnection(System.Net.Sockets.TcpClient client)
    {
        _client = client;
        Id = Guid.NewGuid();
        Stream = client.GetStream();
        RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
    }

    /// <summary>连接唯一标识(在 <see cref="TcpServer.Connections"/> 里定位连接)。</summary>
    public Guid Id { get; }

    /// <summary>对端地址,取不到时为 null。</summary>
    public IPEndPoint? RemoteEndPoint { get; }

    /// <summary>该连接的网络流(接收循环从这里读包)。</summary>
    public NetworkStream Stream { get; }

    /// <summary>是否已关闭。</summary>
    public bool IsClosed => _disposed != 0;

    /// <summary>往本连接写一个包;线程安全,关闭后抛 <see cref="ObjectDisposedException"/>。</summary>
    public async Task WriteAsync(Packet packet, CancellationToken cancellationToken = default)
    {
        if (_disposed != 0)
        {
            throw new ObjectDisposedException(nameof(TcpConnection));
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await Stream.WriteAsync(packet.ToBytes(), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>关闭连接并释放;幂等。注意:写锁故意不 Dispose —— 还有任务在等锁时 Dispose 会改成抛异常。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await Stream.DisposeAsync();
        }
        catch
        {
            // 对端可能已经先断了,忽略
        }

        try
        {
            _client.Dispose();
        }
        catch
        {
            // 同上
        }
    }
}
