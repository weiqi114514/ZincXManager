using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZincXManagerClient.ViewModels;

namespace ZincXManagerClient.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);   // 半透明 / 亚克力:开窗时定一次
    }

    private LoginViewModel? Vm => DataContext as LoginViewModel;

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        if (await Vm.LoginAsync())
        {
            EnterMain();
        }
    }

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
