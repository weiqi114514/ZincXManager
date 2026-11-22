using System.Collections.Generic;
using ZincXManagerShared.Modules;  // ModuleCatalog / ModuleIds

namespace ZincXManagerServer.Modules;

/// <summary>
/// 白名单(as.whitelist):命令 wl 与 wl add / rm / on / off。
/// <para>关掉之后:命令被拒绝,进服时也不再按白名单挡人(白名单表本身不读)。</para>
/// </summary>
internal sealed class WhitelistModule : ServerModule
{
    public WhitelistModule() : base(ModuleCatalog.Find(ModuleIds.AdminWhitelist)!) { }

    /// <summary>归属本模块的命令。</summary>
    public override IReadOnlyList<string> Commands { get; } =
    [
        "wl",
        "wl add",
        "wl rm",
        "wl on",
        "wl off",
    ];
}
