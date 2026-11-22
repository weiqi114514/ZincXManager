using ZincXManagerClient.Models;  // NavItem / Icons
using ZincXManagerShared.Modules;  // ModuleIds

namespace ZincXManagerClient.Modules;

// 左侧导航的 11 个分区。每个分区只声明「叫什么、配哪个图标、什么账号看得见」;
// 到底出不出现由 ModuleState 决定 —— 关掉的分区连同它下面的卡片一起消失。
//
// 顺序就是导航上的顺序:玩家端 6 个(经济 / 社区 / 组织 / 好友 / 小功能组 / 管理)+ 设置,
// 管理账号登录后换成管理端 4 个大类 + 设置。

/// <summary>经济分区(商店 / 银行)。</summary>
public sealed class EconomySection : SectionModule
{
    public EconomySection() : base(ModuleIds.NavEconomy) { }

    public override bool PlayerOnly => true;

    public override NavItem BuildNav() => new(Title, Icons.Economy) { ModuleId = Id };
}

/// <summary>社区分区(子服广场 / 锌源广场)。</summary>
public sealed class CommunitySection : SectionModule
{
    public CommunitySection() : base(ModuleIds.NavCommunity) { }

    public override bool PlayerOnly => true;

    public override NavItem BuildNav() => new(Title, Icons.Community) { ModuleId = Id };
}

/// <summary>组织分区(个人 / 团队 / 企业)。</summary>
public sealed class OrganizationSection : SectionModule
{
    public OrganizationSection() : base(ModuleIds.NavOrganization) { }

    public override bool PlayerOnly => true;

    public override NavItem BuildNav() => new(Title, Icons.Organization) { ModuleId = Id };
}

/// <summary>好友分区:不占内容区,点开的是独立的好友条窗体。</summary>
public sealed class FriendsSection : SectionModule
{
    public FriendsSection() : base(ModuleIds.NavFriends) { }

    public override bool PlayerOnly => true;

    // 图标画布比旧的小一号,宽高按 5 x 3 摆(与旧代码一致)
    public override NavItem BuildNav() => new(Title, Icons.Friends, 5, 3) { ModuleId = Id };
}

/// <summary>小功能组分区(搜索 + 两栏功能列表)。</summary>
public sealed class WidgetsSection : SectionModule
{
    public WidgetsSection() : base(ModuleIds.NavWidgets) { }

    public override bool PlayerOnly => true;

    public override NavItem BuildNav() => new(Title, Icons.Widgets) { ModuleId = Id };
}

/// <summary>管理分区(版本 / 数据源 / 服务器 / 统计)。</summary>
public sealed class ManageSection : SectionModule
{
    public ManageSection() : base(ModuleIds.NavManage) { }

    public override bool PlayerOnly => true;

    public override NavItem BuildNav() => new(Title, Icons.Manage, 5, 4) { ModuleId = Id };
}

/// <summary>设置分区:锁死模块,恒定显示(玩家端与管理端都留着,沉到列表最底下)。</summary>
public sealed class SettingsSection : SectionModule
{
    public SettingsSection() : base(ModuleIds.NavSettings) { }

    public override NavItem BuildNav() => new(Title, Icons.Settings) { ModuleId = Id };
}

/// <summary>管理端:服务器管理(MC 服本身的操作)。</summary>
public sealed class AdminServerSection : SectionModule
{
    public AdminServerSection() : base(ModuleIds.NavAdminServer) { }

    public override bool AdminOnly => true;

    public override NavItem BuildNav() => new(Title, Icons.AdminServer, iconStroke: true) { ModuleId = Id };
}

/// <summary>管理端:功能(跨系统对接与全服级操作)。</summary>
public sealed class AdminFunctionSection : SectionModule
{
    public AdminFunctionSection() : base(ModuleIds.NavAdminFunction) { }

    public override bool AdminOnly => true;

    public override NavItem BuildNav() => new(Title, Icons.AdminFunction, iconStroke: true) { ModuleId = Id };
}

/// <summary>管理端:ZXM 管理(账户 / 社区 / 组织 / 官方业务)。</summary>
public sealed class AdminZxmSection : SectionModule
{
    public AdminZxmSection() : base(ModuleIds.NavAdminZxm) { }

    public override bool AdminOnly => true;

    public override NavItem BuildNav() => new(Title, Icons.AdminZxm, iconStroke: true) { ModuleId = Id };
}

/// <summary>管理端:查询统计(记录类,只读为主)。</summary>
public sealed class AdminQuerySection : SectionModule
{
    public AdminQuerySection() : base(ModuleIds.NavAdminQuery) { }

    public override bool AdminOnly => true;

    public override NavItem BuildNav() => new(Title, Icons.AdminQuery, iconStroke: true) { ModuleId = Id };
}
