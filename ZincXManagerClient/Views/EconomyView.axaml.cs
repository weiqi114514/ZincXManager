using Avalonia.Controls;  // 窗口与控件
using Avalonia.Input;  // 鼠标键盘输入
using Avalonia.Interactivity;  // 路由事件

namespace ZincXManagerClient.Views;

/// <summary>经济页:小功能选择(商店 / 银行)。 卡片整块可点,右下角「进入」也能点;功能本身还没做,先弹占位提示。</summary>
public partial class EconomyView : UserControl
{
    public EconomyView()
    {
        InitializeComponent();
    }

    /// <summary>打开商店。</summary>
    private void OnStorePressed(object? sender, PointerPressedEventArgs e) => OpenStore();

    /// <summary>点「进入」:打开商店。</summary>
    private void OnStoreEnter(object? sender, RoutedEventArgs e) => OpenStore();

    /// <summary>打开银行。</summary>
    private void OnBankPressed(object? sender, PointerPressedEventArgs e) => OpenBank();

    /// <summary>点「进入」:打开银行。</summary>
    private void OnBankEnter(object? sender, RoutedEventArgs e) => OpenBank();

    /// <summary>打开商店(未接服务端)。</summary>
    private void OpenStore()
        => _ = MessageDialog.InfoAsync(OwnerWindow(), "商店", "商店功能还没做", hint: "先占个位置,做好之后从这里进");

    /// <summary>打开银行(未接服务端)。</summary>
    private void OpenBank()
        => _ = MessageDialog.InfoAsync(OwnerWindow(), "银行", "银行功能还没做", hint: "先占个位置,做好之后从这里进");

    /// <summary>取宿主窗口。</summary>
    private Window? OwnerWindow() => TopLevel.GetTopLevel(this) as Window;
}
