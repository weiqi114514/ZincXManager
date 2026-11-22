using System.Linq;  // LINQ 查询
using Avalonia;  // AvaloniaProperty 与引用类型
using Avalonia.Controls;  // 窗口与控件
using Avalonia.Controls.Templates;  // FuncTemplate
using Avalonia.Input;  // 鼠标键盘输入
using Avalonia.Layout;  // Orientation 枚举
using Avalonia.Interactivity;  // 路由事件
using ZincXManagerClient.Models;  // 客户端自身
using ZincXManagerClient.Modules;  // 模块登记处:按 key 查这个功能还开着没

namespace ZincXManagerClient.Views;

/// <summary>子功能选择页(所有页共用这一个模板):标题 + 子功能卡片列表。</summary>
public partial class FunctionPickerView : UserControl
{
    /// <summary>是否按横向网格排卡片(管理端四个大类页用);默认竖排,与玩家端原样一致。</summary>
    public static readonly StyledProperty<bool> WrapLayoutProperty =
        AvaloniaProperty.Register<FunctionPickerView, bool>(nameof(WrapLayout));

    public FunctionPickerView()
    {
        InitializeComponent();
    }

    /// <summary>横向网格排列:卡片的 ItemsPanel 换成 WrapPanel。</summary>
    public bool WrapLayout
    {
        get => GetValue(WrapLayoutProperty);
        set => SetValue(WrapLayoutProperty, value);
    }

    /// <summary>页面标题 + 子功能列表。</summary>
    public void SetContent(string title, System.Collections.Generic.IEnumerable<FunctionItem> items)
    {
        TitleText.Text = title;
        ItemsHost.ItemsSource = items;
    }

    /// <summary>挂到界面树时按 WrapLayout 决定排版方式。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyLayout(WrapLayout);
    }

    /// <summary>竖排用 StackPanel(间距 12);横向网格用 WrapPanel + 卡片自带 Margin。</summary>
    private void ApplyLayout(bool wrap)
    {
        // 卡片样式靠这个类切到收窄版
        ItemsHost.Classes.Set("wrap", wrap);
        ItemsHost.ItemsPanel = wrap
            ? new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal })
            : new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 12 });
    }

    /// <summary>点卡片。</summary>
    private void OnCardPressed(object? sender, PointerPressedEventArgs e) => Open(sender);

    /// <summary>点「进入」。</summary>
    private void OnEnterClick(object? sender, RoutedEventArgs e) => Open(sender);

    /// <summary>打开子功能(目前仅提示)。</summary>
    private void Open(object? sender)
    {
        if (sender is not Control { Tag: string key } || key.Length == 0)
        {
            return;
        }

        // 模块已经关掉:卡片本来就不显示,这里再兜一次 —— 键盘 / 自动化也可能点到旧引用
        if (ModuleRegistry.Find(key) is { IsOn: false } module)
        {
            _ = MessageDialog.InfoAsync(TopLevel.GetTopLevel(this) as Window, module.Title,
                "该功能已关闭", hint: "在服务端的模块配置里重新打开它,重启后重连即可。");
            return;
        }

        var title = (ItemsHost.ItemsSource as System.Collections.Generic.IEnumerable<FunctionItem>)?
            .FirstOrDefault(i => i.Key == key)?.Title ?? key;

        // 服务器管理:不是占位,要开真正的选择窗口(多服数据分离)
        if (key == "mg-server")
        {
            (TopLevel.GetTopLevel(this) as MainWindow)?.OpenServerPicker();
            return;
        }

        // RCON / ZXMM 桥接控制台:页面就在主窗口内容区里,切过去即可
        if (key == "ad-rcon")
        {
            (TopLevel.GetTopLevel(this) as MainWindow)?.OpenConsole();
            return;
        }

        var (source, hint) = key switch
        {
            "srv-square" => ("数据来源:子服务端(ZXMS)", "和锌源广场功能一样,只是社区内容来自你连的子服务端"),
            "zinc-square" => ("数据来源:官方服务端(ZXMOS)", "和服务器广场功能一样,只是社区内容来自官方"),
            _ => ("", "先占个位置,做好之后从这里进"),
        };

        _ = MessageDialog.InfoAsync(TopLevel.GetTopLevel(this) as Window, title,
            key is "srv-square" or "zinc-square" ? title + "还没做 —— " + source : "这个子功能还没做",
            hint: hint);
    }
}
