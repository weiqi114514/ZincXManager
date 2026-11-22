using System.Collections.Generic;
using System.Threading.Tasks;
using ZincXManagerShared.Modules;  // ModuleCatalog / ModuleIds
using ZincXManagerShared.Network;  // Protocol / ServerboundPacket

namespace ZincXManagerServer.Modules;

/// <summary>
/// 账号绑定(wd.bind):ZXMM 转来的绑定码、客户端提交 / 确认 / 解绑 / 查询。
/// <para>处理体在 Program.ModuleHandlers.cs(从原来那个大 switch 里原样搬出来的);
/// 命令 bind / bind list / unbind 的归属也记在这儿,模块关着时会被命令处理器直接拒绝。</para>
/// </summary>
internal sealed class BindModule : ServerModule
{
    public BindModule() : base(ModuleCatalog.Find(ModuleIds.WidgetBind)!) { }

    /// <summary>绑定模块认领的包。</summary>
    public override IReadOnlyList<ushort> Packets { get; } =
    [
        Protocol.BindNotice,    // 625 ZXMM 把玩家手里的绑定码转过来
        Protocol.BindApply,     // 626 客户端提交绑定码
        Protocol.BindAck,       // 627 回执(客户端不该发,收到就当无效包忽略)
        Protocol.BindQuery,     // 629 查绑定关系
        Protocol.Unbind,        // 628 解绑
        Protocol.BindConfirm,   // 631 账号主人二次确认
    ];

    /// <summary>归属本模块的命令(用于日志与"关掉就不可用"的判断)。</summary>
    public override IReadOnlyList<string> Commands { get; } = ["bind", "bind list", "unbind"];

    /// <summary>按包类型转给对应处理。</summary>
    /// <param name="packet">发来的包。</param>
    /// <param name="context">装配时给的上下文。</param>
    public override Task OnPacketAsync(ServerboundPacket packet, ModuleContext context) => packet.Type switch
    {
        Protocol.BindNotice => Program.HandleBindNoticeAsync(packet),
        Protocol.BindApply => Program.HandleBindApplyAsync(packet),
        Protocol.BindConfirm => Program.HandleBindConfirmAsync(packet),
        Protocol.Unbind => Program.HandleUnbindAsync(packet),
        Protocol.BindQuery => Program.HandleBindQueryAsync(packet),
        _ => Task.CompletedTask,
    };
}
