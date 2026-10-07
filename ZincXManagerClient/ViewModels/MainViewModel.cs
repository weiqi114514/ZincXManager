using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ZincXManagerShared.Account;
using CommunityToolkit.Mvvm.ComponentModel;
using ZincXManagerClient.Models;
using ZincXManagerShared.Logging;

namespace ZincXManagerClient.ViewModels;

/// <summary>
/// 主界面视图模型:左侧导航、顶部模式、右侧内容。
/// 选中左侧"设置"时,右侧换成设置页(<see cref="Settings"/>),否则是内容区 + 消息面板。
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    public MainViewModel()
    {
        NavItems.Add(new NavItem("经济", Icons.Economy) { IsSelected = true });
        NavItems.Add(new NavItem("社区", Icons.Community));
        NavItems.Add(new NavItem("组织", Icons.Organization));
        NavItems.Add(new NavItem("好友", Icons.Friends, 5, 3));
        NavItems.Add(new NavItem("设置", Icons.Settings));
        NavItems.Add(new NavItem("管理", Icons.Manage, 5, 4));
        NavItems.Add(new NavItem("小功能组", Icons.Widgets));

        // 设置页里改了权限等级 / 连接方式,标题栏那句要跟着变
        Settings.PropertyChanged += OnSettingsChanged;

        // 个性化里选的背景壁纸:启动时自动应用,之后一改就立刻生效
        Settings.WallpaperChanged += OnWallpaperChanged;

        var wallpaper = ClientConfig.GetOrEmpty("wallpaper");
        if (wallpaper.Length > 0)
        {
            SetBackgroundImage(wallpaper);
        }

        // 登录过的账号:显示在左下角(带头像)
        if (ClientSession.User.Length > 0)
        {
            SetUser(ClientSession.User, ClientConfig.GetOrEmpty("userid"));
            RefreshAvatar();
        }

        // 设置页里换了头像 → 左下角跟着变
        Settings.AvatarChanged += _ => RefreshAvatar();

        // 截图 / 调试用:config 里写 startpage=设置、startsection=模式与数据源 就直接落在那一页
        var startPage = ClientConfig.GetOrEmpty("startpage");
        if (startPage.Length > 0)
        {
            CurrentPage = startPage;
            var startSection = ClientConfig.GetOrEmpty("startsection");
            Settings.ShowSection(startSection.Length > 0 ? startSection : "模式与数据源");
        }
    }

    /// <summary>设置页视图模型。</summary>
    public SettingsViewModel Settings { get; } = new();

    /// <summary>当前一级导航(决定右侧显示哪个页面)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettings))]
    public partial string CurrentPage { get; set; } = "经济";

    /// <summary>是否正在显示设置页。</summary>
    public bool IsSettings => CurrentPage == "设置";

    /// <summary>顶部那句:权限等级 - 连接方式(如 Player - SAOS),两段都来自设置页。</summary>
    public string ModeText => $"{Settings.Role} - {Settings.Mode}";

    /// <summary>消息列表为空时显示的文案。</summary>
    public string EmptyMessage { get; } = "没有其他消息啦";

    /// <summary>当前用户名,登录后由外部赋值。</summary>
    [ObservableProperty]
    public partial string UserName { get; set; } = "未登录";

    /// <summary>当前用户 ID(含 "ID：" 前缀),登录后由外部赋值。</summary>
    [ObservableProperty]
    public partial string UserId { get; set; } = "ID：-";

    /// <summary>内容区提示(设计稿内容区留空,这里只在有操作时给一点反馈)。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    /// <summary>
    /// 窗口背景图。默认不设置(显示界面自带的渐变底色),由用户在设置 → 个性化里选择图片后才有值。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackgroundImage))]
    public partial IImage? BackgroundImage { get; set; }

    public bool HasBackgroundImage => BackgroundImage is not null;

    /// <summary>当前账号头像(左下角显示)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar))]
    public partial IImage? Avatar { get; set; }

    public bool HasAvatar => Avatar is not null;

    /// <summary>从本地缓存读头像(登录后 / 换过头像后调)。</summary>
    public void RefreshAvatar()
    {
        Avatar = ToImage(AvatarStore.Load(ClientSession.User));
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

    /// <summary>左侧导航项。</summary>
    public ObservableCollection<NavItem> NavItems { get; } = new();

    /// <summary>右侧消息列表(内容由服务端下发,界面不写死)。</summary>
    public ObservableCollection<MessageItem> Messages { get; } = new();

    // ---------- 可变数据入口 ----------

    /// <summary>设置当前用户(名称与 ID 都是可变的)。</summary>
    public void SetUser(string userName, string userId)
    {
        UserName = string.IsNullOrWhiteSpace(userName) ? "未登录" : userName.Trim();
        UserId = string.IsNullOrWhiteSpace(userId)
            ? "ID：-"
            : "ID：" + userId.Trim().Replace("ID：", "").Replace("ID:", "");
    }

    /// <summary>整体替换消息列表。</summary>
    public void SetMessages(IEnumerable<MessageItem> messages)
    {
        Messages.Clear();
        foreach (var message in messages)
        {
            Messages.Add(message);
        }
    }

    /// <summary>追加一条消息。</summary>
    public void AddMessage(MessageItem message) => Messages.Add(message);

    /// <summary>清空消息。</summary>
    public void ClearMessages() => Messages.Clear();

    /// <summary>加载设计稿里的示例消息(仅界面预览用)。</summary>
    public void LoadSampleMessages() => SetMessages(SampleMessages.All);

    /// <summary>设置窗口背景图;传空(null / 空串 / 文件不存在)则恢复默认底色。</summary>
    public void SetBackgroundImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            BackgroundImage = null;
            Logger.Log(LogLevel.Warning, "[ZXMC] 背景图不可用,恢复默认底色: " + path);
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            BackgroundImage = new Bitmap(stream);
            Logger.Log(LogLevel.Info, "[ZXMC] 已设置背景图: " + path);
        }
        catch (Exception ex)
        {
            BackgroundImage = null;
            Logger.Log(LogLevel.Error, "[ZXMC] 加载背景图失败: " + ex.Message);
        }
    }

    // ---------- 交互 ----------

    /// <summary>切换左侧导航选中项;选到"设置"时右侧换成设置页。</summary>
    public void SelectNav(NavItem item)
    {
        foreach (var nav in NavItems)
        {
            nav.IsSelected = ReferenceEquals(nav, item);
        }

        CurrentPage = item.Label;
        StatusText = item.Label;

        // 每次从主导航进设置页都落在"模式与数据源";背景壁纸只在"个性化"里,不会自己弹出来
        if (item.Label == "设置")
        {
            Settings.ShowSection("模式与数据源");
        }

        Logger.Log(LogLevel.Info, "[ZXMC] 切换页面:" + item.Label);
    }

    /// <summary>消息卡片上的按钮动作。</summary>
    public void InvokeAction(string action)
    {
        StatusText = action;
        Logger.Log(LogLevel.Info, "[ZXMC] 消息操作:" + action);
    }

    /// <summary>设置页里的权限等级 / 连接方式变了 → 通知标题栏刷新。</summary>
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.Role) or nameof(SettingsViewModel.Mode))
        {
            OnPropertyChanged(nameof(ModeText));
        }
    }

    /// <summary>个性化里换了壁纸(空串 = 恢复默认)→ 立刻应用到窗口。</summary>
    private void OnWallpaperChanged(string path)
    {
        if (path.Length == 0)
        {
            BackgroundImage = null;
            return;
        }

        SetBackgroundImage(path);
    }
}
