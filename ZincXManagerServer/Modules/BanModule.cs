using System.Collections.Generic;
using ZincXManagerShared.Modules;  // ModuleCatalog / ModuleIds

namespace ZincXManagerServer.Modules;

/// <summary>
/// 封禁管理(af.ban):命令 ban add / del / list / purge / user。
/// <para>命令实现在 Command\ZxmsCommands.cs 里,归属记在这儿 —— 模块关着时命令处理器
/// 会先问 <see cref="ModuleRegistry.CommandBlocked"/>,当场拒绝,不会执行到一半再说没权限。</para>
/// </summary>
internal sealed class BanModule : ServerModule
{
    public BanModule() : base(ModuleCatalog.Find(ModuleIds.FuncBan)!) { }

    /// <summary>归属本模块的命令。</summary>
    public override IReadOnlyList<string> Commands { get; } =
    [
        "ban add",
        "ban del",
        "ban list",
        "ban purge",
        "ban user",
    ];
}
