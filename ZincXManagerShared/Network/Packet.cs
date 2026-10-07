using System.Buffers.Binary;

namespace ZincXManagerShared.Network;

/// <summary>
/// 网络包:16 字节包头(大端)+ 负载。
///
/// <code>
///  偏移 长度 字段    作用
///   0    8   Id     消息 id,回包时原样带回,用来配对请求与响应
///   8    2   Type   消息类型,由业务约定(如 100/101 握手、200 身份包、300/301 客户端上线)
///  10    2   Flags  标志位,留给压缩/加密
///  12    4   Length 负载长度
///  16    N   Data   负载
/// </code>
///
/// 发送用 <see cref="ToBytes"/>,接收用 <see cref="ReadAsync"/>(客户端与服务端的接收循环共用,解析只有这一份)。
/// </summary>
public class Packet
{
    /// <summary>包头固定长度。</summary>
    public const int HeaderSize = 16;

    /// <summary>负载上限(8 MB),超过就当非法包断开连接,防止对端报个超大长度把内存吃光。</summary>
    public const int MaxPayloadLength = 8 * 1024 * 1024;

    private readonly ulong _id;      // 64-bit message id
    private readonly ushort _type;   // 16-bit message type
    private readonly ushort _flags;  // 16-bit message flags, default = 0
    private readonly uint _length;   // 32-bit data length
    private readonly byte[] _data;   // n-bit data (n = _length)

    /// <summary>消息 id;回包时原样带回,用于配对请求与响应。</summary>
    public ulong Id => _id;

    /// <summary>消息类型,handler 里靠它分派。</summary>
    public ushort Type => _type;

    /// <summary>标志位,默认 0。</summary>
    public ushort Flags => _flags;

    /// <summary>负载长度(等于 <see cref="Data"/>.Length)。</summary>
    public uint Length => _length;

    /// <summary>负载数据;没有负载时是空数组,不会是 null。</summary>
    public byte[] Data => _data;

    /// <summary>构造一个不带标志位的包。</summary>
    public Packet(ulong id, ushort type, byte[] data)
    {
        _id = id;
        _type = type;
        _flags = 0;
        _length = (uint) data.Length;
        _data = data;
    }

    /// <summary>构造一个带标志位的包。</summary>
    public Packet(ulong id, ushort type, byte[] data, ushort flags)
    {
        _id = id;
        _type = type;
        _flags = flags;
        _length = (uint) data.Length;
        _data = data;
    }

    /// <summary>打包:拼成"16 字节包头 + 负载"的字节数组,直接写进网络流。</summary>
    public byte[] ToBytes()
    {
        var combined = new byte[HeaderSize + _length];
        BinaryPrimitives.WriteUInt64BigEndian(combined.AsSpan(0, 8), _id);
        BinaryPrimitives.WriteUInt16BigEndian(combined.AsSpan(8, 2), _type);
        BinaryPrimitives.WriteUInt16BigEndian(combined.AsSpan(10, 2), _flags);
        BinaryPrimitives.WriteUInt32BigEndian(combined.AsSpan(12, 4), _length);
        _data.AsSpan().CopyTo(combined.AsSpan(HeaderSize));
        return combined;
    }

    /// <summary>
    /// 解包:先读满 16 字节包头,再按 Length 读满负载。
    /// 用 <c>ReadExactlyAsync</c> 攒够才返回,所以网络分片到达也没关系。
    /// </summary>
    /// <exception cref="EndOfStreamException">对端关闭(还没读满就到流末尾)</exception>
    /// <exception cref="InvalidDataException">负载长度超过 <see cref="MaxPayloadLength"/></exception>
    public static async Task<Packet> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, cancellationToken);

        var id = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0, 8));
        var type = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8, 2));
        var flags = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(10, 2));
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12, 4));

        if (length > MaxPayloadLength)
        {
            throw new InvalidDataException($"包负载长度非法:{length} 字节(上限 {MaxPayloadLength} 字节)");
        }

        var data = new byte[length];
        await stream.ReadExactlyAsync(data, cancellationToken);
        return new Packet(id, type, data, flags);
    }
}
