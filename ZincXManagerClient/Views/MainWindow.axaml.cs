using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ZincXManagerClient.Models;
using ZincXManagerClient.ViewModels;
using ZincXManagerShared.Logging;

namespace ZincXManagerClient.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 窗口材质只在这里定一次:运行中改 TransparencyLevelHint 会让 Windows 重建窗口,
        // 屏幕上会闪出"像多了一个窗口"的东西,所以开关只换材料、不动材质
        ThemeManager.ApplyWindowMaterial(this);

        Opened += (_, _) => ApplyMaterial(ThemeManager.GlassEnabled);
        ThemeManager.GlassChanged += ApplyMaterial;
        Closed += (_, _) => ThemeManager.GlassChanged -= ApplyMaterial;
    }


    /// <summary>
    /// 开关只换"材料",不碰窗口材质:
    /// 亚克力 = 壁纸按 Fluent 模糊 30;半透明 = 不模糊、表面 60% —— 就是单纯的半透明。
    /// 透明窗口必须给一个"几乎全透明的底",DWM 才会持续合成,不会残留上一帧(那看着就像复制了一层窗口)。
    /// </summary>
    private void ApplyMaterial(bool acrylic)
    {
        // BlurEffect 不是控件、拿不到 x:Name 字段,所以直接给壁纸这一层换效果
        WallpaperImage.Effect = acrylic ? new BlurEffect { Radius = ThemeManager.GlassBlurRadius } : null;

        Background = acrylic
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>标题栏拖动窗口;双击在最大化 / 还原之间切换。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnNavItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: NavItem item }) return;

        // 只切页面;背景图在 设置 → 个性化 里选(以前那段"借设置入口挑背景图"的临时代码已删)
        Vm?.SelectNav(item);
    }

    private void OnCardActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string action })
        {
            Vm?.InvokeAction(action);
        }
    }

    /// <summary>帮助:打开官网帮助页。</summary>
    private async void OnHelpClick(object? sender, RoutedEventArgs e)
        => await OpenUrlAsync("http://zxm.zincms.top:10/help.html", "帮助");

    /// <summary>反馈:打开 GitHub 仓库的 issues。</summary>
    private async void OnFeedbackClick(object? sender, RoutedEventArgs e)
        => await OpenUrlAsync("https://github.com/weiqi114514/ZincXManager/issues", "反馈");

    /// <summary>用系统默认浏览器打开链接(Avalonia 12 里 Launcher 是 TopLevel 的实例属性)。</summary>
    private async Task OpenUrlAsync(string url, string what)
    {
        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is null)
            {
                Logger.Log(LogLevel.Warning, $"[ZXMC] 拿不到 Launcher,无法打开{what}: {url}");
                return;
            }

            await launcher.LaunchUriAsync(new Uri(url));
            Logger.Log(LogLevel.Info, $"[ZXMC] 打开{what}: {url}");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, $"[ZXMC] 打开{what}失败 {url}: {ex.Message}");
        }
    }
}
