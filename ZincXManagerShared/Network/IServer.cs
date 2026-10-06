namespace ZincXManagerShared.Network;

public interface IServer
{
    void Open();
    void AddHandler(Func<ServerboundPacket, Task> handler);
    Task CloseAsync();
}