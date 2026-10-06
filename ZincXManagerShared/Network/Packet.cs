using System.Buffers.Binary;

namespace ZincXManagerShared.Network;

public class Packet
{
    private readonly ulong _id; // 64-bit message id
    private ushort _type; // 16-bit message type
    private ushort _flags; // 16-bit message flags, default = 0
    private uint _length; // 32-bit data length
    private byte[] _data; // n-bit data (n = _length)
    
    public Packet(ulong id, ushort type, byte[] data)
    {
        _id = id;
        _type = type;
        _flags = 0;
        _length = (uint) data.Length;
        _data = data;
    }

    public Packet(ulong id, ushort type, byte[] data, ushort flags)
    {
        _id = id;
        _type = type;
        _flags = flags;
        _length = (uint) data.Length;
        _data = data;
    }

    public byte[] ToBytes()
    {
        var combined = new byte[16 + _length];
        BinaryPrimitives.WriteUInt64BigEndian(combined.AsSpan(0, 8), _id);
        BinaryPrimitives.WriteUInt16BigEndian(combined.AsSpan(8, 2), _type);
        BinaryPrimitives.WriteUInt16BigEndian(combined.AsSpan(10, 2), _flags);
        BinaryPrimitives.WriteUInt32BigEndian(combined.AsSpan(12, 4), _length);
        _data.AsSpan().CopyTo(combined.AsSpan(16));
        return combined;
    }
}