using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ZincXManagerClient.Models;
using ZincXManagerShared.Account;
using ZincXManagerShared.Logging;
using ZincXManagerShared.Network;

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
    public const int StepCount = 4;

    /// <summary>OOBE 内部自己用的连接(负责 300 握手与收账号回复)。</summary>
    private readonly ConnectViewModel _connect = new();

    private TaskCompletionSource<(bool Ok, string Text)>? _reply;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsPersonalize), nameof(IsDataSource), nameof(IsAccount),
        nameof(StepText), nameof(CanGoBack), nameof(NextText))]
    public partial int Step { get; set; }

    public bool IsWelcome => Step == 0;
    public bool IsPersonalize => Step == 1;
    public bool IsDataSource => Step == 2;
    public bool IsAccount => Step == 3;

    public string StepText => $"{Step + 1} / {StepCount}";
    public bool CanGoBack => Step > 0;
    public string NextText => Step >= StepCount - 1 ? "完成" : "下一步";

    /// <summary>给外面(主界面)接着用的连接。</summary>
    public ConnectViewModel Connect => _connect;

    // ---------- 个性化 ----------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WallpaperHint))]
    public partial string WallpaperPath { get; set; } = "";

    public string WallpaperHint => WallpaperPath.Length > 0
        ? "已选择:" + WallpaperPath
        : "先用默认底色(以后可在 设置 → 个性化 里换)";

    /// <summary>毛玻璃(亚克力)开关:开 = 背景模糊 + Fluent 亚克力着色;关 = 纯半透明。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlassHint))]
    public partial bool GlassEnabled { get; set; } = true;

    /// <summary>毛玻璃开关的说明(跟着状态变)。</summary>
    public string GlassHint => GlassEnabled
        ? "亚克力:背景按 Fluent 亚克力模糊(半径 30),表面着色 80%"
        : "半透明:背景不模糊,表面着色 60%";

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
    public partial string Mode { get; set; } = "SAOS";

    public bool IsSaos => Mode == "SAOS";
    public bool IsOs => Mode == "OS";
    public bool IsOos => Mode == "OOS";

    /// <summary>
    /// OS / SAOS 时客户端只连子服务端(官方那一段由 ZXMS 自己连),所以只填子服务端地址;
    /// OOS 是客户端直连官方服务端,才需要填官方地址。
    /// </summary>
    public bool ShowClientSource => Mode != "OOS";

    /// <summary>只有 OOS 直连官方服务端时才需要填官方地址。</summary>
    public bool ShowOfficialSource => Mode == "OOS";

    [ObservableProperty] public partial string CzIp { get; set; } = "127.0.0.1";
    [ObservableProperty] public partial string CzPort { get; set; } = "21000";
    [ObservableProperty] public partial string OzIp { get; set; } = "127.0.0.1";
    [ObservableProperty] public partial string OzPort { get; set; } = "20000";

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

    partial void OnModeChanged(string value) => ResetLevelDeferred();

    /// <summary>SAOS 才需要选"哪一端的管理账号"。</summary>
    public bool AdminSideVisible => IsAdminAccount && Mode == "SAOS";

    /// <summary>管理账号不能在客户端注册。</summary>
    public bool CanRegister => !IsAdminAccount;

    /// <summary>提示语。</summary>
    public string LevelHint => IsAdminAccount
        ? "管理账号只能在服务端用 user reg 创建,这里只登录"
        : "玩家账号:优先在官方服务端注册 / 登录";

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
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsLoggedIn { get; set; }

    public OobeViewModel()
    {
        _connect.AccountReply += OnAccountReply;

        // 配置里可能已经有值(升级上来的),先填进去
        Mode = Text("mode", "SAOS");
        CzIp = Text("czip", "127.0.0.1");
        CzPort = Text("czport", "21000");
        OzIp = Text("ozip", "127.0.0.1");
        CzName = Text("czname", CzName);
        OzName = Text("ozname", OzName);
        OzPort = Text("ozport", "20000");
        WallpaperPath = ClientConfig.GetOrEmpty("wallpaper");
        GlassEnabled = ThemeManager.GlassEnabled;

        // 截图 / 调试用:配置里写 oobestep=2 就直接从第 3 步开始
        if (int.TryParse(ClientConfig.GetOrEmpty("oobestep"), out var step) && step >= 0 && step < StepCount)
        {
            Step = step;
        }
    }

    private static string Text(string key, string fallback)
    {
        var value = ClientConfig.GetOrEmpty(key);
        return value.Length > 0 ? value : fallback;
    }

    /// <summary>选择连接方式(= ZXMC 模式)。</summary>
    public void SelectMode(string mode) => Mode = mode;

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
            Message = "账号至少 2 个字符,密码至少 4 个字符";
            return false;
        }

        IsBusy = true;
        Message = "正在连接数据源…";

        // 先把这步填的数据源写进配置,再按模式连
        SaveDataSource();

        _connect.Mode = Mode;
        _connect.Host = Mode == "OOS" ? OzIp : CzIp;
        _connect.Port = Mode == "OOS" ? OzPort : CzPort;

        if (!_connect.IsConnected)
        {
            await _connect.ConnectAsync();

            if (!await _connect.WaitConnectedAsync())
            {
                Message = "连接失败:" + _connect.State;
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
        var payload = register ? $"{UserName.Trim()}|{hash}|{MachineCode.Value}" : $"{UserName.Trim()}|{hash}|{Level}|{side}|{MachineCode.Value}";

        if (!await _connect.SendAsync(type, payload))
        {
            Message = "发送失败:连接不可用";
            IsBusy = false;
            return false;
        }

        // 等回复,最多 4 秒
        var finished = await Task.WhenAny(_reply.Task, Task.Delay(4000));
        IsBusy = false;

        if (finished != _reply.Task)
        {
            Message = "服务端没有回应";
            return false;
        }

        var (ok, text) = _reply.Task.Result;
        Message = text;
        IsLoggedIn = ok;
        return ok;
    }

    private void OnAccountReply(ushort type, string text)
    {
        // 负载:"ok|名称|ID|等级" 或 "err|原因"
        var parts = text.Split('|');
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
        }

        _reply?.TrySetResult((ok, message));
    }

    /// <summary>把数据源那几项写进 config.ini。</summary>
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
    public void Finish()
    {
        SaveDataSource();
        ClientConfig.Set("wallpaper", WallpaperPath);
        ClientConfig.Set("level", IsLoggedIn ? ClientSession.Level : Level);
        ClientConfig.Set("oobe", "1");

        Logger.Log(LogLevel.Info,
            $"[ZXMC] OOBE 完成:模式={Mode} 数据源={CzipText()} 官方={OzipText()} 账号={ClientSession.User} 等级={Level}");
    }

    private string CzipText() => $"{CzIp}:{CzPort}";
    private string OzipText() => $"{OzIp}:{OzPort}";
}
