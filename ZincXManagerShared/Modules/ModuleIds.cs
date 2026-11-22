namespace ZincXManagerShared.Modules;

/// <summary>
/// 模块编号集中定义：服务端与客户端共用同一串编号，开关在服务端改一次、两端一起生效。
/// 编号写进服务端 config.ini 的 modules 键（写禁用名单），客户端从 2002 包拿到同样的名单。
/// 命名规则：导航分区用 nav. 前缀，卡片用两位分区缩写加点，后台功能用单词。
/// </summary>
public static class ModuleIds
{
    // ---------- 导航分区(客户端左侧导航,服务端也用它当功能归属) ----------

    /// <summary>经济</summary>
    public const string NavEconomy = "nav.economy";

    /// <summary>社区</summary>
    public const string NavCommunity = "nav.community";

    /// <summary>组织</summary>
    public const string NavOrganization = "nav.organization";

    /// <summary>好友</summary>
    public const string NavFriends = "nav.friends";

    /// <summary>小功能组</summary>
    public const string NavWidgets = "nav.widgets";

    /// <summary>管理</summary>
    public const string NavManage = "nav.manage";

    /// <summary>设置(锁死,不能关闭)</summary>
    public const string NavSettings = "nav.settings";

    /// <summary>服务器管理</summary>
    public const string NavAdminServer = "nav.admin-server";

    /// <summary>功能</summary>
    public const string NavAdminFunction = "nav.admin-function";

    /// <summary>ZXM 管理</summary>
    public const string NavAdminZxm = "nav.admin-zxm";

    /// <summary>查询统计</summary>
    public const string NavAdminQuery = "nav.admin-query";

    // ---------- 经济 ----------

    /// <summary>商店</summary>
    public const string EcoStore = "eco.store";

    /// <summary>银行</summary>
    public const string EcoBank = "eco.bank";

    // ---------- 社区 ----------

    /// <summary>服务器广场</summary>
    public const string ComSrvSquare = "com.srv-square";

    /// <summary>锌源广场</summary>
    public const string ComZincSquare = "com.zinc-square";

    // ---------- 组织 ----------

    /// <summary>个人组织</summary>
    public const string OrgPersonal = "org.personal";

    /// <summary>团队组织</summary>
    public const string OrgTeam = "org.team";

    /// <summary>企业组织</summary>
    public const string OrgCompany = "org.company";

    // ---------- 小功能组 ----------

    /// <summary>绑定与解绑</summary>
    public const string WidgetBind = "wd.bind";

    // ---------- 管理 ----------

    /// <summary>版本管理</summary>
    public const string ManageVersion = "mg.version";

    /// <summary>数据源管理</summary>
    public const string ManageSource = "mg.source";

    /// <summary>服务器管理</summary>
    public const string ManageServer = "mg.server";

    /// <summary>数据统计</summary>
    public const string ManageStats = "mg.stats";

    // ---------- 服务器管理 ----------

    /// <summary>RCON / ZXMM 桥接控制台</summary>
    public const string AdminRcon = "as.rcon";

    /// <summary>监听服务器</summary>
    public const string AdminListen = "as.listen";

    /// <summary>文件管理</summary>
    public const string AdminFile = "as.file";

    /// <summary>mod / 插件管理</summary>
    public const string AdminPlugin = "as.plugin";

    /// <summary>地图管理</summary>
    public const string AdminMap = "as.map";

    /// <summary>地图备份切换</summary>
    public const string AdminMapSwitch = "as.mapswitch";

    /// <summary>玩家数据管理</summary>
    public const string AdminPlayerData = "as.playerdata";

    /// <summary>op 管理</summary>
    public const string AdminOp = "as.op";

    /// <summary>玩家背包管理</summary>
    public const string AdminInventory = "as.inventory";

    /// <summary>白名单</summary>
    public const string AdminWhitelist = "as.whitelist";

    // ---------- 功能 ----------

    /// <summary>MC 地图备份</summary>
    public const string FuncMapBackup = "af.mapbackup";

    /// <summary>多服玩家互通</summary>
    public const string FuncMultiSync = "af.multisync";

    /// <summary>与 MC 服的连接设置</summary>
    public const string FuncMcServer = "af.mcserver";

    /// <summary>qqbot 连接</summary>
    public const string FuncQqBot = "af.qqbot";

    /// <summary>统计</summary>
    public const string FuncStats = "af.stats";

    /// <summary>QQ 绑定</summary>
    public const string FuncQqBind = "af.qqbind";

    /// <summary>联 ban</summary>
    public const string FuncBan = "af.ban";

    /// <summary>投票发布</summary>
    public const string FuncVote = "af.vote";

    // ---------- ZXM 管理 ----------

    /// <summary>账户管理</summary>
    public const string AdminZxmAccount = "az.account";

    /// <summary>账号注册(客户端 6002 注册新账号)</summary>
    public const string AdminZxmRegister = "az.register";

    /// <summary>帖子管理</summary>
    public const string AdminZxmPost = "az.post";

    /// <summary>资源管理</summary>
    public const string AdminZxmResource = "az.resource";

    /// <summary>聊天室管理</summary>
    public const string AdminZxmChatroom = "az.chatroom";

    /// <summary>企业组织管理</summary>
    public const string AdminZxmCompany = "az.company";

    /// <summary>个人组织管理</summary>
    public const string AdminZxmPersonal = "az.personal";

    /// <summary>团队组织管理</summary>
    public const string AdminZxmTeam = "az.team";

    /// <summary>官方商店</summary>
    public const string AdminZxmShop = "az.shop";

    /// <summary>官方贷款</summary>
    public const string AdminZxmLoan = "az.loan";

    /// <summary>官方债券</summary>
    public const string AdminZxmBond = "az.bond";

    /// <summary>开发区 / 机器 / 基地管理</summary>
    public const string AdminZxmZone = "az.zone";

    // ---------- 查询统计 ----------

    /// <summary>开发区机器位置校验</summary>
    public const string QueryZoneCheck = "aq.zonecheck";

    /// <summary>玩家出入服务器记录</summary>
    public const string QueryJoinLog = "aq.joinlog";

    /// <summary>群聊天记录</summary>
    public const string QueryGroupLog = "aq.grouplog";

    /// <summary>服聊天记录</summary>
    public const string QueryServerLog = "aq.serverlog";

    // ---------- 后台功能(服务端模块类) ----------

    /// <summary>好友聊天后端(拉起聊天宿主进程)</summary>
    public const string Chat = "chat";

    /// <summary>强制更新</summary>
    public const string ForceUpdate = "forceupdate";

    /// <summary>多服数据分离</summary>
    public const string DataSeparation = "datasep";

    /// <summary>进服确认</summary>
    public const string LoginConfirm = "loginconfirm";

    /// <summary>消息与公告</summary>
    public const string Message = "msg";

    /// <summary>控制台</summary>
    public const string Console = "console";

    /// <summary>服务器列表与切换</summary>
    public const string ServerList = "serverlist";
}
