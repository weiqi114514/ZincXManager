using System;  // 基础类型 / 常用静态方法
using System.Collections.ObjectModel;  // 可观察集合(界面绑定用)
using System.IO;  // 文件与目录读写
using System.Threading.Tasks;  // async / await 异步
using Avalonia.Media;  // 画刷 / 颜色 / 几何
using Avalonia.Media.Imaging;  // 位图
using ZincXManagerShared.Account;  // 共享库
using ZincXManagerShared.Network;  // 共享库
using CommunityToolkit.Mvvm.ComponentModel;  // MVVM 可观察对象
using ZincXManagerClient.Models;  // 客户端自身
using ZincXManagerShared.Logging;  // 共享库

namespace ZincXManagerClient.ViewModels;

/// <summary>
/// 设置页视图模型:连接方式(SAOS/OS/OOS)+ 权限等级(Player/LAdmin/HAdmin/System)+ 数据源信息 + 背景壁纸。
///
/// <para>标题栏那句 "Player - SAOS" 就是这里的 <b>权限等级 - 连接方式</b>。</para>
/// <para>改动会立刻写进 config.ini,按设计稿提示"重启后生效"。</para>
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private const string KeyMode = "mode";
    private const string KeyRole = "role";
    private const string KeyWallpaper = "wallpaper";
    // config 键:子服地址。
    private const string KeyCzip = "czip";
    // config 键:子服端口。
    private const string KeyCzport = "czport";
    // config 键:官方服地址。
    private const string KeyOzip = "ozip";
    // config 键:官方服端口。
    private const string KeyOzport = "ozport";

    /// <summary>选了新壁纸 / 恢复默认时通知外面(参数为空串表示恢复默认)。</summary>
    public event Action<string>? WallpaperChanged;

    /// <summary>换好头像后通知外面(参数是图片字节)。</summary>
    public event Action<byte[]>? AvatarChanged;

    /// <summary>可选权限等级。</summary>
    public string[] Roles { get; } = ["Player", "LAdmin", "HAdmin", "System"];

    /// <summary>连接方式。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaos), nameof(IsOs), nameof(IsOos),
        nameof(ShowClientSource), nameof(ShowOfficialSource))]
    public partial string Mode { get; set; } = "SAOS";

    /// <summary>权限等级。</summary>
    [ObservableProperty]
    public partial string Role { get; set; } = "Player";

    /// <summary>当前数据源名称。</summary>
    [ObservableProperty]
    public partial string DataSourceName { get; set; } = "Zinc Energy Server锌能源服务器";

    /// <summary>当前官方数据源名称。</summary>
    [ObservableProperty]
    public partial string OfficialDataSourceName { get; set; } = "ZincXManagerOS-锌能源";

    /// <summary>数据源地址(配置里的子服务端地址 + 官网域名)。</summary>
    [ObservableProperty]
    public partial string DataSourceAddress { get; set; } = "-";

    /// <summary>官方数据源地址(配置里的官方服务端地址 + 官网域名)。</summary>
    [ObservableProperty]
    public partial string OfficialAddress { get; set; } = "-";

    /// <summary>背景壁纸路径(空 = 默认底色)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WallpaperHint))]
    public partial string WallpaperPath { get; set; } = "";

    /// <summary>半透明档的表面不透明度(%):个性化滑杆,50-100,默认 90。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpacityHint))]
    public partial double OpacityPercent { get; set; } = 90;

    /// <summary>滑杆说明(跟着数值变)。</summary>
    public string OpacityHint => $"{(int)OpacityPercent}%";

    partial void OnOpacityPercentChanged(double value) => ThemeManager.ApplyAlpha((int)Math.Round(value));

    /// <summary>亚克力开关:开 = 背景模糊 + Fluent 亚克力着色;关 = 纯半透明。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlassHint))]
    public partial bool GlassEnabled { get; set; } = true;

    /// <summary>亚克力开关的说明(跟着状态变)。</summary>
    public string GlassHint => GlassEnabled
        ? "亚克力"
        : "半透明";

    partial void OnGlassEnabledChanged(bool value) => ThemeManager.ApplyGlass(value);

    /// <summary>
    /// 官方服务端地址是不是"自定义"的。<c>false</c> = 用代码里写死的官方默认地址
    /// (<see cref="OfficialServer"/>,界面上的「官方服务端(默认)」那项)。
    /// </summary>
    [ObservableProperty]
    public partial bool UseCustomAddress { get; set; }

    /// <summary>把官方地址改回"官方默认"(点了那个单选框就调它)。</summary>
    public void UseOfficialAddress()
    {
        ClientConfig.Set(KeyOzip, OfficialServer.Host);
        ClientConfig.Set(KeyOzport, OfficialServer.PortText);
        UseCustomAddress = false;

        // 强制通知一次:取消"自定义"时这个值可能本来就是 false(属性没变 → 不发通知),
        // 而单选框已经被用户点成选中了 —— 不推一次的话界面会停在"自定义",和地址自相矛盾
        OnPropertyChanged(nameof(UseCustomAddress));

        RefreshDataSources();
        Logger.Log(LogLevel.Info, $"[ZXMC] 官方服务端地址已恢复为官方默认 {OfficialServer.Display}");
    }

    /// <summary>当前数据源地址(填空弹窗的初始值):OOS 用官方那一组,其余用子服务端那一组。</summary>
    public string CurrentAddress => Mode == "OOS"
        ? $"{ClientConfig.Get(KeyOzip, OfficialServer.Host)}:{ClientConfig.Get(KeyOzport, OfficialServer.PortText)}"
        : $"{ClientConfig.Get(KeyCzip, "127.0.0.1")}:{ClientConfig.Get(KeyCzport, "21000")}";

    /// <summary>改当前数据源的地址与端口(填空弹窗用);格式不对返回原因,成功返回 null。</summary>
    public string? SetAddress(string ipPort)
    {
        var text = ipPort.Trim();

        // 只允许一个冒号:不然 "1.2.3.4:5:6" 这种会被当成合法地址
        var colons = 0;
        foreach (var ch in text)
        {
            if (ch == ':')
            {
                colons++;
            }
        }

        if (colons != 1)
        {
            return "格式应为 IP 或域名:端口(只能有一个冒号),例如 zxm.zincms.top:25000";
        }

        var colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1)
        {
            return "格式应为 IP 或域名:端口,例如 zxm.zincms.top:25000";
        }

        var ip = text[..colon].Trim();   //玩家IP
        var port = text[(colon + 1)..].Trim();
        if (!HostText.IsHost(ip))
        {
            return "地址部分不合法(要 IP 或域名),例如 zxm.zincms.top 或 127.0.0.1";
        }

        if (!ushort.TryParse(port, out var number) || number == 0)
        {
            return "端口不合法(1-65535)";
        }

        // 按当前连接方式决定改哪一组:OOS 改官方数据源,OS / SAOS 改子服务端
        if (Mode == "OOS")
        {
            ClientConfig.Set(KeyOzip, ip);
            ClientConfig.Set(KeyOzport, port);
            UseCustomAddress = !OfficialServer.IsOfficial(ip, port);
        }
        else
        {
            ClientConfig.Set(KeyCzip, ip);
            ClientConfig.Set(KeyCzport, port);
        }

        RefreshDataSources();
        Logger.Log(LogLevel.Info, $"[ZXMC] 数据源地址已改为 {ip}:{port}(模式 {Mode})");
        return null;
    }

    /// <summary>清除本机保存的登录记录(免登录 / 记住的密文)。</summary>
    public void ClearLoginRecord()
    {
        LoginRecord.Clear();
        Remember = false;
        SavePassword = false;
        Logger.Log(LogLevel.Info, "[ZXMC] 已清除本地登录记录");
    }

    /// <summary>可选配色(个性化里点色块切换)。</summary>
    public ThemeOption[] ThemeOptions => Themes.All;

    /// <summary>切换配色:立即生效并记进 config.ini。</summary>
    public void SelectTheme(string name) => ThemeManager.Apply(Themes.Find(name));

    /// <summary>内容区当前显示的页面。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModePage), nameof(IsPersonalizePage), nameof(IsSecurityPage), nameof(IsAdvancedPage), nameof(IsAboutPage), nameof(IsOtherPage))]
    public partial string CurrentSection { get; set; } = "模式与数据源";

    public bool IsModePage => CurrentSection == "模式与数据源";
    public bool IsPersonalizePage => CurrentSection == "个性化";
    public bool IsSecurityPage => CurrentSection == "账户与安全";

    /// <summary>是否在"高级"页(本地消息保留天数在这里)。</summary>
    public bool IsAdvancedPage => CurrentSection == "高级";

    public bool IsAboutPage => CurrentSection == "关于";

    // ---------- 关于页 ----------

    /// <summary>客户端版本(取程序集版本)。</summary>
    public string AppVersion
    {
        get
        {
            // 显示版本用 InformationalVersion(a0.3.0);安装包内部用数字版本 0.3.0
            var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)System.Attribute.GetCustomAttribute(
                typeof(SettingsViewModel).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            var info = attr?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }

            var v = typeof(SettingsViewModel).Assembly.GetName().Version;
            return v is null ? "a0.3.0" : "a" + v.ToString(3);
        }
    }

    /// <summary>客户端名。</summary>
    public string AppName => "ZincXManager 客户端(ZXMC)";

    /// <summary>制作人 QQ。</summary>
    public string ProducerQq => "2016811976";

    /// <summary>官网首页。</summary>
    public string WebsiteUrl => "https://weiqi114514.github.io/ZincXManager/";

    /// <summary>技术文档。</summary>
    public string DocsUrl => "https://weiqi114514.github.io/ZincXManager/help.html";

    /// <summary>源码仓库。</summary>
    public string RepoUrl => "https://github.com/weiqi114514/ZincXManager";

    /// <summary>更新日志。</summary>
    public string ChangelogUrl => "https://weiqi114514.github.io/ZincXManager/changelog.html";

    /// <summary>运行环境一行说明。</summary>
    public string RuntimeInfo =>
        $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} · " +
        $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} · " +
        (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "AOT 单文件");

    /// <summary>配置文件路径。</summary>
    public string ConfigPath => System.IO.Path.Combine(System.AppContext.BaseDirectory, "config.ini");

    /// <summary>日志目录。</summary>
    public string LogDirectory => System.IO.Path.Combine(System.AppContext.BaseDirectory, "logs");

    /// <summary>本地消息保留天数(高级设置;默认 60,范围 1-3650)。</summary>
    [ObservableProperty]
    public partial string MessageDays { get; set; } = "60";

    /// <summary>保留天数改了(主界面据此重算并清理)。</summary>
    public event Action<int>? MessageDaysChanged;

    // ---------- 消息窗口:显示哪些分类(个性化里可改) ----------

    /// <summary>改了消息窗口分类:主界面据此重建页签。</summary>
    public event Action? MessageTabsChanged;

    /// <summary>「消息」分类固定有,不给关。</summary>
    public bool MessageCategoryMessages => true;

    [ObservableProperty] public partial bool MessageCategoryLog { get; set; }

    [ObservableProperty] public partial bool MessageCategoryChangelog { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWebSettings))]
    public partial bool MessageCategoryWeb { get; set; }

    /// <summary>勾了自定义网页才显示名称/网址。</summary>
    public bool ShowWebSettings => MessageCategoryWeb;

    [ObservableProperty] public partial string WebTabName { get; set; } = "";

    [ObservableProperty] public partial string WebTabUrl { get; set; } = "";

    partial void OnMessageCategoryLogChanged(bool value) => SaveMessageCats();

    partial void OnMessageCategoryChangelogChanged(bool value) => SaveMessageCats();

    partial void OnMessageCategoryWebChanged(bool value) => SaveMessageCats();

    partial void OnWebTabNameChanged(string value) => SaveMessageCats();

    partial void OnWebTabUrlChanged(string value) => SaveMessageCats();

    /// <summary>写回 config.ini 并通知主界面重建页签。</summary>
    private void SaveMessageCats()
    {
        var cats = new System.Collections.Generic.List<string> { "messages" };
        if (MessageCategoryLog) cats.Add("log");
        if (MessageCategoryChangelog) cats.Add("changelog");
        if (MessageCategoryWeb) cats.Add("web");

        ClientConfig.Set("msgcats", string.Join(",", cats));
        ClientConfig.Set("webname", WebTabName.Trim().Length > 0 ? WebTabName.Trim() : "自定义网页");
        ClientConfig.Set("weburl", WebTabUrl.Trim());
        MessageTabsChanged?.Invoke();
    }

    partial void OnMessageDaysChanged(string value)
    {
        // 输入过程中不合法就先不管,等输完
        if (!int.TryParse((value ?? "").Trim(), out var days))
        {
            return;
        }

        days = Math.Clamp(days, 1, 3650);
        ClientConfig.Set("msgdays", days.ToString());
        MessageDaysChanged?.Invoke(days);
    }
    /// <summary>下载页:当前版本 + 检查更新</summary>
    public bool IsDownloadPage => CurrentSection == "下载";

    public bool IsOtherPage => !IsModePage && !IsPersonalizePage && !IsSecurityPage && !IsAdvancedPage && !IsDownloadPage && !IsAboutPage;

    // ---------- 更新 ----------

    /// <summary>更新策略:有更新就提示 / 不获取更新</summary>
    [ObservableProperty] public partial bool UpdateOnStartup { get; set; } = true;

    /// <summary>装完自动重新打开(默认开)</summary>
    [ObservableProperty] public partial bool UpdateRelaunch { get; set; } = true;

    /// <summary>正在检查更新(按钮置灰)</summary>
    [ObservableProperty] public partial bool IsCheckingUpdate { get; set; }

    /// <summary>下载镜像前缀(空 = 直连;填了就把原地址拼在后面)</summary>
    [ObservableProperty] public partial string UpdateMirror { get; set; } = "";

    /// <summary>可选镜像(给下拉提示用)</summary>
    public string[] MirrorOptions { get; } =
    [
        "",
        "https://gh-proxy.com/",
        "https://ghfast.top/",
        "https://ghproxy.net/",
    ];

    partial void OnUpdateMirrorChanged(string value) =>
        ClientConfig.Set("update.mirror", (value ?? "").Trim());

    /// <summary>当前客户端版本(显示用)</summary>
    public string CurrentVersionText => Services.UpdateService.CurrentVersion;

    partial void OnUpdateOnStartupChanged(bool value) =>
        ClientConfig.Set("update.mode", value ? "prompt" : "off");

    partial void OnUpdateRelaunchChanged(bool value) =>
        ClientConfig.Set("update.relaunch", value ? "1" : "0");

    // ---------- 账户与安全 ----------
    /// <summary>当前账号的头像。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar))]
    public partial IImage? Avatar { get; set; }

    /// <summary>账号名(未登录为空)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountLine))]
    public partial string AccountName { get; set; } = "";

    /// <summary>账号等级。</summary>
    [ObservableProperty]
    public partial string AccountLevel { get; set; } = "-";

    /// <summary>账号 ID。</summary>
    [ObservableProperty]
    public partial string AccountId { get; set; } = "-";

    /// <summary>7 天内免登录。</summary>
    [ObservableProperty]
    public partial bool Remember { get; set; }

    /// <summary>记住账号密码。</summary>
    [ObservableProperty]
    public partial bool SavePassword { get; set; }

    public bool HasAvatar => Avatar is not null;
    /// <summary>刚切过页面的一小段时间(防误触:切换瞬间弹出来的按钮不该被上一次点击点到)。</summary>
    private DateTime _sectionChangedAt = DateTime.MinValue;

    /// <summary>是否刚刚才切过页面(400ms 内)。</summary>
    public bool IsJustSwitched => (DateTime.Now - _sectionChangedAt).TotalMilliseconds < 400;

    public string AccountLine => AccountName.Length > 0
        ? $"账号:{AccountName}    等级:{AccountLevel}    ID:{AccountId}"
        : "当前没有登录账号";

    /// <summary>刷新账号信息与头像(进这个页面时调)。</summary>
    public void RefreshAccount()
    {
        AccountName = ClientSession.User;
        AccountLevel = ClientSession.Level;
        AccountId = ClientSession.UserId;
        Remember = LoginRecord.Remember;
        SavePassword = LoginRecord.SavePassword;
        Avatar = ToImage(AvatarStore.Load(AccountName));
    }

    /// <summary>换头像(字节,来自框选裁剪)。</summary>
    public async Task<bool> SetAvatarAsync(byte[] bytes)
    {
        try
        {
            var connect = ClientSession.Connect;

            AvatarStore.Save(AccountName, bytes, out _);
            Avatar = ToImage(bytes);
            AvatarChanged?.Invoke(bytes);

            if (connect is null || !connect.IsConnected)
            {
                Logger.Log(LogLevel.Warning, "[ZXMC] 没连上服务端,头像只存在本地");
                return false;
            }

            await connect.SendAsync(Protocol.AccountAvatarSet, bytes);
            Logger.Log(LogLevel.Info, "[ZXMC] 已提交头像到服务端");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[ZXMC] 设置头像失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>删除头像(本地 + 服务端下次登录会拿到空)。</summary>
    public void ClearAvatar()
    {
        AvatarStore.Delete(AccountName);
        Avatar = null;
        Logger.Log(LogLevel.Info, "[ZXMC] 已删除本地头像缓存");
    }

    /// <summary>勾选项存盘。</summary>
    partial void OnRememberChanged(bool value) => ClientConfig.Set("remember", value ? "1" : "0");

    partial void OnSavePasswordChanged(bool value) => ClientConfig.Set("savepassword", value ? "1" : "0");

    /// <summary>退出登录:清记录并断开连接(界面切换由外面处理)。</summary>
    public async Task LogoutAsync()
    {
        LoginRecord.Clear();

        if (ClientSession.Connect is not null)
        {
            await ClientSession.Connect.DisconnectAsync();
        }

        ClientSession.User = "";
        ClientSession.UserId = "";
        ClientSession.Level = Protocol.RegisterLevel;
        RefreshAccount();
        Logger.Log(LogLevel.Info, "[ZXMC] 已退出登录");
    }

    /// <summary>头像字节 → 可显示的位图。</summary>
    private static IImage? ToImage(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    public bool IsSaos => Mode == "SAOS";
    public bool IsOs => Mode == "OS";
    public bool IsOos => Mode == "OOS";

    /// <summary>OS / SAOS:客户端只连子服务端 → 只显示子服务端那一组信息。</summary>
    public bool ShowClientSource => Mode != "OOS";

    /// <summary>OOS:客户端直连官方服务端 → 只显示官方那一组信息。</summary>
    public bool ShowOfficialSource => Mode == "OOS";

    /// <summary>个性化里显示"当前壁纸"的说明。</summary>
    public string WallpaperHint => WallpaperPath.Length > 0
        ? "当前壁纸:" + WallpaperPath
        : "当前未设置壁纸(使用界面默认底色)";

    /// <summary>二级导航(上半组)。</summary>
    public ObservableCollection<SubNavItem> SubNavItems { get; } = new();

    /// <summary>二级导航(下半组,分隔线之下)。</summary>
    public ObservableCollection<SubNavItem> SubNavExtraItems { get; } = new();

    public SettingsViewModel()
    {
        // 读上次的选择(没有就写缺省值)
        MessageDays = ClientConfig.Get("msgdays", "60");
        UpdateOnStartup = ClientConfig.Get("update.mode", "prompt") != "off";
        UpdateRelaunch = ClientConfig.Get("update.relaunch", "1") == "1";
        UpdateMirror = ClientConfig.GetOrEmpty("update.mirror").Trim();
        if (UpdateMirror.Length == 0)
        {
            // 默认给一个能用的镜像,直连 GitHub 在国内经常下不动
            UpdateMirror = "https://gh-proxy.com/";
            ClientConfig.Set("update.mirror", UpdateMirror);
        }

        var cats = ClientConfig.GetOrEmpty("msgcats");
        MessageCategoryLog = cats.Contains("log");
        MessageCategoryChangelog = cats.Contains("changelog");
        MessageCategoryWeb = cats.Contains("web");
        WebTabName = ClientConfig.GetOrEmpty("webname");
        WebTabUrl = ClientConfig.GetOrEmpty("weburl");
        Mode = ClientConfig.Get(KeyMode, "SAOS");

        // 权限等级优先用登录账号的等级(账号系统),没有就退回设置里的 role
        var accountLevel = ClientConfig.GetOrEmpty("level");
        Role = accountLevel.Length > 0 ? accountLevel : ClientConfig.Get(KeyRole, "Player");
        WallpaperPath = ClientConfig.GetOrEmpty(KeyWallpaper);
        GlassEnabled = ThemeManager.GlassEnabled;
        OpacityPercent = ThemeManager.SurfaceAlpha;
        RefreshDataSources();

        foreach (var label in new[] { "个性化", "模式与数据源", "账户与安全", "下载", "高级", "关于" })
        {
            SubNavItems.Add(new SubNavItem(label) { IsSelected = label == CurrentSection });
        }

        foreach (var label in new[] { "QQ/ZXM绑定", "连接服务器配置" })
        {
            SubNavExtraItems.Add(new SubNavItem(label));
        }
    }

    /// <summary>刷新数据源那两行显示(地址取自 config.ini)。</summary>
    public void RefreshDataSources()
    {
        var czip = ClientConfig.Get(KeyCzip, "127.0.0.1");
        var czport = ClientConfig.Get(KeyCzport, "21000");
        var ozip = ClientConfig.Get(KeyOzip, OfficialServer.Host);
        var ozport = ClientConfig.Get(KeyOzport, OfficialServer.PortText);

        // 官方地址是不是被改成"自定义"了(留空或正好是官方默认都算默认)
        UseCustomAddress = !OfficialServer.IsOfficial(ClientConfig.GetOrEmpty(KeyOzip), ClientConfig.GetOrEmpty(KeyOzport));

        DataSourceAddress = $"{czip}:{czport} | zincms.top";
        OfficialAddress = UseCustomAddress
            ? $"{ozip}:{ozport}(自定义)"
            : $"{ozip}:{ozport}(官方默认)";

        // 名称由服务端在连接时下发(201),存在 czname / ozname;没连过就先显示提示
        DataSourceName = "未连接(名称由服务端提供)";
        OfficialDataSourceName = "未连接(名称由服务端提供)";

        var czName = ClientConfig.GetOrEmpty("czname");
        if (czName.Length > 0)
        {
            DataSourceName = czName;
        }

        var ozName = ClientConfig.GetOrEmpty("ozname");
        if (ozName.Length > 0)
        {
            OfficialDataSourceName = ozName;
        }
    }

    /// <summary>切换连接方式(立即存盘;标题栏会跟着变,按设计稿需重启生效)。</summary>
    public void SelectMode(string mode)
    {
        if (mode != "SAOS" && mode != "OS" && mode != "OOS")
        {
            return;
        }

        Mode = mode;
        ClientConfig.Set(KeyMode, mode);
        Logger.Log(LogLevel.Info, $"[ZXMC] 连接方式改为 {mode}(重启后生效)");
    }

    /// <summary>权限等级改了(ComboBox 双向绑定会调这里)→ 存盘。</summary>
    partial void OnRoleChanged(string value)
    {
        ClientConfig.Set(KeyRole, value);
        Logger.Log(LogLevel.Info, $"[ZXMC] 权限等级改为 {value}(重启后生效)");
    }

    /// <summary>选了一张新壁纸(由界面的文件选择器传入路径)。</summary>
    public void SelectWallpaper(string path)
    {
        WallpaperPath = path;
        ClientConfig.Set(KeyWallpaper, path);
        WallpaperChanged?.Invoke(path);
        Logger.Log(LogLevel.Info, "[ZXMC] 已设置背景壁纸: " + path);
    }

    /// <summary>恢复默认底色。</summary>
    public void ClearWallpaper()
    {
        WallpaperPath = "";
        ClientConfig.Set(KeyWallpaper, "");
        WallpaperChanged?.Invoke("");
        Logger.Log(LogLevel.Info, "[ZXMC] 已恢复默认底色");
    }

    /// <summary>按名字切到某个二级页面(主导航进设置页时用来复位到"模式与数据源")。</summary>
    public void ShowSection(string label)
    {
        foreach (var nav in SubNavItems)
        {
            if (nav.Label == label)
            {
                SelectSection(nav);
                return;
            }
        }
    }

    /// <summary>切换二级导航选中项。</summary>
    public void SelectSection(SubNavItem item)
    {
        foreach (var nav in SubNavItems)
        {
            nav.IsSelected = ReferenceEquals(nav, item);
        }

        CurrentSection = item.Label;
        _sectionChangedAt = DateTime.Now;

        // 进"账户与安全"时刷新账号信息与头像
        if (CurrentSection == "账户与安全")
        {
            RefreshAccount();
        }

        Logger.Log(LogLevel.Info, "[ZXMC] 设置页:" + item.Label);
    }

    /// <summary>"重启以应用"。</summary>
    public void ApplyRestart()
        => Logger.Log(LogLevel.Info, "[ZXMC] 设置已保存到 config.ini,重启客户端后生效");

    /// <summary>"启动数据源连接流程"。</summary>
    public void StartDataSourceFlow()
        => Logger.Log(LogLevel.Info, "[ZXMC] 启动数据源连接流程");
}
