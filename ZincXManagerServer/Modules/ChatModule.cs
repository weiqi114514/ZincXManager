using System.Collections.Generic;
using System.Threading.Tasks;
using ZincXManagerShared.Logging;  // Logger
using ZincXManagerShared.Modules;  // ModuleCatalog / ModuleIds
using ZincXManagerShared.Network;  // Protocol / ServerboundPacket

namespace ZincXManagerServer.Modules;

/// <summary>
/// 好友聊天后端(chat,父模块 nav.friends):装配时拉起 BareChat 宿主进程,关闭时把它收掉。
/// <para>以前是 Main 里无条件 StartChatHost(靠 config 的 chatenabled 开关判断),
/// 现在由这个模块的装配与否决定 —— 模块不在,聊天宿主进程就不会被启动。</para>
/// </summary>
internal sealed class ChatModule : ServerModule
{
    public ChatModule() : base(ModuleCatalog.Find(ModuleIds.Chat)!) { }

    /// <summary>认领的包:510 是服务端下发的聊天握手,客户端发来的话没有可做的。</summary>
    public override IReadOnlyList<ushort> Packets { get; } = [Protocol.ChatHello];

    /// <summary>装配:拉起聊天后端。</summary>
    /// <param name="context">装配时给的上下文(这里用配置句柄找端口与密钥)。</param>
    public override void OnLoad(ModuleContext context) => Program.StartChatHost(context.Config);

    /// <summary>卸载:把聊天宿主进程收掉,别留孤儿进程。</summary>
    public override void OnUnload() => Program.StopChatHost();

    /// <summary>510 只由服务端下发:客户端发来就记一条调试日志。</summary>
    /// <param name="packet">发来的包。</param>
    /// <param name="context">装配时给的上下文。</param>
    public override Task OnPacketAsync(ServerboundPacket packet, ModuleContext context)
    {
        Logger.Log(LogLevel.Debug, $"[模块] 收到只有服务端会发的聊天包 type={packet.Type},已忽略");
        return Task.CompletedTask;
    }
}
