using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ZincXManagerClient.ViewModels;
using ZincXManagerClient.Views;

namespace ZincXManagerClient;

public partial class App : Application
{
    /// <summary>连接视图模型(挂到会话上,别被回收,否则连接会断)。</summary>
    private ConnectViewModel? _connect;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 先应用配色,再建窗口(否则第一帧会是默认色)
        ThemeManager.ApplySaved();

        // 截图 / 调试用:config 里写 cropdemo=<图片路径> 就直接开裁剪窗口
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime cropLife
            && ClientConfig.GetOrEmpty("cropdemo") is { Length: > 0 } cropPath)
        {
            cropLife.MainWindow = new AvatarCropWindow(cropPath);
            base.OnFrameworkInitializationCompleted();
            return;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // ① 没走过首次引导 → OOBE(欢迎 → 个性化 → 数据源与模式 → 登录/注册)
            if (ClientConfig.GetOrEmpty("oobe").Length == 0)
            {
                var firstWindow = new OobeWindow { DataContext = new OobeViewModel() };
                WindowPlacement.CenterOnPrimaryScreen(firstWindow);
                desktop.MainWindow = firstWindow;
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // ② 没有数据源参数 → 连接界面
            if (!HasDataSourceConfig())
            {
                _connect = new ConnectViewModel();
                ClientSession.Connect = _connect;
                var firstWindow = new ConnectWindow { DataContext = _connect };
                WindowPlacement.CenterOnPrimaryScreen(firstWindow);
                desktop.MainWindow = firstWindow;
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // ③ 有 7 天内的登录记录(且记住了密码)→ 直接主界面 + 后台免登录
            if (LoginRecord.CanAutoLogin())
            {
                var login = new LoginViewModel();
                _connect = login.Connect;
                ClientSession.Connect = _connect;

                var mainViewModel = new MainViewModel();
                var mainWindow = new MainWindow { DataContext = mainViewModel };
                WindowPlacement.CenterOnPrimaryScreen(mainWindow);
                AppWindows.Register(mainWindow);
                desktop.MainWindow = mainWindow;
                _ = AutoLoginAsync(login, mainViewModel, mainWindow);

                base.OnFrameworkInitializationCompleted();
                return;
            }

            // 调试 / 截图用:config 里写 skipauth=1 就直接进主界面(不连服务端)
            if (ClientConfig.GetOrEmpty("skipauth") == "1")
            {
                _connect = new ConnectViewModel();
                ClientSession.Connect = _connect;
                var skipWindow = new MainWindow { DataContext = new MainViewModel() };
                WindowPlacement.CenterOnPrimaryScreen(skipWindow);
                AppWindows.Register(skipWindow);
                desktop.MainWindow = skipWindow;
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // ④ 其他情况 → 弹登录窗口
            var loginWindow = new LoginViewModel();
            _connect = loginWindow.Connect;
            ClientSession.Connect = _connect;
            var firstLogin = new LoginWindow { DataContext = loginWindow };
            WindowPlacement.CenterOnPrimaryScreen(firstLogin);
            desktop.MainWindow = firstLogin;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 判断配置里有没有"数据源连接参数":按当前连接方式取对应那一组地址 + 端口。
    /// 用 GetOrEmpty,不会顺手把缺省值写进 config.ini,所以"配过"和"没配过"分得清。
    /// </summary>
    private static bool HasDataSourceConfig()
    {
        var official = ClientConfig.GetOrEmpty("mode") == "OOS";

        var ip = ClientConfig.GetOrEmpty(official ? "ozip" : "czip");
        var port = ClientConfig.GetOrEmpty(official ? "ozport" : "czport");

        return ip.Length > 0 && port.Length > 0;
    }

    /// <summary>用记住的密文免登录;成功就用主界面,失败就把登录窗口放出来。</summary>
    private static async Task AutoLoginAsync(LoginViewModel login, MainViewModel main, Window mainWindow)
    {
        main.StatusText = $"正在免登录(登录记录 {LoginRecord.DaysLeft()} 天内有效)…";

        if (await login.AutoLoginAsync())
        {
            main.StatusText = $"已登录:{ClientSession.User}({ClientSession.Level})";
            return;
        }

        main.StatusText = "免登录失败,已打开登录窗口";

        var loginWindow = new LoginWindow { DataContext = login };
        WindowPlacement.PlaceAt(loginWindow, mainWindow);
        loginWindow.Show();
    }
}
