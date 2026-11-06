using System;                                // Exception / TimeSpan
using Avalonia;                              // Thickness
using Avalonia.Controls;                     // Window / TextBlock
using Avalonia.Input;                        // PointerPressedEventArgs
using Avalonia.Interactivity;                // RoutedEventArgs
using Avalonia.Media;                        // SolidColorBrush

namespace ZincXManagerClient.Views;

/// <summary>
/// 发申请前的小窗:显示对方是谁、让玩家填一句验证信息,返回那句信息(取消则返回 null)。
///
/// <para>信息必填:服务端会把这句话原样交给对方,空着发过去对方不知道是谁在申请。</para>
/// </summary>
public partial class AddFriendRequestWindow : Window
{
    private readonly string _targetId;      // 对方完整ID
    private readonly string _targetName;    // 对方账户名

    /// <summary>点「发送申请」时玩家给对方填的备注;取消关闭时是空串。调用方在 ShowDialog 返回后读它。</summary>
    public string Remark { get; private set; } = "";

    public AddFriendRequestWindow() : this("", "")
    {
    }

    public AddFriendRequestWindow(string targetId, string targetName)
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);

        // 这个窗是 ShowDialog(父窗) 弹出来的,显式声明居中于父窗;默认 Manual 会落到系统级联位置
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _targetId = targetId;
        _targetName = targetName.Length > 0 ? targetName : targetId;

        TargetName.Text = _targetName;
        TargetId.Text = targetId;
        TargetInitial.Text = _targetName.Length > 0 ? _targetName[..1].ToUpperInvariant() : "?";
        MessageBox.Focus();
    }

    /// <summary>点「发送申请」:信息空着就提示并把焦点放回去,不关窗。</summary>
    private void OnSendClick(object? sender, RoutedEventArgs e)
    {
        var message = MessageBox.Text?.Trim() ?? "";
        if (message.Length == 0)
        {
            MessageHint.Text = "写一句让对方知道你是谁";
            MessageHint.IsVisible = true;
            MessageBox.Focus();
            return;
        }

        // 备注是可选的,这里只记住不校验;随 654 一起上服务端,等对方同意那一刻落进自己的好友档
        Remark = RemarkBox.Text?.Trim() ?? "";
        Close(message);
    }

    /// <summary>点「取消」/右上角叉:什么也不发。</summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    /// <summary>一开始打字就把红字提示收掉。</summary>
    private void OnMessageTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (MessageHint.IsVisible && (MessageBox.Text?.Trim().Length ?? 0) > 0)
        {
            MessageHint.IsVisible = false;
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
}
