using ZincXManagerShared.Modules;  // ModuleCatalog / ModuleIds

namespace ZincXManagerServer.Modules;

/// <summary>
/// 多服数据分离(datasep):进服 / 绑定 / 经济这些数据按 MC 服分目录存。
/// <para>装配时做两步:读开关与键表,再按上次留下的快照迁一次。
/// 关掉之后这两步都不做 —— 一个目录都不碰,所有数据按"全部共享"处理。</para>
/// </summary>
internal sealed class DataSeparationModule : ServerModule
{
    public DataSeparationModule() : base(ModuleCatalog.Find(ModuleIds.DataSeparation)!) { }

    /// <summary>装配:读开关与键表,再按快照迁移一次。</summary>
    /// <param name="context">装配时给的上下文(这里用配置句柄读两行配置)。</param>
    public override void OnLoad(ModuleContext context) => Program.InitDataSeparation(context.Config);
}
