using System.Buffers.Binary;

namespace ZincXManagerShared.Network;

/// <summary>
/// 网络包 —— 客户端与服务端共用的最小传输单元(一次消息)。
///
/// <para><b>线上格式:16 字节包头 + N 字节负载,所有整数一律大端序(Big Endian)。</b></para>
/// <code>
///  偏移  长度  字段     作用
///   0     8    Id      包序号 / 消息 id —— 回包时原样带回,用来把"响应"配到"请求"上
///   8     2    Type    消息类型 —— 业务自己约定(例如 100=登录、200=聊天),决定怎么解释负载
///  10     2    Flags   标志位 —— 默认 0,留给以后做压缩、加密、分片
///  12     4    Length  负载长度 —— 等于 Data.Length,接收方靠它知道还要读多少字节
///  16     N    Data    负载字节 —— 业务数据;文本用 Encoding.UTF8 转
/// </code>
///
/// <para><b>发包(自己构造 → 交给 WriteAsync):</b></para>
/// <code>
/// // ① 构造:只需给 Id / Type / Data,包头(Flags、Length)会在内部自动算好
/// var packet = new Packet(1, 100, Encoding.UTF8.GetBytes("hello"));
///
/// // ② 序列化:ToBytes() 把包头按大端序写在前 16 字节,再把负载拼在后面
/// //    (如果哪天要换协议,只改 ToBytes / ReadAsync 这两个地方)
/// var bytes = packet.ToBytes();
///
/// // ③ 发送:交给客户端/服务端(内部写锁 + 写流)
/// await client.WriteAsync(packet);
/// </code>
///
/// <para><b>收包(不用自己解析,网络层调 ReadAsync 后把包回调给你):</b></para>
/// <code>
/// client.AddHandler(p =>
/// {
///     // ④ 拿到的已经是完整包:按 Type 分派,按 Data 取内容
///     if (p.Type == 100)
///     {
///         Console.WriteLine(Encoding.UTF8.GetString(p.Data));
///     }
///     return Task.CompletedTask;
/// });
/// </code>
///
/// <para><b>ReadAsync 每步在做什么:</b>先 ReadExactlyAsync 读满 16 字节包头 →
/// 解析 Id/Type/Flags/Length → 校验 Length 不超过 <see cref="MaxPayloadLength"/> →
/// 再 ReadExactlyAsync 读满 Length 字节负载 → 组装成 <see cref="Packet"/> 返回。
/// 收发两侧的接收循环都用它,解析逻辑只有这一份。</para>
/// </summary>
public class Packet
{
    /// <summary>包头固定长度(字节)。</summary>
    public const int HeaderSize = 16;

    /// <summary>
    /// 单个包负载的上限(8 MB)。超过这个长度视为非法包并抛 <see cref="InvalidDataException"/>,
    /// 避免对端用一个假的超大 Length 把服务端内存吃光。
    /// </summary>
    public const int MaxPayloadLength = 8 * 1024 * 1024;

    private readonly ulong _id;      // 64-bit message id
    private readonly ushort _type;   // 16-bit message type
    private readonly ushort _flags;  // 16-bit message flags, default = 0
    private readonly uint _length;   // 32-bit data length
    private readonly byte[] _data;   // n-bit data (n = _length)

    /// <summary>包序号 / 消息 id;回包时原样带回,用于配对请求与响应。</summary>
    public ulong Id => _id;

    /// <summary>消息类型;由业务自行约定(例如 100 = 登录、200 = 聊天),handler 里靠它分派。</summary>
    public ushort Type => _type;

    /// <summary>标志位,默认 0;留给压缩、加密、分片之类的扩展。</summary>
    public ushort Flags => _flags;

    /// <summary>负载长度(与 <see cref="Data"/>.Length 相同),也是写进包头的那个值。</summary>
    public uint Length => _length;

    /// <summary>负载数据;没有负载时是空数组,不会是 null。</summary>
    public byte[] Data => _data;

    /// <summary>构造一个不带标志位的包(flags 取默认值 0)。</summary>
    /// <param name="id">包序号 / 消息 id</param>
    /// <param name="type">消息类型</param>
    /// <param name="data">负载数据(长度会自动写进包头)</param>
    public Packet(ulong id, ushort type, byte[] data)
    {
        _id = id;
        _type = type;
        _flags = 0;
        _length = (uint) data.Length;
        _data = data;
    }

    /// <summary>构造一个带标志位的包(需要额外标记,例如"这条是压缩过的")。</summary>
    /// <param name="id">包序号 / 消息 id</param>
    /// <param name="type">消息类型</param>
    /// <param name="data">负载数据</param>
    /// <param name="flags">标志位</param>
    public Packet(ulong id, ushort type, byte[] data, ushort flags)
    {
        _id = id;
        _type = type;
        _flags = flags;
        _length = (uint) data.Length;
        _data = data;
    }

    /// <summary>
    /// 打包:按线上格式序列化成"16 字节包头 + 负载"的字节数组。
    /// <para>作用:发送前的最后一步,产出可直接写进 <c>NetworkStream</c> 的字节。</para>
    /// </summary>
    /// <returns>长度为 <c>16 + Data.Length</c> 的字节数组</returns>
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
    /// 解包:从流里读出一个完整包(先读满 16 字节包头,再按 Length 读满负载)。
    /// <para>作用:接收循环的核心,客户端与服务端共用,保证两边解析规则完全一致。</para>
    /// <para>它会一直等到"读满"才返回,所以网络分片到达也没关系(<c>ReadExactlyAsync</c> 会自己攒够)。</para>
    /// </summary>
    /// <param name="stream">网络流</param>
    /// <param name="cancellationToken">取消令牌(关闭连接时会取消)</param>
    /// <returns>解析好的包</returns>
    /// <exception cref="EndOfStreamException">连接被对端关闭(还没读满就到流末尾)</exception>
    /// <exception cref="InvalidDataException">包头里的负载长度超过 <see cref="MaxPayloadLength"/></exception>
    /// <exception cref="OperationCanceledException">被取消(通常是主动关闭连接)</exception>
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
