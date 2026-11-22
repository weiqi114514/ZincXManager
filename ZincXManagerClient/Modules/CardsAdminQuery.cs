using ZincXManagerClient.Models;  // FunctionItem / Icons
using ZincXManagerShared.Modules;  // ModuleIds

namespace ZincXManagerClient.Modules;

// 管理端「查询统计」大类:记录类,只读为主。

/// <summary>查询统计 → 开发区机器位置校验。</summary>
public sealed class ZoneCheckCard : CardModule
{
    public ZoneCheckCard() : base(ModuleIds.QueryZoneCheck) { }

    public override string Section => ModuleIds.NavAdminQuery;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-zonecheck",
        Title = Title,
        Description = "自动检查登记的坐标是否真有机器",
        Icon = Icons.AdminZoneCheck,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>查询统计 → 玩家出入服务器记录。</summary>
public sealed class JoinLogCard : CardModule
{
    public JoinLogCard() : base(ModuleIds.QueryJoinLog) { }

    public override string Section => ModuleIds.NavAdminQuery;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-joinlog",
        Title = Title,
        Description = "自动记录玩家进出",
        Icon = Icons.AdminJoinLog,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>查询统计 → 群聊天记录。</summary>
public sealed class GroupLogCard : CardModule
{
    public GroupLogCard() : base(ModuleIds.QueryGroupLog) { }

    public override string Section => ModuleIds.NavAdminQuery;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-grouplog",
        Title = Title,
        Description = "记录群聊天",
        Icon = Icons.AdminGroupLog,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>查询统计 → 服聊天记录。</summary>
public sealed class ServerLogCard : CardModule
{
    public ServerLogCard() : base(ModuleIds.QueryServerLog) { }

    public override string Section => ModuleIds.NavAdminQuery;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-serverlog",
        Title = Title,
        Description = "记录服内聊天",
        Icon = Icons.AdminServerLog,
        IconStroke = true,
        ModuleId = Id,
    };
}
