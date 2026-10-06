namespace ZincXManagerShared.Network;

public interface IClient
{
    Task ConnectAsync();
    Task WriteAsync(Packet packet, CancellationToken cancellationToken = default);
    void AddHandler(Func<Packet, Task> handler);
    Task CloseAsync();
}