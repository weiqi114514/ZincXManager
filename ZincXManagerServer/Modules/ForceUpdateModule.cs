using System.Collections.Generic;
using System.Threading.Tasks;
using ZincXManagerShared.Logging;  // Logger
using ZincXManagerShared.Modules;  // ModuleCatalog / ModuleIds
using ZincXManagerShared.Network;  // Protocol / ServerboundPacket

namespace ZincXManagerServer.Modules;

/// <summary>
/// 客户端强制更新(forceupdate):旧版本客户端拒绝登录 / 在线旧版踢下线。
/// <para>模块关着时 config 里的 forceupdate=1 不再生效(见 Program.ForceUpdateOn),
/// 634 也不会发出去 —— 现在连"客户端发 634"这种事都不再理。</para>
/// </summary>
internal sealed class ForceUpdateModule : ServerModule
{
    public ForceUpdateModule() : base(ModuleCatalog.Find(ModuleIds.ForceUpdate)!) { }

    /// <summary>认领的包:634 只由服务端下发。</summary>
    public override IReadOnlyList<ushort> Packets { get; } = [Protocol.ClientTooOld];

    /// <summary>装配时把当前的生效状态打出来,服主看日志就知道拦不拦版本。</summary>
    /// <param name="context">装配时给的上下文。</param>
    public override void OnLoad(ModuleContext context)
        => Logger.Log(LogLevel.Info, "[强制更新] 模块已装配:按 config 的 forceupdate / minversion 拦旧版客户端");

    /// <summary>634 只由服务端下发:客户端发来就记一条调试日志。</summary>
    /// <param name="packet">发来的包。</param>
    /// <param name="context">装配时给的上下文。</param>
    public override Task OnPacketAsync(ServerboundPacket packet, ModuleContext context)
    {
        Logger.Log(LogLevel.Debug, $"[模块] 收到只有服务端会发的版本包 type={packet.Type},已忽略");
        return Task.CompletedTask;
    }
}
