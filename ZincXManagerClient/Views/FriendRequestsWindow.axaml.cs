using System.Collections.Generic;  // List:演示申请
using Avalonia;  // Thickness
using System;  // StringComparison
using Avalonia.Controls;  // 窗口与控件
using Avalonia.Input;  // PointerPressedEventArgs
using Avalonia.Interactivity;  // RoutedEventArgs
using Avalonia.Layout;  // 对齐
using Avalonia.Media;  // 画刷

namespace ZincXManagerClient.Views;

/// <summary>
/// 好友通知:别人发来的添加申请列表。界面阶段只有演示数据,同意/拒绝还没接协议。
/// </summary>
public partial class FriendRequestsWindow : Window
{
    /// <summary>一条申请:对方名称 + 完整 ID + 来源服务端 + 添加信息。</summary>
    private sealed record Request(string Name, string FullId, string ServerId, string Message);

    /// <summary>当前打开着的申请列表(同时只允许一个),主窗口收到 657 时往这里塞数据。</summary>
    internal static FriendRequestsWindow? Current;

    /// <summary>演示申请;接协议后换成服务端下发的待处理列表。</summary>
    private static readonly List<Request> DemoRequests =
    [
        new("ZES_Admin", "ZXMS-ZES-1-14", "ZXMS-ZES-1", "我是 ZES 的管理员,想加你一起调服务器"),
        new("HT_Builder", "ZXMS-HT-2-23", "ZXMS-HT-2", "上次在广场聊过建筑,加个好友"),
        new("weiqi", "ZXMOS-5", "ZXMOS", "同意一下,方便发日志"),
    ];

    public FriendRequestsWindow()
    {
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

        InitializeComponent();

        foreach (var request in DemoRequests)
        {
            RequestList.Children.Add(BuildRow(request));
        }
    }

    /// <summary>拖动标题栏移动窗口。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>申请行右侧的竖排按钮;界面阶段只把这一行标成已处理,不发协议。</summary>
    private Button BuildActionButton(string text, Request request, bool accepted)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
            Width = 54,
            Height = 26,
            Padding = new Thickness(0),
            FontSize = 11.5,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = request,
            Name = accepted ? "accept" : "reject",
        };
        button.Click += OnActionClick;
        return button;
    }

    /// <summary>同意/拒绝:把同一行的两个按钮都禁掉,并在按钮上标明处理结果。</summary>
    private void OnActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var accepted = button.Name == "accept";

        // 把申请者 ID 从 Tag 上取回来(建按钮时写的是 request)
        var fullId = (button.Tag as Request)?.FullId ?? "";
        if (fullId.Length > 0)
        {
            _ = SendAnswerAsync(fullId, accepted);
        }
        button.Content = new TextBlock
        {
            Text = accepted ? "已同意" : "已拒绝",
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (button.Parent is StackPanel siblings)
        {
            foreach (var child in siblings.Children)
            {
                if (child is Button other)
                {
                    other.IsEnabled = false;
                }
            }
        }
    }

    /// <summary>一行申请:头像 + 名称 + 完整 ID + 来源 + 添加信息。</summary>
    private Control BuildRow(Request request)
    {
        var initial = request.Name.Length > 0 ? request.Name[..1].ToUpperInvariant() : "?";

        var avatar = new Border { Child = new TextBlock { Text = initial }, VerticalAlignment = VerticalAlignment.Top };
        avatar.Classes.Add("avatar");

        var name = new TextBlock
        {
            Text = request.Name,
            FontSize = 13,
            Foreground = (IBrush?)this.FindResource("ThemePrimaryDark") ?? Brushes.Black,
        };

        var id = new TextBlock
        {
            Text = request.FullId,
            FontSize = 10.5,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = (IBrush?)this.FindResource("ThemeMuted") ?? Brushes.Gray,
        };

        var origin = new TextBlock
        {
            Text = $"来源 {request.ServerId}",
            FontSize = 10.5,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (IBrush?)this.FindResource("ThemeMuted") ?? Brushes.Gray,
        };

        var message = new TextBlock
        {
            Text = request.Message,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = (IBrush?)this.FindResource("ThemePrimaryDark") ?? Brushes.Black,
        };

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        right.Children.Add(name);
        right.Children.Add(id);
        right.Children.Add(origin);
        right.Children.Add(message);

        var buttons = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(BuildActionButton("拒绝", request, accepted: false));
        buttons.Children.Add(BuildActionButton("同意", request, accepted: true));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(10, 10) };
        Grid.SetColumn(avatar, 0);
        Grid.SetColumn(right, 1);
        Grid.SetColumn(buttons, 2);
        right.Margin = new Thickness(10, 0, 10, 0);
        grid.Children.Add(avatar);
        grid.Children.Add(right);
        grid.Children.Add(buttons);

        var box = new Border { Child = grid };
        box.Classes.Add("requestRow");
        return box;
    }

    /// <summary>向服务端要申请列表(656)。</summary>
    internal async System.Threading.Tasks.Task RefreshAsync()
    {
        var connect = ClientSession.Connect;   //当前连接
        if (connect is null)
        {
            return;
        }

        try
        {
            await connect.SendAsync(ZincXManagerShared.Network.Protocol.FriendRequestsQuery, string.Empty);
        }
        catch
        {
            //连接没了就算了
        }
    }

    /// <summary>把 657 的负载(&lt;申请者ID&gt;\t&lt;留言&gt;\t&lt;时间&gt; 按行)渲染成申请列表。</summary>
    internal void FillRequests(string payload)
    {
        RequestList.Children.Clear();

        var count = 0;   //有效申请数
        foreach (var raw in payload.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("err|", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split('\t');   //[0]=申请者ID [1]=留言 [2]=时间
            if (!ZincXManagerShared.Account.PlayerId.TryParse(parts[0].Trim(), out var from))
            {
                continue;
            }

            var message = parts.Length > 1 ? parts[1].Trim() : "";   //留言
            var at = parts.Length > 2 ? parts[2].Trim() : "";   //时间
            RequestList.Children.Add(BuildRow(new Request("#" + from.Code.ToString(), from.Full, from.ServerId, message)));
            count++;
        }

        StatusText.IsVisible = count == 0;
    }

    /// <summary>把同意/拒绝发回服务端(658):&lt;申请者完整ID&gt;|accept 或 |reject。</summary>
    private static async System.Threading.Tasks.Task SendAnswerAsync(string fullId, bool accepted)
    {
        var connect = ClientSession.Connect;   //当前连接
        if (connect is null)
        {
            return;
        }

        try
        {
            var payload = fullId + (accepted ? "|accept" : "|reject");   //负载
            await connect.SendAsync(ZincXManagerShared.Network.Protocol.FriendAnswer, payload);
        }
        catch
        {
            //连接没了就算了,主窗口那边会提示
        }
    }
}
