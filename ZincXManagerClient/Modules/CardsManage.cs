using ZincXManagerClient.Models;  // FunctionItem / Icons
using ZincXManagerShared.Modules;  // ModuleIds

namespace ZincXManagerClient.Modules;

/// <summary>管理 → 版本管理。</summary>
public sealed class VersionManageCard : CardModule
{
    public VersionManageCard() : base(ModuleIds.ManageVersion) { }

    public override string Section => ModuleIds.NavManage;

    public override FunctionItem BuildCard() => new()
    {
        Key = "mg-version",
        Title = Title,
        Description = "版本、mod、材质包",
        Icon = Icons.VersionManage,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>管理 → 数据源管理。</summary>
public sealed class SourceManageCard : CardModule
{
    public SourceManageCard() : base(ModuleIds.ManageSource) { }

    public override string Section => ModuleIds.NavManage;

    public override FunctionItem BuildCard() => new()
    {
        Key = "mg-source",
        Title = Title,
        Description = "数据源 切换/管理/删除",
        Icon = Icons.SourceManage,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>管理 → 服务器信息(点开是数据源下的服务器选择)。</summary>
public sealed class ServerManageCard : CardModule
{
    public ServerManageCard() : base(ModuleIds.ManageServer) { }

    public override string Section => ModuleIds.NavManage;

    public override FunctionItem BuildCard() => new()
    {
        Key = "mg-server",
        Title = Title,
        Description = "数据源下的服务器管理/使用/选择",
        Icon = Icons.ServerManage,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>管理 → 数据统计。</summary>
public sealed class StatsManageCard : CardModule
{
    public StatsManageCard() : base(ModuleIds.ManageStats) { }

    public override string Section => ModuleIds.NavManage;

    public override FunctionItem BuildCard() => new()
    {
        Key = "mg-stats",
        Title = Title,
        Description = "各类数据统计",
        Icon = Icons.StatsManage,
        IconStroke = true,
        ModuleId = Id,
    };
}
