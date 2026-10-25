using System;  // 基础类型
using System.Collections.Generic;  // 列表
using System.Linq;  // 过滤
using Avalonia.Controls;  // 窗口与控件
using Avalonia.Interactivity;  // 路由事件
using ZincXManagerClient.Models;  // 客户端自身

namespace ZincXManagerClient.Views;

/// <summary>
/// 小功能组:顶部搜索框 + 下方左右两栏细条列表(图标 / 名称 / 灰色介绍)。
/// 点条目抛 <see cref="WidgetActivated"/>,由主窗口决定打开什么。
/// </summary>
public partial class WidgetsView : UserControl
{
    /// <summary>点开了某个功能。</summary>
    public event Action<string>? WidgetActivated;

    // 全部功能(搜索在它上面过滤) —— 小功能组目前只有绑定/解绑这一个
    private readonly List<WidgetEntry> _all =
    [
        new WidgetEntry
        {
            Key = "bind", Title = "绑定 / 解绑", Description = "把 ZXMC 账号绑到 MC 玩家名上",
            IconPath = Icons.Bind, Stroke = true,
        },
    ];

    public WidgetsView()
    {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>每次显示到界面上都重新判断一次(设置里改了连接方式,这里要跟着变)。</summary>
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Refresh();
    }

    /// <summary>当前是不是直连官方服(OOS):官方服没有账号绑定,连它的时候不显示绑定功能。</summary>
    private static bool IsOfficialOnly()
        => string.Equals(ClientConfig.GetOrEmpty("mode"), "OOS", StringComparison.OrdinalIgnoreCase);

    /// <summary>按搜索词过滤并分到左右两栏。</summary>
    private void Refresh()
    {
        var source = IsOfficialOnly()
            ? _all.Where(w => w.Key != "bind").ToList()
            : _all;

        var keyword = (SearchBox.Text ?? "").Trim();
        var list = keyword.Length == 0
            ? source
            : source.Where(w => w.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                || w.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        // 交替分栏:左 0/2/4…,右 1/3/5…
        LeftList.ItemsSource = list.Where((_, index) => index % 2 == 0).ToList();
        RightList.ItemsSource = list.Where((_, index) => index % 2 == 1).ToList();
    }

    /// <summary>点功能条:把 Key 抛给主窗口。</summary>
    private void OnItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key } && key.Length > 0)
        {
            WidgetActivated?.Invoke(key);
        }
    }
}
