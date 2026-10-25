using System.Collections.Specialized;  // 集合变更通知
using Avalonia.Controls;  // 窗口与控件
using ZincXManagerShared;  // 共享库
using ZincXManagerShared.Logging;  // 共享库
using Avalonia.Input;  // 鼠标键盘输入
using Avalonia.Interactivity;  // 路由事件
using ZincXManagerClient.ViewModels;  // 客户端自身

namespace ZincXManagerClient.Views;

public partial class ConnectWindow : Window
{
    /// <summary>连接窗口的构造函数:初始化界面与事件。</summary>
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

    /// <summary>点最小化按钮:窗口最小化。</summary>
    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>点右上角关闭按钮:关掉这个窗口。</summary>
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

    /// <summary>按当前模式和目标地址连接。</summary>
    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        // 已经连在这个目标上:不重复连,弹窗提醒
        if (Vm.IsConnectedToSameTarget)
        {
            await MessageDialog.InfoAsync(this, "已经连接",
                $"当前已经连在 {Vm.ConnectedHost}:{Vm.ConnectedPort},不用重复连接。\n要换目标请先断开,或直接改地址后再点连接。",
                hint: "同一目标不会重复建立连接");
            return;
        }

        // 连在别的目标上:ConnectAsync 会先断开旧连接再连新目标
        await Vm.ConnectAsync();
    }

    /// <summary>断开当前连接。</summary>
    private async void OnDisconnectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) await Vm.DisconnectAsync();
    }

    /// <summary>清空日志。</summary>
    private void OnClearLogClick(object? sender, RoutedEventArgs e) => Vm?.ClearLogs();

    /// <summary>调官方端连接:先给 5 秒风险说明,同意后填 IP:端口,写进 config 并刷新 OOS 目标</summary>
    private async void OnAdjustOfficialClick(object? sender, RoutedEventArgs e)
    {
        var owner = this;

        var agreed = await MessageDialog.ReadConfirmAsync(owner, "改用第三方官方服务端?",
            "你正在把「官方服务端」改成别人搭的服务器。\n\n" +
            "改完之后,你的这些信息都会发到那台机器上,有被窃取或滥用的风险:\n" +
            "· 账号名与密码凭据(虽然传的是密文,对方仍可能拿去离线破解、撞库试别的站)\n" +
            "· 你的机器码、服务端码、IP 与所在网络\n" +
            "· 你能收到的公告、推送与版本更新提示(这些都可以被伪造)\n\n" +
            "对方还能:冒充官方给你发通知、发假更新骗你下载、把你踢下线或封你的号。\n" +
            "第三方服务端还可能被私下修改过,无法保证你的隐私安全;" +
            "甚至有改过代码后伪装成官方端的(在线验证会因此失败)。\n" +
            "而且 ZXMOS 专有的跨子服务端功能、以及其他社区功能可能用不了 —— 那些只认官方服务端。\n\n" +
            "另外别搞混:官方服务端(ZXMOS)和子服务端(ZXMS)不是一个东西 —— " +
            "这里改的只是「你连哪台官方服务端」;子服务端是你自己或别人搭的中转服务器,在另一边设置。\n\n" +
            "只有当你完全清楚那是谁的服务器、并且愿意承担以上风险时,才继续。",
            seconds: 5, hint: "官方服务端永远是 zxm.zincms.top:25000");

        if (!agreed)
        {
            Logger.Log(LogLevel.Info, "[ZXMC] 用户没有确认第三方官方服务端警告,保持原地址");
            return;
        }

        var current = ClientConfig.Get("ozip", OfficialServer.Host) + ":" + ClientConfig.Get("ozport", OfficialServer.PortText);
        var value = await MessageDialog.AskAsync(owner, "自定义官方服务端地址",
            "填 IP:端口(例如 127.0.0.1:25000)", "请输入 IP:端口", "保存", "", current);

        if (value is null || value.Trim().Length == 0)
        {
            return;
        }

        var text = value.Trim();
        var idx = text.LastIndexOf(':');
        if (idx <= 0 || idx == text.Length - 1 || text.IndexOf(':') != idx)
        {
            await MessageDialog.InfoAsync(owner, "地址不合法", "格式应为 IP:端口(只允许一个冒号)",
                hint: "例:zxm.zincms.top:25000 或 127.0.0.1:25000");
            return;
        }

        var ip = text[..idx].Trim();   //玩家IP
        var portText = text[(idx + 1)..].Trim();
        if (!ushort.TryParse(portText, out var port) || port == 0)
        {
            await MessageDialog.InfoAsync(owner, "端口不合法", "端口应是 1-65535 的数字", hint: "例:25000");
            return;
        }

        ClientConfig.Set("ozip", ip);
        ClientConfig.Set("ozport", port.ToString());
        Logger.Log(LogLevel.Warning, $"[ZXMC] 官方服务端地址已改为 {ip}:{port}");

        Vm?.ReloadOfficialTarget();
        await MessageDialog.InfoAsync(owner, "已保存", $"官方服务端地址已改为 {ip}:{port}。", hint: "");
    }

    /// <summary>进主界面。</summary>
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
