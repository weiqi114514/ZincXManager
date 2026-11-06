using System;                                // DateTime / Guid:时间戳与消息编号
using System.Collections.Generic;            // List / Dictionary:打开的窗口、待回执的消息
using System.Linq;                           // FirstOrDefault:按对方 ID 找窗口
using System.Threading.Tasks;                // Task:聊天后端的 HTTP 调用
using Avalonia;                              // Thickness / HorizontalAlignment
using Avalonia.Controls;                     // Window / TextBlock / Border
using Avalonia.Input;                        // PointerPressedEventArgs / KeyEventArgs
using Avalonia.Interactivity;                // RoutedEventArgs
using Avalonia.Layout;                       // Orientation / VerticalAlignment:消息行与头像的排布
using Avalonia.Media;                        // SolidColorBrush / Color
using Avalonia.Media.Imaging;                // Bitmap:头像图解码
using Avalonia.Threading;                    // DispatcherTimer:轮询聊天后端
using ZincXManagerClient.Chat;               // ChatBridge / ChatApi
using ZincXManagerShared.Account;            // AvatarStore:本地头像缓存
using ZincXManagerShared.Chat;               // ChatChannel:私聊频道号
using ZincXManagerShared.Friend;             // FriendCodec:拼包与拆包;PlayerId:玩家地址与服ID解析
using ZincXManagerShared.Logging;            // Logger / LogLevel:回执对不上号时记一条
using ZincXManagerShared.Network;            // Protocol

namespace ZincXManagerClient.Views;

/// <summary>
/// 与一个好友的聊天窗。
///
/// <para>这个窗口只管「显示和输入」。消息存在服务端,窗口打开时按序号增量拉(<see cref="OnHistoryReply"/>),
/// 自己发出去的消息靠三个小状态告诉用户走到哪一步了:发送中 → 服务端已收(<see cref="OnAck"/>) →
/// 对方收到 / 对方读过(<see cref="OnReceipt"/>)。</para>
/// </summary>
public partial class ChatWindow : Window
{
    /// <summary>现在开着几个聊天窗(关窗时自己摘掉)。</summary>
    private static readonly List<ChatWindow> Open = [];

    /// <summary>谁在等历史回包:403 的负载里不带对方 ID,只能按先来先到时对上号(连接是串行收包的)。</summary>
    private static readonly List<(ChatWindow Window, long At)> HistoryWaiters = [];

    /// <summary>等这么久还没等到 403,就当那次回包丢了,别让它把后面每一次回包都错开一个窗口。</summary>
    private const int HistoryWaitTtlMs = 15_000;

    /// <summary>刚发过 665、在等 666 的那个窗口:666 的负载里也不带对方 ID,只能记下是谁问的。</summary>
    private static ChatWindow? _remarkWaiter;

    /// <summary>两条消息隔多久就在中间插一条时间分隔条。</summary>
    private static readonly TimeSpan DividerGap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 气泡下面那个状态位的等级:数值越大越「确定」,只许往大改。
    /// <para>失败排在「已发送」下面:迟到的成功回执能纠正它,失败本身不能把已经确认的消息打回去;
    /// 「对方离线」排在「已发送」上面:服务端回 401 后才知道对方不在,这个结果得盖住「已发送」。</para>
    /// </summary>
    private enum BubbleState
    {
        Sending = 0,   // 刚上屏,还没拿到服务端回执
        Failed = 1,    // 没发出去
        Sent = 2,      // 服务端存下了
        Offline = 3,   // 对方离线:消息还留在服务端等对方上线,不是失败
        Delivered = 4, // 对方客户端收到了
        Read = 5,      // 对方打开聊天窗看到了
    }

    /// <summary>自己发的消息:消息编号 → 气泡下面那个小状态,收到回执时改它。</summary>
    private readonly Dictionary<string, TextBlock> _pending = [];

    /// <summary>最近一次发出去、还在等回执的消息编号:401 的失败回执不带编号,只能认最近这条。</summary>
    private string _lastPendingId = "";

    /// <summary>已经「已发送」但仍可能被中转判为失败的消息:编号 → 气泡状态块。跨服中转在 401 之后才回失败,那时光靠 <see cref="_pending"/> 已经找不到气泡了。</summary>
    private readonly Dictionary<string, TextBlock> _sentLabels = [];

    /// <summary><see cref="_sentLabels"/> 最多记这么多条,超了先丢最旧的(只影响红叹号能否落到很久以前那一条)。</summary>
    private const int SentLabelLimit = 500;

    /// <summary>最近一次发消息的窗口:失败回执不带编号也不带窗口线索,靠它找回是谁在等。</summary>
    private static ChatWindow? _lastSender;

    /// <summary>已经拿到序号、还可能被「已读」追上的消息,按序号排。</summary>
    private readonly List<(int Seq, string MessageId, TextBlock Label)> _mine = [];

    private readonly string _peerId;      // 对方的完整ID,如 ZXMS-testserver-1-13
    private readonly string _peerName;    // 没有备注时显示的名字(账户名 / 服务端给的名字)
    private readonly string _peerAccount; // 对方的账户名,查头像用(备注不是账号)
    private readonly string _serverName;  // 服务端给的展示名;可能为空,旧数据里还可能是服ID
    private string _remark;               // 我给对方起的备注;空 = 用原名
    private bool _settingsOpen;           // 侧边栏现在是展开的吗
    private bool _removed;                // 好友关系已解除,这个窗作废:不再发送、不再轮询
    private int _lastSeq;                 // 这个会话里我手上最大的序号,拉增量与上报已读都用它

    /// <summary>对方是本服的人时,这里放聊天后端的频道号;跨服对话留空,走原来的 400/403。</summary>
    private string _channel = "";

    /// <summary>聊天后端已经上过屏的消息编号,轮询回来别再画一遍。</summary>
    private readonly HashSet<string> _seen = [];

    /// <summary>
    /// 400 先上屏、随后 403 补序号时,这几条已经在屏上了,别再画第二遍。
    /// 按到达顺序排队:回包里的方向、时间跟刚画的完全一致,对不上就是新消息。
    /// </summary>
    private readonly Queue<(string Text, long At)> _shownBeforeSeq = new();

    /// <summary>先进窗口、还没拿到序号的正文最多记这么久,过了就丢,别让一条丢包卡死后来的同文本消息。</summary>
    private const int PendingTextTtlMs = 10_000;

    private DispatcherTimer? _poll;       // 本地对话的轮询器(1.5 秒一次)
    private bool _polling;                // 上一轮还没回来就别挤第二轮
    private Control? _emptyHint;          //「还没聊过」那条灰提示,来了真消息就收掉
    private DateTime? _lastBubbleAt;      // 上一条真实消息的时间,决定要不要先插一条时间分隔条

    public ChatWindow() : this("", "对象名称", "")
    {
    }

    public ChatWindow(string peerId, string peerName, string serverName, string peerAccount = "")
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);

        _peerId = peerId;
        var shownName = peerName.Length > 0 ? peerName : "对象名称";
        // 头像缓存按账户名存;显示的可能是备注,拿备注去查永远查不到图
        _peerAccount = peerAccount.Length > 0 ? peerAccount : shownName;
        // 好友列表给的显示名是「备注优先」:它和账户名对不上,说明名字位装的其实是备注,开窗就该回填
        _remark = string.Equals(shownName, _peerAccount, StringComparison.Ordinal) ? "" : shownName;
        // 名字位装的是备注时把原名换回账户名,将来清掉备注才回得去
        _peerName = _remark.Length > 0 ? _peerAccount : shownName;
        _serverName = serverName;

        FriendNameText.Text = DisplayName;
        SourceText.Text = ServerLabel();
        SourceText2.Text = ServerLabel();
        ObjectIdText.Text = peerId.Length > 0 ? peerId : "-";
        RefreshTitle();

        Open.Add(this);
        Closed += (_, _) =>
        {
            _poll?.Stop();
            ChatBridge.Configured -= OnChatConfigured;
            Open.Remove(this);
        };

        // 窗口一开就算读过这个会话:红点立刻消,不用等历史回包(跨服也一样)
        FriendsWindow.Current?.ClearUnread(_peerId);

        if (TryStartLocalChat())
        {
            return;
        }

        // 聊天后端的门牌可能比窗口晚到(先点开窗、后登录完),到了再切过去
        ChatBridge.Configured += OnChatConfigured;
        _ = LoadHistoryAsync();
    }

    /// <summary>界面上显示的名字:有备注用备注,没有就用原名。</summary>
    private string DisplayName => _remark.Length > 0 ? _remark : _peerName;

    /// <summary>聊天后端刚就绪:如果对方是本服的人,这个窗口改走它。</summary>
    private void OnChatConfigured()
    {
        ChatBridge.Configured -= OnChatConfigured;
        TryStartLocalChat();
    }

    /// <summary>把服务端展示名翻成界面文案:没名字就明说不知道,不拿内部服 ID 充数。</summary>
    private string ServerLabel()
    {
        if (_serverName.Length > 0 && !LooksLikeServerId(_serverName))
        {
            return _serverName;
        }

        // 同一台子服说「本服」比「未知」有用;聊天后端没就绪时 ServerId 为空,自然落到「未知服务端」
        if (PlayerId.TryParse(_peerId, out var peer) && ChatBridge.ServerId.Length > 0 && peer.ServerId == ChatBridge.ServerId)
        {
            return "本服";
        }

        return "未知服务端";
    }

    /// <summary>
    /// 这段文本是不是内部服 ID(如 <c>ZXMS-testserver-1</c>)而不是给人看的展示名。
    /// <para>旧数据里好友表存的就是服 ID,服务端还没升级时也会原样回传;直接上屏等于把内网编号露给用户。</para>
    /// </summary>
    private static bool LooksLikeServerId(string text)
    {
        if (!text.StartsWith(PlayerId.ChildPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tail = text[(text.LastIndexOf('-') + 1)..];
        return tail.Length > 0 && tail.All(char.IsAsciiDigit);
    }

    /// <summary>重写标题:窗口刚建时聊天后端可能还没就绪,拿到服信息后要再调一次。</summary>
    private void RefreshTitle()
    {
        Title = $"聊天 · {DisplayName} · {ServerLabel()}";
    }

    /// <summary>
    /// 对方和我在同一台子服,就把这个对话交给聊天后端;返回 true 表示已经接管,不必再走 400。
    /// </summary>
    private bool TryStartLocalChat()
    {
        if (_channel.Length > 0 || _peerId.Length == 0)
        {
            return _channel.Length > 0;
        }

        if (!ChatBridge.Ready || !PlayerId.TryParse(_peerId, out var peer) || peer.ServerId != ChatBridge.ServerId)
        {
            return false;
        }

        _channel = ChatChannel.ForPair(ChatBridge.SelfCode, peer.Code);
        RefreshTitle();

        // 打开会话就算读过了:红点先清掉,别等第一次拉取成功
        FriendsWindow.Current?.ClearUnread(_peerId);
        _ = ChatApi.MarkReadAsync(_channel);

        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _poll.Tick += async (_, _) => await RefreshLocalAsync(first: false);
        _poll.Start();

        _ = RefreshLocalAsync(first: true);
        return true;
    }

    /// <summary>去聊天后端把新消息捞回来上屏;第一次多捞一些当历史。</summary>
    private async Task RefreshLocalAsync(bool first)
    {
        if (_polling || _removed || _channel.Length == 0)
        {
            return;
        }

        _polling = true;
        try
        {
            var lines = await ChatApi.FetchAsync(_channel, first ? 60 : 20);
            if (first && lines.Count == 0)
            {
                ShowEmptyHint();
            }

            var added = 0;
            foreach (var line in lines)
            {
                if (!_seen.Add(line.Id))
                {
                    continue;
                }

                var mine = int.TryParse(line.SenderId, out var code) && code == ChatBridge.SelfCode;
                var label = AddBubble(line.Text, incoming: !mine, line.At);
                if (label is not null)
                {
                    SetState(label, BubbleState.Sent);
                }

                added++;
            }

            if (added > 0)
            {
                await ChatApi.MarkReadAsync(_channel);
                FriendsWindow.Current?.ClearUnread(_peerId);
            }
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>把这条消息交给对应的聊天窗(开着才上屏);返回 true 表示已经显示出来了。</summary>
    public static bool Deliver(string peerId, string text)
    {
        var window = ForPeer(peerId);
        if (window is null)
        {
            return false;
        }

        window.OnIncoming(text);
        return true;
    }

    /// <summary>把 403 交回给发起查询的那个窗口。</summary>
    public static void DeliverHistory(string payload)
    {
        // 先扔掉「窗口已经关了」和「等太久、回包八成丢了」的那几个:
        // 留着它们会让之后每一次 403 都错开一个窗口,导致别人的聊天记录画进本窗
        var now = Environment.TickCount64;   //毫秒级,只用来算等了多久
        while (HistoryWaiters.Count > 0)
        {
            var head = HistoryWaiters[0];
            if (now - head.At < HistoryWaitTtlMs && Open.Contains(head.Window))
            {
                break;
            }

            HistoryWaiters.RemoveAt(0);
            Logger.Log(LogLevel.Warning, "[聊天] 丢弃一次没人认领的历史请求(窗口已关,或等了 15 秒没回音)");
        }

        if (HistoryWaiters.Count == 0)
        {
            Logger.Log(LogLevel.Warning, "[聊天] 收到一条没人认领的聊天记录回包,已丢弃");
            return;
        }

        var waiter = HistoryWaiters[0].Window;
        HistoryWaiters.RemoveAt(0);
        waiter.OnHistoryReply(payload);
    }

    /// <summary>把 404 交回给「对方是这个人」的那个窗口(负载里带对端 ID,所以能认出来)。</summary>
    public static void DeliverReceipt(string payload)
    {
        if (!FriendCodec.TryParseReceipt(payload, out var peerId, out var key, out var state))
        {
            return;
        }

        Open.FirstOrDefault(one => one._peerId == peerId)?.OnReceipt(key, state);
    }

    /// <summary>把 401 交回给发出这条消息的那个窗口(消息编号是我自己生成的,全局唯一)。</summary>
    public static void Ack(string messageId, int seq, bool duplicate)
    {
        foreach (var window in Open)
        {
            if (window._pending.ContainsKey(messageId))
            {
                window.OnAck(messageId, seq, duplicate);
                return;
            }
        }
    }

    /// <summary>按对方完整ID找开着的聊天窗;没开返回 null。</summary>
    public static ChatWindow? ForPeer(string peerId) =>
        Open.FirstOrDefault(one => string.Equals(one._peerId, peerId.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>666 回到客户端:只可能是我刚才那次修改的回执,交给当时发 665 的窗口。</summary>
    public static void RemarkAck(string payload)
    {
        var waiter = _remarkWaiter;
        _remarkWaiter = null;
        if (waiter is null)
        {
            Logger.Log(LogLevel.Warning, $"[聊天] 收到没人认领的备注回执:{payload}");
            return;
        }

        waiter.OnRemarkAck(payload);
    }

    /// <summary>401 的失败回执不带消息编号:把最近一次发出去、还没拿到结果的那条标成失败。</summary>
    public static void FailLastPending(string reason)
    {
        var sender = _lastSender;
        _lastSender = null;
        if (sender is null)
        {
            Logger.Log(LogLevel.Warning, $"[聊天] 有意思发送失败的记录,但找不到是哪个聊天窗发的:{reason}");
            return;
        }

        sender.FailNewestPending(reason);
    }

    /// <summary>401 的三段失败回执(err|编号|原因):按消息编号找到发它的那个窗口,把那条气泡标成失败。</summary>
    public static void FailById(string messageId, string reason)
    {
        var target = messageId.Trim();
        foreach (var window in Open)
        {
            if (window.FindStateLabel(target) is null)
            {
                continue;
            }

            window.FailMessage(target, reason);
            return;
        }

        Logger.Log(LogLevel.Warning, $"[聊天] 发送失败的消息 {target} 不在任何开着的聊天窗里:{reason}");
    }

    /// <summary>把本窗口最近一条还没落定的消息标成失败。</summary>
    private void FailNewestPending(string reason)
    {
        if (_lastPendingId.Length == 0)
        {
            Logger.Log(LogLevel.Warning, $"[聊天] 发送失败的记录对不上任何待回执的消息:{reason}");
            return;
        }

        // 先找还在等回执的;已经「已发送」的也要能找到 —— 跨服那条是 401 之后才被中转判失败
        if (!_pending.TryGetValue(_lastPendingId, out var label) && !_sentLabels.TryGetValue(_lastPendingId, out label))
        {
            Logger.Log(LogLevel.Warning, $"[聊天] 发送失败的记录对不上任何待回执的消息:{reason}");
            return;
        }

        MarkFailed(label, reason);
        _pending.Remove(_lastPendingId);
        _sentLabels.Remove(_lastPendingId);
        _lastPendingId = "";

        // 这条其实没发出去,好友行上的临时预览得撤掉,别让列表显示一句对方收不到的
        FriendsWindow.Current?.ReleaseLocalPreview(_peerId);
    }

    /// <summary>服务端回了历史(403):把每行补上屏,然后上报「我读到这里了」。</summary>
    public void OnHistoryReply(string payload)
    {
        var lines = FriendCodec.ParseHistoryReply(payload, out var badLines);
        if (badLines > 0)
        {
            AddNotice($"有 {badLines} 条记录读不出来,已跳过");
        }

        foreach (var line in lines)
        {
            if (line.Seq <= _lastSeq)
            {
                continue;
            }

            // 这条刚才已经靠 400 上屏了,只是那时还不知道序号:记下序号就行,别再画一遍。
            // 队列项带时间戳:一条没落盘的消息不该永久堵在队头,把之后同文本的新消息都吞掉
            if (line.Incoming)
            {
                while (_shownBeforeSeq.Count > 0
                    && Environment.TickCount64 - _shownBeforeSeq.Peek().At >= PendingTextTtlMs)
                {
                    _shownBeforeSeq.Dequeue();
                }
            }

            if (line.Incoming && _shownBeforeSeq.Count > 0 && _shownBeforeSeq.Peek().Text == line.Text)
            {
                _shownBeforeSeq.Dequeue();
                _lastSeq = line.Seq;
                continue;
            }

            var label = AddBubble(line.Text, line.Incoming, line.Time);
            _lastSeq = line.Seq;
            if (!line.Incoming && label is not null)
            {
                // 历史里的自己那条只能确定「服务端存着」,对方读没读这一轮不追
                SetState(label, BubbleState.Sent);
            }
        }

        if (_lastSeq == 0)
        {
            ShowEmptyHint();
        }

        _ = MarkReadAsync();
    }

    /// <summary>服务端收下了我这条(401):记下序号,状态改成「已发送」;重发过就把「重发」标出来。</summary>
    public void OnAck(string messageId, int seq, bool duplicate)
    {
        if (!_pending.Remove(messageId, out var label))
        {
            return;
        }

        SetState(label, BubbleState.Sent, text: duplicate ? "已发送(重发)" : "已发送");
        if (seq > _lastSeq)
        {
            _lastSeq = seq;
        }

        // 401 只说明本服收下了,跨服那条还要经 ZXMOS 转;留下气泡引用,中转失败时才能把红叹号标回它
        RememberSentLabel(messageId, label);
        _mine.Add((seq, messageId, label));
    }

    /// <summary>记下已发送的气泡,超过上限先丢最旧的。</summary>
    private void RememberSentLabel(string messageId, TextBlock label)
    {
        if (messageId.Length == 0)
        {
            return;
        }

        if (_sentLabels.Count >= SentLabelLimit)
        {
            foreach (var oldest in _sentLabels.Keys.Take(_sentLabels.Count - SentLabelLimit + 1).ToArray())
            {
                _sentLabels.Remove(oldest);
            }
        }

        _sentLabels[messageId] = label;
    }

    /// <summary>
    /// 回执(404):编号对应「对方收到了」,序号对应「对方读到第几号」。
    /// 两个都可能来,来的次序不保证,所以只往「更好」的方向改(见 <see cref="SetState"/>)。
    /// </summary>
    public void OnReceipt(string key, string state)
    {
        if (string.Equals(state, "read", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(key, out var seq))
            {
                Logger.Log(LogLevel.Warning, $"[聊天] 已读回执里的序号认不出来:{key}");
                return;
            }

            foreach (var one in _mine.Where(one => one.Seq <= seq))
            {
                SetState(one.Label, BubbleState.Read);
            }

            return;
        }

        if (string.Equals(state, "delivered", StringComparison.OrdinalIgnoreCase))
        {
            // 精确到一条:以前这里把整个列表刷成「已送达」,连已经「已读」的都会被打回去
            if (FindStateLabel(key) is { } label)
            {
                SetState(label, BubbleState.Delivered);
            }
            else
            {
                Logger.Log(LogLevel.Warning, $"[聊天] 送达回执对不上消息:{key}");
            }

            return;
        }

        if (string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase))
        {
            if (FindStateLabel(key) is { } failed)
            {
                MarkFailed(failed, "发送失败");
            }
            else
            {
                Logger.Log(LogLevel.Warning, $"[聊天] 失败回执对不上消息:{key}");
            }

            return;
        }

        if (string.Equals(state, "undelivered", StringComparison.OrdinalIgnoreCase))
        {
            // 对方离线不是发送失败:消息还留在服务端等对方上线,不画红色叹号
            if (FindStateLabel(key) is { } offline)
            {
                SetState(offline, BubbleState.Offline, "对方不在线,消息留在服务端等上线");
            }
            else
            {
                Logger.Log(LogLevel.Warning, $"[聊天] 离线回执对不上消息:{key}");
            }

            return;
        }

        Logger.Log(LogLevel.Warning, $"[聊天] 认不出的回执状态:{state}");
    }

    /// <summary>按消息编号找某条自己发的消息的状态位;还没拿到序号、正在等 401 的也算。</summary>
    private TextBlock? FindStateLabel(string messageId)
    {
        foreach (var one in _mine)
        {
            if (string.Equals(one.MessageId, messageId, StringComparison.Ordinal))
            {
                return one.Label;
            }
        }

        if (_pending.TryGetValue(messageId, out var pending))
        {
            return pending;
        }

        // 已经「已发送」的也要能在中转回失败时被找到
        return _sentLabels.TryGetValue(messageId, out var sent) ? sent : null;
    }

    /// <summary>按消息编号把某条自己发的消息标成发送失败;那条已经不在本会话里就只记日志。</summary>
    public void FailMessage(string messageId, string reason)
    {
        if (FindStateLabel(messageId) is { } label)
        {
            MarkFailed(label, reason.Length > 0 ? reason : "发送失败");
            // 这句其实没发出去,好友列表就不该继续把它当「最后一句」
            FriendsWindow.Current?.ReleaseLocalPreview(_peerId);
            _pending.Remove(messageId);
            _sentLabels.Remove(messageId);
            if (string.Equals(_lastPendingId, messageId, StringComparison.Ordinal))
            {
                _lastPendingId = "";
            }

            return;
        }

        Logger.Log(LogLevel.Warning, $"[聊天] 要标失败的消息不在这个会话里:{messageId}");
    }

    /// <summary>收到对方一句话:上屏,再把序号补上(回执与未读都靠序号)。</summary>
    private void OnIncoming(string text)
    {
        _shownBeforeSeq.Enqueue((text, Environment.TickCount64));
        AddBubble(text, incoming: true);
        _ = RequestHistoryAsync(_lastSeq);
    }

    /// <summary>打开窗口时拉一遍历史(从 0 开始;服务端只给最近 200 条),空提示由回包决定。</summary>
    private async System.Threading.Tasks.Task LoadHistoryAsync()
    {
        if (_peerId.Length == 0 || _channel.Length > 0)
        {
            return;
        }

        RefreshTitle();
        await RequestHistoryAsync(0);
    }

    /// <summary>向服务端要「序号大于 afterSeq」的那些消息。</summary>
    private async System.Threading.Tasks.Task RequestHistoryAsync(int afterSeq)
    {
        var connect = ClientSession.Connect;
        if (connect is null)
        {
            return;
        }

        // 先排队再发包:回包可能在我拿到 await 结果之前就到
        var slot = (Window: this, At: Environment.TickCount64);
        HistoryWaiters.Add(slot);
        try
        {
            if (await connect.SendAsync(Protocol.FriendMsgHistory, FriendCodec.BuildHistoryQuery(_peerId, afterSeq)))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[聊天] 拉聊天记录没发出去:{ex.Message}");
        }

        HistoryWaiters.Remove(slot);
    }

    /// <summary>告诉服务端「这个对话我读到第几号了」,对方那边会显示已读。</summary>
    private async System.Threading.Tasks.Task MarkReadAsync()
    {
        if (_channel.Length > 0)
        {
            await ChatApi.MarkReadAsync(_channel);
            return;
        }

        var connect = ClientSession.Connect;
        if (connect is null || _lastSeq <= 0)
        {
            return;
        }

        await connect.SendAsync(Protocol.FriendReadMark, FriendCodec.BuildReadMark(_peerId, _lastSeq));
    }

    /// <summary>往消息区加一条聊天消息;incoming = 对方说的。自己发的会返回状态标签。</summary>
    private TextBlock? AddBubble(string text, bool incoming, DateTime? at = null)
    {
        var when = at ?? DateTime.Now;
        HideEmptyHint();
        InsertTimeDivider(when);
        _lastBubbleAt = when;

        // 这里的 HorizontalAlignment 是 Window 自己的属性,取枚举值得写全名
        var align = incoming ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;

        var body = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(incoming ? Color.Parse("#1F2A36") : Colors.White),
        };

        var bubble = new Border
        {
            Child = body,
            Padding = new Thickness(11, 7, 11, 7),
            CornerRadius = new CornerRadius(0),
            MaxWidth = 420,
            HorizontalAlignment = align,
            Background = new SolidColorBrush(incoming ? Color.Parse("#EAF2FB") : Color.Parse("#2662A7")),
        };

        // 气泡、时间、状态竖着排成一列;头像挂在列外面(靠自己那边的边缘),所以整行的左右由外层 StackPanel 决定
        var column = new StackPanel { Spacing = 2, HorizontalAlignment = align };
        column.Children.Add(bubble);
        column.Children.Add(new TextBlock
        {
            Text = when.ToString("HH:mm"),
            FontSize = 10,
            HorizontalAlignment = align,
            Foreground = new SolidColorBrush(Color.Parse("#8899A6")),
        });

        // 自己发的才挂状态位
        TextBlock? state = null;
        if (!incoming)
        {
            state = new TextBlock
            {
                Text = "发送中",
                FontSize = 9.5,
                HorizontalAlignment = align,
                Foreground = new SolidColorBrush(Color.Parse("#8899A6")),
            };
            column.Children.Add(state);
        }

        var avatar = AvatarFor(incoming, 28);
        avatar.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        avatar.Margin = new Thickness(0, 2, 0, 0);

        // 对方发的头像在左,自己的在右
        var line = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = align,
        };

        if (incoming)
        {
            line.Children.Add(avatar);
            line.Children.Add(column);
        }
        else
        {
            line.Children.Add(column);
            line.Children.Add(avatar);
        }

        MessageList.Children.Add(line);
        MessageScroll.ScrollToEnd();
        return state;
    }

    /// <summary>按上一条消息的间隔插一条时间分隔:第一条、跨天、隔了 5 分钟以上都算新的一段。</summary>
    private void InsertTimeDivider(DateTime at)
    {
        if (_lastBubbleAt is { } previous && previous.Date == at.Date && at - previous < DividerGap)
        {
            return;
        }

        MessageList.Children.Add(BuildNotice(DividerText(at), wrap: false));
    }

    /// <summary>一条居中的浅色提示,高度按内容自适应;wrap = 长文(比如报错原因)允许换行。</summary>
    private static Control BuildNotice(string text, bool wrap) => new Border
    {
        Background = new SolidColorBrush(Color.Parse("#F1F4F7")),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8, 2, 8, 2),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MaxWidth = 420,
            Foreground = new SolidColorBrush(Color.Parse("#8899A6")),
        },
    };

    /// <summary>往消息区加一条居中提示并滚到底。</summary>
    private Control AddNotice(string text)
    {
        var notice = BuildNotice(text, wrap: true);
        MessageList.Children.Add(notice);
        MessageScroll.ScrollToEnd();
        return notice;
    }

    /// <summary>分隔文字要能一眼看出是哪一天:今天 / 昨天都写中文,更早的写完整日期。</summary>
    private static string DividerText(DateTime at)
    {
        var today = DateTime.Today;
        if (at.Date == today)
        {
            return $"今天 {at:HH:mm}";
        }

        return at.Date == today.AddDays(-1) ? $"昨天 {at:HH:mm}" : at.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>消息旁边那个头像:本地有图就用图,没有就画「青绿底 + 首字」。</summary>
    private Control AvatarFor(bool incoming, double size)
    {
        // 头像按账户名缓存(跨服也唯一),自己就是登录账号;对方用账户名而不是备注
        var account = incoming ? _peerAccount : ClientSession.User;
        var key = account.Length > 0 ? account : incoming ? _peerId : "?";
        var initial = account.Length > 0 ? account : incoming ? _peerId : "?";

        var image = AvatarStore.Load(key);
        return image.Length > 0 ? ImageAvatar(image, size, initial) : InitialAvatar(initial, size);
    }

    /// <summary>有头像图:裁成圆形,跟默认头像保持同一个轮廓。图坏了就退回首字头像,别让一条坏图把整段历史画断。</summary>
    private static Control ImageAvatar(byte[] image, double size, string fallbackName)
    {
        Bitmap bitmap;
        try
        {
            bitmap = new Bitmap(new System.IO.MemoryStream(image));
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[聊天] 头像图读不出来,改用首字头像:{ex.Message}");
            return InitialAvatar(fallbackName, size);
        }

        var picture = new Image
        {
            Source = bitmap,
            Stretch = Stretch.UniformToFill,
        };

        // 图片自己再套一层椭圆裁剪,跟外层 Border 的圆角一起保证头像是圆的
        picture.Clip = new EllipseGeometry(new Rect(0, 0, size, size));

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            ClipToBounds = true,
            Child = picture,
        };
    }

    /// <summary>取首字;名字以 emoji 开头时按代理对切两个字,别切出半个字符。</summary>
    private static string InitialOf(string name) =>
        name.Length > 1 && char.IsHighSurrogate(name[0]) ? name[..2] : name[..1];

    /// <summary>默认头像:青绿底 + 白色首字。</summary>
    private static Control InitialAvatar(string name, double size) => new Border
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = new SolidColorBrush(Color.Parse("#4E9C9C")),
        Child = new TextBlock
        {
            Text = name.Length > 0 ? InitialOf(name).ToUpperInvariant() : "?",
            FontSize = size * 0.42,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>空对话时插一条灰提示;已经插过就不重复。</summary>
    private void ShowEmptyHint()
    {
        _emptyHint ??= AddNotice("还没聊过");
    }

    /// <summary>来了真消息就把「还没聊过」收掉。</summary>
    private void HideEmptyHint()
    {
        if (_emptyHint is null)
        {
            return;
        }

        MessageList.Children.Remove(_emptyHint);
        _emptyHint = null;
    }

    /// <summary>状态位的文字与颜色;失败态另走 <see cref="MarkFailed"/>。</summary>
    private static void SetState(TextBlock label, BubbleState state, string? text = null)
    {
        // 只升不降:回执来的顺序不保证,迟到的成功可以纠正失败,已经确认的不能被后来的低等级打回
        var current = label.Tag is BubbleState known ? known : BubbleState.Sending;
        if (current == BubbleState.Failed)
        {
            // 失败是终态:没发出去的那句不能被后来的「已读」洗成已读,否则玩家会以为对方看到了
            return;
        }

        if (state < current)
        {
            return;
        }

        label.Tag = state;
        label.Text = text ?? state switch
        {
            BubbleState.Sent => "已发送",
            BubbleState.Delivered => "已送达",
            BubbleState.Read => "已读",
            _ => "发送中",
        };
        label.FontSize = 9.5;
        label.FontWeight = FontWeight.Normal;
        label.Foreground = new SolidColorBrush(Color.Parse(state == BubbleState.Read ? "#2662A7" : "#8899A6"));
        ToolTip.SetTip(label, null);
    }

    /// <summary>发送失败:状态位换成红色感叹号,鼠标悬停看原因。</summary>
    private static void MarkFailed(TextBlock label, string reason)
    {
        // 送达/已读是终点,不回退成红叹号;「已发送」只说明本服收下了,跨服中转失败要能降级成失败
        var current = label.Tag is BubbleState known ? known : BubbleState.Sending;
        if (current is BubbleState.Delivered or BubbleState.Read or BubbleState.Offline)
        {
            return;
        }

        label.Tag = BubbleState.Failed;
        label.Text = "!";
        label.FontSize = 11;
        label.FontWeight = FontWeight.Bold;
        label.Foreground = new SolidColorBrush(Color.Parse("#E04F4F"));
        ToolTip.SetTip(label, reason);
    }

    /// <summary>点发送:先上屏并挂「发送中」,再把编号和正文交给服务端(服务端校验是不是好友)。</summary>
    private async void OnSendClick(object? sender, RoutedEventArgs e)
    {
        if (_removed)
        {
            return;
        }

        var text = InputBox.Text?.Trim() ?? "";
        if (text.Length == 0 || _peerId.Length == 0)
        {
            return;
        }

        var connect = ClientSession.Connect;
        if (connect is null)
        {
            AddNotice("还没连上服务端,发不出去");
            return;
        }

        InputBox.Text = "";
        var messageId = NewMessageId();
        var label = AddBubble(text, incoming: false);

        // 好友列表的预览不等链路回复,点发送就顶上去(本服跨服一样)
        FriendsWindow.Current?.ShowLocalPreview(_peerId, text);

        // 本服好友:直接交给聊天后端,不经过 400
        if (_channel.Length > 0)
        {
            var sentId = await ChatApi.SendAsync(_channel, text);
            if (sentId.Length == 0)
            {
                if (label is not null)
                {
                    MarkFailed(label, "没发出去");
                }

                FriendsWindow.Current?.ReleaseLocalPreview(_peerId);
                return;
            }

            _seen.Add(sentId);
            if (label is not null)
            {
                SetState(label, BubbleState.Sent);
            }

            return;
        }

        if (label is not null)
        {
            _pending[messageId] = label;
            _lastPendingId = messageId;
            _lastSender = this;
        }

        if (!await connect.SendAsync(Protocol.FriendMsg, FriendCodec.BuildFriendMessage(_peerId, messageId, text)))
        {
            if (label is not null)
            {
                MarkFailed(label, "没发出去");
                _pending.Remove(messageId);
                _lastPendingId = "";
            }

            FriendsWindow.Current?.ReleaseLocalPreview(_peerId);
        }
    }

    /// <summary>消息编号:只用来去重和对回执,不需要全局有序,取一小段随机串就够。</summary>
    private static string NewMessageId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>输入框里按回车等于点发送。</summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        OnSendClick(sender, new RoutedEventArgs());
    }

    /// <summary>窗口级的回车:焦点在输入框里时也算发送(别的时候不抢键)。</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !InputBox.IsFocused)
        {
            return;
        }

        e.Handled = true;
        OnSendClick(sender, new RoutedEventArgs());
    }

    /// <summary>点 ⋯:侧边栏滑出/收起;抽屉里的「保存」「取消」也绑在这个处理器上,先按按钮文字分流。</summary>
    private async void OnToggleSettings(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: TextBlock { Text: "保存" } })
        {
            await SaveRemarkAsync();
            return;
        }

        _settingsOpen = !_settingsOpen;
        if (_settingsOpen)
        {
            RemarkBox.Text = _remark;
        }

        DrawerHost.Width = _settingsOpen ? 286 : 0;
    }

    /// <summary>保存备注:本地先改显示,再把 665 发给服务端;失败原因由 666 回执带回来。</summary>
    private async Task SaveRemarkAsync()
    {
        var remark = (RemarkBox.Text ?? "").Trim();
        ApplyRemark(remark);

        var connect = ClientSession.Connect;
        if (connect is null || _peerId.Length == 0)
        {
            AddNotice("还没连上服务端,备注只改了本机显示");
            return;
        }

        //666 的负载里不带对方 ID,只能先记下是谁在等这个回执
        _remarkWaiter = this;
        if (!await connect.SendAsync(Protocol.FriendRemark, _peerId + "\t" + remark))
        {
            _remarkWaiter = null;
            AddNotice("备注没保存到服务端");
        }
    }

    /// <summary>好友备注变了:更新标题与名字行(有备注用备注,没备注用原名)。</summary>
    public void ApplyRemark(string remark)
    {
        _remark = (remark ?? "").Trim();
        FriendNameText.Text = DisplayName;
        RefreshTitle();
    }

    /// <summary>收到 666 备注回执:成功就让好友条重拉一遍(行名要跟着变),失败把原因摆出来。</summary>
    public void OnRemarkAck(string payload)
    {
        if (payload.StartsWith("ok", StringComparison.OrdinalIgnoreCase))
        {
            //备注已经落盘,好友条那一行的名字还停在旧值上,重拉一次才一致
            FriendsWindow.Current?.RefreshList();
            return;
        }

        if (payload.StartsWith("err|", StringComparison.OrdinalIgnoreCase))
        {
            AddNotice("备注没改成:" + payload[4..].Trim());
            return;
        }

        Logger.Log(LogLevel.Warning, $"[聊天] 认不出的备注回执:{payload}");
    }

    /// <summary>侧边栏里的「删除好友」。</summary>
    private async void OnRemoveFriendClick(object? sender, RoutedEventArgs e)
    {
        var connect = ClientSession.Connect;
        if (connect is null || _peerId.Length == 0)
        {
            return;
        }

        // 本服删好友会连 BareChat 频道一起删(消息没了);跨服记录在服务端 ChatLog 里,删好友不动它 —— 文案得分开说
        var keepHistory = _channel.Length == 0;
        var historyText = keepHistory ? "聊天记录会留着。" : "本服聊天记录会一起清掉。";
        if (!await MessageDialog.ConfirmAsync(this, "删除好友", $"把「{_peerName}」从好友列表里删掉?{historyText}"))
        {
            return;
        }

        if (await connect.SendAsync(Protocol.FriendRemove, _peerId))
        {
            ShowFriendRemoved();
            return;
        }

        // 没发出去就不能装作删掉了:下一次开窗照样能聊
        AddNotice("删好友没发出去,检查一下与服务端的连接");
    }

    /// <summary>好友关系已解除:停轮询、禁输入、留一句灰字提示。</summary>
    private void ShowFriendRemoved()
    {
        if (_removed)
        {
            return;
        }

        _removed = true;
        _poll?.Stop();
        _poll = null;
        InputBox.IsEnabled = false;
        AddNotice("你们已不是好友,聊天已停止");
    }

    /// <summary>好友关系没了:把对应的聊天窗锁死(窗口本身留着,聊天记录还能看)。</summary>
    public static void FriendRemoved(string peerFullId)
    {
        var target = (peerFullId ?? string.Empty).Trim();
        if (target.Length == 0)
        {
            return;
        }

        foreach (var window in Open)
        {
            if (string.Equals(window._peerId.Trim(), target, StringComparison.OrdinalIgnoreCase))
            {
                window.ShowFriendRemoved();
            }
        }
    }

    /// <summary>按住标题栏拖动窗口。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>最小化。</summary>
    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>最大化 / 还原。</summary>
    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>关窗。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // ---- 红包 / 发文件 / 图片预览这一轮不做:点了给一句提示,别让按钮看起来是坏的 ----

    /// <summary>发红包(未实现)。</summary>
    private void OnRedPacketClick(object? sender, RoutedEventArgs e) => AddNotice("红包还没做");

    /// <summary>发送文件(未实现)。</summary>
    private void OnSendFileClick(object? sender, RoutedEventArgs e) => AddNotice("发文件还没做");

    /// <summary>关预览层(这一轮不会打开)。</summary>
    private void OnPreviewCloseClick(object? sender, RoutedEventArgs e) => PreviewLayer.IsVisible = false;

    /// <summary>预览层滚轮(未实现)。</summary>
    private void OnPreviewWheel(object? sender, PointerWheelEventArgs e) => e.Handled = true;

    /// <summary>预览层双击(未实现)。</summary>
    private void OnPreviewDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e) => e.Handled = true;

    /// <summary>缩小(未实现)。</summary>
    private void OnZoomOutClick(object? sender, RoutedEventArgs e) { }

    /// <summary>重置缩放(未实现)。</summary>
    private void OnZoomResetClick(object? sender, RoutedEventArgs e) { }

    /// <summary>放大(未实现)。</summary>
    private void OnZoomInClick(object? sender, RoutedEventArgs e) { }
}
