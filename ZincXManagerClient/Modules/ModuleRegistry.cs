using System;  // Array.Empty / InvalidOperationException
using System.Collections.Generic;  // Dictionary / List / IReadOnlyList
using System.Collections.ObjectModel;  // ObservableCollection
using ZincXManagerClient.Models;  // NavItem / FunctionItem
using ZincXManagerShared.Modules;  // ModuleIds
using ZincXManagerShared.Network;  // Protocol.IsManagementLevel

namespace ZincXManagerClient.Modules;

/// <summary>
/// 客户端模块登记处:左侧 11 个分区 + 各分区下的卡片,一处分区一处卡片地登记在下面两张表里。
///
/// <para>导航与卡片都由它造出来,主界面不再手写任何一条 —— 加一个入口只改这里的一个类。
/// 「显不显示」分两层:<see cref="ModuleState"/> 管服务端开关(关掉的模块连入口都不出现),
/// 卡片上的 <see cref="FunctionItem.BaseVisible"/> 管连接方式这类本地条件,两者都开着才显示。</para>
/// </summary>
public static class ModuleRegistry
{
    /// <summary>全部分区,顺序即左侧导航的顺序(设置沉底)。</summary>
    private static readonly IReadOnlyList<SectionModule> _sections =
    [
        new EconomySection(),
        new CommunitySection(),
        new OrganizationSection(),
        new FriendsSection(),
        new WidgetsSection(),
        new ManageSection(),
        new SettingsSection(),
        new AdminServerSection(),
        new AdminFunctionSection(),
        new AdminZxmSection(),
        new AdminQuerySection(),
    ];

    /// <summary>全部卡片,按分区归到一起;每个分区的顺序就是界面上的顺序。</summary>
    private static readonly IReadOnlyList<CardModule> _cards =
    [
        // 经济
        new StoreCard(),
        new BankCard(),

        // 社区
        new ServerSquareCard(),
        new ZincSquareCard(),

        // 组织
        new PersonalOrgCard(),
        new TeamOrgCard(),
        new CompanyOrgCard(),

        // 小功能组
        new BindCard(),

        // 管理
        new VersionManageCard(),
        new SourceManageCard(),
        new ServerManageCard(),
        new StatsManageCard(),

        // 管理端:服务器管理
        new RconCard(),
        new ListenCard(),
        new FileCard(),
        new PluginCard(),
        new MapCard(),
        new MapSwitchCard(),
        new PlayerDataCard(),
        new OpCard(),
        new InventoryCard(),
        new WhitelistCard(),

        // 管理端:功能
        new MapBackupCard(),
        new MultiSyncCard(),
        new McServerCard(),
        new QqBotCard(),
        new StatsCard(),
        new QqBindCard(),
        new BanCard(),
        new VoteCard(),

        // 管理端:ZXM 管理
        new AccountCard(),
        new PostCard(),
        new ResourceCard(),
        new ChatroomCard(),
        new CompanyCard(),
        new PersonalCard(),
        new TeamCard(),
        new ShopCard(),
        new LoanCard(),
        new BondCard(),
        new ZoneCard(),

        // 管理端:查询统计
        new ZoneCheckCard(),
        new JoinLogCard(),
        new GroupLogCard(),
        new ServerLogCard(),
    ];

    /// <summary>按 key 找卡片用的索引。</summary>
    private static readonly Dictionary<string, CardModule> _byKey = BuildByKey();

    /// <summary>按分区找卡片用的索引。</summary>
    private static readonly Dictionary<string, List<FunctionItem>> _bySection = BuildBySection();

    /// <summary>
    /// 当前账号该看到的导航项。每次现造一份:换了账号或权限等级变了,也不会拿到上一次的那批。
    /// 管理账号看到管理端 4 个大类,玩家看到玩家端 6 个;设置两边都在。
    /// 造出来就按 <see cref="IsNavVisible"/> 定好初值,之后由 <see cref="MainViewModel.ApplyModules"/> 跟着开关重算。
    /// </summary>
    public static ObservableCollection<NavItem> BuildNavItems()
    {
        var admin = Protocol.IsManagementLevel(ClientConfig.GetOrEmpty("level"));
        var items = new ObservableCollection<NavItem>();
        var halfSections = new List<NavItem>();   // 管理账号补进来的玩家端分区,排在管理端大类后面

        ApplyCardVisibility();   // 先按现在的开关算好卡片,IsNavVisible 才有得看

        foreach (var section in _sections)
        {
            if (section.AdminOnly && !admin)
            {
                continue;
            }

            if (section.PlayerOnly && admin)
            {
                // 管理账号平时不看玩家端分区;只有分区里有半加载模块时才补进来 ——
                // 半加载是「玩家不能用、管理员照常操作」,管理员那份入口得留着,不然无处可点。
                if (!HasHalfCard(section.Id))
                {
                    continue;
                }

                var halfNav = section.BuildNav();
                halfNav.IsVisible = IsNavVisible(section.Id);
                halfSections.Add(halfNav);
                continue;
            }

            var nav = section.BuildNav();
            nav.IsVisible = IsNavVisible(section.Id);
            items.Add(nav);
        }

        // 补进来的分区排在管理端大类之后:它们是「额外多出来的一块」,不该顶在导航最上面
        foreach (var nav in halfSections)
        {
            items.Add(nav);
        }

        return items;
    }

    /// <summary>
    /// 分区里有没有半加载的卡片。管理账号看不到玩家端分区,半加载的模块得靠这条判据把分区补回导航。
    /// 用 <see cref="ModuleState.ModeOf"/> 而不是只比卡片自身的编号:父模块半加载时子卡片也算。
    /// </summary>
    public static bool HasHalfCard(string sectionId)
    {
        foreach (var card in CardsOf(sectionId))
        {
            if (ModuleState.ModeOf(card.ModuleId) == ModuleMode.Half)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 左侧这个分区该不该露头:分区自己的开关开着,并且分区里还有卡片能显示。
    /// 一张卡都没登记的分区(设置、好友这类不走卡片的)只看分区开关。
    /// 判据与 <see cref="ApplyCardVisibility"/> 同源,调用前先让它算一遍卡片。
    /// </summary>
    public static bool IsNavVisible(string sectionId)
        => ModuleState.IsOn(sectionId) && HasVisibleCard(sectionId);

    /// <summary>
    /// 分区里现在还有没有一张显示的卡片。分区开着、卡片却全被关掉时,留着空壳入口只会让人点进去对着空白页。
    /// </summary>
    public static bool HasVisibleCard(string sectionId)
    {
        var cards = CardsOf(sectionId);
        if (cards.Count == 0)
        {
            return true;
        }

        foreach (var card in cards)
        {
            if (card.IsVisible)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>某个分区下的卡片(顺序即界面显示顺序);不认识的编号给空表,调用方不用判空。</summary>
    public static IReadOnlyList<FunctionItem> CardsOf(string sectionId)
        => _bySection.TryGetValue(sectionId.Trim(), out var list) ? list : Array.Empty<FunctionItem>();

    /// <summary>按卡片的 key 找模块;找不到给 null(调用方按「不受开关控制」处理)。</summary>
    public static CardModule? Find(string key)
        => _byKey.TryGetValue(key.Trim(), out var module) ? module : null;

    /// <summary>
    /// 按现在的开关重算每张卡片显不显示:基准可见性(连接方式等)与模块开关都开着才显示。
    /// 改的是同一批卡片对象,界面绑在它们身上,所以改完立刻刷新。
    /// </summary>
    public static void ApplyCardVisibility()
    {
        foreach (var module in _cards)
        {
            var card = module.Card;
            card.IsVisible = card.BaseVisible && ModuleState.IsOn(card.ModuleId);
        }
    }

    /// <summary>建 key 索引:两个模块抢同一个 key 说明清单写重了,启动时直接报出来。</summary>
    private static Dictionary<string, CardModule> BuildByKey()
    {
        var map = new Dictionary<string, CardModule>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in _cards)
        {
            if (!map.TryAdd(module.Key, module))
            {
                throw new InvalidOperationException("功能卡片的 key 重复了:" + module.Key);
            }
        }

        return map;
    }

    /// <summary>把卡片按所属分区归堆。</summary>
    private static Dictionary<string, List<FunctionItem>> BuildBySection()
    {
        var map = new Dictionary<string, List<FunctionItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in _cards)
        {
            if (!map.TryGetValue(module.Section, out var list))
            {
                list = new List<FunctionItem>();
                map[module.Section] = list;
            }

            list.Add(module.Card);
        }

        return map;
    }
}
