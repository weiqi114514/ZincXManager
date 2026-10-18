using Avalonia.Controls;
using Avalonia.Input;

namespace ZincXManagerClient.Views;

/// <summary>聊天窗体:从好友条里选中好友后单独打开。</summary>
public partial class ChatWindow : Window
{
    public ChatWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);
    }

    public ChatWindow(string friendName) : this()
    {
        TitleText.Text = friendName;
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
