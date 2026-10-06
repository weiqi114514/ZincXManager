using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ZincXManagerClient.Models;
using ZincXManagerClient.ViewModels;
using ZincXManagerShared.Logging;

namespace ZincXManagerClient.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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

    private async void OnNavItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: NavItem item }) return;

        Vm?.SelectNav(item);

        // 设置页面还没做,先借「设置」这个入口让用户挑背景图
        if (item.Label == "设置" && Vm is not null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择窗口背景图",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("图片")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif" }
                    }
                }
            });

            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (!string.IsNullOrEmpty(path)) Vm.SetBackgroundImage(path);
        }
    }

    private void OnCardActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string action })
        {
            Vm?.InvokeAction(action);
        }
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e) => Logger.Log(LogLevel.Info, "[ZXMC] 点击「帮助」");

    private void OnFeedbackClick(object? sender, RoutedEventArgs e) => Logger.Log(LogLevel.Info, "[ZXMC] 点击「反馈」");
}
