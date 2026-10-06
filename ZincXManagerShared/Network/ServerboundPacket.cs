using System.Net.Sockets;

namespace ZincXManagerShared.Network;

// The packet that bound to server, server need to handle it
public class ServerboundPacket : Packet
{
    private readonly NetworkStream _stream;
    
    public ServerboundPacket(ulong id, ushort type, byte[] data, NetworkStream stream) : base(id, type, data)
    {
        _stream = stream;
    }

    public ServerboundPacket(ulong id, ushort type, byte[] data, ushort flags, NetworkStream stream) : base(id, type, data, flags)
    {
        _stream = stream;
    }

    public async Task WriteAsync(Packet packet)
    {
        await _stream.WriteAsync(packet.ToBytes());
    }
}