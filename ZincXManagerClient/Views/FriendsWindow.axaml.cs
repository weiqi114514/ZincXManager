using Avalonia.Controls;  // 窗口与控件
using Avalonia.Input;  // 鼠标键盘输入

namespace ZincXManagerClient.Views;

/// <summary>好友条:单独的小窗体(类似老版 QQ / Steam 的好友条),选好友后再开聊天窗体。</summary>
public partial class FriendsWindow : Window
{
    public FriendsWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);
    }

    /// <summary>双击好友 → 单独打开聊天窗体。</summary>
    private void OnFriendDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is Control { Tag: string name } && name.Length > 0)
        {
            var chat = new ChatWindow(name);
            WindowPlacement.PlaceAt(chat, this);
            chat.Show();
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

    /// <summary>点最小化按钮:窗口最小化。</summary>
    private void OnMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>点右上角关闭按钮:关掉这个窗口。</summary>
    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
