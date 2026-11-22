using System.Collections.Generic;
using System.Threading.Tasks;
using ZincXManagerShared.Modules;  // ModuleCatalog / ModuleIds
using ZincXManagerShared.Network;  // Protocol / ServerboundPacket

namespace ZincXManagerServer.Modules;

/// <summary>
/// 远程控制台(console):执行命令 / 订阅输出 / 列服务器,以及 net 与 zxmm 两组命令。
/// <para>关掉之后:这三类包不会再送到任何服务器(连权限都不查),net / zxmm 命令被拒绝。</para>
/// </summary>
internal sealed class ConsoleModule : ServerModule
{
    public ConsoleModule() : base(ModuleCatalog.Find(ModuleIds.Console)!) { }

    /// <summary>控制台模块认领的包。</summary>
    public override IReadOnlyList<ushort> Packets { get; } =
    [
        Protocol.ConsoleCommand,      // 680 执行一条命令
        Protocol.ConsoleTap,          // 683 开 / 关持续输出
        Protocol.ConsoleServersReq,   // 684 列可控制的服务器
    ];

    /// <summary>归属本模块的命令。</summary>
    public override IReadOnlyList<string> Commands { get; } =
    [
        "net status",
        "net clients",
        "net reconnect",
        "net close",
        "zxmm config",
        "zxmm connect",
        "zxmm status",
    ];

    /// <summary>按包类型分派。</summary>
    /// <param name="packet">客户端发来的包。</param>
    /// <param name="context">装配时给的上下文。</param>
    public override Task OnPacketAsync(ServerboundPacket packet, ModuleContext context) => packet.Type switch
    {
        Protocol.ConsoleCommand => Program.HandleConsoleCommandAsync(packet),
        Protocol.ConsoleTap => Program.HandleConsoleTapAsync(packet),
        Protocol.ConsoleServersReq => Program.HandleConsoleServersReqAsync(packet),
        _ => Task.CompletedTask,
    };
}
