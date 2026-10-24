namespace ZincXManagerShared.Network;

/// <summary>
/// 服务端下发的消息卡片(公告 500 / 定向推送 501 / 补发 504 的负载)。
///
/// <para><b>线格式 —— 固定 7 段,顺序不能变(第 8 段可选 = 消息号):</b></para>
/// <code>
/// 种类|标题|正文|来源|主动作|次动作|选项[|消息号]
/// </code>
/// <para>种类 = text / update / choice / input / select;空的段留空,但段数必须是 7。
/// 字段里不能出现 <c>|</c>(拼接时会被换成全角"／"),选项之间用半角逗号(选项文本里的逗号会换成"、")。
/// 正文可以带真换行 —— 包是长度前缀的,不怕换行。
/// 第 8 段 = 服务端存档分配的消息号(补发去重用);老格式没有这一段,解析回来是 0。</para>
/// </summary>
public static class MessageCard
{
    /// <summary>段分隔符。</summary>
    public const char Separator = '|';

    /// <summary>段数。</summary>
    public const int FieldCount = 7;

    /// <summary>正文长度上限(字符)。</summary>
    public const int MaxBody = 500;

    /// <summary>纯文本卡片(只有标题 + 正文,不显示按钮)。</summary>
    public const string Text = "text";

    public const string Update = "update";
    public const string Choice = "choice";
    public const string Input = "input";
    // 卡片动作:选择。
    public const string Select = "select";

    /// <summary>合法种类。</summary>
    public static readonly string[] Kinds = [Text, Update, Choice, Input, Select];

    /// <summary>这个种类合法吗。</summary>
    public static bool IsKind(string kind) => Array.IndexOf(Kinds, kind) >= 0;

    /// <summary>拼一张卡片(字段里的分隔符会被换掉,保证解析回来还是 7 段)。</summary>
    public static string Build(string kind, string title, string body, string source,
        string primary = "", string secondary = "", IEnumerable<string>? options = null, int id = 0)
    {
        var optionText = "";

        if (options is not null)
        {
            var list = new List<string>();

            foreach (var option in options)
            {
                var safe = SafeOption(option);
                if (safe.Length > 0)
                {
                    list.Add(safe);
                }
            }

            optionText = string.Join(',', list);
        }

        var head = string.Join(Separator,
            Safe(IsKind(kind) ? kind : Text), Safe(title), Safe(body), Safe(source),
            Safe(primary), Safe(secondary), optionText);

        // 消息号(服务端存档用,客户端补发去重靠它):0 = 不带这一段,保持 7 段的兼容写法
        return id > 0 ? head + Separator + id : head;
    }

    /// <summary>501 的负载:目标账号 + 卡片(每一跳都带着目标,方便逐跳过滤)。</summary>
    public static string BuildPush(string target, string card) => Safe(target) + Separator + card;

    /// <summary>
    /// 补发去重用的键:「来源 + 消息号」。消息号 ≤ 0(老格式卡片 / 本地消息)返回空串 ——
    /// 空串表示这条不参与去重,照旧显示。
    /// </summary>
    public static string DedupeKey(string source, int id)
        => id > 0 ? (source ?? "").Trim() + "#" + id : "";

    /// <summary>拆 501 的负载;格式不对返回 false。</summary>
    public static bool TryParsePush(string text, out string target, out string card)
    {
        target = "";
        card = "";

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var pos = text.IndexOf(Separator);
        if (pos <= 0 || pos >= text.Length - 1)
        {
            return false;
        }

        target = text[..pos].Trim();
        card = text[(pos + 1)..];
        return target.Length > 0;
    }

    /// <summary>解析卡片;段数不足或种类非法返回 false。</summary>
    public static bool TryParse(string text, out CardFields card)
    {
        card = new CardFields();

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var fields = text.Split(Separator);
        if (fields.Length < FieldCount)
        {
            return false;
        }

        var kind = fields[0].Trim().ToLowerInvariant();
        if (!IsKind(kind))
        {
            return false;
        }

        card = new CardFields
        {
            Id = fields.Length > FieldCount && int.TryParse(fields[FieldCount].Trim(), out var cardId) ? cardId : 0,
            Kind = kind,
            Title = fields[1].Trim(),
            Body = fields[2],
            Source = fields[3].Trim(),
            Primary = fields[4].Trim(),
            Secondary = fields[5].Trim(),
            Options = fields[6].Length == 0
                ? Array.Empty<string>()
                : fields[6].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };

        return true;
    }

    /// <summary>
    /// 从控制台参数读一张卡片。写法就是线格式去掉"来源":
    /// <c>&lt;标题&gt;|&lt;正文&gt;[|种类|主动作|次动作|选项]</c>(选项用逗号分隔)。
    /// 举例:<c>维护公告|今晚 23:00 停机|text</c>、<c>是否同意|新协议要点…|choice|是|否</c>、
    /// <c>版本选择|请选一个|select|||1.21.1,1.21,1.20.6</c>。
    /// </summary>

    /// <summary>
    /// 把「空格分参」的命令参数读成一张卡片:
    /// <c>&lt;种类&gt; &lt;标题&gt; &lt;正文&gt; [主动作] [次动作] [选项]</c>。
    /// 选项只有 select 用,逗号分隔;标题/正文不能为空。
    /// </summary>
    public static bool TryReadCardArgs(string[] args, out CardInput card, out string error)
    {
        card = new CardInput();
        error = "";

        if (args.Length < 3)
        {
            error = "至少写 种类 标题 正文(完整:种类 标题 正文 [主动作] [次动作] [选项])";
            return false;
        }

        var kind = args[0].Trim().ToLowerInvariant();
        if (!IsKind(kind))
        {
            error = "种类只能是 " + string.Join(" / ", Kinds);
            return false;
        }

        var options = args.Length > 5
            ? args[5].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

        card = new CardInput
        {
            Title = args[1].Trim(),
            Body = args[2].Trim(),
            Kind = kind,
            Primary = args.Length > 3 ? args[3].Trim() : "",
            Secondary = args.Length > 4 ? args[4].Trim() : "",
            Options = options,
        };

        if (card.Title.Length == 0)
        {
            error = "标题不能为空";
            return false;
        }

        if (card.Body.Length > MaxBody)
        {
            error = $"正文最长 {MaxBody} 字,现在 {card.Body.Length} 字";
            return false;
        }

        return true;
    }

    public static bool TryReadCard(string text, out CardInput card, out string error)
    {
        card = new CardInput();
        error = "";

        var fields = (text ?? "").Split(Separator);
        if (fields.Length < 2)
        {
            error = "至少写 标题|正文(完整:标题|正文|种类|主动作|次动作|选项)";
            return false;
        }

        var kind = fields.Length > 2 && fields[2].Trim().Length > 0 ? fields[2].Trim().ToLowerInvariant() : Text;
        if (!IsKind(kind))
        {
            error = "种类只能是 " + string.Join(" / ", Kinds);
            return false;
        }

        var options = fields.Length > 5
            ? fields[5].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

        card = new CardInput
        {
            Title = fields[0].Trim(),
            Body = fields[1].Trim(),
            Kind = kind,
            Primary = fields.Length > 3 ? fields[3].Trim() : "",
            Secondary = fields.Length > 4 ? fields[4].Trim() : "",
            Options = options,
        };

        if (card.Title.Length == 0)
        {
            error = "标题不能为空";
            return false;
        }

        if (card.Body.Length > MaxBody)
        {
            error = $"正文最长 {MaxBody} 字,现在 {card.Body.Length} 字";
            return false;
        }

        if (kind is Update or Input or Choice && card.Primary.Length == 0)
        {
            error = $"{kind} 卡片要写主动作(第 4 段)";
            return false;
        }

        if (kind == Choice && card.Secondary.Length == 0)
        {
            error = "choice 卡片要写次动作(第 5 段)";
            return false;
        }

        if (kind == Select && card.Options.Count == 0)
        {
            error = "select 卡片要写选项(第 6 段,逗号分隔)";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 拼"来源"标签,一共三种形态:
    /// <c>ZXMOS-&lt;官方服务端名称&gt;</c> / <c>ZXMS-&lt;服务端名称&gt;</c> / <c>ZXMC</c>(客户端本地消息)。
    /// 名称为空就只留端名。
    /// </summary>
    public static string SourceTag(string role, string name)
        => string.IsNullOrWhiteSpace(name) ? role : role + "-" + name.Trim();

    /// <summary>拼卡片动作的负载:<c>动作|输入|原卡片</c>(原卡片就是收到的那张 7 段文本)。</summary>
    public static string BuildAction(string action, string input, string card)
        => Safe(action) + Separator + Safe(input) + Separator + card;

    /// <summary>
    /// 拆卡片动作的负载。原卡片一起带上,服务端不用引入"卡片序号"就知道是哪张卡片、
    /// 是谁发的(来源段)、点了什么(动作段)、填空填了什么(输入段)。
    /// </summary>
    public static bool TryParseAction(string text, out CardActionFields action)
    {
        action = new CardActionFields();

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var first = text.IndexOf(Separator);
        if (first <= 0)
        {
            return false;
        }

        var second = text.IndexOf(Separator, first + 1);
        if (second < 0)
        {
            return false;
        }

        var card = text[(second + 1)..];
        var parsed = TryParse(card, out var fields);

        action = new CardActionFields
        {
            Action = text[..first].Trim(),
            Input = text[(first + 1)..second].Trim(),
            Card = card,
            CardSource = parsed ? fields.Source : "",
            CardTitle = parsed ? fields.Title : "",
        };

        return true;
    }

    /// <summary>从动作负载里取卡片标题(日志用)。</summary>
    public static string ActionTitle(string actionPayload)
        => TryParseAction(actionPayload, out var action) && action.CardTitle.Length > 0 ? action.CardTitle : "?";

    /// <summary>从动作负载里取"点了什么"(日志用);填了输入就一并带上。</summary>
    public static string ActionText(string actionPayload)
        => TryParseAction(actionPayload, out var action)
            ? (action.Input.Length > 0 ? $"{action.Action}:{action.Input}" : action.Action)
            : "?";

    /// <summary>日志里用的一句话摘要。</summary>
    public static string Summary(string text)
        => TryParse(text, out var card) ? $"[{card.Kind}] {card.Title}" : "(卡片格式不对)";

    /// <summary>字段里的分隔符换成全角,顺带去掉回车(只留 \n)。</summary>
    private static string Safe(string value) => (value ?? "").Replace(Separator, '／').Replace("\r", "");

    /// <summary>选项里的逗号换成顿号(逗号是选项分隔符)。</summary>
    private static string SafeOption(string value) => Safe(value).Replace(',', '、').Trim();
}

/// <summary>客户端上报的卡片动作(502 的负载):动作 / 输入 / 原卡片,以及从原卡片解析出来的来源与标题。</summary>
public sealed class CardActionFields
{
    /// <summary>按钮文案(下拉框固定是"选择")。</summary>
    public string Action { get; init; } = "";

    /// <summary>填空卡片里填的内容(其它形态为空)。</summary>
    public string Input { get; init; } = "";

    /// <summary>原卡片的完整 7 段文本。</summary>
    public string Card { get; init; } = "";

    /// <summary>原卡片的来源(ZXMOS-… / ZXMS-… / ZXMC);解析不出来就是空。</summary>
    public string CardSource { get; init; } = "";

    /// <summary>原卡片的标题;解析不出来就是空。</summary>
    public string CardTitle { get; init; } = "";
}

/// <summary>控制台命令读出来的一张卡片(标题 / 正文 / 种类 / 按钮文案 / 选项)。</summary>
public sealed class CardInput
{
    public string Title { get; init; } = "";

    public string Body { get; init; } = "";

    /// <summary>text / update / choice / input / select。</summary>
    public string Kind { get; init; } = MessageCard.Text;

    public string Primary { get; init; } = "";

    public string Secondary { get; init; } = "";

    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
}

/// <summary>解析出来的卡片字段。</summary>
public sealed class CardFields
{
    /// <summary>消息号(服务端存档分配的;0 = 没有 / 老格式)。</summary>
    public int Id { get; init; }

    /// <summary>种类:text / update / choice / input / select。</summary>
    public string Kind { get; init; } = MessageCard.Text;

    public string Title { get; init; } = "";

    public string Body { get; init; } = "";

    /// <summary>来源(客户端显示成"源:XXX");空则由客户端填默认值。</summary>
    public string Source { get; init; } = "";

    public string Primary { get; init; } = "";

    public string Secondary { get; init; } = "";

    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
}