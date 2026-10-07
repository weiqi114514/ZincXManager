using System.Text;
using ZincXManagerShared.Logging;

namespace ZincXManagerShared.Network;

/// <summary>
/// 发包小封装:一行把文本 / 字节发出去,不用每次手写 <c>new Packet(...)</c> 和 UTF-8 编码。
///
/// <para>业务包(4xx 及以上,见 <see cref="Protocol.IsBusiness"/>)会自动追加一行审计到 clients.log;
/// 控制包(1xx/2xx/3xx)不记,免得日志被握手刷屏。</para>
///
/// <code>
/// await conn.SendAsync(Protocol.FriendMsg, "114514", role: "ZXMC");       // 服务端 → 某条连接
/// await packet.SendAsync(Protocol.FriendMsgAck, "ok", packet.Id);         // handler 里回包
/// await server.BroadcastAsync(Protocol.Notice, "维护公告");                // 群发
/// await client.SendAsync(Protocol.FriendMsg, "114514");                   // 客户端 → 服务端
/// </code>
/// </summary>
public static class SendExtensions
{
    // ---------- 服务端:某条连接 ----------

    /// <summary>发文本包(UTF-8)。</summary>
    /// <param name="conn">目标连接</param>
    /// <param name="type">消息类型(用 <see cref="Protocol"/> 里的常量)</param>
    /// <param name="text">文本负载</param>
    /// <param name="id">包序号;回包时沿用请求的 id,主动推送填 0</param>
    /// <param name="role">对端身份(ZXMS / ZXMC),只用于审计日志</param>
    public static async Task SendAsync(this TcpConnection conn, ushort type, string text, ulong id = 0, string role = "-")
    {
        await conn.WriteAsync(new Packet(id, type, Encoding.UTF8.GetBytes(text)));
        Audit(type, role, conn.RemoteEndPoint?.ToString() ?? "?", text);
    }

    /// <summary>发字节包(二进制负载;审计只记长度,不记内容)。</summary>
    public static async Task SendAsync(this TcpConnection conn, ushort type, byte[] data, ulong id = 0, string role = "-")
    {
        await conn.WriteAsync(new Packet(id, type, data));
        Audit(type, role, conn.RemoteEndPoint?.ToString() ?? "?", $"{data.Length} 字节");
    }

    // ---------- 服务端:handler 里回包 ----------

    /// <summary>回包给"发来这个包的那条连接"。</summary>
    public static Task SendAsync(this ServerboundPacket packet, ushort type, string text, ulong id = 0, string role = "-")
        => packet.Connection.SendAsync(type, text, id, role);

    // ---------- 服务端:群发 ----------

    /// <summary>向所有在线客户端群发文本包。</summary>
    public static Task BroadcastTextAsync(this TcpServer server, ushort type, string text, ulong id = 0)
        => server.BroadcastAsync(new Packet(id, type, Encoding.UTF8.GetBytes(text)));

    // ---------- 客户端 ----------

    /// <summary>客户端发文本包(UTF-8)。</summary>
    public static async Task SendAsync(this TcpClient client, ushort type, string text, ulong id = 0)
    {
        await client.WriteAsync(new Packet(id, type, Encoding.UTF8.GetBytes(text)));
        Audit(type, "ZXMC", "→服务端", text);
    }

    /// <summary>客户端发字节包。</summary>
    public static async Task SendAsync(this TcpClient client, ushort type, byte[] data, ulong id = 0)
    {
        await client.WriteAsync(new Packet(id, type, data));
        Audit(type, "ZXMC", "→服务端", $"{data.Length} 字节");
    }

    /// <summary>业务包才写审计,控制包跳过。</summary>
    private static void Audit(ushort type, string role, string address, string content)
    {
        if (Protocol.IsBusiness(type))
        {
            ClientLog.Data(role, address, $"type={type} 内容={content}");
        }
    }
}
