using System;  // Math
using System.Collections.Generic;  // List:演示用户列表
using System.Linq;  // Where/Take:按输入过滤演示数据
using Avalonia;  // Thickness
using Avalonia.Controls;  // 窗口与控件
using Avalonia.Input;  // PointerPressedEventArgs
using Avalonia.Interactivity;  // RoutedEventArgs
using Avalonia.Layout;  // Orientation / 对齐
using Avalonia.Media;  // 画刷

namespace ZincXManagerClient.Views;

/// <summary>
/// 添加好友窗口:一个搜索框吃三种输入(完整 ID / 账户名 / 账户编号),结果列表先放演示数据。
/// 界面阶段不连服务端,三种输入的解析规则已经按最终方案写在这里,接协议时换掉数据源即可。
/// </summary>
public partial class AddFriendWindow : Window
{
    /// <summary>演示用的查找结果:服务端ID + 编码 + 账户名。</summary>
    private sealed record DemoUser(string ServerId, string Code, string Name);

    /// <summary>这次搜索按什么匹配:主行就显示玩家搜的那个,别的不抢位置。</summary>
    private enum SearchMode { Id, Name, Code }

    private SearchMode _mode = SearchMode.Name;

    /// <summary>演示数据;接上协议后换成「用户列表 + 跨服查询」的结果。</summary>
    private static readonly List<DemoUser> DemoUsers =
    [
        new("ZXMS-ZES-1", "14", "ZES_Admin"),
        new("ZXMS-ZES-1", "1", "ZES_Player"),
        new("ZXMS-HT-2", "23", "HT_Builder"),
        new("ZXMS-RAINSERVER-3", "14", "Rain_14"),
        new("ZXMOS", "5", "weiqi"),
        new("ZXMOS", "1", "testplayer"),
    ];

    public AddFriendWindow()
    {
        InitializeComponent();
        WindowResizeHelper.Attach(this, horizontal: true);
    }

    /// <summary>拖动标题栏移动窗口。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>输入变化就重新解析并刷新结果。</summary>
    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => Refresh();

    /// <summary>按输入的形态决定去哪儿找,然后把结果铺出来。</summary>
    private void Refresh()
    {
        var raw = (SearchBox.Text ?? "").Trim();
        ResultList.Children.Clear();

        if (raw.Length == 0)
        {
            HintText.Text = "输入 ZXMS-ZES-1-14 按 ID 找;输入账户名或编号在本服用户列表里找";
            ResultList.Children.Add(Empty("输入内容开始搜索"));
            return;
        }

        List<DemoUser> hits;

        if (TryParseFullId(raw, out var serverId, out var code))
        {
            // ① 完整 ID:直接瞄准它所属的服务端
            _mode = SearchMode.Id;
            HintText.Text = $"按完整 ID 搜索:服务端 {serverId},编码 {code}";
            hits = DemoUsers.Where(u => u.ServerId == serverId && u.Code == code).ToList();
            //本地用户列表还没同步时,完整 ID 本身就是唯一凭据:直接给出可添加的一行
            if (hits.Count == 0 && ZincXManagerShared.Account.PlayerId.TryParse(raw, out var direct))
            {
                hits.Add(new DemoUser(direct.ServerId, direct.Code.ToString(System.Globalization.CultureInfo.InvariantCulture), "#" + direct.Code.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        else if (raw.All(char.IsDigit))
        {
            // ② 纯数字:当成账户编号,在本服用户列表里找(同一编号可能挂在多个服务端)
            _mode = SearchMode.Code;
            HintText.Text = $"按账户编号搜索:全部服务端里找编码 {raw}";
            hits = DemoUsers.Where(u => u.Code == raw).ToList();
        }
        else
        {
            // ③ 其它:当成账户名,先在本地用户列表里找
            _mode = SearchMode.Name;
            HintText.Text = $"按账户名搜索:{raw}";
            hits = DemoUsers.Where(u => u.Name.Contains(raw, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (hits.Count == 0)
        {
            ResultList.Children.Add(Empty("没找到,检查一下 ID 或名字"));
            return;
        }

        foreach (var user in hits.Take(30))
        {
            ResultList.Children.Add(BuildRow(user));
        }
    }

    /// <summary>完整 ID 形如 ZXMS-ZES-1-14 / ZXMOS-5:最后一段是编码,前面整体是服务端 ID。</summary>
    private static bool TryParseFullId(string text, out string serverId, out string code)
    {
        serverId = "";
        code = "";

        var dash = text.LastIndexOf('-');
        if (dash <= 0 || dash == text.Length - 1)
        {
            return false;
        }

        var head = text[..dash];
        var tail = text[(dash + 1)..];

        if (!tail.All(char.IsDigit))
        {
            return false;
        }

        if (!head.StartsWith("ZXMS-", StringComparison.OrdinalIgnoreCase)
            && !head.Equals("ZXMOS", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        serverId = head;
        code = tail;
        return true;
    }

    /// <summary>一行结果:头像 + 账户名 + 完整 ID + 来源服务端。</summary>
    private Control BuildRow(DemoUser user)
    {
        var initial = user.Name.Length > 0 ? user.Name[..1].ToUpperInvariant() : "?";
        var fullId = $"{user.ServerId}-{user.Code}";

        //主行 = 玩家这次搜的那个;次行补齐另一个 + 来源服务端
        var (primary, secondary) = _mode switch
        {
            SearchMode.Id => (fullId, $"{user.Name}   ·   {user.ServerId}"),
            SearchMode.Code => (user.Code, $"{user.Name}   ·   {fullId}"),
            _ => (user.Name, $"{fullId}"),
        };

        var name = new TextBlock
        {
            Text = primary,
            FontSize = 12.5,
            Foreground = (IBrush?)this.FindResource("ThemePrimaryDark") ?? Brushes.Black,
        };

        var detail = new TextBlock
        {
            Text = secondary,
            FontSize = 10.5,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = (IBrush?)this.FindResource("ThemeMuted") ?? Brushes.Gray,
        };

        var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(detail);

        var avatar = new Border { Child = new TextBlock { Text = initial } };
        avatar.Classes.Add("avatar");

        var add = new Button
        {
            Content = "添加",
            FontSize = 11.5,
            MinWidth = 56,
            Padding = new Avalonia.Thickness(0, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        add.Click += async (_, _) =>
        {
            var initial = user.Name.Length > 0 ? user.Name[..1].ToUpperInvariant() : "?";
            var request = new AddFriendRequestWindow(user.Name, fullId, initial);
            var sent = await request.ShowDialog<bool>(this);
            if (sent)
            {
                StatusText.Text = await SendRequestAsync(fullId, request.Message);
            }
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(8, 6) };
        Grid.SetColumn(avatar, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(add, 2);
        text.Margin = new Thickness(10, 0, 10, 0);
        row.Children.Add(avatar);
        row.Children.Add(text);
        row.Children.Add(add);

        var box = new Border { Child = row };
        box.Classes.Add("resultRow");
        return box;
    }

    /// <summary>居中的空态提示。</summary>
    private static Control Empty(string text)
        => new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 0),
            Foreground = Brushes.Gray,
        };

    /// <summary>发好友申请(654):&lt;对方完整ID&gt;|&lt;验证消息&gt;,回一句话给状态栏。</summary>
    private async System.Threading.Tasks.Task<string> SendRequestAsync(string fullId, string message)
    {
        var connect = ClientSession.Connect;   //当前连接
        if (connect is null)
        {
            return "还没连上服务端,连上再点一次添加。";
        }

        try
        {
            var payload = fullId + "|" + message;   //负载
            await connect.SendAsync(ZincXManagerShared.Network.Protocol.FriendRequest, payload);
            return "申请已发出,等对方在「好友通知」里处理。";
        }
        catch
        {
            return "发送失败:连接断了,连上再点一次添加。";
        }
    }
}
