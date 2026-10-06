using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ZincXManagerShared.Network;

public class TcpServer : IServer
{
    private readonly IPAddress _ipAddress; // the ip address that will listen to
    private readonly ushort _port;
    private Task? _receiveTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Func<ServerboundPacket, Task>> _handlers = [];
    private readonly TcpListener _listener;
    
    public TcpServer(IPAddress ipAddress, ushort port)
    {
        _ipAddress = ipAddress;
        _port = port;
        _listener = new TcpListener(_ipAddress, port);
    }

    public void Open()
    {
        _listener.Start();
        _receiveTask = ReceiveLoopAsync();
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(System.Net.Sockets.TcpClient client)
    {
        await using var stream = client.GetStream();
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var header = new byte[16];
                await stream.ReadExactlyAsync(header, _cts.Token);
                var id = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0, 8));
                var type = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8, 2));
                var flags = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(10, 2));
                var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12, 4));
                var data = new byte[length];
                await stream.ReadExactlyAsync(data, _cts.Token);
                var packet = new ServerboundPacket(id, type, data, flags, stream);
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
        finally
        {
            client.Dispose();
        }
    }

    public void AddHandler(Func<ServerboundPacket, Task> handler)
    {
        _handlers.Add(handler);
    }

    public async Task CloseAsync()
    {
        _cts.Cancel();
        _listener.Stop();
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
        _cts.Dispose();
    }
}