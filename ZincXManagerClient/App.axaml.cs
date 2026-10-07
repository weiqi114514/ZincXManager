using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ZincXManagerClient.ViewModels;
using ZincXManagerClient.Views;

namespace ZincXManagerClient;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 启动先进连接界面;连上之后可以点"进入主界面"打开主窗口
            desktop.MainWindow = new ConnectWindow
            {
                DataContext = new ConnectViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
