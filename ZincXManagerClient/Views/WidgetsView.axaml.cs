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

    // 全部功能(搜索在它上面过滤)
    private readonly List<WidgetEntry> _all =
    [
        new WidgetEntry
        {
            Key = "bind", Title = "绑定 / 解绑", Description = "把 ZXMC 账号绑到 MC 玩家名上",
            IconPath = Icons.Friends,
        },
        new WidgetEntry
        {
            Key = "friends", Title = "好友", Description = "打开好友条与聊天窗口", IconPath = Icons.Friends,
        },
        new WidgetEntry
        {
            Key = "store", Title = "商店", Description = "看货架、下单、查余额", IconPath = Icons.Store,
        },
        new WidgetEntry
        {
            Key = "bank", Title = "银行", Description = "存取、转账、看流水", IconPath = Icons.Bank,
        },
        new WidgetEntry
        {
            Key = "help", Title = "帮助", Description = "常见问题与使用文档", IconPath = Icons.Help,
        },
        new WidgetEntry
        {
            Key = "feedback", Title = "反馈", Description = "把问题或建议发给我们", IconPath = Icons.Feedback,
        },
    ];

    public WidgetsView()
    {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>按搜索词过滤并分到左右两栏。</summary>
    private void Refresh()
    {
        var keyword = (SearchBox.Text ?? "").Trim();
        var list = keyword.Length == 0
            ? _all
            : _all.Where(w => w.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
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
