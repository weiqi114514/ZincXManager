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
    /// <summary>
    /// 好友私聊。ZXMC → ZXMS:负载 <c>对方完整ID\t消息编号\t正文</c>。
    /// 消息编号由客户端生成,只用于「重发不产生重复消息」和「回执对号入座」,不是序号。
    /// </summary>
    public const ushort FriendMsg = 400;

    /// <summary>
    /// 好友私聊回执。ZXMC → ZXMS / ZXMS → ZXMC:负载 <c>ok|消息编号|序号</c> 表示消息已收下并分配了序号,
    /// <c>ok|消息编号|序号|dup</c> 表示这条之前就收过(重发,没有重复落盘),
    /// <c>err|原因</c> 表示没发出去(不是好友 / 目标不对 / 对方不在线且没落盘)。
    /// </summary>
    public const ushort FriendMsgAck = 401;

    /// <summary>
    /// ZXMC → ZXMS:要一段聊天记录。负载 <c>对方完整ID\t起点序号</c>(起点序号空或 0 = 从头要)。
    /// 只回序号大于起点的那几条,最多 200 条。
    /// </summary>
    public const ushort FriendMsgHistory = 402;

    /// <summary>
    /// ZXMS → ZXMC:聊天记录。每行一条 <c>序号\t时间\tdirection\t正文</c>,
    /// direction = <c>in</c>(对方发来的) 或 <c>out</c>(自己发出去的)。
    /// </summary>
    public const ushort FriendMsgHistoryReply = 403;

    /// <summary>
    /// 送达 / 已读回执。服务端 → 客户端:负载 <c>对端完整ID\t消息编号或序号\t状态</c>,
    /// 状态 = <c>delivered</c>(对方客户端收到了) 或 <c>read</c>(对方打开聊天窗看到了)。
    /// </summary>
    public const ushort FriendMsgReceipt = 404;

    /// <summary>
    /// 已读上报。ZXMC → ZXMS:负载 <c>对端完整ID\t已读到第几号</c>,服务端据此算未读数并转给对方。
    /// </summary>
    public const ushort FriendReadMark = 405;

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
    public const ushort AccountLoginAck = 601;    // ZXMS → ZXMC:"ok|名称|ID|等级|完整ID" 或 "err|原因"(第五段是本服完整ID,老服务端不发时客户端只用编号)
    public const ushort AccountRegister = 602;    // ZXMC → ZXMS:注册,负载 "名称|密码密文"(等级固定 Player)
    public const ushort AccountRegisterAck = 603; // ZXMS → ZXMC:"ok|名称|ID|等级|完整ID" 或 "err|原因"(第五段同 601)
    public const ushort AccountAvatarReq = 604;   // ZXMC → ZXMS:要当前账号的头像(负载空)
    public const ushort AccountAvatarAck = 605;   // ZXMS → ZXMC:图片字节(空 = 没头像);设置头像时回 "ok" / "err|原因"
    public const ushort AccountAvatarSet = 606;

    /// <summary>
    /// ZXMC → ZXMS:要一个好显示的头像。负载 <c>对方完整ID|账户名</c>(旧客户端只发账户名也照收)。
    /// 只有本人和好友能要。
    /// </summary>
    public const ushort FriendAvatarReq = 607;

    /// <summary>ZXMS → ZXMC:头像。<c>账户名|base64</c>;图片段为空 = 没有头像。</summary>
    public const ushort FriendAvatarAck = 608;

    /// <summary>
    /// ZXMS → ZXMOS:本服账号表(613 上报)。每行 <c>ID|名称|等级</c>。
    /// 注意这是 3 段:账号 ID 是<b>各服自己的本地编号</b>,跨服会撞号,ZXMOS 只按来源服分开存,不解析。
    /// </summary>
    public const ushort UserListUp = 613;

    /// <summary>
    /// ZXMOS → ZXMS:全网账号表(614 下发),整份替换。每行 <c>归属服完整ID|ID|名称|等级</c>。
    /// 归属服完整ID 形如 <c>ZXMS-testserver2-2</c>;玩家直连 ZXMOS 注册的没有归属服,这一段留空,
    /// 此时按 <c>#ID</c> 认人、不跨服找。兼容旧的 3 段格式(没有归属服,一律当本服编号看)。
    /// </summary>
    public const ushort UserListDown = 614;

    /// <summary>
    /// 跨服中转(ZNMS 之间经 ZXMOS)。负载 <c>来源完整ID|目标完整ID|动作|附加</c>,
    /// 动作见 <see cref="FriendRelayAction"/>。ZXMOS 只按目标完整ID 找到归属子服转发,不实现好友业务。
    /// </summary>
    public const ushort FriendRelay = 615;

    /// <summary>好友上下线广播。<c>完整ID|online|offline</c>,经 ZXMOS 转给这个人的所有好友所在服。</summary>
    public const ushort FriendPresence = 616;

    /// <summary>ZXMS → ZXMOS:上报本服显示名。<c>服务端ID|显示名</c>。</summary>
    public const ushort ServerNameReport = 617;

    /// <summary>ZXMOS → ZXMS:全服显示名表。每行 <c>服务端ID\t显示名</c>。</summary>
    public const ushort ServerNameMap = 618;

    /// <summary>
    /// ZXMS → ZXMOS → 目标 ZXMS:跨服要头像。<c>目标服务端ID|账户名|请求者完整ID</c>。
    /// 好友关系由发起服校验,ZXMOS 只按目标服务端ID 定向转发(不广播、不落盘)。
    /// </summary>
    public const ushort AvatarRelay = 648;

    /// <summary>目标 ZXMS → ZXMOS → 发起 ZXMS:跨服头像回执。<c>账户名|base64|请求者完整ID</c>;空 = 那边也没有。</summary>
    public const ushort AvatarRelayAck = 649;

    // ---------- 65x:好友(ZXMC ↔ ZXMS,跨服经 ZXMOS) ----------

    /// <summary>ZXMC → ZXMS:要好友列表。负载空。回 <see cref="FriendListReply"/>。</summary>
    public const ushort FriendListQuery = 650;

    /// <summary>ZXMS → ZXMC:好友列表。每行 <c>完整ID\t名字\t备注\t在线\t来源服名\t未读数\t最后一条</c>;没好友就是空负载。</summary>
    public const ushort FriendListReply = 651;

    /// <summary>ZXMC → ZXMS:发好友申请。负载 <c>完整ID|验证消息</c>。回 <see cref="FriendRequestAck"/>,对方在线会立刻收到 657。</summary>
    public const ushort FriendRequest = 654;

    /// <summary>ZXMS → ZXMC:好友申请回执。<c>ok|</c> 或 <c>err|原因</c>(已是好友 / 不能加自己 / 账号不存在 / 官方端未连接)。</summary>
    public const ushort FriendRequestAck = 655;

    /// <summary>ZXMC → ZXMS:要"别人发给我的好友申请"列表。负载空。</summary>
    public const ushort FriendRequestsQuery = 656;

    /// <summary>ZXMS → ZXMC:好友申请列表。每行 <c>申请者完整ID\t验证消息\t时间</c>。收到新申请时服务端会主动推这一包。</summary>
    public const ushort FriendRequestsReply = 657;

    /// <summary>ZXMC → ZXMS:回答一个申请。负载 <c>申请者完整ID|yes|no</c>。</summary>
    public const ushort FriendAnswer = 658;

    /// <summary>ZXMS → ZXMC:回答回执。<c>ok|</c> 或 <c>err|原因</c>。</summary>
    public const ushort FriendAnswerAck = 659;

    /// <summary>ZXMC → ZXMS:删好友。负载 <c>完整ID</c>。</summary>
    public const ushort FriendRemove = 660;

    /// <summary>ZXMS → ZXMC:删好友回执。<c>ok|</c> 或 <c>err|原因</c>。</summary>
    public const ushort FriendRemoveAck = 661;

    /// <summary>ZXMS → ZXMC:好友事件推送。<c>事件|完整ID</c>,事件见 <see cref="FriendEventKind"/>。</summary>
    public const ushort FriendEvent = 662;

    /// <summary>
    /// ZXMC → ZXMS:搜好友。负载 = 关键词。
    /// 规则:关键词按最后一个 <c>-</c> 切出编码段,能转成数字就按 ID 找,否则按名字找;
    /// 名字必须<b>完全一致</b>(大小写不敏感),不做前缀/模糊匹配。结果最多 <see cref="FriendSearchLimit"/> 条。
    /// </summary>
    public const ushort FriendSearch = 663;

    /// <summary>
    /// ZXMS → ZXMC:搜索结果。每行 <c>完整ID|名称|等级</c>;
    /// 本服不认识的账号回 <c>完整ID|#编码|-</c>(意思是"我只能给你地址,成不成问对方服")。
    /// </summary>
    public const ushort FriendSearchReply = 664;

    /// <summary>ZXMC → ZXMS：设置好友备注。负载 对方完整ID\t备注(空串表示清除备注)。</summary>
    public const ushort FriendRemark = 665;

    /// <summary>ZXMS → ZXMC：设置好友备注的回执。"ok" 或 "err|原因"。</summary>
    public const ushort FriendRemarkAck = 666;

    /// <summary>ZXMOS → ZXMS:把某些用户账号移交给子服务端(拉黑前先移交,别让玩家丢号)。负载 "名称|密文|等级" 每行一条。</summary>
    public const ushort AccountMigrate = 610;

    /// <summary>ZXMS → ZXMOS:改本端子服务端账号的名字(顺带改密码),由控制台 osuser 命令发起。 负载 "旧名|新名|新密文|机器码|服务端码";ZXMOS 校验账号存在 + 机器码 / 服务端码与登记一致才改。</summary>
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

    /// <summary>服务端 → 客户端:这批补发完了(负载 = 补发条数)。客户端拿它收尾报提示, 中转端(ZXMS)拿它清掉"是哪条连接要的"那条记录。</summary>
    public const ushort MessageHistoryDone = 506;

    /// <summary>507 客户端回执:告诉服务端"这几条我已经收下并落盘了"。 负载 = 消息号,空格分隔(可一次回多条)。服务端收到才把消息记进已发清单,以后补发不再给。</summary>
    public const ushort MessageAck = 507;

    /// <summary>508 ZXMS → ZXMOS:要官方端的玩家返回记录(msg backget ... -os)。负载 = 查询参数原文。</summary>
    public const ushort MegBackReq = 508;

    /// <summary>509 ZXMOS → ZXMS:玩家返回记录的查询结果(纯文本,一行一条;err| 开头表示失败)。</summary>
    public const ushort MegBackAck = 509;

    /// <summary>
    /// 510 ZXMS → ZXMC:好友聊天后端的端口与凭据(登录成功后发一次)。
    /// 负载 <c>端口\ttoken\t本服完整ID</c>;聊天后端地址 = 客户端连的那个地址 + 这个端口 + <c>/chat</c>。
    /// </summary>
    public const ushort ChatHello = 510;

    // ---------- 62x:登录确认与账号绑定(ZXMM ↔ ZXMS ↔ ZXMC) ----------

    /// <summary>
    /// 620 LoginRequest:ZXMM → ZXMS。有玩家要进服,问一句"这个号绑了谁、要不要放行"。
    /// 负载 <c>玩家名|玩家IP</c>。
    /// </summary>
    public const ushort LoginRequest = 620;

    /// <summary>
    /// 621 LoginAsk:ZXMS → ZXMC。把登录请求弹给绑定的客户端账号。
    /// 负载 <c>请求ID|玩家名|玩家IP|归属地</c>(归属地是服务端查好的,查不到写"未知")。
    /// </summary>
    public const ushort LoginAsk = 621;

    /// <summary>622 LoginAnswer:ZXMC → ZXMS。负载 <c>请求ID|yes</c> 或 <c>请求ID|no</c>。</summary>
    public const ushort LoginAnswer = 622;

    /// <summary>
    /// 623 LoginResult:ZXMS → ZXMM。负载 <c>请求ID|ok|no|timeout|玩家名|ZXM账号名|ZXM账号ID</c>。
    /// </summary>
    public const ushort LoginResult = 623;

    /// <summary>624 LoginTimeout:ZXMS → ZXMC。五分钟没人点,告诉客户端一次。负载 <c>请求ID|玩家名</c>。</summary>
    public const ushort LoginTimeout = 624;

    /// <summary>625 BindNotice:ZXMM → ZXMS。没绑定的玩家拿到的绑定码转给服务端。负载 <c>绑定码|玩家名</c>。</summary>
    public const ushort BindNotice = 625;

    /// <summary>626 BindApply:ZXMC → ZXMS。客户端用绑定码绑定。负载 <c>绑定码</c>。</summary>
    public const ushort BindApply = 626;

    /// <summary>
    /// 627 BindAck:ZXMS → ZXMC。回执三种:校验码通过但<b>还没绑</b>时回 <c>pending|确认令牌|玩家名</c>,
    /// 用户二次确认后回 <c>ok|玩家名</c>,其它失败 <c>err|原因</c>。
    /// </summary>
    public const ushort BindAck = 627;

    /// <summary>628 Unbind:ZXMC → ZXMS。解绑当前账号。负载 <c>账号ID</c> 或空(空 = 按连接账号)。</summary>
    public const ushort Unbind = 628;

    /// <summary>
    /// 629 BindQuery:ZXMM → ZXMS。查绑定关系(谁绑了谁)。
    /// 负载 <c>玩家名</c> 或 <c>ZXM账号名</c>;回 630 BindList。
    /// </summary>
    public const ushort BindQuery = 629;

    /// <summary>630 BindList:ZXMS → ZXMM。绑定关系列表,一行一条 <c>玩家名=ZXM账号名</c>,没有就是空负载。</summary>
    public const ushort BindList = 630;

    /// <summary>
    /// 631 BindConfirm:ZXMC → ZXMS。绑定二次确认(626 回了 pending 之后,用户点了确定才发)。
    /// 负载 = 确认令牌;令牌属于发起账号、2 分钟内有效、一次性。服务端收到才真正写绑定表。
    /// </summary>
    public const ushort BindConfirm = 631;


    /// <summary>
    /// 632 ClientIpReport:ZXMC → ZXMS。客户端把自己的<b>局域网 IPv4</b>(网卡地址,如 <c>192.168.1.23</c>)报上来。
    ///
    /// <para>为什么需要它:ZXMC 经 frp 隧道连进来时,ZXMS 侧看到的连接地址是隧道对端(127.0.0.1),
    /// 登录弹窗里给玩家看的 IP 与归属地都失去意义。</para>
    ///
    /// <para>上报值<b>只用于展示与日志</b>:白名单、封禁、持久登录表、限速等安全判断一律仍用连接 IP。
    /// 负载 = IP 文本(空负载或非法地址由服务端丢掉)。</para>
    /// </summary>
    public const ushort ClientIpReport = 632;

    /// <summary>
    /// 633 ClientIpAsk:ZXMS → ZXMC。玩家进服时服务端没有(或已过期)这个账号的局域网 IP,向它的客户端要一次。
    /// 负载空;客户端收到就回一条 <see cref="ClientIpReport"/>。
    /// <para>服务端<b>不等</b>这条回复:玩家进服不能卡在客户端回包上,这次没赶上就下一次进服生效。</para>
    /// </summary>
    public const ushort ClientIpAsk = 633;

    /// <summary>
    /// 服务端 → 客户端:服务端配置(客户端连上后下发一次),负载按行 <c>键=值</c>。
    /// 目前只有 <c>msgdays</c> = 这个子服务端的消息保留天数(客户端据此决定本地留多久;不认识的行忽略)。
    /// </summary>
    public const ushort ServerConfig = 202;

    /// <summary>验证方 → 服务端:要一张"你确实是官方服务端"的证明(官方验证第二层)。 负载 = 32 位十六进制 nonce(一次性随机数);只有**已经登录过**的连接才会得到应答。</summary>
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

    /// <summary>服务端账号(ZXMS 接入官方服务端用的账号)没有等级分类 —— 用这个占位符存字段位置。 它和"用户账号"(Player / Admin / System)是两回事。</summary>
    public const string ServerAccountLevel = "-";

    /// <summary>服务端账号专用的等级表(只有占位符,用来复用同一套账号表实现)。</summary>
    public static readonly string[] ServerLevels = [ServerAccountLevel];

    /// <summary>是 ZXMOS 那边的等级吗。</summary>
    public static bool IsOSLevel(string level) => Array.IndexOf(OSLevels, level) >= 0;

    /// <summary>是否业务包(会被自动审计)。</summary>
    public static bool IsBusiness(ushort type) => type >= BusinessFrom && type < 600;

    /// <summary>是不是合法的权限等级。</summary>
    public static bool IsLevel(string level) => Array.IndexOf(Levels, level) >= 0;

    /// <summary>一次搜索最多返回多少条。</summary>
    public const int FriendSearchLimit = 20;

    /// <summary>好友列表负载里的分隔符(字段之间用 \t,免得和名字里的竖线打架)。</summary>
    public const char FriendFieldSeparator = '\t';

    /// <summary>
    /// <see cref="FriendRelay"/>(615) 的动作词。ZXMOS 按动作原样转发,不解释含义;
    /// 含义只在收发双方的 ZXMS 里有意义。
    /// </summary>
    public static class FriendRelayAction
    {
        /// <summary>好友申请。<c>来源|目标|request|申请者完整ID|验证消息</c>。</summary>
        public const string Request = "request";

        /// <summary>申请被同意/拒绝。<c>来源|目标|answer|yes 或 no</c>。</summary>
        public const string Answer = "answer";

        /// <summary>好友列表里加一条(跨服同意后通知对方服写档)。<c>来源|目标|add|</c>。</summary>
        public const string Add = "add";

        /// <summary>好友列表里删一条。<c>来源|目标|remove|</c>。</summary>
        public const string Remove = "remove";

        /// <summary>跨服私聊。<c>来源|目标|message|消息编号|正文</c>(编号为空表示不参与重发去重)。</summary>
        public const string Message = "message";

        /// <summary>跨服回执(送达 / 已读)。<c>来源|目标|receipt|消息编号或序号|delivered 或 read</c>。</summary>
        public const string Receipt = "receipt";
    }

    /// <summary><see cref="FriendEvent"/>(662) 的事件词。</summary>
    public static class FriendEventKind
    {
        /// <summary>有人给我发了好友申请。</summary>
        public const string Request = "request";

        /// <summary>我的申请被同意了。</summary>
        public const string Agree = "agree";

        /// <summary>对方把我删了(列表要刷)。</summary>
        public const string Remove = "remove";

        /// <summary>对方上线了。</summary>
        public const string Online = "online";

        /// <summary>对方下线了。</summary>
        public const string Offline = "offline";
    }
}
