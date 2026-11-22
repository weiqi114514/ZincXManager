using System.Collections.Generic;
using System.Threading.Tasks;
using ZincXManagerShared.Logging;  // Logger
using ZincXManagerShared.Modules;  // ModuleCatalog / ModuleIds
using ZincXManagerShared.Network;  // Protocol / ServerboundPacket

namespace ZincXManagerServer.Modules;

/// <summary>
/// 进服登录确认(loginconfirm):弹窗问账号主人,等客户端回 6022。
/// <para>这个模块关着时,进服流程里不再有"等确认"这一环(见 Program 里进服判断处的分支),
/// 玩家直接放行并记一条免确认;6020/6022 也不会再送到账号那边。</para>
/// </summary>
internal sealed class LoginConfirmModule : ServerModule
{
    public LoginConfirmModule() : base(ModuleCatalog.Find(ModuleIds.LoginConfirm)!) { }

    /// <summary>认领的包。</summary>
    public override IReadOnlyList<ushort> Packets { get; } =
    [
        Protocol.LoginRequest,   // 620 老的确认请求(已改走 ZXMM 事件,收到就当无效包)
        Protocol.LoginAnswer,    // 622 账号主人点了同意 / 拒绝
    ];

    /// <summary>按包类型分派。</summary>
    /// <param name="packet">客户端发来的包。</param>
    /// <param name="context">装配时给的上下文。</param>
    public override Task OnPacketAsync(ServerboundPacket packet, ModuleContext context) => packet.Type switch
    {
        Protocol.LoginAnswer => Program.HandleLoginAnswerAsync(packet),
        _ => Skip(packet),
    };

    /// <summary>620 已经不用了(ZXMM 直接推事件过来):收到就记一条调试日志。</summary>
    /// <param name="packet">发来的包。</param>
    private static Task Skip(ServerboundPacket packet)
    {
        Logger.Log(LogLevel.Debug, $"[模块] 收到已废弃的登录请求包 type={packet.Type},已忽略");
        return Task.CompletedTask;
    }
}
