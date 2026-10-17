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

    // ---------- 会话加密(ZCMC ↔ ZXMS,抗抓包重放) ----------
    public const ushort SessionHello = 380;      // ZXMC → ZXMS:客户端 ECDH 公钥
    public const ushort SessionHelloAck = 381;   // ZXMS → ZXMC:服务端 ECDH 公钥

    // ---------- 4xx+:业务(先占位,协议定好再填实) ----------
    /// <summary>好友消息 —— <b>未实施</b>:编号先占着,以后要做再定负载(那时才需要"每跳按包 Id 记上游"的回执链路)。</summary>
    public const ushort FriendMsg = 400;

    /// <summary>好友消息回执 —— <b>未实施</b>,理由同上。</summary>
    public const ushort FriendMsgAck = 401;

    /// <summary>
    /// 服务端公告(群发,单向:服务端 → 客户端)。负载 = 消息卡片,见 <see cref="MessageCard"/>:
    /// <c>种类|标题|正文|来源|主动作|次动作|选项</c>。
    /// ZXMOS → ZXMS → ZXMC:ZXMS 收到后原样转给自己名下所有 ZXMC。
    /// </summary>
    public const ushort Notice = 500;

    /// <summary>
    /// 服务端定向推送(单向:服务端 → 客户端)。负载 = <c>目标账号|卡片</c>(见 <see cref="MessageCard.BuildPush"/>)。
    /// 每一跳都按目标账号本地过滤:命中才投,不命中静默丢弃。
    /// </summary>
    public const ushort Push = 501;

    // ---------- 6xx:账号 ----------
    public const ushort AccountLogin = 600;       // ZXMC → ZXMS:登录,负载 "名称|密码密文|等级"
    public const ushort AccountLoginAck = 601;    // ZXMS → ZXMC:"ok|名称|ID|等级" 或 "err|原因"
    public const ushort AccountRegister = 602;    // ZXMC → ZXMS:注册,负载 "名称|密码密文"(等级固定 Player)
    public const ushort AccountRegisterAck = 603; // ZXMS → ZXMC:"ok|名称|ID|等级" 或 "err|原因"
    public const ushort AccountAvatarReq = 604;   // ZXMC → ZXMS:要当前账号的头像(负载空)
    public const ushort AccountAvatarAck = 605;   // ZXMS → ZXMC:图片字节(空 = 没头像);设置头像时回 "ok" / "err|原因"
    public const ushort AccountAvatarSet = 606;

    /// <summary>ZXMOS → ZXMS:把某些用户账号移交给子服务端(拉黑前先移交,别让玩家丢号)。负载 "名称|密文|等级" 每行一条。</summary>
    public const ushort AccountMigrate = 610;

    /// <summary>
    /// ZXMS → ZXMOS:改本端子服务端账号的名字(顺带改密码),由控制台 osuser 命令发起。
    /// 负载 "旧名|新名|新密文|机器码|服务端码";ZXMOS 校验账号存在 + 机器码 / 服务端码与登记一致才改。
    /// </summary>
    public const ushort AccountRename = 611;

    /// <summary>ZXMOS → ZXMS:改名回执,"ok|新名" 或 "err|原因"。</summary>
    public const ushort AccountRenameAck = 612;

    /// <summary>
    /// ZXMC → 服务端:玩家点了消息卡片上的按钮 / 选了下拉框。负载 <c>动作|输入|原卡片</c>
    /// (见 <see cref="MessageCard.BuildAction"/>):动作 = 按钮文案(下拉框固定"选择"),
    /// 输入 = 填空卡片填的内容,原卡片 = 收到的那张卡片原文 —— 服务端据此知道是哪张卡片,不用另加卡片序号。
    ///
    /// <para>卡片来源是 <c>ZXMOS-…</c> 时,子服务端会把动作转交给官方服务端(转发时负载前面多一段账号)。</para>
    /// </summary>
    public const ushort CardAction = 502;

    /// <summary>服务端 → ZXMC:卡片动作回执,<c>ok|提示文本</c> 或 <c>err|原因</c>。</summary>
    public const ushort CardActionAck = 503;

    /// <summary>
    /// 服务端 → 客户端:补发的<b>一条</b>历史消息(一条消息一个包,负载就是那张卡片原文 ——
    /// 第 8 段带着消息号)。正文里有换行也不会坏,不用按行切分。
    ///
    /// <para>客户端按「来源 + 消息号」去重(见 <see cref="MessageCard.DedupeKey"/>):
    /// 本地已经有的跳过,所以老客户端不会重复、新客户端一条不落。</para>
    /// </summary>
    public const ushort MessageHistory = 504;

    /// <summary>
    /// 客户端 → 服务端:把我还缺的服务端消息给我。客户端<b>登录成功后</b>发一次,负载 =
    /// <c>client</c>(替玩家要,子服务端中转时用)或 <c>self</c>(子服务端自己来要;空 = client)。
    /// 服务端按连接逐条回 <see cref="MessageHistory"/>,最后回一条 <see cref="MessageHistoryDone"/> 收尾。
    /// </summary>
    public const ushort MessageHistoryReq = 505;

    /// <summary>
    /// 服务端 → 客户端:这批补发完了(负载 = 补发条数)。客户端拿它收尾报提示,
    /// 中转端(ZXMS)拿它清掉"是哪条连接要的"那条记录。
    /// </summary>
    public const ushort MessageHistoryDone = 506;

    /// <summary>
    /// 服务端 → 客户端:服务端配置(客户端连上后下发一次),负载按行 <c>键=值</c>。
    /// 目前只有 <c>msgdays</c> = 这个子服务端的消息保留天数(客户端据此决定本地留多久;不认识的行忽略)。
    /// </summary>
    public const ushort ServerConfig = 202;

    /// <summary>
    /// 验证方 → 服务端:要一张"你确实是官方服务端"的证明(官方验证第二层)。
    /// 负载 = 32 位十六进制 nonce(一次性随机数);只有**已经登录过**的连接才会得到应答。
    /// </summary>
    public const ushort OfficialChallenge = 520;

    /// <summary>
    /// 服务端 → 验证方:证明自己。负载 = <c>Base64(许可证原文)|nonce|HMAC-SHA256(pepper, nonce)</c>;
    /// 拿不出合法证明时回 <c>err|&lt;原因代号&gt;</c>(例如 <c>err|development</c> = 开发构建没有证)。
    ///
    /// <para>验证方用内置公钥验签(证明这张证是你签发的)+ 校验 nonce(证明是现算的,不是重放),
    /// 见 <see cref="Verify.OfficialHandshake"/>。</para>
    /// </summary>
    public const ushort OfficialProof = 521;

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
