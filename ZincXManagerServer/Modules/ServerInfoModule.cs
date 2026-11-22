using System.Collections.Generic;
using ZincXManagerShared.Modules;  // ModuleCatalog / ModuleIds

namespace ZincXManagerServer.Modules;

/// <summary>
/// 本服信息展示(mg.server):命令 srv show(版本 / 在线数 / 端口这类汇总)。
/// <para>关掉只隐藏这一个展示命令,不影响服务端本职的对外应答。</para>
/// </summary>
internal sealed class ServerInfoModule : ServerModule
{
    public ServerInfoModule() : base(ModuleCatalog.Find(ModuleIds.ManageServer)!) { }

    /// <summary>归属本模块的命令。</summary>
    public override IReadOnlyList<string> Commands { get; } = ["srv show"];
}
