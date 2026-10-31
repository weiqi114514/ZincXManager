using System;  // DateTime / StringComparison
using System.IO;  // MemoryStream:头像字节转图片
using Avalonia.Controls;  // 窗口与控件
using Avalonia.Input;  // 鼠标键盘输入
using Avalonia.Layout;  // 布局对齐
using Avalonia.Media;  // FontWeight / 画刷
using Avalonia.Media.Imaging;  // Bitmap:账号头像
using ZincXManagerShared.Account;  // AvatarStore:头像缓存
using ZincXManagerShared.Network;  // Protocol / 连接实例

namespace ZincXManagerClient.Views;

/// <summary>好友条:单独的小窗体(类似老版 QQ / Steam 的好友条),选好友后再开聊天窗体。</summary>
public partial class FriendsWindow : Window
{
    /// <summary>当前打开着的好友条(同时只允许一个),主窗口收到 651 时往这里塞数据。</summary>
    internal static FriendsWindow? Current;

    public FriendsWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);

        //无边框窗口自己处理边缘拖拽:好友条只允许上下拉长,宽度锁死
        WindowResizeHelper.Attach(this, horizontal: false);

        //登录态在 ClientSession 里,这里只负责把名字与头像显示出来
        SelfNameText.Text = string.IsNullOrWhiteSpace(ClientSession.User) ? "未登录" : ClientSession.User;
        ShowSelfAvatar();

        Opened += (_, _) =>
        {
            Current = this;
            _ = RefreshAsync();
        };
        Closed += (_, _) =>
        {
            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }
        };
    }

    /// <summary>向服务端要好友列表(650),回包在主窗口那边转成 FillList。</summary>
    internal async System.Threading.Tasks.Task RefreshAsync()
    {
        var connect = ClientSession.Connect;   //当前连接
        if (connect is null)
        {
            return;
        }

        try
        {
            await connect.SendAsync(Protocol.FriendListQuery, string.Empty);
        }
        catch
        {
            //连接没了就算了,下次打开再拉
        }
    }

    /// <summary>好友条的右键菜单:只有一个「删除好友」。</summary>
    private MenuFlyout BuildFriendMenu(PlayerId id, string remark)
    {
        var remove = new MenuItem { Header = "删除好友" };
        remove.Click += async (_, _) => await RemoveFriendAsync(id, remark);

        var flyout = new MenuFlyout();
        flyout.Items.Add(remove);
        return flyout;
    }

    /// <summary>确认后发 660 删好友,两边列表由服务端一起删。</summary>
    private async System.Threading.Tasks.Task RemoveFriendAsync(PlayerId id, string remark)
    {
        var name = remark.Length > 0 ? remark : id.Full;   //二次确认里显示的名字
        var yes = await MessageDialog.ConfirmAsync(this, "删除好友", $"确定把「{name}」从好友里删掉吗?两边的好友列表都会删。", "删除", "算了");
        if (!yes)
        {
            return;
        }

        var connect = ClientSession.Connect;   //当前连接
        if (connect is null)
        {
            return;
        }

        try
        {
            await connect.SendAsync(Protocol.FriendRemove, id.Full);
        }
        catch
        {
            //连接断了,661 不会来,等下次打开好友窗再刷
        }
    }

    /// <summary>把 651 的负载(&lt;完整玩家ID&gt;	&lt;备注&gt; 按行)解析成好友条。</summary>
    internal void FillList(string payload)
    {
        FriendListPanel.Children.Clear();

        var count = 0;   //有效好友数
        foreach (var raw in payload.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("err|", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split('\t');   //[0]=完整ID [1]=备注
            if (!PlayerId.TryParse(parts[0].Trim(), out var id))
            {
                continue;
            }

            var remark = parts.Length > 1 ? parts[1].Trim() : "";   //备注
            FriendListPanel.Children.Add(BuildRow(id, remark));
            count++;
        }

        FriendsEmptyText.IsVisible = count == 0;
        FriendsEmptyText.Text = count == 0 ? "还没有好友,点右上角 + 加一个" : "";
    }

    /// <summary>一条好友:方形头像 + 名字(有备注用备注) + 完整 ID。</summary>
    private Control BuildRow(PlayerId id, string remark)
    {
        var initial = new TextBlock
        {
            Text = remark.Length > 0 ? remark[..1] : "#",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
            Foreground = Brushes.White,
        };   //占位首字

        var avatar = new Border
        {
            Width = 38,
            Height = 38,
            Background = new SolidColorBrush(Color.Parse("#4E9C9C")),
            Child = initial,
        };

        var title = new TextBlock
        {
            Text = remark.Length > 0 ? remark : "#" + id.Code,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#285050")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };   //显示名

        var detail = new TextBlock
        {
            Text = id.Full,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#8A9A9A")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };   //完整玩家 ID

        var text = new StackPanel { Margin = new Avalonia.Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Spacing = 3 };
        text.Children.Add(title);
        text.Children.Add(detail);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("38,*") };
        Grid.SetColumn(avatar, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(avatar);
        grid.Children.Add(text);

        var row = new Border
        {
            Padding = new Avalonia.Thickness(8),
            Tag = id.Full,
            Child = grid,
            Background = Brushes.Transparent,
        };   //一行好友
        row.Classes.Add("friendRow");
        row.DoubleTapped += OnFriendDoubleTapped;
        row.ContextFlyout = BuildFriendMenu(id, remark);
        return row;
    }

    /// <summary>有账号头像就换成图片,没有就保留首字占位。</summary>
    private void ShowSelfAvatar()
    {
        if (string.IsNullOrWhiteSpace(ClientSession.User))
        {
            return;
        }

        try
        {
            var bytes = AvatarStore.Load(ClientSession.User);
            if (bytes.Length == 0)
            {
                return;
            }

            SelfAvatarImage.Source = new Bitmap(new MemoryStream(bytes));
            SelfAvatarImage.IsVisible = true;
            SelfAvatarInitial.IsVisible = false;
        }
        catch
        {
            //头像读不出来就继续用首字
        }
    }

    /// <summary>双击好友 → 单独打开聊天窗体。</summary>
    private void OnFriendDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is Control { Tag: string name } && name.Length > 0)
        {
            var chat = new ChatWindow(name);
            WindowPlacement.PlaceAt(chat, this);
            chat.Show();
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

    /// <summary>点最小化按钮:窗口最小化。</summary>
    private void OnMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>点右上角关闭按钮:关掉这个窗口。</summary>
    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    /// <summary>点底部「好友通知」打开申请列表。</summary>
    private void OnNotificationsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var window = new FriendRequestsWindow();
        WindowPlacement.PlaceAt(window, this);
        window.Show();
    }

    /// <summary>点标题栏的「+」打开添加好友窗口。</summary>
    private void OnAddFriendClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var window = new AddFriendWindow();
        WindowPlacement.PlaceAt(window, this);
        window.Show();
    }

}
