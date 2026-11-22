using ZincXManagerClient.Models;  // FunctionItem / Icons
using ZincXManagerShared.Modules;  // ModuleIds

namespace ZincXManagerClient.Modules;

/// <summary>小功能组 → 绑定与解绑(ZXM 账户与 MC 玩家名)。</summary>
public sealed class BindCard : CardModule
{
    public BindCard() : base(ModuleIds.WidgetBind) { }

    public override string Section => ModuleIds.NavWidgets;

    public override FunctionItem BuildCard() => new()
    {
        Key = "bind",
        Title = Title,
        Description = "ZXM与MC账号的绑定",
        Icon = Icons.Bind,
        IconStroke = true,
        ModuleId = Id,
    };
}
