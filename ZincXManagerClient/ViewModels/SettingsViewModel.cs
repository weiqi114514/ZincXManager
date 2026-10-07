using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ZincXManagerShared.Account;
using ZincXManagerShared.Network;
using CommunityToolkit.Mvvm.ComponentModel;
using ZincXManagerClient.Models;
using ZincXManagerShared.Logging;

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
    private const string KeyCzip = "czip";
    private const string KeyCzport = "czport";
    private const string KeyOzip = "ozip";
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

    /// <summary>毛玻璃(亚克力)开关:开 = 背景模糊 + Fluent 亚克力着色;关 = 纯半透明。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlassHint))]
    public partial bool GlassEnabled { get; set; } = true;

    /// <summary>毛玻璃开关的说明(跟着状态变)。</summary>
    public string GlassHint => GlassEnabled
        ? "亚克力:背景按 Fluent 亚克力模糊(半径 30),表面着色 80%"
        : "半透明:背景不模糊,表面着色 60%";

    partial void OnGlassEnabledChanged(bool value) => ThemeManager.ApplyGlass(value);

    /// <summary>可选配色(个性化里点色块切换)。</summary>
    public ThemeOption[] ThemeOptions => Themes.All;

    /// <summary>切换配色:立即生效并记进 config.ini。</summary>
    public void SelectTheme(string name) => ThemeManager.Apply(Themes.Find(name));

    /// <summary>内容区当前显示的页面。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModePage), nameof(IsPersonalizePage), nameof(IsSecurityPage), nameof(IsOtherPage))]
    public partial string CurrentSection { get; set; } = "模式与数据源";

    public bool IsModePage => CurrentSection == "模式与数据源";
    public bool IsPersonalizePage => CurrentSection == "个性化";
    public bool IsSecurityPage => CurrentSection == "账户与安全";
    public bool IsOtherPage => !IsModePage && !IsPersonalizePage && !IsSecurityPage;

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
        Mode = ClientConfig.Get(KeyMode, "SAOS");

        // 权限等级优先用登录账号的等级(账号系统),没有就退回设置里的 role
        var accountLevel = ClientConfig.GetOrEmpty("level");
        Role = accountLevel.Length > 0 ? accountLevel : ClientConfig.Get(KeyRole, "Player");
        WallpaperPath = ClientConfig.GetOrEmpty(KeyWallpaper);
        GlassEnabled = ThemeManager.GlassEnabled;
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
        var ozip = ClientConfig.Get(KeyOzip, "127.0.0.1");
        var ozport = ClientConfig.Get(KeyOzport, "20000");

        DataSourceAddress = $"{czip}:{czport} | zincms.top";
        OfficialAddress = $"{ozip}:{ozport} | zxm.zincms.top";

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
