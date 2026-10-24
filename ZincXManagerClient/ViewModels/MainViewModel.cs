using System;  // 基础类型 / 常用静态方法
using System.Collections.Generic;  // 泛型集合
using System.Threading.Tasks;  // async / await 异步
using System.Collections.ObjectModel;  // 可观察集合(界面绑定用)
using System.ComponentModel;  // INotifyPropertyChanged / 属性变更
using System.IO;  // 文件与目录读写
using System.Linq;  // LINQ 查询
using Avalonia.Media;  // 画刷 / 颜色 / 几何
using Avalonia.Media.Imaging;  // 位图
using ZincXManagerShared.Account;  // 共享库
using CommunityToolkit.Mvvm.ComponentModel;  // MVVM 可观察对象
using ZincXManagerClient.Models;  // 客户端自身
using ZincXManagerShared.Logging;  // 共享库
using ZincXManagerShared.Network;  // 共享库

namespace ZincXManagerClient.ViewModels;

/// <summary>
/// 主界面视图模型:左侧导航、顶部模式、右侧内容。
/// 选中左侧"设置"时,右侧换成设置页(<see cref="Settings"/>),否则是内容区 + 消息面板。
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    public MainViewModel()
    {
        // 子功能清单(图标先占位,后面替换)
        var isOos = ClientConfig.GetOrEmpty("mode") == "OOS";
        var officialOn = ClientConfig.GetOrEmpty("connectOS") == "1";

        // 两个广场功能一样(看社区内容),区别是数据来源:服务器广场走 ZXMS,锌源广场走官方 ZXMOS
        CommunityItems.Add(new Models.FunctionItem
        {
            Key = "srv-square", Title = "服务器广场", Description = "子服务端的社区内容",
            IconSvg = "avares://ZincXManagerClient/Assets/icons/server-square.svg", Icon = Models.Icons.ServerSquare, IconStroke = true, IsVisible = !isOos,
        });
        CommunityItems.Add(new Models.FunctionItem
        {
            Key = "zinc-square", Title = "锌源广场", Description = "官方服务端的社区内容",
            IconSvg = "avares://ZincXManagerClient/Assets/icons/official-square.svg", Icon = Models.Icons.OfficialSquare, IconStroke = true, IsVisible = false,
        });

        OrganizationItems.Add(new Models.FunctionItem { Key = "org-personal", Title = "个人组织", Description = "个人小组 / 小团队", Icon = Models.Icons.PersonalOrg, IconStroke = true });
        OrganizationItems.Add(new Models.FunctionItem { Key = "org-team", Title = "团队组织", Description = "多人协作的团队", Icon = Models.Icons.TeamOrg, IconStroke = true });
        OrganizationItems.Add(new Models.FunctionItem { Key = "org-company", Title = "企业组织", Description = "企业 / 大型组织", Icon = Models.Icons.CompanyOrg, IconStroke = true });

        ManageItems.Add(new Models.FunctionItem { Key = "mg-version", Title = "版本管理", Description = "服务端 / 客户端版本", Icon = Models.Icons.VersionManage, IconStroke = true });
        ManageItems.Add(new Models.FunctionItem { Key = "mg-source", Title = "数据源管理", Description = "数据源与连接配置", Icon = Models.Icons.SourceManage, IconStroke = true });
        ManageItems.Add(new Models.FunctionItem { Key = "mg-server", Title = "服务器管理", Description = "开服 / 重启 / 备份", Icon = Models.Icons.ServerManage, IconStroke = true });
        ManageItems.Add(new Models.FunctionItem { Key = "mg-stats", Title = "数据统计", Description = "在线 / 活跃 / 经济数据", Icon = Models.Icons.StatsManage, IconStroke = true });

        // 顺序:设置沉到功能列表最底下,小功能组顶替它原来的位置
        NavItems.Add(new NavItem("经济", Icons.Economy));
        NavItems.Add(new NavItem("社区", Icons.Community));
        NavItems.Add(new NavItem("组织", Icons.Organization));
        NavItems.Add(new NavItem("好友", Icons.Friends, 5, 3));
        NavItems.Add(new NavItem("小功能组", Icons.Widgets));
        NavItems.Add(new NavItem("管理", Icons.Manage, 5, 4));
        NavItems.Add(new NavItem("设置", Icons.Settings));

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

        // 消息列表空/非空要切换那句"没有其他消息啦"
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMessages));

        // 本地消息保留天数(高级设置里改)+ 读上次存下来的消息
        LocalMessageDays = int.TryParse(ClientConfig.GetOrEmpty("msgdays"), out var keepDays)
            ? Math.Clamp(keepDays, 1, 3650)
            : MessageStore.DefaultLocalKeepDays;
        Settings.MessageTabsChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(BuildTabs);
        Settings.MessageDaysChanged += days =>
        {
            LocalMessageDays = days;
            PruneMessages();
            SaveMessages();
        };
        LoadMessages();
        BuildTabs();

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
    [NotifyPropertyChangedFor(nameof(IsSettings), nameof(IsEconomy), nameof(IsPlaceholder), nameof(IsCommunity), nameof(IsOrganization), nameof(IsManage), nameof(ShowMessages))]
    public partial string CurrentPage { get; set; } = "";

    /// <summary>是否正在显示设置页。</summary>
    public bool IsSettings => CurrentPage == "设置";

    /// <summary>经济页(小功能选择:商店 / 银行)。</summary>
    public bool IsEconomy => CurrentPage == "经济";

    /// <summary>
    /// 锌源广场(官方社区)是否显示:按实际连接状态判断 ——
    /// OOS 直连 ZXMOS,或 SAOS 经子服连官方(连上即认为有官方社区);OS(纯子服)不显示。
    /// </summary>
    public void RefreshOfficialVisibility()
    {
        var connect = ClientSession.Connect;
        var connected = connect?.IsConnected == true;
        var official = connected && Settings.Mode != "OS";

        // 调试开关:config 写 showsquare=1 时强制显示锌源广场(方便看图标/排版)
        if (ClientConfig.GetOrEmpty("showsquare") == "1")
        {
            official = true;
        }

        foreach (var item in CommunityItems)
        {
            if (item.Key == "zinc-square")
            {
                item.IsVisible = official;
            }
        }
    }

    /// <summary>消息窗口只在默认页(没选任何功能页)显示;进功能页就收起来。</summary>
    public bool ShowMessages => CurrentPage.Length == 0;

    /// <summary>没选任何页面(点了 Logo)时不显示"该页面还没做"。</summary>
    public bool IsPlaceholder => CurrentPage.Length > 0 && !IsEconomy
        && CurrentPage != "社区" && CurrentPage != "组织" && CurrentPage != "管理";

    public bool IsCommunity => CurrentPage == "社区";

    public bool IsOrganization => CurrentPage == "组织";


    public bool IsManage => CurrentPage == "管理";

    /// <summary>社区页的子功能(按模式 / 是否连官方决定显示)。</summary>
    public System.Collections.ObjectModel.ObservableCollection<Models.FunctionItem> CommunityItems { get; } = new();

    /// <summary>组织页的子功能。</summary>
    public System.Collections.ObjectModel.ObservableCollection<Models.FunctionItem> OrganizationItems { get; } = new();

    /// <summary>管理页的子功能。</summary>
    public System.Collections.ObjectModel.ObservableCollection<Models.FunctionItem> ManageItems { get; } = new();

    /// <summary>顶部那句:权限等级 - 连接方式(如 Player - SAOS),两段都来自设置页。</summary>
    public string ModeText => $"{Settings.Role} - {Settings.Mode}";

    // ================= 消息面板分类 =================

    /// <summary>消息面板顶部的页签(消息一定有;日志 / 更新日志 / 自定义网页 由 OOBE 里勾选生成)。</summary>
    public ObservableCollection<MessageTab> Tabs { get; } = new();

    /// <summary>当前选中页签的下标。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTabMessages), nameof(IsTabLog), nameof(IsTabChangelog), nameof(IsTabWeb),
        nameof(UnderlineOffset))]
    public partial int TabIndex { get; set; }

    /// <summary>页签宽度(和 <see cref="MessageTab.Width"/> 一致)。</summary>
    public static double TabWidthOf(MessageTab tab) => tab.Width;

    /// <summary>所有页签加起来有多宽(给滚动区与蓝色小条的画布用)。</summary>
    public double TabsWidth
    {
        get
        {
            double w = 0;
            foreach (var t in Tabs)
            {
                w += t.Width;
            }

            return w;
        }
    }

    /// <summary>蓝色小条的位置:前面所有页签宽度之和 + 当前页签居中。</summary>
    public double UnderlineOffset
    {
        get
        {
            double x = 0;
            for (var i = 0; i < Tabs.Count && i < TabIndex; i++)
            {
                x += TabWidthOf(Tabs[i]);
            }

            return x + (TabIndex >= 0 && TabIndex < Tabs.Count ? (TabWidthOf(Tabs[TabIndex]) - 30) / 2 : 19);
        }
    }

    public bool IsTabMessages => TabOf(TabIndex) == "messages";
    public bool IsTabLog => TabOf(TabIndex) == "log";
    public bool IsTabChangelog => TabOf(TabIndex) == "changelog";
    public bool IsTabWeb => TabOf(TabIndex) == "web";

    /// <summary>按页面名找对应的消息页签。</summary>
    private string TabOf(int index) => index >= 0 && index < Tabs.Count ? Tabs[index].Key : "messages";

    /// <summary>运行日志内容(取最新一份日志的末尾)。</summary>
    [ObservableProperty] public partial string LogText { get; set; } = "";

    /// <summary>更新日志内容(根目录 UpdateLog.MD)。</summary>
    [ObservableProperty] public partial string ChangelogText { get; set; } = "";

    /// <summary>自定义网页地址与抓到的正文。</summary>
    [ObservableProperty] public partial string WebUrl { get; set; } = "";

    [ObservableProperty] public partial string WebText { get; set; } = "";

    /// <summary>建立页签:按 config 的 msgcats 决定有哪些。</summary>
    private void BuildTabs()
    {
        var cats = ClientConfig.GetOrEmpty("msgcats");
        Tabs.Clear();
        Tabs.Add(new MessageTab("messages", "消息"));

        if (cats.Contains("log"))
        {
            Tabs.Add(new MessageTab("log", "日志"));
        }

        if (cats.Contains("changelog"))
        {
            Tabs.Add(new MessageTab("changelog", "更新日志"));
        }

        if (cats.Contains("web"))
        {
            // 页签名由用户在 OOBE 里自己起;没填就用默认
            var webName = ClientConfig.GetOrEmpty("webname").Trim();
            Tabs.Add(new MessageTab("web", webName.Length > 0 ? webName : "自定义网页"));
        }

        WebUrl = ClientConfig.GetOrEmpty("weburl");
        TabIndex = 0;
    }

    /// <summary>点页签。</summary>
    public void SelectTab(int index)
    {
        TabIndex = Math.Clamp(index, 0, Math.Max(0, Tabs.Count - 1));

        if (IsTabLog)
        {
            LoadLog();
        }
        else if (IsTabChangelog)
        {
            LoadChangelog();
        }
        else if (IsTabWeb)
        {
            _ = LoadWebAsync();
        }
    }

    /// <summary>读最新一份 ZXMC 日志的末尾(最多 400 行)。</summary>
    public void LoadLog()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            var file = Directory.Exists(dir)
                ? new DirectoryInfo(dir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTime).FirstOrDefault()
                : null;

            if (file is null)
            {
                LogText = "还没有日志文件(客户端还没写过日志)。";
                return;
            }

            // 日志文件正被本进程开着写:必须带 FileShare.ReadWrite,否则会报"被另一个进程占用"
            using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var all = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n');
            var lines = all;
            var tail = lines.Length > 400 ? lines[(lines.Length - 400)..] : lines;
            LogText = $"# {file.Name}(最近 400 行){Environment.NewLine}{Environment.NewLine}" + string.Join(Environment.NewLine, tail);
        }
        catch (Exception ex)
        {
            LogText = "读取日志失败:" + ex.Message;
        }
    }

    /// <summary>读根目录 UpdateLog.MD。</summary>
    public void LoadChangelog()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "UpdateLog.MD");
            ChangelogText = File.Exists(path)
                ? File.ReadAllText(path)
                : $"还没有更新日志。把 UpdateLog.MD 放到程序根目录({AppContext.BaseDirectory})就会显示在这里。";
        }
        catch (Exception ex)
        {
            ChangelogText = "读取更新日志失败:" + ex.Message;
        }
    }

    /// <summary>抓自定义网页的正文(纯文本显示;打不开就给出原因)。</summary>
    public async Task LoadWebAsync()
    {
        if (WebUrl.Length == 0)
        {
            WebText = "还没有设置网址:在 OOBE 第 4 步或设置里填一个 http(s) 地址。";
            return;
        }

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var html = await http.GetStringAsync(WebUrl);

            // 保留 HTML 结构给渲染器(以前这里把所有标签剥成纯文本,标题/列表全丢了);
            // 只去掉脚本和样式,避免把代码当正文显示。
            var body = System.Text.RegularExpressions.Regex.Replace(html,
                @"<(script|style)[\s\S]*?</\1>", " ",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            WebText = body.Length > 20000 ? body[..20000] + "…" : body;
        }
        catch (Exception ex)
        {
            WebText = "打开失败:" + ex.Message;
        }
    }

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

    /// <summary>左上角滑出来的提示条(一次说一件事,几秒后自己收回去)。</summary>
    public ObservableCollection<ToastItem> Toasts { get; } = new();

    /// <summary>同时最多留几条提示(多了挤掉最老的,别把界面糊住)。</summary>
    private const int MaxToasts = 4;

    /// <summary>
    /// 弹一条提示条:从左上角滑出来,<paramref name="ms"/> 毫秒后自己收回去。
    /// 界面那边负责动画(加 in 类滑进来、去掉 in 类滑回去)。
    /// </summary>
    public ToastItem ShowToast(string text, ToastKind kind = ToastKind.Info, int ms = 3600)
    {
        var toast = new ToastItem { Text = text, Kind = kind, Milliseconds = ms };
        Toasts.Add(toast);

        while (Toasts.Count > MaxToasts)
        {
            Toasts.RemoveAt(0);
        }

        Logger.Log(LogLevel.Info, $"[ZXMC] 提示条:{text}");
        return toast;
    }

    /// <summary>收掉一条提示(动画放完由界面调用)。</summary>
    public void RemoveToast(ToastItem toast) => Toasts.Remove(toast);

    /// <summary>消息面板里有没有消息(空的时候才显示"没有其他消息啦")。</summary>
    public bool HasMessages => Messages.Count > 0;

    /// <summary>子服务端给的消息保留天数(202 配置包下发,默认 7)。</summary>
    public int ZxmsMessageDays { get; private set; } = MessageStore.DefaultZxmsKeepDays;

    /// <summary>本地消息保留天数(高级设置里改,默认 60)。</summary>
    public int LocalMessageDays { get; private set; } = MessageStore.DefaultLocalKeepDays;

    /// <summary>下一条消息的编号。</summary>
    private int _nextMessageId = 1;

    /// <summary>读本地存档:过期的先清掉,然后新的在上面。</summary>
    private void LoadMessages()
    {
        Messages.Clear();

        foreach (var item in MessageStore.Load())
        {
            Messages.Add(item);
            _nextMessageId = Math.Max(_nextMessageId, item.Id + 1);
        }

        // 存档里是"老的在前"(写入顺序),显示要"新的在最上面"
        var ordered = new List<MessageItem>(Messages);
        ordered.Sort((a, b) => b.ReceivedAt.CompareTo(a.ReceivedAt));
        Messages.Clear();
        foreach (var item in ordered)
        {
            Messages.Add(item);
        }

        PruneMessages();
    }

    /// <summary>按各来源的保留期清掉过期消息;有变化就存一次。返回清掉几条。</summary>
    private int PruneMessages()
    {
        var now = DateTime.Now;
        var removed = 0;

        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            var item = Messages[i];
            var keep = MessageStore.KeepDaysOf(item.SourceTag, ZxmsMessageDays, LocalMessageDays);
            if ((now - item.ReceivedAt).TotalDays > keep)
            {
                Messages.RemoveAt(i);
                removed++;
            }
        }

        if (removed > 0)
        {
            Logger.Log(LogLevel.Info, $"[ZXMC] 本地消息清理掉 {removed} 条(超过保留期)");
            SaveMessages();     // 立刻写回,别让文件里还留着界面上已经看不到的过期消息
        }

        return removed;
    }

    /// <summary>存一次本地消息。</summary>
    private void SaveMessages() => MessageStore.Save(Messages);

    /// <summary>删掉一条消息(界面上的「删除」)。</summary>
    public void DeleteMessage(MessageItem item)
    {
        if (Messages.Remove(item))
        {
            SaveMessages();
            Logger.Log(LogLevel.Info, $"[ZXMC] 已删除消息:{item.Title}");
            StatusText = "已删除一条消息";
            ShowToast("已删除这条消息");
        }
    }

    /// <summary>
    /// 应用服务端配置(202):按行 <c>键=值</c>。目前认 <c>msgdays</c>(子服务端的消息保留天数)。
    /// </summary>
    public void ApplyServerConfig(string payload)
    {
        foreach (var line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var key = parts[0].Trim().ToLowerInvariant();
            var value = parts[1].Trim();

            // 服务器广场的名称由 ZXMS 自己定(config 的 squarename),客户端照它改卡片标题
            if (key == "squarename" && value.Length > 0)
            {
                foreach (var item in CommunityItems)
                {
                    if (item.Key == "srv-square")
                    {
                        item.Title = value;
                    }
                }

                Logger.Log(LogLevel.Info, $"[ZXMC] 服务器广场名称:{value}");
                continue;
            }

            if (key == "msgdays" && int.TryParse(value, out var days))
            {
                ZxmsMessageDays = Math.Clamp(days, 1, 365);
                Logger.Log(LogLevel.Info, $"[ZXMC] 子服务端消息保留 {ZxmsMessageDays} 天");
                PruneMessages();
                SaveMessages();
            }
        }
    }

    /// <summary>
    /// 把服务端下发的消息卡片(公告 500 / 定向推送 501 / 补发 504)加进消息面板。参数是卡片原文(7 段,
    /// 第 8 段可选 = 消息号),解析不出来只记一条警告 ——
    /// 原文也会存进 <see cref="MessageItem.RawCard"/>,回动作时原样带给服务端。
    ///
    /// <para>来源一共三种形态:<c>ZXMOS-&lt;官方服务端名称&gt;</c> / <c>ZXMS-&lt;服务端名称&gt;</c> /
    /// <c>ZXMC</c>(客户端本地产生的消息,服务端没带来源时按这个算)。</para>
    ///
    /// <para>带消息号的卡片按「来源 + 消息号」去重:本地已经有的直接跳过(补发时重复收不会多出一条)。
    /// 返回"是不是新加了一条"。</para>
    /// </summary>
    public bool AddCard(string rawCard, string fallbackSource = "ZXMC")
    {
        if (!MessageCard.TryParse(rawCard, out var card))
        {
            Logger.Log(LogLevel.Warning, "[ZXMC] 收到格式不对的卡片,已忽略:" + rawCard);
            return false;
        }

        var sourceTag = card.Source.Length > 0 ? card.Source : fallbackSource;

        // 服务端补发去重:同一个来源的同一条消息号只留一条
        if (card.Id > 0 && Messages.Any(m => m.ServerId == card.Id &&
                                             string.Equals(m.SourceTag, sourceTag, StringComparison.OrdinalIgnoreCase)))
        {
            Logger.Log(LogLevel.Info, $"[ZXMC] 这条消息本地已经有了({sourceTag} 消息号 {card.Id}),跳过");
            return false;
        }

        var options = card.Options.Count > 0 ? card.Options : Array.Empty<string>();

        var item = new MessageItem
        {
            Id = _nextMessageId++,
            ServerId = card.Id,
            ReceivedAt = DateTime.Now,
            SourceTag = sourceTag,
            KeepDays = MessageStore.KeepDaysOf(sourceTag, ZxmsMessageDays, LocalMessageDays),
            RawCard = rawCard,
            Kind = card.Kind switch
            {
                MessageCard.Choice => MessageKind.Choice,
                MessageCard.Input => MessageKind.Input,
                MessageCard.Select => MessageKind.Select,
                MessageCard.Update => MessageKind.Update,
                _ => MessageKind.Text,
            },
            Title = card.Title,
            Body = card.Body,
            Source = "源:" + sourceTag,
            PrimaryAction = card.Primary,
            SecondaryAction = card.Secondary,
            Options = options,
            SelectedOption = options.Count > 0 ? options[0] : "",
        };

        // 新的放最上面(消息从顶上开始)
        Messages.Insert(0, item);
        PruneMessages();
        SaveMessages();
        return true;
    }

    /// <summary>
    /// 实时收到一条服务端消息(公告 500 / 定向推送 501):进消息面板,顺带弹一条提示条。
    /// 补发(504)不走这里 —— 补十条就弹十条太吵。
    /// </summary>
    public bool ReceiveCard(string rawCard)
    {
        if (!AddCard(rawCard))
        {
            return false;
        }

        if (MessageCard.TryParse(rawCard, out var card))
        {
            ShowToast("收到新消息:" + card.Title);
        }

        return true;
    }

    // ---------- 补发(504 / 505 / 506) ----------
    /// <summary>本轮补发里真正新收到的条数(506 收尾时用来报提示)。</summary>
    private int _historyReceived;

    /// <summary>这次开窗已经报过一次"历史消息"的提示条了(两次请求别弹两条差不多的)。</summary>
    private bool _historyAnnounced;

    /// <summary>
    /// 登录成功后向服务端要一次补发(505 = MessageHistoryReq):服务端会把保留期内、我还没收到的
    /// 消息**逐条**(504)回过来,最后回一条 506 收尾。
    /// 没连上就什么都不做(不假装发过)。
    /// </summary>
    public async Task RequestMessageHistoryAsync()
    {
        var connect = ClientSession.Connect;
        if (connect is null || !connect.IsConnected)
        {
            Logger.Log(LogLevel.Info, "[ZXMC] 还没有连上服务端,这次不请求历史消息");
            return;
        }

        _historyReceived = 0;
        await connect.RequestHistoryAsync();
    }

    /// <summary>服务端补发的一条历史消息(504):和实时消息走同一条路,只是要按「来源+消息号」去重。</summary>
    public bool AddHistoryCard(string rawCard)
    {
        if (!AddCard(rawCard))
        {
            return false;
        }

        _historyReceived++;
        return true;
    }

    /// <summary>服务端补发结束(506,负载 = 服务端发了多少条)。</summary>
    public void OnHistoryDone(string payload)
    {
        var text = payload.Trim();
        Logger.Log(LogLevel.Info, $"[ZXMC] 服务端补发结束:{text} 条,其中新收到 {_historyReceived} 条");

        if (_historyReceived > 0)
        {
            StatusText = $"已补齐 {_historyReceived} 条历史消息";
            ShowToast($"已加载 {_historyReceived} 条历史消息", ToastKind.Success);
            _historyReceived = 0;
            _historyAnnounced = true;
            return;
        }

        // 服务端发了几条、但本地都有(补收过一次 / 之前就收过):给一条轻提示,不重复显示消息
        if (!_historyAnnounced && int.TryParse(text, out var total) && total > 0)
        {
            _historyAnnounced = true;
            ShowToast("消息已是最新,没有新的要收");
        }
    }

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
    /// <summary>点 Logo:取消左侧所有选中,回到"没选页面"的主界面状态。</summary>
    public void ClearSelection()
    {
        foreach (var nav in NavItems)
        {
            nav.IsSelected = false;
        }

        CurrentPage = "";
    }

    /// <summary>点左侧导航:切页面,并把选中态挪过去。</summary>
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

    /// <summary>
    /// 把卡片上的动作上报给服务端(202 = CardAction,负载 动作|输入|原卡片)。
    /// 没连上服务端就只提示一下,不假装发成功。
    /// </summary>
    public async Task SendCardActionAsync(MessageItem item, string action, string input)
    {
        var payload = MessageCard.BuildAction(action, input, item.RawCard);
        var connect = ClientSession.Connect;

        if (connect is null || !connect.IsConnected)
        {
            StatusText = "还没连上服务端,这次选择没有发出去";
            Logger.Log(LogLevel.Warning, $"[ZXMC] 未连接服务端,卡片动作发不出去:{action}");
            return;
        }

        StatusText = $"已回复:{action}";
        Logger.Log(LogLevel.Info,
            $"[ZXMC] 回复卡片「{item.Title}」:{action}{(input.Length > 0 ? ":" + input : "")}");

        await connect.SendAsync(Protocol.CardAction, payload);
    }

    /// <summary>服务端对卡片动作的回执(503):ok|提示 / err|原因。</summary>
    public void ShowCardReply(string text)
    {
        var ok = text.StartsWith("ok|");
        var message = ok ? text[3..].Trim() : "失败:" + (text.StartsWith("err|") ? text[4..].Trim() : text.Trim());

        if (message.Length == 0)
        {
            message = ok ? "服务端已收到你的选择" : "服务端没有接受这次选择";
        }

        StatusText = message;
        Logger.Log(ok ? LogLevel.Info : LogLevel.Warning, "[ZXMC] 卡片动作回执:" + message);
        ShowToast(message, ok ? ToastKind.Success : ToastKind.Warning);
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
