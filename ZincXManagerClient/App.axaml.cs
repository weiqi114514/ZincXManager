using System;  // 基础类型 / 常用静态方法
using System.Collections.Generic;  // 泛型集合
using System.Threading.Tasks;  // async / await 异步
using Avalonia;  // 界面框架入口
using Avalonia.Controls;  // 窗口与控件
using Avalonia.Controls.ApplicationLifetimes;  // 桌面应用生命周期
using Avalonia.Markup.Xaml;  // XAML 加载
using ZincXManagerClient.ViewModels;  // 客户端自身
using ZincXManagerClient.Views;  // 客户端自身

namespace ZincXManagerClient;

public partial class App : Application
{
    /// <summary>连接视图模型(挂到会话上,别被回收,否则连接会断)。</summary>
    private ConnectViewModel? _connect;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>单实例互斥:避免开两个客户端各连一条,服务端里出现重复连接。</summary>
    private static readonly System.Threading.Mutex SingleInstanceMutex =
        new(false, @"Local\ZincXManagerClient.SingleInstance");

    public override void OnFrameworkInitializationCompleted()
    {
        // 已经有一个在跑 → 直接退出(新开的这个不留)
        // 注意:客户端没有控制台,这句一定要落日志,不然"双击没反应"完全查不到原因
        // 测试要同时开多个客户端(套件9/11 会各起一个),给个开关跳过互斥
        var skipSingleInstance = Environment.GetEnvironmentVariable("ZXM_NO_SINGLE_INSTANCE") == "1";
        if (!skipSingleInstance && !SingleInstanceMutex.WaitOne(0))
        {
            ZincXManagerShared.Logging.Logger.Log(ZincXManagerShared.Logging.LogLevel.Warning,
                "[ZXMC] 已有一个客户端在运行,本次启动退出(单实例互斥)");
            Environment.Exit(0);
            return;
        }

        ZincXManagerShared.Logging.Logger.Log(ZincXManagerShared.Logging.LogLevel.Info,
            "[ZXMC] 单实例互斥已拿到,开始初始化界面");
        // 先把 config 里存的配色 / 透明样式应用一次,否则所有窗口都是写死的默认蓝
        ThemeManager.ApplySaved();

        // 启动先把上次登录的账号填进会话(不然免登录握手完成前,主界面左下角是「未登录」)
        ClientSession.SeedFromConfig();

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

            // 维护 / 截图用:skipauth=1 直接进主界面(不连服务端)
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

            // ③ 打开客户端先连数据源:连不上就停在连接界面(弹窗给 host:port + 具体异常);
            //    连上了才进登录界面 / 免登录主界面
            var probe = new ConnectViewModel();
            _connect = probe;
            ClientSession.Connect = probe;

            var waitWindow = new StartupWindow($"{probe.Host}:{probe.Port}");
            WindowPlacement.CenterOnPrimaryScreen(waitWindow);
            desktop.MainWindow = waitWindow;

            waitWindow.Opened += (_, _) => _ = StartAfterProbeAsync(desktop, probe, waitWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>判断配置里有没有"数据源连接参数":按当前连接方式取对应那一组地址 + 端口。 用 GetOrEmpty,不会顺手把缺省值写进 config.ini,所以"配过"和"没配过"分得清。</summary>
    private static bool HasDataSourceConfig()
    {
        var official = ClientConfig.GetOrEmpty("mode") == "OOS";

        var ip = ClientConfig.GetOrEmpty(official ? "ozip" : "czip");
        var port = ClientConfig.GetOrEmpty(official ? "ozport" : "czport");

        return ip.Length > 0 && port.Length > 0;
    }

    /// <summary>打开客户端时的数据源自检:连得上返回 null,连不上返回错误说明。 探测期间把 ErrorRaised 收走,避免和界面自己的弹窗重复。</summary>
    private static async Task<string?> ProbeDataSourceAsync(ConnectViewModel vm)
    {
        if (vm.IsConnected)
        {
            return null;
        }

        var errors = new List<string>();
        void OnError(string title, string text) => errors.Add(text);

        vm.ErrorRaised += OnError;
        try
        {
            await vm.ConnectAsync();

            // 连接本身就被拒(TCP 都连不上)就不用再等满 6 秒
            if (!vm.IsConnected)
            {
                await Task.Delay(300);
                if (!vm.IsConnected)
                {
                    return errors.Count > 0 ? errors[^1] : "无法建立连接(地址或端口不对,或服务端没启动)";
                }
            }

            if (await vm.WaitConnectedAsync(6000))
            {
                return null;
            }

            return errors.Count > 0 ? errors[^1] : "6 秒内没有完成握手(服务端无响应或地址/端口不对)";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            vm.ErrorRaised -= OnError;
        }
    }

    /// <summary>启动先连数据源:连上才进登录/主界面,连不上停在连接界面</summary>
    private static async Task StartAfterProbeAsync(IClassicDesktopStyleApplicationLifetime desktop, ConnectViewModel vm, Window waitWindow)
    {
        vm.SuppressErrorDialog = true;   // 探测期间的错误由这里统一弹,界面别再弹一次
        var error = await ProbeDataSourceAsync(vm);
        vm.SuppressErrorDialog = false;

        if (error is not null)
        {
            // 连不上:换成完整连接窗口 + 弹「连接 ZXMS 失败」/「连接 ZXMOS 失败」
            var target = vm.Mode == "OOS" ? "ZXMOS" : "ZXMS";

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                var connectWindow = new ConnectWindow { DataContext = vm };
                WindowPlacement.CenterOnPrimaryScreen(connectWindow);
                desktop.MainWindow = connectWindow;
                connectWindow.Show();
                waitWindow.Close();

                _ = MessageDialog.InfoAsync(connectWindow, $"连接 {target} 失败",
                    $"{vm.Host}:{vm.Port}\n{error}",
                    hint: "检查数据源地址、端口,确认服务端已启动后重试");
            });

            return;
        }

        // 连上了:进登录窗口(有免登录记录就直接主界面)
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (LoginRecord.CanAutoLogin())
            {
                var autoLogin = new LoginViewModel(vm);
                var mainViewModel = new MainViewModel();
                var mainWindow = new MainWindow { DataContext = mainViewModel };
                WindowPlacement.CenterOnPrimaryScreen(mainWindow);
                AppWindows.Register(mainWindow);
                desktop.MainWindow = mainWindow;
                mainWindow.Show();
                waitWindow.Close();
                _ = AutoLoginAsync(autoLogin, mainViewModel, mainWindow);
                return;
            }

            var login = new LoginViewModel(vm);
            var loginWindow = new LoginWindow { DataContext = login };
            WindowPlacement.CenterOnPrimaryScreen(loginWindow);
            desktop.MainWindow = loginWindow;
            loginWindow.Show();
            waitWindow.Close();
        });
    }

    /// <summary>用记住的密文免登录;成功就用主界面,失败就把登录窗口放出来。</summary>
    private static async Task AutoLoginAsync(LoginViewModel login, MainViewModel main, Window mainWindow)
    {
        main.StatusText = $"正在免登录(登录记录 {LoginRecord.DaysLeft()} 天内有效)…";

        if (await login.AutoLoginAsync())
        {
            // 主界面是登录前就建好的 —— 会话数据要补推给它,否则左下角会一直停在「未登录」
            main.SetUser(ClientSession.User, ClientSession.UserId);
            main.RefreshAvatar();
            _ = main.RequestMessageHistoryAsync();
            main.StatusText = $"已登录:{ClientSession.User}({ClientSession.Level})";

            // 头像(605)是登录之后才回来的:一秒后再补刷一次
            _ = Task.Delay(1200).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(main.RefreshAvatar));
            return;
        }

        main.StatusText = "免登录失败,已打开登录窗口";

        var loginWindow = new LoginWindow { DataContext = login };
        WindowPlacement.PlaceAt(loginWindow, mainWindow);
        loginWindow.Show();
    }
}
