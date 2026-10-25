using System;  // 基础类型 / 常用静态方法
using System.Threading.Tasks;  // async / await 异步
using Avalonia.Threading;  // UI 线程调度
using CommunityToolkit.Mvvm.ComponentModel;  // MVVM 可观察对象
using ZincXManagerClient.Models;  // 客户端自身
using ZincXManagerShared.Account;  // 共享库
using ZincXManagerShared.Logging;  // 共享库
using ZincXManagerShared.Network;  // 共享库

namespace ZincXManagerClient.ViewModels;

/// <summary>
/// OOBE(首次使用引导)四步:欢迎 → 个性化 → 数据源与连接方式 → 登录 / 注册。
///
/// <para>规则:自助注册只能得到 <see cref="Protocol.RegisterLevel"/>(Player);
/// 更高等级(Player / LAdmin / HAdmin / System)的账号要在 ZXMS 控制台用
/// <c>account add &lt;名称&gt; &lt;密码&gt; &lt;等级&gt;</c> 创建,登录时选择对应等级。</para>
///
/// <para>完成后把选择写进 config.ini(含 <c>oobe=1</c>),下次启动直接进主界面。</para>
/// </summary>
public partial class OobeViewModel : ViewModelBase
{
    /// <summary>OOS 模式不连 ZXMS,所以不给 S1/S2(ZXMS 的欢迎/介绍) */}
    public int LastStep => Mode == "OOS" ? 6 : 8;

    public int StepCount => LastStep + 1;

    /// <summary>OOBE 内部自己用的连接(负责 300 握手与收账号回复)。</summary>
    private readonly ConnectViewModel _connect = new();

    // 上次连上的目标(用来判断要不要重连)
    private string _connectedHost = "";
    private string _connectedPort = "";
    private string _connectedMode = "";

    private TaskCompletionSource<(bool Ok, string Text)>? _reply;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsTheme), nameof(IsGlass), nameof(IsMessages), nameof(IsDataSource), nameof(IsAccount),
        nameof(IsWelcomeInfo), nameof(IsIntroInfo), nameof(IsFinish), nameof(StepText), nameof(CanGoBack),
        nameof(NextText), nameof(CanGoNext), nameof(ProgressValue),
        nameof(StepText), nameof(CanGoBack), nameof(NextText))]
    public partial int Step { get; set; }

    // ---------- 右上角进度:百分比 + 每一步高亮 ----------

    /// <summary>进度条上的步骤(名字;OOS 模式没有 S1/S2 两颗)。</summary>
    public System.Collections.ObjectModel.ObservableCollection<ZincXManagerClient.Models.StepChip> Chips { get; } = new();

    /// <summary>进度条填充比例(0-100)。</summary>
    public double ProgressValue => StepCount <= 1 ? 100 : (Step + 1) * 100.0 / StepCount;

    private void RebuildChips()
    {
        var names = new System.Collections.Generic.List<(string Name, int Step)>
        {
            ("欢迎", 0), ("配色", 1), ("透明样式", 2), ("消息窗口", 3),
            ("数据源", 4), ("账号", 5),
        };

        if (Mode != "OOS")
        {
            names.Add(("欢迎信息", 6));
            names.Add(("介绍", 7));
            names.Add(("开始", LastStep));
        }
        else
        {
            names.Add(("开始", 6));
        }

        Chips.Clear();
        foreach (var (name, step) in names)
        {
            Chips.Add(new ZincXManagerClient.Models.StepChip(name) { IsCurrent = step == Step });
        }
    }

    /// <summary>刷新进度条:百分比 + 当前步骤高亮。</summary>
    private void UpdateChips()
    {
        var i = 0;
        foreach (var chip in Chips)
        {
            chip.IsCurrent = i == Step;
            i++;
        }

        OnPropertyChanged(nameof(ProgressValue));
    }

    partial void OnStepChanged(int value)
    {
        UpdateChips();

        ZincXManagerShared.Logging.Logger.Log(ZincXManagerShared.Logging.LogLevel.Info,
            $"[ZXMC] OOBE 步骤={value} 数据源={IsDataSource} 账号={IsAccount} 欢迎信息={IsWelcomeInfo} 介绍={IsIntroInfo} 完成={IsFinish} 已连接={IsSourceConnected} 已登录={IsLoggedIn}");
    }

    public bool IsWelcome => Step == 0;
    public bool IsTheme => Step == 1;
    public bool IsGlass => Step == 2;
    public bool IsMessages => Step == 3;
    public bool IsDataSource => Step == 4;
    public bool IsAccount => Step == 5;
    public bool IsWelcomeInfo => Step == 6 && Mode != "OOS";   // oobeS1:来自 ZXMS 的欢迎信息(占位)
    public bool IsIntroInfo => Step == 7 && Mode != "OOS";     // oobeS2:来自 ZXMS 的介绍信息(占位)
    public bool IsFinish => Step == LastStep;                  // 开始使用

    /// <summary>旧名字,留着让别处引用不炸。</summary>
    public bool IsPersonalize => false;

    public string StepText => $"{Step + 1} / {StepCount}";
    public bool CanGoBack => Step > 0;
    public string NextText => Step >= LastStep ? "开始使用" : "下一步";

    /// <summary>能不能点"下一步":数据源那步要先连上,账号那步要先登录。</summary>
    public bool CanGoNext => Step switch
    {
        4 => IsSourceConnected,
        5 => IsLoggedIn,
        _ => true,
    };

    /// <summary>数据源连上了没(下一步按这个放开)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext), nameof(SourceHint))]
    public partial bool IsSourceConnected { get; set; }

    /// <summary>数据源那边的提示。</summary>
    public string SourceHint => IsSourceConnected ? "已连接,可以下一步" : "先点「连接」,连上才能下一步";

    /// <summary>给外面(主界面)接着用的连接。</summary>
    public ConnectViewModel Connect => _connect;

    // ---------- 个性化 ----------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WallpaperHint))]
    public partial string WallpaperPath { get; set; } = "";

    public string WallpaperHint => WallpaperPath.Length > 0
        ? "已选择:" + WallpaperPath
        : "先用默认底色(以后可在 设置 → 个性化 里换)";

    /// <summary>透明效果不透明度(%):个性化滑杆,50-100,默认 90(只作用于半透明样式)。</summary>
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

    /// <summary>可选配色(OOBE 个性化这一步选)。</summary>
    public ThemeOption[] ThemeOptions => Themes.All;

    /// <summary>切换配色:立即生效并记进 config.ini。</summary>
    public void SelectTheme(string name) => ThemeManager.Apply(Themes.Find(name));

    // ---------- 数据源与连接方式 ----------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaos), nameof(IsOs), nameof(IsOos),
        nameof(ShowClientSource), nameof(ShowOfficialSource),
        nameof(Levels), nameof(LevelHint), nameof(AdminSideVisible))]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(IsSaos), nameof(IsOosMode), nameof(IsZxmsMode))]
    public partial string Mode { get; set; } = "SAOS";

    public bool IsSaos => Mode == "SAOS";

    /// <summary>OOS 模式:数据源页要填官方服务端地址(ozip/ozport)。</summary>
    public bool IsOosMode => Mode == "OOS";

    /// <summary>非 OOS:数据源页填子服务端地址(czip/czport)。</summary>
    public bool IsZxmsMode => Mode != "OOS";
    public bool IsOs => Mode == "OS";
    public bool IsOos => Mode == "OOS";

    /// <summary>OS / SAOS 时客户端只连子服务端(官方那一段由 ZXMS 自己连),所以只填子服务端地址; OOS 是客户端直连官方服务端,才需要填官方地址。</summary>
    public bool ShowClientSource => Mode != "OOS";

    /// <summary>只有 OOS 直连官方服务端时才需要填官方地址。</summary>
    public bool ShowOfficialSource => Mode == "OOS";

    [ObservableProperty] public partial string CzIp { get; set; } = "127.0.0.1";
    [ObservableProperty] public partial string CzPort { get; set; } = "21000";
    [ObservableProperty] public partial string OzIp { get; set; } = OfficialServer.Host;
    [ObservableProperty] public partial string OzPort { get; set; } = OfficialServer.PortText;

    /// <summary>子服务端名称(设置页里 IP 旁边显示的就是它)。</summary>
    [ObservableProperty] public partial string CzName { get; set; } = "Zinc Energy Server 锌能源服务器";

    /// <summary>官方服务端名称。</summary>
    [ObservableProperty] public partial string OzName { get; set; } = "ZXMOS 官方服务端";

    // ---------- 账号 ----------
    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string Level { get; set; } = Protocol.RegisterLevel;

    /// <summary>可选权限等级(登录时用;注册固定 Player)。</summary>
    public string[] Levels => AccountLevels.For(Mode, IsAdminAccount, AdminSide);

    /// <summary>是否登录管理账号(勾上就只登录,不给注册)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Levels), nameof(CanRegister), nameof(AdminSideVisible), nameof(LevelHint))]
    public partial bool IsAdminAccount { get; set; }

    /// <summary>SAOS 下选管理账号属于哪端:ZXMS / ZXMOS。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Levels), nameof(LevelHint))]
    public partial string AdminSide { get; set; } = "ZXMS";

    /// <summary>
    /// "哪一端的管理账号"下拉的选中项(0=ZXMS,1=ZXMOS)。
    /// 下拉项是 ComboBoxItem,和字符串双向绑定会静默失败,所以绑索引再折算成 <see cref="AdminSide"/>。
    /// </summary>
    [ObservableProperty]
    public partial int AdminSideIndex { get; set; }

    partial void OnAdminSideIndexChanged(int value) => AdminSide = value == 1 ? "ZXMOS" : "ZXMS";

    partial void OnModeChanged(string value)
    {
        ResetLevelDeferred();
        OnPropertyChanged(nameof(LastStep));
        OnPropertyChanged(nameof(StepCount));
        OnPropertyChanged(nameof(IsWelcomeInfo));
        OnPropertyChanged(nameof(IsIntroInfo));
        OnPropertyChanged(nameof(IsFinish));
        OnPropertyChanged(nameof(NextText));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(ModeIndex));
        RebuildChips();
        if (Step > LastStep)
        {
            Step = LastStep;
        }
    }

    /// <summary>SAOS 才需要选"哪一端的管理账号"。</summary>
    public bool AdminSideVisible => IsAdminAccount && Mode == "SAOS";

    /// <summary>管理账号不能在客户端注册。</summary>
    public bool CanRegister => !IsAdminAccount;

    /// <summary>提示语。</summary>
    public string LevelHint => IsAdminAccount
        ? "管理账号由服务端创建"
        : "玩家账号";

    // 等级必须等 ItemsSource 的通知走完再复位:直接改的话,ComboBox 会先把"不在旧列表里"的值清成空
    partial void OnIsAdminAccountChanged(bool value) => ResetLevelDeferred();

    partial void OnAdminSideChanged(string value) => ResetLevelDeferred();

    /// <summary>延后一拍复位(等 Levels 的通知走完)。</summary>
    private void ResetLevelDeferred() => Dispatcher.UIThread.Post(ResetLevel, DispatcherPriority.Background);

    /// <summary>模式 / 账号类型 / 哪一端变了,把等级拉回合法值。</summary>
    private void ResetLevel()
    {
        var list = Levels;
        if (Array.IndexOf(list, Level) < 0)
        {
            Level = list[0];
        }
    }

    [ObservableProperty] public partial string Message { get; set; } = "";

    /// <summary>需要弹窗提示的错误(标题, 内容)。</summary>
    public event Action<string, string>? ErrorRaised;

    /// <summary>失败:写状态栏 + 弹窗。</summary>
    private void Fail(string title, string message)
    {
        Message = message;
        ErrorRaised?.Invoke(title, message);
    }

    /// <summary>去掉服务端回执里的 "err|" 前缀。</summary>
    private static string Clean(string text)
    {
        var body = text.StartsWith("err|") ? text[4..] : text;

        // 回执里常带 "失败:" 前缀,和弹窗标题重复,去掉
        return body.StartsWith("失败:") ? body[3..] : body;
    }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    public partial bool IsLoggedIn { get; set; }

    /// <summary>首次使用向导的视图模型。</summary>
    public OobeViewModel()
    {
        _connect.AccountReply += OnAccountReply;

        // 配置里可能已经有值(升级上来的),先填进去
        Mode = Text("mode", "SAOS");
        CzIp = Text("czip", "127.0.0.1");
        CzPort = Text("czport", "21000");
        OzIp = Text("ozip", OfficialServer.Host);
        CzName = Text("czname", CzName);
        OzName = Text("ozname", OzName);
        OzPort = Text("ozport", OfficialServer.PortText);
        WallpaperPath = ClientConfig.GetOrEmpty("wallpaper");
        GlassEnabled = ThemeManager.GlassEnabled;
        OpacityPercent = ThemeManager.SurfaceAlpha;

        var cats = ClientConfig.GetOrEmpty("msgcats");
        CategoryLog = cats.Contains("log");
        CategoryChangelog = cats.Contains("changelog");
        CategoryWeb = cats.Contains("web");
        WebUrl = ClientConfig.GetOrEmpty("weburl");
        WebName = ClientConfig.GetOrEmpty("webname");

        RebuildChips();

        // 截图 / 调试用:配置里写 oobestep=2 就直接从第 3 步开始
        if (int.TryParse(ClientConfig.GetOrEmpty("oobestep"), out var step) && step >= 0 && step < StepCount)
        {
            Step = step;
        }
    }

    /// <summary>取消息的纯文本正文(不带格式段)。</summary>
    private static string Text(string key, string fallback)
    {
        var value = ClientConfig.GetOrEmpty(key);
        return value.Length > 0 ? value : fallback;
    }

    /// <summary>选择连接方式(= ZXMC 模式)。</summary>
    public void SelectMode(string mode) => Mode = mode;

    /// <summary>模式在联排按钮里的下标(0=SAOS,1=OS,2=OOS),高亮块按它滑动。</summary>
    public int ModeIndex => Mode switch { "OS" => 1, "OOS" => 2, _ => 0 };

    /// <summary>选了背景壁纸。</summary>
    public void SelectWallpaper(string path) => WallpaperPath = path;

    /// <summary>恢复默认底色。</summary>
    public void ClearWallpaper() => WallpaperPath = "";

    /// <summary>登录(等级由用户选择)。</summary>
    public Task<bool> LoginAsync() => AccountAsync(register: false);

    /// <summary>注册(等级固定 Player)。</summary>
    public Task<bool> RegisterAsync() => AccountAsync(register: true);

    private async Task<bool> AccountAsync(bool register)
    {
        if (IsBusy)
        {
            return false;
        }

        if (UserName.Trim().Length < 2 || Password.Length < 4)
        {
            Fail("输入不完整", "账号至少 2 个字符,密码至少 4 个字符");
            return false;
        }

        IsBusy = true;
        Message = "正在连接数据源…";

        // 先把这步填的数据源写进配置,再按模式连
        ApplyConnectTarget();

        // 已经连着但目标/模式变了(比如先连了 ZXMS 又改选 OOS)→ 先断开重连,别复用错连接
        var targetChanged = _connectedHost != _connect.Host
                            || _connectedPort != _connect.Port
                            || _connectedMode != _connect.Mode;
        if (_connect.IsConnected && targetChanged)
        {
            await _connect.DisconnectAsync();
        }

        if (!_connect.IsConnected)
        {
            await _connect.ConnectAsync();

            _connectedHost = _connect.Host;
            _connectedPort = _connect.Port;
            _connectedMode = _connect.Mode;

            if (!await _connect.WaitConnectedAsync())
            {
                Fail("无法连接服务器", $"连不上 {_connect.Host}:{_connect.Port},请检查数据源地址与端口后重试。");
                IsBusy = false;
                return false;
            }
        }

        Message = register ? "正在注册…" : "正在登录…";
        _reply = new TaskCompletionSource<(bool, string)>();

        // 密码在本机先算成密文再发出去(SHA-256,账号名当盐);服务端只存/只比密文
        var hash = PasswordHash.Hash(UserName.Trim(), Password);
        var type = register ? Protocol.AccountRegister : Protocol.AccountLogin;
        var side = AccountLevels.SideFor(Mode, IsAdminAccount, AdminSide);
        // 注册也要带机器码:服务端在注册阶段就能按机器码拦(注册载荷 = 名称|密文|机器码)
        // 附带挑战-响应:nonce 每次不同,proof = SHA256(密文 : nonce)
        var nonce = ZincXManagerShared.Account.LoginProof.NewNonce();
        var proof = ZincXManagerShared.Account.LoginProof.Compute(hash, nonce);
        var payload = register
            ? $"{UserName.Trim()}|{hash}|{MachineCode.Value}|{nonce}|{proof}"
            : $"{UserName.Trim()}|{hash}|{Level}|{side}|{MachineCode.Value}||{nonce}|{proof}";

        if (!await _connect.SendAsync(type, payload))
        {
            Fail("发送失败", "连接不可用(数据源已断开)");
            IsBusy = false;
            return false;
        }

        // 等回复,最多 4 秒
        var finished = await Task.WhenAny(_reply.Task, Task.Delay(4000));
        IsBusy = false;

        if (finished != _reply.Task)
        {
            Fail("服务端没有回应", "请求超时(4 秒),请检查数据源地址或稍后重试");
            return false;
        }

        var (ok, text) = _reply.Task.Result;
        Message = text;
        IsLoggedIn = ok;

        if (!ok)
        {
            // 被封禁 / 密码错误 / 账号不存在:都弹窗告诉用户
            Fail(register ? "注册失败" : "登录失败", Clean(text));
        }

        return ok;
    }

    /// <summary>收到账号类回执(601/603)后更新界面状态。</summary>
    private void OnAccountReply(ushort type, string text)
    {
        // 负载:"ok|名称|ID|等级" 或 "err|原因"
        var parts = text.Split('|');   //拆分结果
        var ok = parts.Length > 0 && parts[0] == "ok";
        var message = ok
            ? $"登录成功:{parts[1]}({parts[3]})"
            : "失败:" + (parts.Length > 1 ? parts[1] : text);

        if (ok)
        {
            ClientSession.User = parts.Length > 1 ? parts[1] : UserName.Trim();
            ClientSession.UserId = parts.Length > 2 ? parts[2] : "0";
            ClientSession.Level = parts.Length > 3 ? parts[3] : Level;
            ClientConfig.Set("user", ClientSession.User);
            ClientConfig.Set("userid", ClientSession.UserId);
            ClientConfig.Set("level", ClientSession.Level);

            // 登录成功:顺手把服务端保留期内还没收到的消息补齐(505 → 逐条 504 → 506 收尾)
            _ = _connect.RequestHistoryAsync();

            // 直连官方服务端时,再要一张"你确实是官方服务端"的证明(520 / 521)
            _ = _connect.VerifyOfficialAsync();
        }

        _reply?.TrySetResult((ok, message));
    }

    /// <summary>按当前模式设连接目标(OS/SAOS → czip;OOS → ozip)并写进 config</summary>
    public void ApplyConnectTarget()
    {
        SaveDataSource();

        _connect.Mode = Mode;
        _connect.Host = Mode == "OOS" ? OzIp : CzIp;
        _connect.Port = Mode == "OOS" ? OzPort : CzPort;
    }

    /// <summary>把当前填的数据源存进 config。</summary>
    private void SaveDataSource()
    {
        ClientConfig.Set("mode", Mode);
        ClientConfig.Set("czip", CzIp.Trim());
        ClientConfig.Set("czport", CzPort.Trim());
        ClientConfig.Set("ozip", OzIp.Trim());
        // 服务端名称不在这写:连接后由服务端下发(201),客户端只收不填
        ClientConfig.Set("ozport", OzPort.Trim());
    }

    /// <summary>走完流程:存配置 + 标记 OOBE 完成。</summary>
    // ---------- 第 4 步:消息分类 ----------

    /// <summary>「消息」分类固定勾选,不允许取消。</summary>
    public bool CategoryMessages => true;

    [ObservableProperty] public partial bool CategoryLog { get; set; }

    [ObservableProperty] public partial bool CategoryChangelog { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWebUrl))]
    public partial bool CategoryWeb { get; set; }

    /// <summary>勾了「自定义网页」才显示网址输入框。</summary>
    public bool ShowWebUrl => CategoryWeb;

    [ObservableProperty] public partial string WebUrl { get; set; } = "";

    /// <summary>这个网页在消息面板里显示的名字(用户自己起)。</summary>
    [ObservableProperty] public partial string WebName { get; set; } = "";

    public void Finish()
    {
        SaveDataSource();
        ClientConfig.Set("wallpaper", WallpaperPath);
        ClientConfig.Set("level", IsLoggedIn ? ClientSession.Level : Level);
        ClientConfig.Set("oobe", "1");

        // 消息分类(消息始终在)
        var cats = new System.Collections.Generic.List<string> { "messages" };
        if (CategoryLog) cats.Add("log");
        if (CategoryChangelog) cats.Add("changelog");
        if (CategoryWeb) cats.Add("web");
        ClientConfig.Set("msgcats", string.Join(",", cats));
        ClientConfig.Set("weburl", WebUrl.Trim());
        ClientConfig.Set("webname", WebName.Trim().Length > 0 ? WebName.Trim() : "自定义网页");

        Logger.Log(LogLevel.Info,
            $"[ZXMC] OOBE 完成:模式={Mode} 数据源={CzipText()} 官方={OzipText()} 账号={ClientSession.User} 等级={Level}");
    }

    /// <summary>子服地址的输入框文本(读写 config 里的 czip/czport)。</summary>
    private string CzipText() => $"{CzIp}:{CzPort}";
    /// <summary>官方服地址的输入框文本(读写 config 里的 ozip/ozport)。</summary>
    private string OzipText() => $"{OzIp}:{OzPort}";
}
