using Avalonia.Controls;  // 窗口与控件
using Avalonia.Input;  // 鼠标键盘输入

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
