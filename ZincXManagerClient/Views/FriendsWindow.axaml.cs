using System;                                // DateTime / Exception
using System.Collections.Generic;            // List / Dictionary:好友列表与临时预览
using System.Linq;                           // Where / FirstOrDefault
using System.Threading.Tasks;                // Task:要头像
using Avalonia;                              // Thickness / HorizontalAlignment
using Avalonia.Controls;                     // Window / Border / TextBlock / Grid
using Avalonia.Input;                        // PointerPressedEventArgs
using Avalonia.Interactivity;                // RoutedEventArgs
using Avalonia.Layout;                       // VerticalAlignment
using Avalonia.Media;                        // SolidColorBrush / Brushes
using Avalonia.Media.Imaging;                // Bitmap:好友头像
using Avalonia.Threading;                    // Dispatcher:头像到了重画
using ZincXManagerClient.Chat;               // ChatBridge / ChatApi:本服对话的最后一句与未读
using ZincXManagerClient.ViewModels;         // ConnectViewModel:发协议
using ZincXManagerShared.Account;            // AvatarStore:本地头像缓存
using ZincXManagerShared.Chat;               // ChatChannel:私聊频道号
using ZincXManagerShared.Friend;             // FriendCodec / ChatLog / FriendEntry
using ZincXManagerShared.Logging;            // Logger / LogLevel
using ZincXManagerShared.Network;            // Protocol

namespace ZincXManagerClient.Views;

/// <summary>
/// 好友列表窗。
///
/// <para>它只做三件事:把服务端给的好友列表画出来(650/651)、按关键词过滤、开别的窗。
/// 「谁是我的好友」「谁在线」全部以服务端下发的为准,这里不自己判定。</para>
/// </summary>
public partial class FriendsWindow : Window
{
    /// <summary>当前开着的列表窗(主窗收到推送时找它刷新)。</summary>
    public static FriendsWindow? Current { get; private set; }

    private readonly List<FriendEntry> _friends = [];   // 服务端下发的完整列表
    private readonly Dictionary<string, (string Text, long At)> _previewHold = new(StringComparer.OrdinalIgnoreCase);   // 刚发出去、还没被轮询证实的预览,键=好友完整ID
    private readonly Dictionary<string, DateTime> _unreadClearedAt = new(StringComparer.OrdinalIgnoreCase);   // 红点刚被清掉的时间,挡住并发刷新把 0 盖回去
    private string _keyword = "";                        // 搜索框里的字
    private DispatcherTimer? _preview;                   // 本服对话的「最后一句 + 未读」定时补
    private DateTime _lastListAt;                        // 上次发 650 的时间戳,用来节流
    private bool _previewBusy;                           // 预览刷新正在跑,挡住重入(否则旧快照会盖掉新值)
    private readonly Dictionary<string, long> _recentAt = new(StringComparer.OrdinalIgnoreCase);   // 本机记下的最后消息时间(unix 秒),键=好友完整ID,和服务端给的时间取大的那个用来排序

    /// <summary>红点清掉后压住未读的秒数:必须盖住 5 秒轮询周期,不然旧值下一轮又冒出来。</summary>
    private const int UnreadHoldSeconds = 8;

    /// <summary>本地预览最多顶多久:消息其实没送出去时没人来撤,不设上限那句假预览会一直挂在列表上。</summary>
    private const int PreviewHoldMs = 30_000;

    private AddFriendWindow? _addFriendWindow;           // 开着的「添加好友」窗(同一个时间只留一个)
    private FriendRequestsWindow? _requestsWindow;       // 开着的「好友通知」窗

    public FriendsWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);

        Current = this;
        Closed += (_, _) =>
        {
            _preview?.Stop();
            _preview = null;
            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }
        };

        // 未读数是聊天后端那边变的,服务端不会推 651 通知我们,只能自己隔几秒问一次
        _preview = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _preview.Tick += async (_, _) => await RefreshLocalPreviewsAsync();
        _preview.Start();

        ShowSelf();
        RefreshAll();
    }

    /// <summary>重新向服务端要一次好友列表和申请列表(刚开窗时、以及收到推送时调)。</summary>
    public void RefreshAll()
    {
        if (ClientSession.Connect is not { } connect)
        {
            return;
        }

        // 列表统一走 RefreshList(带节流),申请列表只在开窗和事件里发,量小不用节流
        _ = SendAsync(connect, Protocol.FriendRequestsQuery);
        RefreshList();
    }

    /// <summary>只重发一次 650 重拉好友列表,不重建窗口;同一秒内重复调用只发一次(跨服回执可能连着来)。</summary>
    public void RefreshList()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastListAt).TotalSeconds < 1)
        {
            return;
        }

        _lastListAt = now;

        if (ClientSession.Connect is not { } connect)
        {
            return;
        }

        Logger.Log(LogLevel.Info, "[好友] 列表刷新:重新向服务端要好友列表(650)");
        _ = SendAsync(connect, Protocol.FriendListQuery);
    }

    /// <summary>服务端回了好友列表(651)。</summary>
    public void FillList(string payload)
    {
        var entries = FriendCodec.ParseFriendList(payload, out var badLines);
        if (badLines > 0)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 好友列表里有 {badLines} 行认不出来,已跳过");
        }

        _friends.Clear();
        _friends.AddRange(entries);
        // 已经不存在的行别再占着时间戳,否则重新加回来时会莫名其妙顶到最前
        foreach (var gone in _recentAt.Keys
                     .Where(key => !entries.Any(one => string.Equals(one.Id, key, StringComparison.OrdinalIgnoreCase)))
                     .ToArray())
        {
            _recentAt.Remove(gone);
        }

        Resort();

        Logger.Log(LogLevel.Info, $"[好友] 列表刷新:{_friends.Count} 条");

        ApplyLocalOverrides();
        Paint();
        _ = AskMissingAvatarsAsync();
        _ = RefreshLocalPreviewsAsync();
    }

    /// <summary>某个好友刚有消息:记下时间,把他这一行按时间排到该在的位置并重画。</summary>
    public void Touch(string friendFullId)
    {
        if (friendFullId.Length == 0)
        {
            return;
        }

        _recentAt[friendFullId] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Resort();
        Paint();
    }

    /// <summary>本机记下的时间(自己刚发出去的那条),和服务端给的时间取大的那个。</summary>
    private long StampOf(string friendFullId) =>
        _recentAt.TryGetValue(friendFullId, out var stamp) ? stamp : 0;

    /// <summary>排序:按最后一条消息的时间倒序,没有消息的排后面,再按在线、显示名,免得顺序乱跳。</summary>
    private void Resort()
    {
        _friends.Sort((left, right) =>
        {
            var leftAt = Math.Max(left.LastAt, StampOf(left.Id));
            var rightAt = Math.Max(right.LastAt, StampOf(right.Id));
            if (leftAt != rightAt)
            {
                return rightAt.CompareTo(leftAt);
            }

            if (left.Online != right.Online)
            {
                return right.Online.CompareTo(left.Online);
            }

            return string.Compare(DisplayNameOf(left), DisplayNameOf(right), StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// 本服好友的「最后一句 + 未读」去聊天后端补。
    ///
    /// <para>服务端 651 只管「谁是我的好友」,本服对话的正文已经搬到聊天后端了,
    /// 所以这里以聊天后端为准把这两列改掉;跨服好友仍用服务端给的值。</para>
    /// </summary>
    private async Task RefreshLocalPreviewsAsync()
    {
        if (!ChatBridge.Ready)
        {
            return;
        }

        // 三个地方都会叫它(5 秒定时器 / 列表刷新后 / 撤掉本地预览时),而它要发 N+1 个 HTTP。
        // 上一次还没回来就又起一次,后回来的那次会拿旧快照把新值盖掉 —— 直接放弃这一轮,下一轮自然会补
        if (_previewBusy)
        {
            return;
        }

        _previewBusy = true;
        try
        {
            await RefreshLocalPreviewsCoreAsync();
        }
        finally
        {
            _previewBusy = false;
        }
    }

    /// <summary>真正干活的半边:拼本服好友列表、取未读与最后一句、写回行里。</summary>
    private async Task RefreshLocalPreviewsCoreAsync()
    {
        var locals = new List<(string Id, int Code, string Name)>();
        foreach (var entry in _friends)
        {
            if (PlayerId.TryParse(entry.Id, out var parsed) && parsed.ServerId == ChatBridge.ServerId)
            {
                locals.Add((entry.Id, parsed.Code, DisplayNameOf(entry)));
            }
        }

        if (locals.Count == 0)
        {
            return;
        }

        var unread = await ChatApi.UnreadAsync();
        var results = new (string Id, string Preview, int Unread, long At)[locals.Count];
        await Task.WhenAll(locals.Select(async (one, index) =>
        {
            var channel = ChatChannel.ForPair(ChatBridge.SelfCode, one.Code);
            var last = await ChatApi.LastAsync(channel);
            // 对方发的带上他的名字,自己发的只给正文
            var preview = last is null
                ? ""
                : int.TryParse(last.SenderId, out var sender) && sender == ChatBridge.SelfCode
                    ? last.Text
                    : one.Name + "：" + last.Text;
            // 本服对话的时间只有聊天后端知道,服务端那份聊天记录是空的
            var at = last is null ? 0L : new DateTimeOffset(last.At.ToUniversalTime()).ToUnixTimeSeconds();
            results[index] = (one.Id, preview, unread.TryGetValue(channel, out var count) ? count : 0, at);
        }));

        var changed = false;   //有没有真的变了,没变就不用重画(免得每 5 秒闪一下)
        foreach (var (id, polledPreview, polledCount, polledAt) in results)
        {
            var index = _friends.FindIndex(one => string.Equals(one.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                continue;
            }

            var preview = polledPreview;
            // 刚发出去的那句还在等回执:轮询拿回来的多半还是旧预览,先按本地这句顶着
            if (_previewHold.TryGetValue(id, out var hold))
            {
                if (string.Equals(preview, hold.Text, StringComparison.Ordinal) || Environment.TickCount64 - hold.At >= PreviewHoldMs)
                {
                    _previewHold.Remove(id);   //轮询已经追上了,或者顶太久了(多半根本没送出去)
                }
                else
                {
                    preview = hold.Text;
                }
            }

            // 红点刚清掉:这次轮询拿到的未读数可能还是清之前的,按住一会儿(要盖住整个轮询周期,不然 5 秒后旧值又冒出来)
            var count = polledCount;
            if (_unreadClearedAt.TryGetValue(id, out var clearedAt))
            {
                if ((DateTime.UtcNow - clearedAt).TotalSeconds < UnreadHoldSeconds)
                {
                    count = 0;
                }
                else
                {
                    _unreadClearedAt.Remove(id);
                }
            }

            var before = _friends[index];
            var nextAt = polledAt > before.LastAt ? polledAt : before.LastAt;
            if (string.Equals(before.Preview, preview, StringComparison.Ordinal) && before.Unread == count && nextAt == before.LastAt)
            {
                continue;
            }

            _friends[index] = before with { Preview = preview, Unread = count, LastAt = nextAt };
            changed = true;
        }

        if (changed)
        {
            Resort();
            Dispatcher.UIThread.Post(Paint);
        }
    }

    /// <summary>服务端回了申请列表(657):列表窗不用它画,只用来决定通知红点亮不亮。</summary>
    public void FillRequests(string payload)
    {
        var entries = FriendCodec.ParseFriendRequests(payload, out _);
        NoticeDot.IsVisible = entries.Count > 0;
    }

    /// <summary>某个好友的在线状态变了(662 里的 online / offline)。</summary>
    public void SetOnline(string friendFullId, bool online)
    {
        var index = _friends.FindIndex(entry => string.Equals(entry.Id, friendFullId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }

        _friends[index] = _friends[index] with { Online = online };
        Paint();
    }

    /// <summary>651 刚回来时把本地的临时状态盖回去:跨服好友只有 651 这一个来源,不盖就会被旧值顶掉。</summary>
    private void ApplyLocalOverrides()
    {
        for (var index = 0; index < _friends.Count; index++)
        {
            var entry = _friends[index];
            var preview = entry.Preview;

            // 刚发出去的那句还在等回执:服务端给的预览可能还是旧的
            if (_previewHold.TryGetValue(entry.Id, out var hold))
            {
                if (string.Equals(preview, hold.Text, StringComparison.Ordinal) || Environment.TickCount64 - hold.At >= PreviewHoldMs)
                {
                    _previewHold.Remove(entry.Id);   //服务端已经追上了,或者顶太久了
                }
                else
                {
                    preview = hold.Text;
                }
            }

            // 红点刚清掉:正在飞的那次 651 可能带回清之前的未读数,按住一会儿
            var count = entry.Unread;
            if (_unreadClearedAt.TryGetValue(entry.Id, out var clearedAt))
            {
                if ((DateTime.UtcNow - clearedAt).TotalSeconds < UnreadHoldSeconds)
                {
                    count = 0;
                }
                else
                {
                    _unreadClearedAt.Remove(entry.Id);
                }
            }

            if (!string.Equals(entry.Preview, preview, StringComparison.Ordinal) || entry.Unread != count)
            {
                _friends[index] = entry with { Preview = preview, Unread = count };
            }
        }
    }

    /// <summary>发送瞬间把好友行的「最后一句」顶上刚发出去的正文,不等链路回执。</summary>
    public void ShowLocalPreview(string friendFullId, string text)
    {
        // 先记下来:轮询如果跑在回执前面,拿回来的还是旧的一句
        _previewHold[friendFullId] = (text, Environment.TickCount64);

        var index = _friends.FindIndex(entry => string.Equals(entry.Id, friendFullId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }

        _friends[index] = _friends[index] with { Preview = text };
        // 自己刚发出一条:按这条的时间重排,和对方发来一样
        _recentAt[friendFullId] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Resort();
        Paint();
    }

    /// <summary>没发出去:撤掉临时预览,让轮询把真实的一句拿回来。</summary>
    public void ReleaseLocalPreview(string friendFullId)
    {
        _previewHold.Remove(friendFullId);
        _ = RefreshLocalPreviewsAsync();
    }

    /// <summary>打开会话:把这个好友的未读红点清掉并立刻重画。</summary>
    public void ClearUnread(string friendFullId)
    {
        var index = _friends.FindIndex(entry => string.Equals(entry.Id, friendFullId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }

        // 记时间:正在飞的那次轮询可能带回清之前的未读数,两秒内一律按 0 压住
        _unreadClearedAt[friendFullId] = DateTime.UtcNow;
        _friends[index] = _friends[index] with { Unread = 0 };
        Paint();
    }

    /// <summary>按搜索框过滤并重画整个列表。</summary>
    private void Paint()
    {
        FriendListPanel.Children.Clear();

        var visible = _friends.Where(Matches).ToList();
        foreach (var entry in visible)
        {
            FriendListPanel.Children.Add(BuildRow(entry));
        }

        FriendsEmptyText.IsVisible = visible.Count == 0;
        FriendsEmptyText.Text = _friends.Count == 0 ? "还没有好友,点右上角 + 加一个" : "没有匹配的好友";
    }

    /// <summary>关键词命中:名字、备注或完整 ID 里包含就算;空关键词全显。</summary>
    private bool Matches(FriendEntry entry)
    {
        if (_keyword.Length == 0)
        {
            return true;
        }

        return entry.Id.Contains(_keyword, StringComparison.OrdinalIgnoreCase)
            || entry.Name.Contains(_keyword, StringComparison.OrdinalIgnoreCase)
            || entry.Remark.Contains(_keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>画一条好友:左头像、右边名字、名字下面最后一句。</summary>
    private Control BuildRow(FriendEntry entry)
    {
        var title = new TextBlock
        {
            Text = DisplayNameOf(entry),
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#1F2A36")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var preview = new TextBlock
        {
            Text = entry.Preview,
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.Parse("#8899A6")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        text.Children.Add(title);
        text.Children.Add(preview);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(AvatarOf(entry, 38));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // 未读数是服务端算好的:对方发来的条数 - 我读到第几号
        if (entry.Unread > 0)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#E04F4F")),
                CornerRadius = new CornerRadius(8),
                MinWidth = 16,
                Height = 16,
                Padding = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = entry.Unread > 99 ? "99+" : entry.Unread.ToString(),
                    FontSize = 9,
                    Foreground = Brushes.White,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
        }

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(6),
            Opacity = entry.Online ? 1 : 0.45,
        };
        row.Classes.Add("friendRow");
        row.DoubleTapped += (_, _) => OpenChat(entry);
        ToolTip.SetTip(row, entry.Id + (entry.ServerName.Length > 0 ? "\n来自 " + entry.ServerName : ""));
        return row;
    }

    /// <summary>显示名:有备注用备注,否则用账户名,再没有就用完整 ID。</summary>
    private static string DisplayNameOf(FriendEntry entry) =>
        entry.Remark.Length > 0 ? entry.Remark : entry.Name.Length > 0 ? entry.Name : entry.Id;

    /// <summary>好友头像:本地有图就用图,没有就画「青绿底 + 首字」。</summary>
    private static Control AvatarOf(FriendEntry entry, double size)
    {
        var key = AvatarKeyOf(entry);
        var image = AvatarStore.Load(key);
        if (image.Length > 0)
        {
            return ImageAvatar(image, size);
        }

        return InitialAvatar(entry.Name.Length > 0 ? entry.Name : entry.Id, size);
    }

    /// <summary>头像缓存用的键:优先账户名(跨服也唯一),没有名字时退回完整 ID。</summary>
    private static string AvatarKeyOf(FriendEntry entry) => entry.Name.Length > 0 ? entry.Name : entry.Id;

    private static Control ImageAvatar(byte[] image, double size) => new Border
    {
        Width = size,
        Height = size,
        ClipToBounds = true,
        Child = new Image
        {
            Source = new Bitmap(new System.IO.MemoryStream(image)),
            Stretch = Stretch.UniformToFill,
        },
    };

    /// <summary>默认头像:青绿底 + 白色首字。</summary>
    private static Control InitialAvatar(string name, double size) => new Border
    {
        Width = size,
        Height = size,
        Background = new SolidColorBrush(Color.Parse("#4E9C9C")),
        Child = new TextBlock
        {
            Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?",
            FontSize = size * 0.42,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };


    /// <summary>自己的头像:有图用图,没图用账户名首字。</summary>
    private void ShowSelf()
    {
        if (ClientSession.User is not { Length: > 0 } self)
        {
            return;
        }

        SelfNameText.Text = self;
        SelfAvatarInitial.Text = self[..1].ToUpperInvariant();

        var image = AvatarStore.Load(self);
        if (image.Length == 0)
        {
            return;
        }

        SelfAvatarImage.Source = new Bitmap(new System.IO.MemoryStream(image));
        SelfAvatarImage.IsVisible = true;
        SelfAvatarInitial.IsVisible = false;
        SelfAvatarBox.Background = Brushes.Transparent;
    }

    /// <summary>本地没有谁的头像就挨个问服务端要;失败只记一条日志,下次开窗再问。</summary>
    private async Task AskMissingAvatarsAsync()
    {
        if (ClientSession.Connect is not { } connect)
        {
            return;
        }

        foreach (var entry in _friends.ToList())
        {
            if (AvatarStore.Load(AvatarKeyOf(entry)).Length > 0)
            {
                continue;
            }

            // 负载是「完整ID + 账户名」:服务端先按完整 ID 认人,再按名字找图
            if (!await SendAsync(connect, Protocol.FriendAvatarReq, entry.Id + "|" + entry.Name))
            {
                return;
            }
        }
    }

    /// <summary>服务端送来了头像(608):存到本地缓存,然后重画。</summary>
    public void SaveAvatar(string accountName, byte[] image)
    {
        if (accountName.Length == 0 || image.Length == 0)
        {
            return;
        }

        if (!AvatarStore.Save(accountName, image, out var error))
        {
            Logger.Log(LogLevel.Warning, $"[好友] 头像存不下来({accountName}): {error}");
            return;
        }

        Dispatcher.UIThread.Post(Paint);
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _keyword = SearchBox.Text?.Trim() ?? "";
        Paint();
    }

    /// <summary>点「+」:开添加好友窗,交给 Avalonia 以上一个窗口为中心弹出。</summary>
    private void OnAddFriendClick(object? sender, RoutedEventArgs e)
    {
        // 连点两下会开出两个窗,而申请结果只投给最后一个 —— 已经开着就把它提到前面
        if (_addFriendWindow is { } exist)
        {
            exist.Activate();
            return;
        }

        // Avalonia 12 的 Owner 是 protected,外面只能走 Show(owner);它先记下 owner,CenterOwner 才有参照物
        var window = new AddFriendWindow { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        _addFriendWindow = window;
        window.Closed += (_, _) => _addFriendWindow = null;
        window.Show(this);
    }

    /// <summary>点「好友通知」:开申请窗,顺手把红点清掉(申请窗会自己拉最新列表)。</summary>
    private void OnNotificationsClick(object? sender, RoutedEventArgs e)
    {
        if (_requestsWindow is { } exist)
        {
            exist.Activate();
            NoticeDot.IsVisible = false;
            return;
        }

        var window = new FriendRequestsWindow { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        _requestsWindow = window;
        window.Closed += (_, _) => _requestsWindow = null;
        window.Show(this);
        NoticeDot.IsVisible = false;
    }

    /// <summary>双击好友:开聊天窗,以好友条为中心弹出。已经开着就直接提到前面,不为同一个人开第二个窗。</summary>
    private void OpenChat(FriendEntry entry)
    {
        if (ChatWindow.ForPeer(entry.Id) is { } exist)
        {
            exist.Activate();
            return;
        }

        var window = new ChatWindow(entry.Id, DisplayNameOf(entry), entry.ServerName, entry.Name);
        // 不设 Owner:聊天窗要有自己的任务栏项、能单独最小化,好友条关了也不该跟着关
        WindowPlacement.PlaceAt(window, this);
        window.Show();
    }

    /// <summary>发一个包;连不上或写失败返回 false(调用方自己决定要不要提示)。</summary>
    private static async Task<bool> SendAsync(ConnectViewModel connect, ushort type, string payload = "")
    {
        try
        {
            return await connect.SendAsync(type, payload);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 发 {type} 失败: {ex.Message}");
            return false;
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

    /// <summary>关窗。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
