using System;                                // Exception / StringComparison
using System.Collections.Generic;            // List / Dictionary:申请列表与名字缓存
using System.Linq;                           // Select / FirstOrDefault
using System.Threading.Tasks;                // Task / Task.Delay:发协议、等回执
using Avalonia;                              // Thickness
using Avalonia.Controls;                     // Window / Button / TextBlock / Grid
using Avalonia.Input;                        // PointerPressedEventArgs
using Avalonia.Interactivity;                // RoutedEventArgs
using Avalonia.Layout;                       // VerticalAlignment / HorizontalAlignment
using Avalonia.Media;                        // SolidColorBrush / Brushes
using Avalonia.Threading;                    // Dispatcher:回包不在 UI 线程时切回来
using ZincXManagerClient.ViewModels;         // ConnectViewModel
using ZincXManagerShared.Friend;             // FriendCodec / FriendRequestEntry / SearchHit
using ZincXManagerShared.Logging;            // Logger / LogLevel
using ZincXManagerShared.Network;            // Protocol

namespace ZincXManagerClient.Views;

/// <summary>
/// 好友通知窗:别人发来的申请一条一行,可以同意或拒绝。
///
/// <para>列表、同意、拒绝全部以服务端为准:开窗发 6056,同意/拒绝发 6058,
/// 6059 说成了才把这一行摘掉。客户端不判断「能不能加」「是不是好友」,那些是服务端的事。</para>
///
/// <para>657 里只有申请人的完整 ID 没有名字,所以每遇到一个没见过的申请人就用
/// 6063 查一次名字(6064 带编号回来),查不到就照旧显示完整 ID —— 不为显示名字新增协议。</para>
/// </summary>
public partial class FriendRequestsWindow : Window
{
    /// <summary>回执等多久还没来就放开按钮(网络断在半路时按钮不会一直灰着)。</summary>
    private const int AnswerTimeoutMs = 10000;

    /// <summary>662 可能连着来几条,拉列表按这个间隔合并,不跟着刷屏。</summary>
    private const int RefreshThrottleMs = 600;

    /// <summary>当前开着的通知窗(主窗收到 6057 时找它刷新)。</summary>
    public static FriendRequestsWindow? Current { get; private set; }

    private readonly List<FriendRequestEntry> _entries = [];                                  // 服务端最近一次给的全部申请(6057)
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase); // 申请人完整ID → 查到的名字
    private readonly Dictionary<ulong, string> _pendingNames = [];                            // 查名字的请求编号 → 申请人完整ID

    private string _shownSignature = "";   // 上一次画出来的申请列表签名,一样就不用重建
    private ulong _nextTicket;             // 本窗发请求用的编号,回包按它认领,不和别的窗抢
    private bool _loaded;                  // 收到过 657 没有:没有时显示「正在取」而不是「没有申请」
    private bool _answering;               // 有一条申请正在处理,挡住连点发出的第二个 658
    private ulong _answerTicket;           // 这一步 658 的请求编号,659 回来时按它认
    private FriendRequestEntry? _answeringEntry;  // 正在处理的那条,成功后就从列表里摘掉
    private bool _answerAgree;             // 这一步是同意还是拒绝,决定提示文案
    private long _lastQueryTicks;          // 上一次发 6056 的时刻,用于刷新节流

    public FriendRequestsWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);

        Current = this;
        Closed += (_, _) =>
        {
            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }
        };

        PaintRows(force: true);
        _ = QueryAsync();
    }

    /// <summary>服务端回了申请列表(6057)。</summary>
    public void FillRequests(string payload)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => FillRequests(payload));
            return;
        }

        var entries = FriendCodec.ParseFriendRequests(payload, out var badLines);
        if (badLines > 0)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 申请列表里有 {badLines} 行认不出来,已跳过");
        }

        _loaded = true;
        _entries.Clear();
        _entries.AddRange(entries);

        AskNamesForNewApplicants();
        PaintRows(force: false);
    }

    /// <summary>
    /// 收到 6062(对方同意 / 撤销申请)后重拉一次列表。
    /// 662 会连着来,600 毫秒内只发一个 656。
    /// </summary>
    public void RefreshRequests()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshRequests);
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastQueryTicks < RefreshThrottleMs)
        {
            return;
        }

        _lastQueryTicks = now;
        _ = QueryAsync();
    }

    /// <summary>
    /// 6064 搜索结果回来了,如果是我为申请人名字发的那次就吃掉它。
    /// 返回 true = 这条 664 归本窗,主窗不要再转给加好友窗。
    /// </summary>
    public bool TakeSearchReply(ulong packetId, string payload)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => TakeSearchReply(packetId, payload));
            return true;
        }

        if (!_pendingNames.Remove(packetId, out var fullId))
        {
            return false;
        }

        var hits = FriendCodec.ParseSearchHits(payload, out _);
        var hit = hits.FirstOrDefault(one => string.Equals(one.Id, fullId, StringComparison.OrdinalIgnoreCase));
        // 服务端查不到真人时名字段是 "#编号" 这种占位,拿它当名字比完整 ID 还难认
        if (hit is null || hit.Name.Length == 0 || hit.Name.StartsWith('#'))
        {
            Logger.Log(LogLevel.Info, $"[好友] 查不到 {fullId} 的名字,申请行照旧显示完整 ID");
            return true;
        }

        _names[fullId] = hit.Name;
        PaintRows(force: true);
        return true;
    }

    /// <summary>
    /// 6059 回执回来了。返回 true = 这条归本窗(已经弹了提示),主窗不要再走通用的「操作没成功」弹窗。
    /// </summary>
    public bool HandleAnswerAck(ulong packetId, string payload)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => HandleAnswerAck(packetId, payload));
            return true;
        }

        if (!_answering)
        {
            return false;
        }

        // 老服务端可能回 0;此刻只有一条在飞,认它也不会错
        if (packetId != 0 && packetId != _answerTicket)
        {
            return false;
        }

        var ok = payload.StartsWith("ok", StringComparison.OrdinalIgnoreCase);
        var reason = payload.StartsWith("err|", StringComparison.OrdinalIgnoreCase) ? payload[4..] : payload;
        var entry = _answeringEntry;
        var agree = _answerAgree;

        _answering = false;
        _answeringEntry = null;

        if (!ok)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 回申请没成功: {payload}");
            _ = ShowIfOpen(MessageDialog.WarnAsync(this, "操作没成功", reason.Length > 0 ? reason : "服务端没说原因。"));
            // 列表可能和服务端已经不一致(比如这条被别人处理了),重拉一次对齐
            _ = QueryAsync();
            return true;
        }

        if (entry is not null)
        {
            RemoveEntry(entry);
        }

        Logger.Log(LogLevel.Info, $"[好友] 已{(agree ? "同意" : "拒绝")} {(entry?.FromId ?? "?")} 的申请");
        FriendsWindow.Current?.RefreshAll();
        _ = QueryAsync();
        _ = ShowIfOpen(MessageDialog.InfoAsync(
            this,
            agree ? "已同意" : "已拒绝",
            agree ? "已经把他加进好友列表了。" : "已回绝这条申请。"));
        return true;
    }

    /// <summary>画一条申请:左头像、中间「谁想加你 + 他写的什么 + 什么时候 / ID」、右边同意/拒绝。</summary>
    private Control BuildRow(FriendRequestEntry entry)
    {
        var known = _names.TryGetValue(entry.FromId, out var resolved) && resolved.Length > 0;
        var name = known ? resolved! : entry.FromId.Length > 0 ? entry.FromId : "有人";

        var title = new TextBlock
        {
            Text = name + " 想加你好友",
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#1F2A36")),
        };

        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        text.Children.Add(title);

        if (entry.Message.Length > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = entry.Message,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#4A5B6B")),
            });
        }

        // 名字查到了才需要单独一行报 ID;没查到的话 ID 就是标题,再写一遍是重复
        var meta = new List<string>();
        if (entry.Time.Length > 0)
        {
            meta.Add(entry.Time);
        }

        if (known)
        {
            meta.Add(entry.FromId);
        }

        if (meta.Count > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", meta),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse("#8899A6")),
            });
        }

        var agree = new Button { Content = "同意", MinWidth = 52, MinHeight = 26 };
        var refuse = new Button { Content = "拒绝", MinWidth = 52, MinHeight = 26 };
        agree.Click += async (_, _) => await AnswerAsync(entry, agree: true, agree, refuse);
        refuse.Click += async (_, _) => await AnswerAsync(entry, agree: false, agree, refuse);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        buttons.Children.Add(agree);
        buttons.Children.Add(refuse);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(InitialAvatar(name));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);

        var row = new Border { Child = grid, Padding = new Thickness(8, 7, 8, 7), CornerRadius = new CornerRadius(6) };
        row.Classes.Add("requestRow");
        return row;
    }

    /// <summary>默认头像:青绿底 + 白色首字。</summary>
    private static Control InitialAvatar(string name) => new Border
    {
        Width = 36,
        Height = 36,
        Background = new SolidColorBrush(Color.Parse("#4E9C9C")),
        Child = new TextBlock
        {
            Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>重画整个列表。签名没变就不重建,免得滚动条跳回顶部。</summary>
    private void PaintRows(bool force)
    {
        var signature = string.Join("\n", _entries.Select(one =>
            one.FromId + "\t" + one.Message + "\t" + one.Time + "\t" +
            (_names.TryGetValue(one.FromId, out var name) ? name : "")));

        if (force || !string.Equals(signature, _shownSignature, StringComparison.Ordinal))
        {
            _shownSignature = signature;
            RequestList.Children.Clear();
            foreach (var entry in _entries)
            {
                RequestList.Children.Add(BuildRow(entry));
            }
        }

        UpdateEmptyState();
    }

    /// <summary>列表为空时把空态显示出来:没连上 / 还在取 / 真没有,三句话别混着说。</summary>
    private void UpdateEmptyState()
    {
        EmptyState.IsVisible = _entries.Count == 0;
        if (!EmptyState.IsVisible)
        {
            return;
        }

        if (ClientSession.Connect is null)
        {
            EmptyTitle.Text = "还没连上服务端";
            EmptyHint.Text = "连上以后会自动取申请列表";
        }
        else if (!_loaded)
        {
            EmptyTitle.Text = "正在取申请列表…";
            EmptyHint.Text = "稍等一下";
        }
        else
        {
            EmptyTitle.Text = "没有待处理的好友申请";
            EmptyHint.Text = "别人发来的申请会显示在这里";
        }
    }

    /// <summary>把新出现的申请人拿 663 查一次名字(657 不带名字)。</summary>
    private void AskNamesForNewApplicants()
    {
        if (ClientSession.Connect is not { } connect)
        {
            return;
        }

        foreach (var entry in _entries)
        {
            if (_names.ContainsKey(entry.FromId) || _pendingNames.ContainsValue(entry.FromId))
            {
                continue;
            }

            var ticket = ++_nextTicket;
            _pendingNames[ticket] = entry.FromId;
            _ = AskNameAsync(connect, ticket, entry.FromId);
        }
    }

    /// <summary>发一次 663 查名字;失败就把这条从待查里去掉,下次 657 再试。</summary>
    private async Task AskNameAsync(ConnectViewModel connect, ulong ticket, string fullId)
    {
        try
        {
            await connect.SendAsync(Protocol.FriendSearch, fullId, ticket);
        }
        catch (Exception ex)
        {
            _pendingNames.Remove(ticket);
            Logger.Log(LogLevel.Warning, $"[好友] 查 {fullId} 的名字失败: {ex.Message}");
        }
    }

    /// <summary>点同意/拒绝:发 6058,等 6059 回来才算成。同一时刻只放一条申请过去。</summary>
    private async Task AnswerAsync(FriendRequestEntry entry, bool agree, Button agreeButton, Button refuseButton)
    {
        if (_answering)
        {
            return;
        }

        if (ClientSession.Connect is not { } connect)
        {
            _ = ShowIfOpen(MessageDialog.WarnAsync(this, "还没连上服务端", "等重新连上服务端后再试。"));
            return;
        }

        // 手快点两下会发两个 658,第二个必然拿到「没有这条申请」,用户看到的就是同意成功却弹失败
        _answering = true;
        _answeringEntry = entry;
        _answerAgree = agree;
        _answerTicket = ++_nextTicket;
        agreeButton.IsEnabled = false;
        refuseButton.IsEnabled = false;

        try
        {
            await connect.SendAsync(Protocol.FriendAnswer, FriendCodec.BuildFriendAnswer(entry.FromId, agree), _answerTicket);
            Logger.Log(LogLevel.Info, $"[好友] 已把「{(agree ? "同意" : "拒绝")} {entry.FromId}」发给服务端(6058)");
            _ = WatchAnswerAsync(_answerTicket);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 回申请失败: {ex.Message}");
            _answering = false;
            _answeringEntry = null;
            agreeButton.IsEnabled = true;
            refuseButton.IsEnabled = true;
            _ = ShowIfOpen(MessageDialog.WarnAsync(this, "操作没发出去", "连接不在了,等重新连上服务端后再试。"));
        }
    }

    /// <summary>回执迟迟不来就放开按钮并重拉列表,不然这一行会永远点不动。</summary>
    private async Task WatchAnswerAsync(ulong ticket)
    {
        await Task.Delay(AnswerTimeoutMs);
        if (!_answering || _answerTicket != ticket)
        {
            return;
        }

        _answering = false;
        _answeringEntry = null;
        Logger.Log(LogLevel.Warning, "[好友] 回申请的 6059 回执没在 10 秒内到,先放开按钮并重拉列表");
        PaintRows(force: true);
        await QueryAsync();
    }

    /// <summary>把处理掉的那一条从列表里摘掉:服务端不会推空列表(657 空负载不发),只能自己摘。</summary>
    private void RemoveEntry(FriendRequestEntry entry)
    {
        if (_entries.RemoveAll(one => string.Equals(one.FromId, entry.FromId, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            PaintRows(force: true);
        }
    }

    /// <summary>向服务端要一次申请列表(6056)。</summary>
    private async Task QueryAsync()
    {
        if (ClientSession.Connect is not { } connect)
        {
            UpdateEmptyState();
            return;
        }

        try
        {
            await connect.SendAsync(Protocol.FriendRequestsQuery, "");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 取申请列表失败: {ex.Message}");
        }
    }

    /// <summary>窗已经关了就别再弹提示(否则在已关闭的窗口上开会抛)。</summary>
    private Task ShowIfOpen(Task dialog) => IsVisible ? dialog : Task.CompletedTask;

    /// <summary>按住标题栏拖动窗口。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>关窗。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
