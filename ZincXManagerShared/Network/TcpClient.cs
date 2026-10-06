using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ZincXManagerShared.Network;

public class TcpClient : IClient
{
    private readonly IPAddress _ipAddress;
    private readonly ushort _port;
    private readonly System.Net.Sockets.TcpClient _client;
    private NetworkStream? _stream;
    private Task? _receiveTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Func<Packet, Task>> _handlers = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    
    public TcpClient(IPAddress ipAddress, ushort port)
    {
        _ipAddress = ipAddress;
        _port = port;
        _client = new System.Net.Sockets.TcpClient();
    }

    public async Task ConnectAsync()
    {
        await _client.ConnectAsync(_ipAddress, _port);
        _stream = _client.GetStream();
        _receiveTask = ReceiveLoopAsync();
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var header = new byte[16];
                await _stream.ReadExactlyAsync(header);
                var id = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0, 8));
                var type = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8, 2));
                var flags = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(10, 2));
                var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12, 4));
                var data = new byte[length];
                await _stream.ReadExactlyAsync(data);
                var packet = new Packet(id, type, data, flags);
                foreach (var handler in _handlers)
                {
                    await handler.Invoke(packet);
                }
            }
        }
        catch (OperationCanceledException)
        {

        }
        catch (EndOfStreamException)
        {
            
        }
    }

    public void AddHandler(Func<Packet, Task> handler)
    {
        _handlers.Add(handler);
    }

    public async Task CloseAsync()
    {
        _cts.Cancel();
        if (_stream is not null)
        {
            await _stream.DisposeAsync();
        }
        if (_receiveTask is not null)
        {
            // Wait for the receiving task to be canceled
            try
            {
                await _receiveTask;
            }
            catch (IOException)
            {
                
            }
        }
        _client.Dispose();
        _cts.Dispose();
        _writeLock.Dispose();
    }

    public async Task WriteAsync(Packet packet, CancellationToken cancellationToken = default)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Client is not connected");
        }
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(packet.ToBytes(), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}