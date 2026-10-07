using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ZincXManagerShared.Logging;
using ZincXManagerClient.ViewModels;

namespace ZincXManagerClient.Views;

public partial class OobeWindow : Window
{
    public OobeWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);   // 半透明 / 亚克力:开窗时定一次
    }

    private OobeViewModel? Vm => DataContext as OobeViewModel;

    /// <summary>标题栏拖动。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>选择背景壁纸。</summary>
    private async void OnPickWallpaperClick(object? sender, RoutedEventArgs e)
    {
        // 只有"个性化"这一步、且按钮确实可见时才处理
        if (Vm is null || !Vm.IsPersonalize) return;
        if (sender is Button button && !button.IsEffectivelyVisible) return;

        Logger.Log(LogLevel.Info, "[ZXMC] 打开文件选择框:OOBE 背景壁纸\n" + Environment.StackTrace);

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择背景壁纸",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] }],
        });

        if (files.Count > 0)
        {
            Vm?.SelectWallpaper(files[0].Path.LocalPath);
        }
    }

    private void OnClearWallpaperClick(object? sender, RoutedEventArgs e) => Vm?.ClearWallpaper();

    /// <summary>个性化 → 切换配色。</summary>
    private void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && Vm is not null)
        {
            Vm.SelectTheme(name);
        }
    }

    /// <summary>切换连接方式。</summary>
    private void OnModeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode })
        {
            Vm?.SelectMode(mode);
        }
    }

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) await Vm.LoginAsync();
    }

    private async void OnRegisterClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) await Vm.RegisterAsync();
    }

    /// <summary>上一步。</summary>
    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && Vm.Step > 0)
        {
            Vm.Step--;
        }
    }

    /// <summary>下一步 / 完成。</summary>
    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        // 最后一步必须先登录或注册
        if (Vm.Step == OobeViewModel.StepCount - 1 && !Vm.IsLoggedIn)
        {
            Vm.Message = "请先登录或注册,再点完成";
            return;
        }

        if (Vm.Step < OobeViewModel.StepCount - 1)
        {
            Vm.Step++;
            return;
        }

        // 完成:存配置 → 连接交给会话 → 进主界面
        Vm.Finish();
        ClientSession.Connect = Vm.Connect;

        AppWindows.ShowMain(this);
    }
}
