using System;  // 基础类型
using Avalonia.Controls;  // 窗口与控件
using Avalonia.Input;  // 鼠标输入
using Avalonia.Interactivity;  // 路由事件

namespace ZincXManagerClient.Views;

/// <summary>
/// 「登录请求」弹窗:有人要进服时问玩家放不放行。
/// 外观和 <see cref="MessageDialog"/> 一套(无系统边框 + 标题栏 + 右下按钮),内容换成账号 / IP / 归属地。
/// </summary>
public partial class LoginRequestWindow : Window
{
    /// <summary>玩家点了「允许进服」。</summary>
    public bool Agreed { get; private set; }

    public LoginRequestWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);
    }

    /// <summary>按登录请求内容构造弹窗。</summary>
    /// <param name="player">MC 玩家名。</param>
    /// <param name="ip">玩家 IP。</param>
    /// <param name="geo">服务端查好的归属地(可能为空)。</param>
    public LoginRequestWindow(string player, string ip, string geo) : this()
    {
        PlayerText.Text = player.Length > 0 ? player : "-";
        IpText.Text = ip.Length > 0 ? ip : "-";
        GeoText.Text = geo.Length > 0 ? geo : "未知";
    }

    /// <summary>允许进服。</summary>
    private void OnAgreeClick(object? sender, RoutedEventArgs e)
    {
        Agreed = true;
        Close();
    }

    /// <summary>拒绝 / 关窗,一律按拒绝算。</summary>
    private void OnDenyClick(object? sender, RoutedEventArgs e)
    {
        Agreed = false;
        Close();
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
