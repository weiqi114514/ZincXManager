using System.Globalization;  // 区域与格式化
using System.Text;  // 文本编码与 StringBuilder
using System.Text.Json;  // JSON 序列化
using ZincXManagerShared.Logging;  // 共享库

namespace ZincXManagerServer;

/// <summary>
/// 子服务端自己的消息存档(ZXMS):<b>本服</b>发出去的公告 / 定向推送,以及官方服务端转下来的消息,各留一份。
/// 客户端登录后来要补发(505)时,本端自己这批和官方端那批<b>一起</b>给出去 ——
/// 官方端没连上 / 客户端走 OS 模式(只连子服)时,玩家仍然能把本服的历史补回来。
///
/// <list type="bullet">
///   <item>本服自己发的消息分配<b>本服消息号</b>(从 1 起),卡片第 8 段带着它;</item>
///   <item>官方端转下来的消息<b>原样存</b>(消息号是官方端分配的那个,不重新编号);</item>
///   <item>两批的来源标签不同(ZXMS-&lt;本服名&gt; / ZXMOS-&lt;官方名&gt;),所以客户端按「来源 + 消息号」去重不会撞;</item>
///   <item>保留天数 = <c>config.ini</c> 的 <c>srvmsgdays</c>(默认 <see cref="DefaultKeepDays"/> 天,可改);</item>
///   <item>存档文件 <c>messages.json</c>(手写 JSON,不依赖反射,AOT 安全)。</item>
/// </list>
/// </summary>
internal static class MessageArchive
{
    /// <summary>存档文件名(相对进程工作目录)。</summary>
    public const string FilePath = "messages.json";

    /// <summary>默认保留天数(比官方端的 50 天短:本服只是兜住"这段时间没收到"的玩家)。</summary>
    public const int DefaultKeepDays = 7;

    /// <summary>一条本服消息。</summary>
    public sealed class Entry
    {
        /// <summary>本端存档序号(排序用;不写进卡片)。</summary>
        public int Id { get; init; }

        /// <summary>卡片原文(下发时原样再发一遍,里面已经带着各自的消息号)。</summary>
        public string Card { get; init; } = "";

        /// <summary>来源标签(ZXMS-&lt;本服名&gt; 或 ZXMOS-&lt;官方名&gt;)。</summary>
        public string Source { get; init; } = "";

        /// <summary>发送时间。</summary>
        public DateTime SentAt { get; init; } = DateTime.Now;

        /// <summary>
        /// 定向目标:公告为空;<c>@all</c> = 全体客户端;<c>@clients:&lt;本端&gt;</c> = 本端名下所有客户端;
        /// <c>@server:&lt;本端&gt;</c> = 只给本端自己(不转给玩家);其余 = 玩家账号。
        /// </summary>
        public string Target { get; init; } = "";
    }

    private static readonly List<Entry> _list = new();
    private static int _nextId = 1;

    /// <summary>当前存档条数。</summary>
    public static int Count { get { lock (_list) { return _list.Count; } } }

    /// <summary>当前保留天数(启动时从 config 读,srvmsgdays 命令可改)。</summary>
    public static int KeepDays { get; private set; } = DefaultKeepDays;

    /// <summary>设置保留天数(由控制台命令改,写 config 在外面做)。</summary>
    public static void SetKeepDays(int days) => KeepDays = Math.Clamp(days, 1, 365);

    /// <summary>
    /// 存一条<b>本服</b>消息并分配消息号。<paramref name="buildCard"/> 拿到消息号后才拼卡片 ——
    /// 存档里那张和发出去的必须是<b>同一张</b>(都带第 8 段消息号),不然客户端补发时没法去重。
    /// 返回(消息号, 卡片原文)。
    /// </summary>
    public static (int Id, string Card) Add(Func<int, string> buildCard, string source, string target = "")
    {
        int id;
        string card;
        lock (_list)
        {
            id = _nextId++;
            card = buildCard(id);
            _list.Add(new Entry { Id = id, Card = card, Source = source, Target = target });
            Save();
        }

        Prune();
        return (id, card);
    }

    /// <summary>存一条已经拼好的卡片(官方端转下来的消息用:消息号是官方端的,原样存)。</summary>
    public static int Add(string card, string source, string target = "")
        => Add(_ => card, source, target).Id;

    /// <summary>清掉超过保留期的消息(过期的连补发也不给)。返回清掉几条。</summary>
    public static int Prune()
    {
        int removed;
        lock (_list)
        {
            removed = _list.RemoveAll(e => (DateTime.Now - e.SentAt).TotalDays > KeepDays);
            if (removed > 0)
            {
                Save();
            }
        }

        if (removed > 0)
        {
            Logger.Log(LogLevel.Info, $"[消息] 本服消息存档清掉 {removed} 条(超过 {KeepDays} 天)");
        }

        return removed;
    }

    /// <summary>按消息号找一条存档(找不到返回 null)</summary>
    public static Entry? Find(int id)
    {
        lock (_list)
        {
            return _list.FirstOrDefault(e => e.Id == id);
        }
    }

    /// <summary>保留期内的全部消息(按存档顺序)</summary>
    public static IReadOnlyList<Entry> Recent()
    {
        lock (_list)
        {
            Prune();
            return _list.OrderBy(e => e.Id).ToArray();
        }
    }

    /// <summary>读存档(启动时调一次;没有文件就是空的)。</summary>
    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath, Encoding.UTF8));

            lock (_list)
            {
                _list.Clear();

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var entry = new Entry
                    {
                        Id = item.TryGetProperty("id", out var id) && id.TryGetInt32(out var n) ? n : 0,
                        Card = Text(item, "card"),
                        Source = Text(item, "source"),
                        Target = Text(item, "target"),
                        SentAt = DateTime.TryParse(Text(item, "sentAt"), out var at) ? at : DateTime.Now,
                    };

                    if (entry.Id > 0 && entry.Card.Length > 0)
                    {
                        _list.Add(entry);
                        _nextId = Math.Max(_nextId, entry.Id + 1);
                    }
                }
            }

            Logger.Log(LogLevel.Info, $"[消息] 已载入本服消息存档 {Count} 条({FilePath},保留 {KeepDays} 天)");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[消息] 读本服消息存档失败,按空表处理: " + ex.Message);
        }
    }

    /// <summary>写存档。</summary>
    private static void Save()
    {
        try
        {
            var json = new StringBuilder("[\n");
            var list = _list.ToArray();

            for (var i = 0; i < list.Length; i++)
            {
                var e = list[i];
                json.Append("  {\"id\":").Append(e.Id)
                    .Append(",\"source\":\"").Append(Escape(e.Source)).Append('"')
                    .Append(",\"target\":\"").Append(Escape(e.Target)).Append('"')
                    .Append(",\"sentAt\":\"").Append(e.SentAt.ToString("o", CultureInfo.InvariantCulture)).Append('"')
                    .Append(",\"card\":\"").Append(Escape(e.Card)).Append("\"}");

                if (i < list.Length - 1)
                {
                    json.Append(',');
                }

                json.Append('\n');
            }

            json.Append("]\n");
            File.WriteAllText(FilePath, json.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[消息] 写本服消息存档失败: " + ex.Message);
        }
    }

    /// <summary>读一个字符串字段(没有就返回空串)。</summary>
    private static string Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>JSON 转义(手写,不依赖反射)。</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);

        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}
