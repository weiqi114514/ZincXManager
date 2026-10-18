using Avalonia.Controls;
using Avalonia.Input;

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

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
