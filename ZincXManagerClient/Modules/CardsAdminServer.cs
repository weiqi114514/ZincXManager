using ZincXManagerClient.Models;  // FunctionItem / Icons
using ZincXManagerShared.Modules;  // ModuleIds

namespace ZincXManagerClient.Modules;

// 管理端「服务器管理」大类:MC 服本身的操作。

/// <summary>服务器管理 → RCON / ZXMM 桥接控制台。</summary>
public sealed class RconCard : CardModule
{
    public RconCard() : base(ModuleIds.AdminRcon) { }

    public override string Section => ModuleIds.NavAdminServer;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-rcon",
        Title = Title,
        Description = "与 mc 服务器连接的远程控制台",
        Icon = Icons.AdminRcon,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>服务器管理 → 监听服务器。</summary>
public sealed class ListenCard : CardModule
{
    public ListenCard() : base(ModuleIds.AdminListen) { }

    public override string Section => ModuleIds.NavAdminServer;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-listen",
        Title = Title,
        Description = "监听 mc 服的输出",
        Icon = Icons.AdminListen,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>服务器管理 → 文件管理。</summary>
public sealed class FileCard : CardModule
{
    public FileCard() : base(ModuleIds.AdminFile) { }

    public override string Section => ModuleIds.NavAdminServer;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-file",
        Title = Title,
        Description = "管理 mc 服或 ZXMS 所在机器的文件",
        Icon = Icons.AdminFile,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>服务器管理 → mod / 插件管理。</summary>
public sealed class PluginCard : CardModule
{
    public PluginCard() : base(ModuleIds.AdminPlugin) { }

    public override string Section => ModuleIds.NavAdminServer;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-plugin",
        Title = Title,
        Description = "管理 mc 服的插件 / mod",
        Icon = Icons.AdminPlugin,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>服务器管理 → 地图管理。</summary>
public sealed class MapCard : CardModule
{
    public MapCard() : base(ModuleIds.AdminMap) { }

    public override string Section => ModuleIds.NavAdminServer;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-map",
        Title = Title,
        Description = "管理 mc 服的地图",
        Icon = Icons.AdminMap,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>服务器管理 → 地图备份切换。</summary>
public sealed class MapSwitchCard : CardModule
{
    public MapSwitchCard() : base(ModuleIds.AdminMapSwitch) { }

    public override string Section => ModuleIds.NavAdminServer;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-mapswitch",
        Title = Title,
        Description = "快速切换 mc 服地图的备份",
        Icon = Icons.AdminMapSwitch,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>服务器管理 → 玩家数据管理。</summary>
public sealed class PlayerDataCard : CardModule
{
    public PlayerDataCard() : base(ModuleIds.AdminPlayerData) { }

    public override string Section => ModuleIds.NavAdminServer;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-playerdata",
        Title = Title,
        Description = "直接修改玩家的数据文件",
        Icon = Icons.AdminPlayerData,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>服务器管理 → op 管理。</summary>
public sealed class OpCard : CardModule
{
    public OpCard() : base(ModuleIds.AdminOp) { }

    public override string Section => ModuleIds.NavAdminServer;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-op",
        Title = Title,
        Description = "设置 / 取消 op",
        Icon = Icons.AdminOp,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>服务器管理 → 玩家背包管理。</summary>
public sealed class InventoryCard : CardModule
{
    public InventoryCard() : base(ModuleIds.AdminInventory) { }

    public override string Section => ModuleIds.NavAdminServer;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-inventory",
        Title = Title,
        Description = "查看、管理玩家背包",
        Icon = Icons.AdminInventory,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>服务器管理 → 白名单。</summary>
public sealed class WhitelistCard : CardModule
{
    public WhitelistCard() : base(ModuleIds.AdminWhitelist) { }

    public override string Section => ModuleIds.NavAdminServer;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-whitelist",
        Title = Title,
        Description = "mc 服的白名单管理",
        Icon = Icons.AdminWhitelist,
        IconStroke = true,
        ModuleId = Id,
    };
}
