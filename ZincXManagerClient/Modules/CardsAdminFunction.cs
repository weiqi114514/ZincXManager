using ZincXManagerClient.Models;  // FunctionItem / Icons
using ZincXManagerShared.Modules;  // ModuleIds

namespace ZincXManagerClient.Modules;

// 管理端「功能」大类:跨系统对接与全服级操作。

/// <summary>功能 → MC 地图备份。</summary>
public sealed class MapBackupCard : CardModule
{
    public MapBackupCard() : base(ModuleIds.FuncMapBackup) { }

    public override string Section => ModuleIds.NavAdminFunction;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-mapbackup",
        Title = Title,
        Description = "备份 mc 服的地图",
        Icon = Icons.AdminMapBackup,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>功能 → 多服玩家互通。</summary>
public sealed class MultiSyncCard : CardModule
{
    public MultiSyncCard() : base(ModuleIds.FuncMultiSync) { }

    public override string Section => ModuleIds.NavAdminFunction;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-multisync",
        Title = Title,
        Description = "自动同步多个服的玩家数据",
        Icon = Icons.AdminMultiSync,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>功能 → 与 MC 服的连接设置。</summary>
public sealed class McServerCard : CardModule
{
    public McServerCard() : base(ModuleIds.FuncMcServer) { }

    public override string Section => ModuleIds.NavAdminFunction;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-mcserver",
        Title = Title,
        Description = "连哪个服、用什么端口",
        Icon = Icons.AdminMcServer,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>功能 → qqbot 连接。</summary>
public sealed class QqBotCard : CardModule
{
    public QqBotCard() : base(ModuleIds.FuncQqBot) { }

    public override string Section => ModuleIds.NavAdminFunction;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-qqbot",
        Title = Title,
        Description = "与 qqbot 框架对接",
        Icon = Icons.AdminQqBot,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>功能 → 统计。</summary>
public sealed class StatsCard : CardModule
{
    public StatsCard() : base(ModuleIds.FuncStats) { }

    public override string Section => ModuleIds.NavAdminFunction;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-stats",
        Title = Title,
        Description = "玩家人数、经济等",
        Icon = Icons.AdminStats,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>功能 → QQ 绑定。</summary>
public sealed class QqBindCard : CardModule
{
    public QqBindCard() : base(ModuleIds.FuncQqBind) { }

    public override string Section => ModuleIds.NavAdminFunction;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-qqbind",
        Title = Title,
        Description = "zxmc 账户与 qq 绑定",
        Icon = Icons.AdminQqBind,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>功能 → 联 ban。</summary>
public sealed class BanCard : CardModule
{
    public BanCard() : base(ModuleIds.FuncBan) { }

    public override string Section => ModuleIds.NavAdminFunction;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-ban",
        Title = Title,
        Description = "在 zxmc 里 ban 玩家,同时推送 zxmc 与 qq 群",
        Icon = Icons.AdminBan,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>功能 → 投票发布。</summary>
public sealed class VoteCard : CardModule
{
    public VoteCard() : base(ModuleIds.FuncVote) { }

    public override string Section => ModuleIds.NavAdminFunction;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-vote",
        Title = Title,
        Description = "小区域(特定人)/ 全区(整个 zxms)",
        Icon = Icons.AdminVote,
        IconStroke = true,
        ModuleId = Id,
    };
}
