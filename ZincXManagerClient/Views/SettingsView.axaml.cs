using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ZincXManagerShared.Logging;
using ZincXManagerClient.Models;
using ZincXManagerClient.ViewModels;

namespace ZincXManagerClient.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    /// <summary>切换连接方式(SAOS / OS / OOS);这项就是 ZXMC 模式,与连接界面共用同一配置。</summary>
    private void OnModeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode })
        {
            Vm?.SelectMode(mode);
        }
    }

    /// <summary>切换左侧二级导航。</summary>
    private void OnSubNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SubNavItem item })
        {
            Vm?.SelectSection(item);
        }
    }

    /// <summary>"重启以应用"。</summary>
    private void OnApplyRestartClick(object? sender, RoutedEventArgs e) => Vm?.ApplyRestart();

    /// <summary>"启动数据源连接流程":打开连接界面。</summary>
    private void OnStartFlowClick(object? sender, RoutedEventArgs e)
    {
        Vm?.StartDataSourceFlow();

        var window = new ConnectWindow { DataContext = new ConnectViewModel() };
        WindowPlacement.PlaceAt(window, TopLevel.GetTopLevel(this) as Window);
        window.Show();
    }

    /// <summary>个性化 → 选择背景壁纸。</summary>
    private async void OnPickWallpaperClick(object? sender, RoutedEventArgs e)
    {
        // 三重保险:① 当前确实是"个性化"页 ② 按钮真的可见 ③ 不是切页瞬间的余波
        if (Vm is null || !Vm.IsPersonalizePage) return;
        if (sender is Button button && !button.IsEffectivelyVisible) return;
        if (Vm.IsJustSwitched) { Logger.Log(LogLevel.Warning, "[ZXMC] 忽略刚切页时的壁纸选择请求"); return; }

        Logger.Log(LogLevel.Info, "[ZXMC] 打开文件选择框:背景壁纸\n" + Environment.StackTrace);

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择背景壁纸",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] },
            ],
        });

        if (files.Count > 0)
        {
            Vm?.SelectWallpaper(files[0].Path.LocalPath);
        }
    }

    /// <summary>个性化 → 恢复默认底色。</summary>
    private void OnClearWallpaperClick(object? sender, RoutedEventArgs e) => Vm?.ClearWallpaper();

    /// <summary>个性化 → 切换配色。</summary>
    private void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && Vm is not null)
        {
            Vm.SelectTheme(name);
        }
    }

    /// <summary>账户与安全 → 更换头像。</summary>
    private async void OnPickAvatarClick(object? sender, RoutedEventArgs e)
    {
        // 三重保险,同上
        if (Vm is null || !Vm.IsSecurityPage) return;
        if (sender is Button button && !button.IsEffectivelyVisible) return;
        if (Vm.IsJustSwitched) { Logger.Log(LogLevel.Warning, "[ZXMC] 忽略刚切页时的头像选择请求"); return; }

        Logger.Log(LogLevel.Info, "[ZXMC] 打开文件选择框:头像\n" + Environment.StackTrace);

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择头像",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] }],
        });

        if (files.Count == 0)
        {
            return;
        }

        // 先开 QQ 式框选窗口裁剪,确定后拿到 PNG 字节
        var crop = new AvatarCropWindow(files[0].Path.LocalPath);
        var owner = TopLevel.GetTopLevel(this) as Window;
        var bytes = owner is null ? null : await crop.ShowDialog<byte[]?>(owner);

        if (bytes is { Length: > 0 })
        {
            await Vm.SetAvatarAsync(bytes);
        }
    }

    /// <summary>账户与安全 → 删除头像。</summary>
    private void OnClearAvatarClick(object? sender, RoutedEventArgs e) => Vm?.ClearAvatar();

    /// <summary>账户与安全 → 退出登录(关主界面,回登录窗口)。</summary>
    private async void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        await Vm.LogoutAsync();

        var owner = TopLevel.GetTopLevel(this) as Window;
        var login = new LoginViewModel();
        ClientSession.Connect = login.Connect;

        var loginWindow = new LoginWindow { DataContext = login };
        WindowPlacement.PlaceAt(loginWindow, owner);
        loginWindow.Show();
        owner?.Close();
    }
}
