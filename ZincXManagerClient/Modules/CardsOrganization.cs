using ZincXManagerClient.Models;  // FunctionItem / Icons
using ZincXManagerShared.Modules;  // ModuleIds

namespace ZincXManagerClient.Modules;

/// <summary>组织 → 个人组织。</summary>
public sealed class PersonalOrgCard : CardModule
{
    public PersonalOrgCard() : base(ModuleIds.OrgPersonal) { }

    public override string Section => ModuleIds.NavOrganization;

    public override FunctionItem BuildCard() => new()
    {
        Key = "org-personal",
        Title = Title,
        Description = "个人的组织形式 开发区/基地/机器",
        Icon = Icons.PersonalOrg,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>组织 → 团队组织。</summary>
public sealed class TeamOrgCard : CardModule
{
    public TeamOrgCard() : base(ModuleIds.OrgTeam) { }

    public override string Section => ModuleIds.NavOrganization;

    public override FunctionItem BuildCard() => new()
    {
        Key = "org-team",
        Title = Title,
        Description = "多人的团队组织形式 开发区/基地/机器",
        Icon = Icons.TeamOrg,
        IconStroke = true,
        ModuleId = Id,
    };
}

/// <summary>组织 → 企业组织。</summary>
public sealed class CompanyOrgCard : CardModule
{
    public CompanyOrgCard() : base(ModuleIds.OrgCompany) { }

    public override string Section => ModuleIds.NavOrganization;

    public override FunctionItem BuildCard() => new()
    {
        Key = "org-company",
        Title = Title,
        Description = "大规模的多人组织形式",
        Icon = Icons.CompanyOrg,
        IconStroke = true,
        ModuleId = Id,
    };
}
