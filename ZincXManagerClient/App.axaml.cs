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
            var viewModel = new MainViewModel();

#if DEBUG
            // 仅调试预览:加载设计稿里的示例消息;正式运行由服务端数据填充 Messages / 调用 SetUser
            viewModel.LoadSampleMessages();
#endif

            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
