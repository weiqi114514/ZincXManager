using Avalonia.Controls;  // 窗口与控件
using Avalonia.Input;  // PointerPressedEventArgs
using Avalonia.Interactivity;  // RoutedEventArgs

namespace ZincXManagerClient.Views;

/// <summary>
/// 添加好友的申请框:显示对方是谁,填可选的备注和一句添加信息。
/// 界面阶段不发协议,点「发送申请」只把结果带回调用方。
/// </summary>
public partial class AddFriendRequestWindow : Window
{
    /// <summary>设计器/框架用的空构造。</summary>
    public AddFriendRequestWindow()
    {
        InitializeComponent();
    }

    /// <summary>按对方信息填好界面。</summary>
    public AddFriendRequestWindow(string name, string fullId, string initial) : this()
    {
        TargetName.Text = name;
        TargetId.Text = fullId;
        TargetInitial.Text = string.IsNullOrWhiteSpace(initial) ? "?" : initial;
        Title = $"ZincXManagerClient · 添加好友申请 · {name}";
    }

    /// <summary>备注(可空)。</summary>
    public string Remark => (RemarkBox.Text ?? "").Trim();

    /// <summary>添加信息(可空,留给对方看)。</summary>
    public string Message => (MessageBox.Text ?? "").Trim();

    /// <summary>拖动标题栏移动窗口。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnSendClick(object? sender, RoutedEventArgs e) => Close(true);
}
