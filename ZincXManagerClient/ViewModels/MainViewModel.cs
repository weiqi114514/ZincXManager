using System;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ZincXManagerClient.Models;
using ZincXManagerShared.Logging;

namespace ZincXManagerClient.ViewModels;

/// <summary>主界面视图模型:左侧导航、顶部模式、右侧消息。</summary>
public partial class MainViewModel : ViewModelBase
{
    public string ModeText { get; } = "Player - SAOS";

    public string UserName { get; } = "用户114511414";

    public string UserId { get; } = "ID：114514";

    public string EmptyMessage { get; } = "没有其他消息啦";

    public ObservableCollection<NavItem> NavItems { get; } = new();

    public ObservableCollection<MessageItem> Messages { get; } = new();

    /// <summary>内容区提示(设计稿内容区留空,这里只在有操作时给一点反馈)。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    /// <summary>
    /// 窗口背景图。默认不设置(显示界面自带的渐变底色),由用户在设置里选择图片后才有值。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackgroundImage))]
    public partial IImage? BackgroundImage { get; set; }

    public bool HasBackgroundImage => BackgroundImage is not null;

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

    public MainViewModel()
    {
        NavItems.Add(new NavItem("经济", Icons.Economy) { IsSelected = true });
        NavItems.Add(new NavItem("社区", Icons.Community));
        NavItems.Add(new NavItem("组织", Icons.Organization));
        NavItems.Add(new NavItem("好友", Icons.Friends, 5, 3));
        NavItems.Add(new NavItem("设置", Icons.Settings));
        NavItems.Add(new NavItem("管理", Icons.Manage, 5, 4));
        NavItems.Add(new NavItem("小功能组", Icons.Widgets));

        Messages.Add(new MessageItem
        {
            Kind = MessageKind.Update,
            Title = "ZincXManager更新 | 1.0.0 > 1.0.1",
            Body = "加入XXX功能\n修复了已知BUG",
            Source = "源:ZXMOS",
            PrimaryAction = "立刻下载并更新"
        });

        Messages.Add(new MessageItem
        {
            Kind = MessageKind.Choice,
            Title = "是否添加猫娘",
            Body = "猫娘猫娘猫娘猫娘猫娘猫娘猫娘猫娘猫娘猫娘猫娘猫娘",
            Source = "源:ZXMS-锌能源",
            PrimaryAction = "是",
            SecondaryAction = "否"
        });

        Messages.Add(new MessageItem
        {
            Kind = MessageKind.Input,
            Title = "Where are you from",
            Body = "请填写你所在的省份",
            Source = "源:ZXMS-锌能源",
            PrimaryAction = "确认"
        });

        Messages.Add(new MessageItem
        {
            Kind = MessageKind.Select,
            Title = "版本选择",
            Body = "请选择你游玩的版本",
            Source = "源:ZXMS-锌能源",
            Options = new[] { "1.21.1", "1.21", "1.20.6", "1.20.1", "1.19.4" },
            SelectedOption = "1.21.1"
        });
    }

    /// <summary>切换左侧导航选中项。</summary>
    public void SelectNav(NavItem item)
    {
        foreach (var nav in NavItems)
        {
            nav.IsSelected = ReferenceEquals(nav, item);
        }

        StatusText = item.Label;
        Logger.Log(LogLevel.Info, "[ZXMC] 切换页面:" + item.Label);
    }

    /// <summary>消息卡片上的按钮动作。</summary>
    public void InvokeAction(string action)
    {
        StatusText = action;
        Logger.Log(LogLevel.Info, "[ZXMC] 消息操作:" + action);
    }
}
