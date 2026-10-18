using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ZincXManagerClient.Views;

/// <summary>
/// 经济页:小功能选择(商店 / 银行)。
/// 卡片整块可点,右下角「进入」也能点;功能本身还没做,先弹占位提示。
/// </summary>
public partial class EconomyView : UserControl
{
    public EconomyView()
    {
        InitializeComponent();
    }

    private void OnStorePressed(object? sender, PointerPressedEventArgs e) => OpenStore();

    private void OnStoreEnter(object? sender, RoutedEventArgs e) => OpenStore();

    private void OnBankPressed(object? sender, PointerPressedEventArgs e) => OpenBank();

    private void OnBankEnter(object? sender, RoutedEventArgs e) => OpenBank();

    private void OpenStore()
        => _ = MessageDialog.InfoAsync(OwnerWindow(), "商店", "商店功能还没做", hint: "先占个位置,做好之后从这里进");

    private void OpenBank()
        => _ = MessageDialog.InfoAsync(OwnerWindow(), "银行", "银行功能还没做", hint: "先占个位置,做好之后从这里进");

    private Window? OwnerWindow() => TopLevel.GetTopLevel(this) as Window;
}
