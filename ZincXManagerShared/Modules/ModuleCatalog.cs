using System;  // StringComparer
using System.Collections.Generic;  // IReadOnlyList / List

namespace ZincXManagerShared.Modules;

/// <summary>
/// 一个模块的纯静态声明：只有数据，没有任何行为。
/// 服务端与客户端都读它来做开关判断，所以它必须是两端一致的一份。
/// </summary>
/// <param name="Id">模块编号，见 <see cref="ModuleIds"/>。</param>
/// <param name="Title">中文名，给控制台清单和客户端提示用。</param>
/// <param name="Parent">父模块编号；父关则子一起关，顶层模块留空。</param>
/// <param name="DefaultOn">首次启动没有旧配置时是否默认开启。</param>
/// <param name="Locked">锁死模块：写进禁用名单也会被剔除。</param>
/// <param name="LegacyKey">旧开关的配置键名，首次启动时用它迁移出初始名单。</param>
public sealed record ModuleDef(
    string Id,
    string Title,
    string Parent = "",
    bool DefaultOn = true,
    bool Locked = false,
    string LegacyKey = "");

/// <summary>
/// 模块全集：63 个模块的编号、中文名、父子关系与默认开关。
/// 服务端只给其中一部分写了模块类（后台功能 + 需要接管包的功能），
/// 其余是纯客户端入口；但开关名单是同一份，两端都按它判断。
/// </summary>
public static class ModuleCatalog
{
    /// <summary>全部模块，顺序即控制台清单的显示顺序。</summary>
    public static readonly IReadOnlyList<ModuleDef> All =
    [
        // ---------- 导航分区 ----------
        new(ModuleIds.NavEconomy, "经济"),
        new(ModuleIds.NavCommunity, "社区"),
        new(ModuleIds.NavOrganization, "组织"),
        new(ModuleIds.NavFriends, "好友"),
        new(ModuleIds.NavWidgets, "小功能组"),
        new(ModuleIds.NavManage, "管理"),
        new(ModuleIds.NavSettings, "设置", Locked: true),
        new(ModuleIds.NavAdminServer, "服务器管理"),
        new(ModuleIds.NavAdminFunction, "功能"),
        new(ModuleIds.NavAdminZxm, "ZXM 管理"),
        new(ModuleIds.NavAdminQuery, "查询统计"),

        // ---------- 经济 ----------
        new(ModuleIds.EcoStore, "商店", ModuleIds.NavEconomy),
        new(ModuleIds.EcoBank, "银行", ModuleIds.NavEconomy),

        // ---------- 社区 ----------
        new(ModuleIds.ComSrvSquare, "服务器广场", ModuleIds.NavCommunity),
        new(ModuleIds.ComZincSquare, "锌源广场", ModuleIds.NavCommunity),

        // ---------- 组织 ----------
        new(ModuleIds.OrgPersonal, "个人组织", ModuleIds.NavOrganization),
        new(ModuleIds.OrgTeam, "团队组织", ModuleIds.NavOrganization),
        new(ModuleIds.OrgCompany, "企业组织", ModuleIds.NavOrganization),

        // ---------- 小功能组 ----------
        new(ModuleIds.WidgetBind, "绑定 / 解绑", ModuleIds.NavWidgets),

        // ---------- 管理 ----------
        new(ModuleIds.ManageVersion, "版本管理", ModuleIds.NavManage),
        new(ModuleIds.ManageSource, "数据源管理", ModuleIds.NavManage),
        new(ModuleIds.ManageServer, "服务器管理", ModuleIds.NavManage),
        new(ModuleIds.ManageStats, "数据统计", ModuleIds.NavManage),

        // ---------- 服务器管理 ----------
        new(ModuleIds.AdminRcon, "RCON/ZXMM桥接控制台", ModuleIds.NavAdminServer),
        new(ModuleIds.AdminListen, "监听服务器", ModuleIds.NavAdminServer),
        new(ModuleIds.AdminFile, "文件管理", ModuleIds.NavAdminServer),
        new(ModuleIds.AdminPlugin, "mod/插件管理", ModuleIds.NavAdminServer),
        new(ModuleIds.AdminMap, "地图管理", ModuleIds.NavAdminServer),
        new(ModuleIds.AdminMapSwitch, "地图备份切换", ModuleIds.NavAdminServer),
        new(ModuleIds.AdminPlayerData, "玩家数据管理", ModuleIds.NavAdminServer),
        new(ModuleIds.AdminOp, "op 管理", ModuleIds.NavAdminServer),
        new(ModuleIds.AdminInventory, "玩家背包管理", ModuleIds.NavAdminServer),
        new(ModuleIds.AdminWhitelist, "白名单", ModuleIds.NavAdminServer),

        // ---------- 功能 ----------
        new(ModuleIds.FuncMapBackup, "mc 地图备份", ModuleIds.NavAdminFunction),
        new(ModuleIds.FuncMultiSync, "多服数据互通", ModuleIds.NavAdminFunction),
        new(ModuleIds.FuncMcServer, "与 mc 服的连接设置", ModuleIds.NavAdminFunction),
        new(ModuleIds.FuncQqBot, "qqbot 连接", ModuleIds.NavAdminFunction),
        new(ModuleIds.FuncStats, "统计", ModuleIds.NavAdminFunction),
        new(ModuleIds.FuncQqBind, "qq 绑定", ModuleIds.NavAdminFunction),
        new(ModuleIds.FuncBan, "联 ban", ModuleIds.NavAdminFunction),
        new(ModuleIds.FuncVote, "投票发布", ModuleIds.NavAdminFunction),

        // ---------- ZXM 管理 ----------
        new(ModuleIds.AdminZxmAccount, "账户管理", ModuleIds.NavAdminZxm),
        new(ModuleIds.AdminZxmRegister, "账号注册", ModuleIds.NavAdminZxm),
        new(ModuleIds.AdminZxmPost, "帖子管理", ModuleIds.NavAdminZxm),
        new(ModuleIds.AdminZxmResource, "资源管理", ModuleIds.NavAdminZxm),
        new(ModuleIds.AdminZxmChatroom, "聊天室管理", ModuleIds.NavAdminZxm),
        new(ModuleIds.AdminZxmCompany, "企业管理", ModuleIds.NavAdminZxm),
        new(ModuleIds.AdminZxmPersonal, "个人管理", ModuleIds.NavAdminZxm),
        new(ModuleIds.AdminZxmTeam, "团队管理", ModuleIds.NavAdminZxm),
        new(ModuleIds.AdminZxmShop, "官方商店", ModuleIds.NavAdminZxm),
        new(ModuleIds.AdminZxmLoan, "官方贷款", ModuleIds.NavAdminZxm),
        new(ModuleIds.AdminZxmBond, "官方债券", ModuleIds.NavAdminZxm),
        new(ModuleIds.AdminZxmZone, "开发区/机器/基地管理", ModuleIds.NavAdminZxm),

        // ---------- 查询统计 ----------
        new(ModuleIds.QueryZoneCheck, "开发区/机器位置校验", ModuleIds.NavAdminQuery),
        new(ModuleIds.QueryJoinLog, "玩家出入服务器记录", ModuleIds.NavAdminQuery),
        new(ModuleIds.QueryGroupLog, "群聊天记录", ModuleIds.NavAdminQuery),
        new(ModuleIds.QueryServerLog, "服聊天记录", ModuleIds.NavAdminQuery),

        // ---------- 后台功能 ----------
        new(ModuleIds.Chat, "好友聊天后端", ModuleIds.NavFriends, LegacyKey: "chatenabled"),
        new(ModuleIds.ForceUpdate, "强制更新", DefaultOn: false, LegacyKey: "forceupdate"),
        new(ModuleIds.DataSeparation, "多服数据分离", DefaultOn: false, LegacyKey: "data-separation"),
        new(ModuleIds.LoginConfirm, "进服确认"),
        new(ModuleIds.Message, "消息与公告"),
        new(ModuleIds.Console, "控制台"),
        new(ModuleIds.ServerList, "服务器列表与切换"),
    ];

    private static readonly Dictionary<string, ModuleDef> _byId = Build();

    /// <summary>按编号查模块声明；查不到返回 null。</summary>
    public static ModuleDef? Find(string id)
        => _byId.TryGetValue(id.Trim(), out var def) ? def : null;

    /// <summary>编号是否存在。</summary>
    public static bool Exists(string id) => _byId.ContainsKey(id.Trim());

    /// <summary>查中文名；不认识的编号原样返回，方便日志里带出来。</summary>
    public static string TitleOf(string id) => Find(id)?.Title ?? id;

    /// <summary>这个模块是否锁死不能关闭。</summary>
    public static bool IsLocked(string id) => Find(id)?.Locked == true;

    /// <summary>父模块编号；顶层模块返回空串。</summary>
    public static string ParentOf(string id) => Find(id)?.Parent ?? "";

    /// <summary>列出某个模块的全部儿子编号。</summary>
    public static IEnumerable<string> ChildrenOf(string id)
    {
        foreach (var def in All)
        {
            if (string.Equals(def.Parent, id, StringComparison.OrdinalIgnoreCase))
            {
                yield return def.Id;
            }
        }
    }

    private static Dictionary<string, ModuleDef> Build()
    {
        var map = new Dictionary<string, ModuleDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in All)
        {
            map[def.Id] = def;
        }

        return map;
    }
}
