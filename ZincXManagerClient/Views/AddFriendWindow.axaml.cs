using System;                                // Exception / TimeSpan
using System.Collections.Generic;            // List
using System.Threading.Tasks;                // Task:防抖
using Avalonia;                              // Thickness
using Avalonia.Controls;                     // Window / Button / TextBlock
using Avalonia.Input;                        // PointerPressedEventArgs
using Avalonia.Interactivity;                // RoutedEventArgs
using Avalonia.Layout;                       // VerticalAlignment
using Avalonia.Media;                        // SolidColorBrush
using ZincXManagerClient.ViewModels;         // ConnectViewModel
using ZincXManagerShared.Friend;             // FriendCodec / SearchHit / PlayerId
using ZincXManagerShared.Logging;            // Logger / LogLevel
using ZincXManagerShared.Network;            // Protocol

namespace ZincXManagerClient.Views;

/// <summary>
/// 添加好友窗:输入点什么就交给服务端去搜(663),把结果画出来,点「添加」再发申请。
///
/// <para>这里不判断「能不能加」——已经是好友、目标不存在都由服务端回话。
/// 客户端只做两件事:别每敲一个字就发一次请求、把服务端的回话画成一行行结果。</para>
/// </summary>
public partial class AddFriendWindow : Window
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(260);   // 停手 260ms 再发,免得一个字一个包

    /// <summary>当前开着的添加好友窗(主窗收到 664 时找它填结果)。</summary>
    public static AddFriendWindow? Current { get; private set; }

    private int _searchTicket;   // 每次新搜索 +1,晚回来的旧结果直接丢掉

    public AddFriendWindow()
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

        Opened += (_, _) => SearchBox.Focus();
    }

    /// <summary>服务端回了搜索结果(664)。</summary>
    public void FillResults(string payload)
    {
        var hits = FriendCodec.ParseSearchHits(payload, out var badLines);
        if (badLines > 0)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 搜索结果里有 {badLines} 行认不出来,已跳过");
        }

        ResultList.Children.Clear();
        foreach (var hit in hits)
        {
            ResultList.Children.Add(BuildRow(hit));
        }

        EmptyHint.IsVisible = hits.Count == 0;
        EmptyHint.Text = hits.Count == 0 ? "没找到这个人" : "";
        StatusText.Text = hits.Count == 0 ? "换个完整 ID、账户名或编号试试" : $"找到 {hits.Count} 个";
    }

    /// <summary>画一条搜索结果:左头像、中间名字和 ID、右边「添加」。</summary>
    private Control BuildRow(SearchHit hit)
    {
        var title = new TextBlock
        {
            Text = hit.Name.Length > 0 ? hit.Name : hit.Id,
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#1F2A36")),
        };

        var sub = new TextBlock
        {
            Text = hit.Id + (hit.Level.Length > 0 && hit.Level != "-" ? "  ·  " + hit.Level : ""),
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.Parse("#8899A6")),
        };

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        text.Children.Add(title);
        text.Children.Add(sub);

        var add = new Button { Content = "添加", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        add.Classes.Add("primary");
        add.Click += async (_, _) => await AskAsync(hit);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(InitialAvatar(hit.Name.Length > 0 ? hit.Name : hit.Id));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(add, 2);
        grid.Children.Add(add);

        var row = new Border { Child = grid, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(6) };
        row.Classes.Add("resultRow");
        return row;
    }

    /// <summary>默认头像:青绿底 + 白色首字。</summary>
    private static Control InitialAvatar(string name) => new Border
    {
        Width = 34,
        Height = 34,
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

    /// <summary>点「添加」:先让玩家写一句申请信息,再发 654。</summary>
    private async Task AskAsync(SearchHit hit)
    {
        if (ClientSession.Connect is not { } connect)
        {
            StatusText.Text = "还没连上服务端";
            return;
        }

        var request = new AddFriendRequestWindow(hit.Id, hit.Name);
        var message = await request.ShowDialog<string?>(this);
        if (message is null)
        {
            return;
        }

        try
        {
            await connect.SendAsync(Protocol.FriendRequest, FriendCodec.BuildFriendRequest(hit.Id, message, request.Remark));
            StatusText.Text = "申请已发往服务端,结果以弹窗为准。";
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 发好友申请失败: {ex.Message}");
            StatusText.Text = "发送失败:连接不在了,重连后再试";
        }
    }

    /// <summary>搜索框变了:停手 260ms 再问服务端;空输入只是清屏,不发请求。</summary>
    private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        var keyword = SearchBox.Text?.Trim() ?? "";
        var ticket = ++_searchTicket;

        if (keyword.Length == 0)
        {
            ResultList.Children.Clear();
            EmptyHint.IsVisible = true;
            EmptyHint.Text = "输入内容开始搜索";
            StatusText.Text = "按完整 ID / 账户名 / 账户编号搜";
            return;
        }

        HintText.Text = Describe(keyword);
        await Task.Delay(Debounce);
        if (ticket != _searchTicket)
        {
            return;
        }

        if (ClientSession.Connect is not { } connect)
        {
            StatusText.Text = "还没连上服务端";
            return;
        }

        try
        {
            await connect.SendAsync(Protocol.FriendSearch, keyword);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 搜索失败: {ex.Message}");
            StatusText.Text = "搜索失败:连接不在了";
        }
    }

    /// <summary>告诉玩家这次按什么找:完整 ID 就按 ID,否则按名字/编号。</summary>
    private static string Describe(string keyword)
    {
        if (PlayerId.TryParse(keyword, out var id))
        {
            return $"按完整 ID 找:{id.Full}";
        }

        return int.TryParse(keyword, out _)
            ? $"按账户编号找:{keyword}"
            : $"按账户名找:{keyword}(名字要完全一致)";
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
