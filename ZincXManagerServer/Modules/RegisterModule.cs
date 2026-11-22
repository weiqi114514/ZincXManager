using System.Collections.Generic;
using System.Threading.Tasks;
using ZincXManagerShared.Modules;  // ModuleCatalog / ModuleIds
using ZincXManagerShared.Network;  // Protocol / ServerboundPacket

namespace ZincXManagerServer.Modules;

/// <summary>
/// 账号注册(az.register):客户端发来的注册请求(6002),回 6003。
/// <para>处理体在 Program.ModuleHandlers.cs(从核心分发表里原样搬出来的);命令 user reg 也归它。</para>
/// <para>这个模块最适合半加载:关掉或不装配时客户端注册不了,但服主还能在控制台用
/// <c>user reg</c> 手动开账号 —— 白名单服常见这么用。</para>
/// </summary>
internal sealed class RegisterModule : ServerModule
{
    public RegisterModule() : base(ModuleCatalog.Find(ModuleIds.AdminZxmRegister)!) { }

    /// <summary>注册模块认领的包(603 是回执,服务端发,不用接管)。</summary>
    public override IReadOnlyList<ushort> Packets { get; } =
    [
        Protocol.AccountRegister,   // 602 客户端提交注册
    ];

    /// <summary>归属本模块的命令。</summary>
    public override IReadOnlyList<string> Commands { get; } = ["user reg"];

    /// <summary>转到注册处理体。</summary>
    /// <param name="packet">发来的包。</param>
    /// <param name="context">装配时给的上下文。</param>
    public override Task OnPacketAsync(ServerboundPacket packet, ModuleContext context)
        => Program.HandleAccountRegisterAsync(packet);
}
