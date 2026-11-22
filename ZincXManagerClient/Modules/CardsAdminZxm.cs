using ZincXManagerClient.Models;  // FunctionItem / Icons
using ZincXManagerShared.Modules;  // ModuleIds

namespace ZincXManagerClient.Modules;

// 管理端「ZXM 管理」大类:ZXM 自己那套账户 / 社区 / 组织 / 官方业务。

/// <summary>ZXM 管理 → 账户管理。</summary>
public sealed class AccountCard : CardModule
{
    public AccountCard() : base(ModuleIds.AdminZxmAccount) { }

    public override string Section => ModuleIds.NavAdminZxm;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-account",
        Title = Title,
        Description = "管理 zxm 的账户",
        Icon = Icons.AdminAccount,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>ZXM 管理 → 帖子管理。</summary>
public sealed class PostCard : CardModule
{
    public PostCard() : base(ModuleIds.AdminZxmPost) { }

    public override string Section => ModuleIds.NavAdminZxm;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-post",
        Title = Title,
        Description = "社区帖子",
        Icon = Icons.AdminPost,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>ZXM 管理 → 资源管理。</summary>
public sealed class ResourceCard : CardModule
{
    public ResourceCard() : base(ModuleIds.AdminZxmResource) { }

    public override string Section => ModuleIds.NavAdminZxm;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-resource",
        Title = Title,
        Description = "社区资源库",
        Icon = Icons.AdminResource,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>ZXM 管理 → 聊天室管理。</summary>
public sealed class ChatroomCard : CardModule
{
    public ChatroomCard() : base(ModuleIds.AdminZxmChatroom) { }

    public override string Section => ModuleIds.NavAdminZxm;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-chatroom",
        Title = Title,
        Description = "社区聊天室与房间",
        Icon = Icons.AdminChatRoom,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>ZXM 管理 → 企业组织管理。</summary>
public sealed class CompanyCard : CardModule
{
    public CompanyCard() : base(ModuleIds.AdminZxmCompany) { }

    public override string Section => ModuleIds.NavAdminZxm;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-company",
        Title = Title,
        Description = "组织里的企业",
        Icon = Icons.AdminCompany,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>ZXM 管理 → 个人组织管理。</summary>
public sealed class PersonalCard : CardModule
{
    public PersonalCard() : base(ModuleIds.AdminZxmPersonal) { }

    public override string Section => ModuleIds.NavAdminZxm;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-personal",
        Title = Title,
        Description = "组织里的个人",
        Icon = Icons.AdminPersonal,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>ZXM 管理 → 团队组织管理。</summary>
public sealed class TeamCard : CardModule
{
    public TeamCard() : base(ModuleIds.AdminZxmTeam) { }

    public override string Section => ModuleIds.NavAdminZxm;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-team",
        Title = Title,
        Description = "组织里的团队",
        Icon = Icons.AdminTeam,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>ZXM 管理 → 官方商店。</summary>
public sealed class ShopCard : CardModule
{
    public ShopCard() : base(ModuleIds.AdminZxmShop) { }

    public override string Section => ModuleIds.NavAdminZxm;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-shop",
        Title = Title,
        Description = "管理官方商店、设置上架",
        Icon = Icons.AdminShop,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>ZXM 管理 → 官方贷款。</summary>
public sealed class LoanCard : CardModule
{
    public LoanCard() : base(ModuleIds.AdminZxmLoan) { }

    public override string Section => ModuleIds.NavAdminZxm;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-loan",
        Title = Title,
        Description = "官方贷款业务",
        Icon = Icons.AdminLoan,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>ZXM 管理 → 官方债券。</summary>
public sealed class BondCard : CardModule
{
    public BondCard() : base(ModuleIds.AdminZxmBond) { }

    public override string Section => ModuleIds.NavAdminZxm;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-bond",
        Title = Title,
        Description = "官方债券业务",
        Icon = Icons.AdminBond,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>ZXM 管理 → 开发区 / 机器 / 基地管理。</summary>
public sealed class ZoneCard : CardModule
{
    public ZoneCard() : base(ModuleIds.AdminZxmZone) { }

    public override string Section => ModuleIds.NavAdminZxm;

    public override FunctionItem BuildCard() => new()
    {
        Key = "ad-zone",
        Title = Title,
        Description = "登记信息与后续校验",
        Icon = Icons.AdminZone,
        IconStroke = true,
        ModuleId = Id,
    };
}
