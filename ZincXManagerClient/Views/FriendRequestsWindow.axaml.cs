using System;                                // Exception
using System.Collections.Generic;            // List
using System.Threading.Tasks;                // Task:发协议
using Avalonia;                              // Thickness
using Avalonia.Controls;                     // Window / Button / TextBlock / Grid
using Avalonia.Input;                        // PointerPressedEventArgs
using Avalonia.Interactivity;                // RoutedEventArgs
using Avalonia.Layout;                       // VerticalAlignment
using Avalonia.Media;                        // SolidColorBrush / Brushes
using ZincXManagerClient.ViewModels;         // ConnectViewModel
using ZincXManagerShared.Friend;             // FriendCodec / FriendRequestEntry / PlayerId
using ZincXManagerShared.Logging;            // Logger / LogLevel
using ZincXManagerShared.Network;            // Protocol

namespace ZincXManagerClient.Views;

/// <summary>
/// 好友通知窗:别人发来的申请一条一行,可以同意或拒绝。
///
/// <para>同意/拒绝都只是发 658 给服务端,列表刷新也以服务端的回话为准 ——
/// 这样两台服、两边客户端看到的结果永远一致。</para>
/// </summary>
public partial class FriendRequestsWindow : Window
{
    /// <summary>当前开着的通知窗(主窗收到 657 时找它刷新)。</summary>
    public static FriendRequestsWindow? Current { get; private set; }

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

        StatusText.Text = "正在取申请列表…";
        _ = QueryAsync();
    }

    /// <summary>服务端回了申请列表(657)。</summary>
    public void FillRequests(string payload)
    {
        var entries = FriendCodec.ParseFriendRequests(payload, out var badLines);
        if (badLines > 0)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 申请列表里有 {badLines} 行认不出来,已跳过");
        }

        RequestList.Children.Clear();
        foreach (var entry in entries)
        {
            RequestList.Children.Add(BuildRow(entry));
        }

        StatusText.Text = entries.Count == 0 ? "没有待处理的申请" : $"共 {entries.Count} 条待处理";
    }

    /// <summary>画一条申请:左头像、中间「谁想加你 + 他写的什么 + 什么时候」、右边同意/拒绝。</summary>
    private Control BuildRow(FriendRequestEntry entry)
    {
        var name = DisplayNameOf(entry.FromId);

        var title = new TextBlock
        {
            Text = name + " 想加你好友",
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#1F2A36")),
        };

        var message = new TextBlock
        {
            Text = entry.Message,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#4A5B6B")),
        };

        var when = new TextBlock
        {
            Text = entry.Time + "  ·  " + entry.FromId,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#8899A6")),
        };

        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        text.Children.Add(title);
        text.Children.Add(message);
        text.Children.Add(when);

        var agree = new Button { Content = "同意", MinWidth = 52, MinHeight = 26 };
        agree.Click += async (_, _) => await AnswerAsync(entry, agree: true);

        var refuse = new Button { Content = "拒绝", MinWidth = 52, MinHeight = 26 };
        refuse.Click += async (_, _) => await AnswerAsync(entry, agree: false);

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

    /// <summary>申请人显示名:申请里只有 ID,取最后一个「-」前的名字拿不到,就用完整 ID。</summary>
    private static string DisplayNameOf(string fullId) => fullId.Length > 0 ? fullId : "有人";

    /// <summary>点同意/拒绝:发 658,成功后就重新拉一次列表。</summary>
    private async Task AnswerAsync(FriendRequestEntry entry, bool agree)
    {
        if (ClientSession.Connect is not { } connect)
        {
            StatusText.Text = "还没连上服务端";
            return;
        }

        try
        {
            await connect.SendAsync(Protocol.FriendAnswer, FriendCodec.BuildFriendAnswer(entry.FromId, agree));
            StatusText.Text = agree ? "已同意,正在刷新…" : "已拒绝,正在刷新…";
            FriendsWindow.Current?.RefreshAll();
            await Task.Delay(300);
            await QueryAsync();
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 回申请失败: {ex.Message}");
            StatusText.Text = "操作失败:连接不在了,重连后再试";
        }
    }

    /// <summary>向服务端要一次申请列表(656)。</summary>
    private async Task QueryAsync()
    {
        if (ClientSession.Connect is not { } connect)
        {
            StatusText.Text = "还没连上服务端";
            return;
        }

        try
        {
            await connect.SendAsync(Protocol.FriendRequestsQuery, "");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 取申请列表失败: {ex.Message}");
            StatusText.Text = "取申请列表失败:连接不在了";
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

    /// <summary>关窗。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
