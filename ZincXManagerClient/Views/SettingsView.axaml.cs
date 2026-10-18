using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ZincXManagerShared.Logging;
using ZincXManagerClient.Models;
using ZincXManagerClient.ViewModels;

namespace ZincXManagerClient.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 走一遍"改用自定义(第三方)官方服务端"的流程:风险说明(读满 5 秒)→ 填地址。
    /// 调试 / 自动化用(和 skipauth / startpage 一个路子,由主窗口在 <c>testaction=customaddress</c> 时调)。
    /// </summary>
    public async Task OpenCustomAddressFlowAsync()
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await HandleOfficialSourceChangeAsync(owner);
        }
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    /// <summary>切换连接方式(SAOS / OS / OOS);这项就是 ZXMC 模式,与连接界面共用同一配置。</summary>
    private void OnModeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode })
        {
            Vm?.SelectMode(mode);
        }
    }

    /// <summary>勾回「官方服务端(默认)」:地址恢复成写死的那个。</summary>
    private void OnOfficialAddressClick(object? sender, RoutedEventArgs e)
    {
        Vm?.UseOfficialAddress();
    }

    /// <summary>
    /// 勾「自定义 IP:端口」:先弹一段"第三方官方服务端的风险"说明,<b>读满 5 秒才能确认</b>;
    /// 确认之后才允许填地址(否则自动勾回官方那一项)。
    /// </summary>
    private async void OnCustomAddressClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        await HandleOfficialSourceChangeAsync(owner);
    }

    /// <summary>
    /// 「自定义官方服务端」这条路的公共逻辑:已经是自定义就直接填地址;
    /// 否则先弹风险说明(读满 5 秒才能确认),同意才允许改,不同意自动勾回官方。
    /// </summary>
    private async Task HandleOfficialSourceChangeAsync(Window owner)
    {
        if (Vm is null)
        {
            return;
        }

        if (Vm.UseCustomAddress)
        {
            await EditOfficialAddressAsync(owner);
            return;
        }

        // 弹窗是纯文本(不渲染 Markdown),所以这里只用「」和分段,不要写 ** 之类的标记
        Logger.Log(LogLevel.Info, "[ZXMC] 已弹出第三方官方服务端风险说明(必须读满 5 秒才能确认)");

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
            Vm.UseOfficialAddress();
            Logger.Log(LogLevel.Info, "[ZXMC] 用户没有确认第三方官方服务端警告,保持官方默认地址");
            return;
        }

        Vm.UseCustomAddress = true;
        Logger.Log(LogLevel.Warning, "[ZXMC] 用户已确认第三方官方服务端风险,进入自定义地址");
        await EditOfficialAddressAsync(owner);
    }

    /// <summary>填官方服务端地址(自定义那一项)。</summary>
    private async Task EditOfficialAddressAsync(Window owner)
    {
        if (Vm is null)
        {
            return;
        }

        var value = await MessageDialog.AskAsync(owner, "自定义官方服务端地址",
            "填 IP:端口(例如 127.0.0.1:25000)",
            "请输入 IP:端口", "保存", "", Vm.CurrentAddress);

        if (value is null)
        {
            return;
        }

        if (Vm.SetAddress(value) is { } error)
        {
            await MessageDialog.InfoAsync(owner, "地址不合法", error, hint: "例:zxm.zincms.top:25000 或 127.0.0.1:25000");
            return;
        }

        await MessageDialog.InfoAsync(owner, "已保存",
            $"官方服务端地址已改为 {value}。\n以后想换回官方默认,勾「官方服务端(默认)」就行。", hint: "");
    }

    /// <summary>改数据源地址:填空弹窗 → 写进 config.ini。</summary>
    private async void OnEditAddressClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        if (Vm.Mode == "OOS")
        {
            // 官方那一组走"单选框 + 5 秒警告"那条路(它自己会处理"已经是自定义"的情况)
            await HandleOfficialSourceChangeAsync(owner);
            return;
        }

        var value = await MessageDialog.AskAsync(owner, "改数据源地址",
            "填 IP:端口(例如 127.0.0.1:21000)",
            "请输入 IP:端口", "保存", "", Vm.CurrentAddress);

        if (value is null)
        {
            return;
        }

        if (Vm.SetAddress(value) is { } error)
        {
            await MessageDialog.InfoAsync(owner, "地址不合法", error, hint: "例:yourserver.com:21000 或 127.0.0.1:21000");
            return;
        }

        await MessageDialog.InfoAsync(owner, "已保存", $"数据源地址已改为 {value}。", hint: "");
    }

    /// <summary>切换连接方式:选择弹窗。</summary>
    private async void OnSelectModeClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var mode = await MessageDialog.ChooseAsync(owner, "切换连接方式",
            "SAOS = 子服务端 + 官方服务端(先连子服务端,由它转发)" + Environment.NewLine +
            "OS = 只连子服务端" + Environment.NewLine +
            "OOS = 只连官方服务端",
            ["SAOS", "OS", "OOS"], "切换", "");

        if (mode is not null)
        {
            Vm.SelectMode(mode);
        }
    }

    /// <summary>清除本地登录记录:双按钮确认。</summary>
    private async void OnClearRecordClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var ok = await MessageDialog.ConfirmAsync(owner, "清除本地登录记录",
            "会清掉本机记住的密码与免登录设置,账号名保留。",
            "清除", "取消", "");

        if (ok)
        {
            Vm.ClearLoginRecord();
            await MessageDialog.InfoAsync(owner, "已清除", "本地登录记录已清除。", hint: "");
        }
    }

    /// <summary>切换左侧二级导航。</summary>
    // ---------- 关于页:外链与目录 ----------

    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ZincXManagerShared.Logging.Logger.Log(ZincXManagerShared.Logging.LogLevel.Warning, "[ZXMC] 打开链接失败: " + ex.Message);
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            if (!System.IO.Directory.Exists(path))
            {
                System.IO.Directory.CreateDirectory(path);
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ZincXManagerShared.Logging.Logger.Log(ZincXManagerShared.Logging.LogLevel.Warning, "[ZXMC] 打开目录失败: " + ex.Message);
        }
    }

    private void OnOpenWebsiteClick(object? sender, RoutedEventArgs e) => OpenUrl(Vm?.WebsiteUrl ?? "");

    private void OnOpenDocsClick(object? sender, RoutedEventArgs e) => OpenUrl(Vm?.DocsUrl ?? "");

    private void OnOpenRepoClick(object? sender, RoutedEventArgs e) => OpenUrl(Vm?.RepoUrl ?? "");

    private void OnOpenChangelogClick(object? sender, RoutedEventArgs e) => OpenUrl(Vm?.ChangelogUrl ?? "");

    private void OnOpenConfigFolderClick(object? sender, RoutedEventArgs e)
    {
        var p = Vm?.ConfigPath;
        if (!string.IsNullOrEmpty(p))
        {
            var dir = System.IO.Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) OpenFolder(dir);
        }
    }

    private void OnOpenLogFolderClick(object? sender, RoutedEventArgs e) => OpenFolder(Vm?.LogDirectory ?? "");

    private void OnSubNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SubNavItem item })
        {
            Vm?.SelectSection(item);
        }
    }

    /// <summary>"重启以应用"。</summary>
    private void OnApplyRestartClick(object? sender, RoutedEventArgs e) => Vm?.ApplyRestart();

    /// <summary>"启动数据源连接流程":打开连接界面。</summary>
    private void OnStartFlowClick(object? sender, RoutedEventArgs e)
    {
        Vm?.StartDataSourceFlow();

        var owner = TopLevel.GetTopLevel(this) as Window;

        // 会话持有这条连接:否则连接窗口一关,OnClosed 会把它当"临时连接"Dispose 掉(断线)
        var connect = new ConnectViewModel();
        ClientSession.Connect = connect;

        var window = new ConnectWindow { DataContext = connect };
        if (owner is not null)
        {
            WindowPlacement.PlaceAt(window, owner);
        }

        window.Show();

        // 只留连接流程:主界面窗口关掉(否则两个窗口叠在一起)
        if (owner is MainWindow)
        {
            owner.Close();
        }
    }

    /// <summary>个性化 → 选择背景壁纸。</summary>
    private async void OnPickWallpaperClick(object? sender, RoutedEventArgs e)
    {
        // 三重保险:① 当前确实是"个性化"页 ② 按钮真的可见 ③ 不是切页瞬间的余波
        if (Vm is null || !Vm.IsPersonalizePage) return;
        if (sender is Button button && !button.IsEffectivelyVisible) return;
        if (Vm.IsJustSwitched) { Logger.Log(LogLevel.Warning, "[ZXMC] 忽略刚切页时的壁纸选择请求"); return; }

        Logger.Log(LogLevel.Info, "[ZXMC] 打开文件选择框:背景壁纸\n" + Environment.StackTrace);

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择背景壁纸",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] },
            ],
        });

        if (files.Count > 0)
        {
            Vm?.SelectWallpaper(files[0].Path.LocalPath);
        }
    }

    /// <summary>个性化 → 恢复默认底色。</summary>
    private void OnClearWallpaperClick(object? sender, RoutedEventArgs e) => Vm?.ClearWallpaper();

    /// <summary>个性化 → 切换配色。</summary>
    private void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && Vm is not null)
        {
            Vm.SelectTheme(name);
        }
    }

    /// <summary>账户与安全 → 更换头像。</summary>
    private async void OnPickAvatarClick(object? sender, RoutedEventArgs e)
    {
        // 三重保险,同上
        if (Vm is null || !Vm.IsSecurityPage) return;
        if (sender is Button button && !button.IsEffectivelyVisible) return;
        if (Vm.IsJustSwitched) { Logger.Log(LogLevel.Warning, "[ZXMC] 忽略刚切页时的头像选择请求"); return; }

        Logger.Log(LogLevel.Info, "[ZXMC] 打开文件选择框:头像\n" + Environment.StackTrace);

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择头像",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] }],
        });

        if (files.Count == 0)
        {
            return;
        }

        // 先开 QQ 式框选窗口裁剪,确定后拿到 PNG 字节
        var crop = new AvatarCropWindow(files[0].Path.LocalPath);
        var owner = TopLevel.GetTopLevel(this) as Window;
        var bytes = owner is null ? null : await crop.ShowDialog<byte[]?>(owner);

        if (bytes is { Length: > 0 })
        {
            await Vm.SetAvatarAsync(bytes);
        }
    }

    /// <summary>账户与安全 → 删除头像。</summary>
    private void OnClearAvatarClick(object? sender, RoutedEventArgs e) => Vm?.ClearAvatar();

    /// <summary>账户与安全 → 退出登录(关主界面,回登录窗口)。</summary>
    private async void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        await Vm.LogoutAsync();

        var owner = TopLevel.GetTopLevel(this) as Window;
        var login = new LoginViewModel();
        ClientSession.Connect = login.Connect;

        var loginWindow = new LoginWindow { DataContext = login };
        WindowPlacement.PlaceAt(loginWindow, owner);
        loginWindow.Show();
        owner?.Close();
    }
}
