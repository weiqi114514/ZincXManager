using Avalonia.Controls;  // 窗口与控件
using Avalonia.Input;  // 鼠标键盘输入
using Avalonia.Interactivity;  // 路由事件
using ZincXManagerClient.Modules;  // 模块登记处:按 key 查这个功能还开着没

namespace ZincXManagerClient.Views;

/// <summary>
/// 经济页:小功能选择(商店 / 银行)。卡片清单来自模块登记处,关掉的那张不显示;
/// 卡片整块可点,右下角「进入」也能点;功能本身还没做,先弹占位提示。
/// </summary>
public partial class EconomyView : UserControl
{
    public EconomyView()
    {
        InitializeComponent();
    }

    /// <summary>点卡片。</summary>
    private void OnCardPressed(object? sender, PointerPressedEventArgs e) => OpenCard(sender);

    /// <summary>点「进入」。</summary>
    private void OnEnterClick(object? sender, RoutedEventArgs e) => OpenCard(sender);

    /// <summary>按卡片的 key 打开功能(目前仅提示)。</summary>
    private void OpenCard(object? sender)
    {
        if (sender is not Control { Tag: string key } || key.Length == 0)
        {
            return;
        }

        // 模块已经关掉:卡片本来就不显示,这里再兜一次
        if (ModuleRegistry.Find(key) is { IsOn: false } module)
        {
            _ = MessageDialog.InfoAsync(OwnerWindow(), module.Title, "该功能已关闭",
                hint: "在服务端的模块配置里重新打开它,重启后重连即可。");
            return;
        }

        var (title, body) = key switch
        {
            "eco-store" => ("商店", "商店功能还没做"),
            "eco-bank" => ("银行", "银行功能还没做"),
            _ => ("经济", "这个功能还没做"),
        };

        _ = MessageDialog.InfoAsync(OwnerWindow(), title, body, hint: "先占个位置,做好之后从这里进");
    }

    /// <summary>取宿主窗口。</summary>
    private Window? OwnerWindow() => TopLevel.GetTopLevel(this) as Window;
}
