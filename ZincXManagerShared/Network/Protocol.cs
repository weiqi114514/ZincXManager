namespace ZincXManagerShared.Network;

/// <summary>
/// 协议常量:ZXMC / ZXMS / ZXMOS 三端共用同一份,免得各写一份对不上号。
///
/// <para>编号分段:</para>
/// <code>
///   1xx  子服务端 ↔ 官方服务端(ZXMS ↔ ZXMOS)
///   2xx  服务端 → 对方(身份声明等)
///   3xx  客户端 ↔ 服务端(ZXMC 上线)
///   4xx+ 业务包(好友消息等;4xx 及以上会被自动记进 clients.log)
///   6xx  账号(注册 / 登录 / 头像,ZXMC ↔ ZXMS)
/// </code>
/// </summary>
public static class Protocol
{
    // ---------- 1xx:子服务端 ↔ 官方服务端 ----------
    public const ushort SOnline = 100;   // ZXMS → ZXMOS:子服务端上线(握手)
    public const ushort SBack = 101;     // ZXMOS → ZXMS:收到

    // ---------- 2xx:服务端身份 ----------
    public const ushort ServerIdent = 200;   // 服务端 → 对方:我是谁(负载 "ZXMS" / "ZXMOS")
    public const ushort ServerName = 201;    // 服务端 → 对方:服务端名称(负载 = 显示名;由服务端给,客户端不用填)

    // ---------- 3xx:客户端 ↔ 服务端 ----------
    public const ushort Conline = 300;   // ZXMC → 服务端:客户端上线(负载 "ZXMC:<模式>")
    public const ushort CBack = 301;     // 服务端 → ZXMC:收到

    // ---------- 4xx+:业务(先占位,协议定好再填实) ----------
    public const ushort FriendMsg = 400;      // 好友消息(负载 "文本" 或自定义结构)
    public const ushort FriendMsgAck = 401;   // 好友消息回执
    public const ushort Notice = 500;         // 服务端公告(群发)
    public const ushort Push = 501;           // 服务端定向推送

    // ---------- 6xx:账号 ----------
    public const ushort AccountLogin = 600;       // ZXMC → ZXMS:登录,负载 "名称|密码密文|等级"
    public const ushort AccountLoginAck = 601;    // ZXMS → ZXMC:"ok|名称|ID|等级" 或 "err|原因"
    public const ushort AccountRegister = 602;    // ZXMC → ZXMS:注册,负载 "名称|密码密文"(等级固定 Player)
    public const ushort AccountRegisterAck = 603; // ZXMS → ZXMC:"ok|名称|ID|等级" 或 "err|原因"
    public const ushort AccountAvatarReq = 604;   // ZXMC → ZXMS:要当前账号的头像(负载空)
    public const ushort AccountAvatarAck = 605;   // ZXMS → ZXMC:图片字节(空 = 没头像);设置头像时回 "ok" / "err|原因"
    public const ushort AccountAvatarSet = 606;

    /// <summary>ZXMOS → ZXMS:把某些用户账号移交给子服务端(拉黑前先移交,别让玩家丢号)。负载 "名称|密文|等级" 每行一条。</summary>
    public const ushort AccountMigrate = 610;   // ZXMC → ZXMS:设置当前账号头像(负载 = 图片字节)

    /// <summary>业务包起始编号:4xx 及以上算业务包,发送时会自动记 clients.log。</summary>
    public const ushort BusinessFrom = 400;

    /// <summary>自助注册固定用这个等级;更高等级只能由 ZXMS 控制台命令创建。</summary>
    public const string RegisterLevel = "Player";

    /// <summary>权限等级(从低到高)。</summary>
    public static readonly string[] Levels = ["Player", "LAdmin", "HAdmin", "System"];

    /// <summary>ZXMOS 的等级表:玩家账号是 Player,子服务端(ZXMS)接入账号是 Admin / System。</summary>
    public static readonly string[] OSLevels = ["Player", "Admin", "System"];

    /// <summary>ZXMOS 上自助注册(玩家)能拿到的等级。</summary>
    public const string OSRegisterLevel = "Player";

    /// <summary>
    /// 服务端账号(ZXMS 接入官方服务端用的账号)没有等级分类 —— 用这个占位符存字段位置。
    /// 它和"用户账号"(Player / Admin / System)是两回事。
    /// </summary>
    public const string ServerAccountLevel = "-";

    /// <summary>服务端账号专用的等级表(只有占位符,用来复用同一套账号表实现)。</summary>
    public static readonly string[] ServerLevels = [ServerAccountLevel];

    /// <summary>是 ZXMOS 那边的等级吗。</summary>
    public static bool IsOSLevel(string level) => Array.IndexOf(OSLevels, level) >= 0;

    /// <summary>是否业务包(会被自动审计)。</summary>
    public static bool IsBusiness(ushort type) => type >= BusinessFrom && type < 600;

    /// <summary>是不是合法的权限等级。</summary>
    public static bool IsLevel(string level) => Array.IndexOf(Levels, level) >= 0;
}
