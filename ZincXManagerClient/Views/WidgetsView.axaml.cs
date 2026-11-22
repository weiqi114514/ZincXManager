using System;  // 基础类型
using System.Collections.Generic;  // 列表
using System.Linq;  // 过滤
using Avalonia.Controls;  // 窗口与控件
using Avalonia.Interactivity;  // 路由事件
using ZincXManagerClient.Models;  // 客户端自身
using ZincXManagerClient.Modules;  // 模块登记处:功能卡片都登记在那儿
using ZincXManagerShared.Modules;  // 模块编号

namespace ZincXManagerClient.Views;

/// <summary>
/// 小功能组:顶部搜索框 + 下方左右两栏细条列表(图标 / 名称 / 灰色介绍)。
/// 点条目抛 <see cref="WidgetActivated"/>,由主窗口决定打开什么。
/// </summary>
public partial class WidgetsView : UserControl
{
    /// <summary>点开了某个功能。</summary>
    public event Action<string>? WidgetActivated;

    // 全部功能(搜索在它上面过滤):卡片来自模块登记处,关掉的功能在 Refresh 里滤掉
    private readonly IReadOnlyList<FunctionItem> _all = ModuleRegistry.CardsOf(ModuleIds.NavWidgets);

    /// <summary>卡片 → 功能条(字段一一对应,图标沿用卡片的矢量路径)。</summary>
    private static WidgetEntry ToEntry(FunctionItem card) => new()
    {
        Key = card.Key,
        Title = card.Title,
        Description = card.Description,
        IconPath = card.Icon,
        Stroke = card.IconStroke,
    };

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

        // 先减再加:切页面时这个钩子可能重复触发,减一次避免订阅上两份
        ModuleState.Changed -= OnModulesChanged;
        ModuleState.Changed += OnModulesChanged;
        Refresh();
    }

    /// <summary>离开界面就退订,别让静态事件钉住这个控件。</summary>
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ModuleState.Changed -= OnModulesChanged;
    }

    /// <summary>
    /// 开关名单变了(重连 / 切服后 202 才到):页面还开着也要把已经关掉的小功能移走。
    /// 收包线程也可能触发,所以不在 UI 线程时先 post 回去。
    /// </summary>
    private void OnModulesChanged()
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Refresh();
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>当前是不是直连官方服(OOS):官方服没有账号绑定,连它的时候不显示绑定功能。</summary>
    private static bool IsOfficialOnly()
        => string.Equals(ClientConfig.GetOrEmpty("mode"), "OOS", StringComparison.OrdinalIgnoreCase);

    /// <summary>按搜索词过滤并分到左右两栏。</summary>
    private void Refresh()
    {
        // 三层过滤:模块开关(关掉的功能不出现)→ 连接方式(直连官方时没有绑定)→ 搜索词
        var source = _all
            .Where(card => card.IsVisible)
            .Where(card => !(IsOfficialOnly() && card.Key == "bind"))
            .Select(ToEntry)
            .ToList();

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
