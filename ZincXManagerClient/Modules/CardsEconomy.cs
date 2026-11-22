using ZincXManagerClient.Models;  // FunctionItem / Icons
using ZincXManagerShared.Modules;  // ModuleIds

namespace ZincXManagerClient.Modules;

// 经济分区下的卡片:商店 / 银行。注意文件里不写「显不显示」——
// 三个条件(连接方式 / 是否连官方 / 模块开关)统一由 ModuleState 与 ModuleRegistry 算。

/// <summary>经济 → 商店。</summary>
public sealed class StoreCard : CardModule
{
    public StoreCard() : base(ModuleIds.EcoStore) { }

    public override string Section => ModuleIds.NavEconomy;

    public override FunctionItem BuildCard() => new()
    {
        Key = "eco-store",
        Title = Title,          // 中文名照 ModuleCatalog,与分享的清单一致
        Description = "官方与玩家的交易市场",
        Icon = Icons.Store,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>经济 → 银行。</summary>
public sealed class BankCard : CardModule
{
    public BankCard() : base(ModuleIds.EcoBank) { }

    public override string Section => ModuleIds.NavEconomy;

    public override FunctionItem BuildCard() => new()
    {
        Key = "eco-bank",
        Title = Title,
        Description = "存钱、转账、借贷",
        Icon = Icons.Bank,
        IconStroke = true,
        ModuleId = Id,
    };
}
