using System.Collections.Generic;
using System.Threading.Tasks;
using ZincXManagerShared.Modules;  // ModuleCatalog / ModuleIds
using ZincXManagerShared.Network;  // Protocol / ServerboundPacket

namespace ZincXManagerServer.Modules;

/// <summary>
/// 多服服务器列表(serverlist):客户端要列表 → 回 6071;客户端切服 → 记住并回 6073。
/// <para>关掉之后客户端拿不到列表、也切不了服,包按规则回 err。</para>
/// </summary>
internal sealed class ServerListModule : ServerModule
{
    public ServerListModule() : base(ModuleCatalog.Find(ModuleIds.ServerList)!) { }

    /// <summary>认领的包。</summary>
    public override IReadOnlyList<ushort> Packets { get; } =
    [
        Protocol.ServerListReq,     // 670 要列表
        Protocol.ServerSwitchReq,   // 672 切服务器
    ];

    /// <summary>按包类型分派。</summary>
    /// <param name="packet">客户端发来的包。</param>
    /// <param name="context">装配时给的上下文。</param>
    public override Task OnPacketAsync(ServerboundPacket packet, ModuleContext context) => packet.Type switch
    {
        Protocol.ServerListReq => Program.HandleServerListReqAsync(packet),
        Protocol.ServerSwitchReq => Program.HandleServerSwitchReqAsync(packet),
        _ => Task.CompletedTask,
    };
}
