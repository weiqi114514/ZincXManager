using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZincXManagerClient.ViewModels;

namespace ZincXManagerClient.Views;

public partial class ConnectWindow : Window
{
    public ConnectWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);   // 半透明 / 亚克力:开窗时定一次

        // 连不上 / 模式不匹配:弹窗告诉用户
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ConnectViewModel vm)
            {
                vm.ErrorRaised += (title, text) =>
                {
                    if (vm.SuppressErrorDialog)
                    {
                        return;
                    }

                    _ = MessageDialog.InfoAsync(this, title, text, hint: "检查数据源地址与连接方式");
                };
            }
        };
    }

    private ConnectViewModel? Vm => DataContext as ConnectViewModel;

    /// <summary>窗口初始化完成后挂上日志自动滚动。</summary>
    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);

        if (Vm is not null)
        {
            Vm.Logs.CollectionChanged += OnLogsChanged;
        }
    }

    /// <summary>窗口关闭时摘掉日志目标,避免 Logger 继续往已销毁的界面写。</summary>
    protected override void OnClosed(System.EventArgs e)
    {
        if (Vm is not null)
        {
            Vm.Logs.CollectionChanged -= OnLogsChanged;

            // 会话里持有的连接不释放,否则关掉连接窗口就断线了
            if (!ReferenceEquals(ClientSession.Connect, Vm))
            {
                Vm.Dispose();
            }
        }

        base.OnClosed(e);
    }

    /// <summary>新日志进来就滚到底部,保证"实时"看着不累。</summary>
    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        LogScroll.ScrollToEnd();
    }

    /// <summary>标题栏拖动;双击最大化/还原。</summary>
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

    /// <summary>切换连接模式(按钮的 Tag 就是模式名);重复点同一个模式也重填一次目标地址。</summary>
    private void OnModeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode } && Vm is not null)
        {
            Vm.Mode = mode;
            Vm.ApplyModeTarget();
        }
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) await Vm.ConnectAsync();
    }

    private async void OnDisconnectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) await Vm.DisconnectAsync();
    }

    private void OnClearLogClick(object? sender, RoutedEventArgs e) => Vm?.ClearLogs();

    /// <summary>连上之后进入主界面(原来的主界面,连接界面关闭)。</summary>
    private void OnOpenMainClick(object? sender, RoutedEventArgs e)
    {
        // 连接好之后先进登录界面;登录成功再由登录窗口进主界面(不在这里直进主界面)
        if (Vm is null)
        {
            return;
        }

        var login = new LoginViewModel(Vm);
        var loginWindow = new LoginWindow { DataContext = login };
        WindowPlacement.PlaceAt(loginWindow, this);
        loginWindow.Show();
        Close();
    }
}
