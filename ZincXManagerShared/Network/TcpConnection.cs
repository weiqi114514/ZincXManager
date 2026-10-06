using System.Net;
using System.Net.Sockets;

namespace ZincXManagerShared.Network;

/// <summary>
/// 服务端侧的一条客户端连接:包住 socket、网络流与写锁。
///
/// <para><b>为什么要单独抽这个类(而不是直接用 NetworkStream):</b></para>
/// <code>
/// ① 回包要对准人:handler 拿到的 ServerboundPacket 绑着 Connection,回包只会写给这条连接
/// ② 并发要安全:同一条连接可能被多个任务同时写(回包 + 广播 + 主动推送),
///    这里用一个写锁把写入串行化,避免两个包字节交错变成废包
/// ③ 连接要能识别:Id 可以做会话表的键,RemoteEndPoint 知道是谁连上来的
/// </code>
///
/// <para><b>发包用法(每段都标了作用):</b></para>
/// <code>
/// // ① 回包场景:handler 里直接写回来源连接
/// await packet.WriteAsync(new Packet(packet.Id, 101, Encoding.UTF8.GetBytes("ok")));
///
/// // ② 主动推送场景:从在线表里挑一条连接发通知
/// var conn = server.Connections.FirstOrDefault(c => c.Id == someId);
/// if (conn is not null)
/// {
///     await conn.WriteAsync(new Packet(0, 998, Encoding.UTF8.GetBytes("你的申请通过了")));
/// }
/// </code>
///
/// <para><b>WriteAsync 每步在做什么:</b>检查连接是否已关闭 → 抢写锁(等别人写完)→
/// <c>Packet.ToBytes()</c> 打包 → 写进 <see cref="Stream"/> → 释放写锁。</para>
/// </summary>
public sealed class TcpConnection : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpClient _client;

    /// <summary>写锁:保证同一条连接的并发写不会交错(初始可用数 1)。</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>0 = 未关闭,1 = 已关闭;用 <see cref="Interlocked.Exchange"/> 保证只释放一次。</summary>
    private int _disposed;

    internal TcpConnection(System.Net.Sockets.TcpClient client)
    {
        _client = client;
        Id = Guid.NewGuid();
        Stream = client.GetStream();
        RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
    }

    /// <summary>连接唯一标识(用来在 <see cref="TcpServer.Connections"/> 里定位某条连接)。</summary>
    public Guid Id { get; }

    /// <summary>对端地址;某些情况下取不到时为 null。</summary>
    public IPEndPoint? RemoteEndPoint { get; }

    /// <summary>该连接的网络流(接收循环从这个流里读包)。</summary>
    public NetworkStream Stream { get; }

    /// <summary>是否已关闭(关闭后不可再写)。</summary>
    public bool IsClosed => _disposed != 0;

    /// <summary>
    /// 向本连接写一个包(发包入口)。
    /// <para>作用:回包给客户端,或主动推送消息;内部有写锁,多个任务并发调用不会把包写花。</para>
    /// </summary>
    /// <param name="packet">要发送的包</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="ObjectDisposedException">连接已关闭</exception>
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

    /// <summary>
    /// 关闭连接并释放资源(断开时由服务端自动调用,一般不用手写)。
    /// <para>作用:关流 → 释放 socket;幂等(第二次直接返回)。</para>
    /// <para>注意:<c>SemaphoreSlim</c> 故意不 Dispose —— 若还有任务在等写锁,
    /// Dispose 会让它们抛 <see cref="ObjectDisposedException"/> 而不是正常结束。</para>
    /// </summary>
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
            // 关闭时的异常忽略(对端可能已经先断了)
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
