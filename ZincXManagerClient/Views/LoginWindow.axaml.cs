using Avalonia.Controls;  // 窗口与控件
using Avalonia.Input;  // 鼠标键盘输入
using Avalonia.Interactivity;  // 路由事件
using ZincXManagerClient.ViewModels;  // 客户端自身

namespace ZincXManagerClient.Views;

public partial class LoginWindow : Window
{
    /// <summary>登录窗口的构造函数。</summary>
    public LoginWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);   // 半透明 / 亚克力:开窗时定一次

        // 出错(被封禁 / 连不上服务器)弹窗告诉用户,不再只写一行状态文字
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LoginViewModel vm)
            {
                vm.ErrorRaised += (title, text) => _ = MessageDialog.InfoAsync(this, title, text, hint: "");
            }
        };
    }

    private LoginViewModel? Vm => DataContext as LoginViewModel;

    /// <summary>按住标题栏拖动窗口。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
    }

    /// <summary>点最小化按钮:窗口最小化。</summary>
    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>点右上角关闭按钮:关掉这个窗口。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        if (await Vm.LoginAsync())
        {
            EnterMain();
        }
    }

    /// <summary>注册账号(等级固定 Player)。</summary>
    private async void OnRegisterClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        if (await Vm.RegisterAsync())
        {
            EnterMain();
        }
    }

    /// <summary>登录成功:连接交给会话,打开主界面。</summary>
    private void EnterMain()
    {
        if (Vm is null) return;

        ClientSession.Connect = Vm.Connect;

        AppWindows.ShowMain(this);
    }
}
