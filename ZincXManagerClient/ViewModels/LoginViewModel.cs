using System;  // 基础类型 / 常用静态方法
using System.IO;  // 文件与目录读写
using System.Threading.Tasks;  // async / await 异步
using Avalonia.Media;  // 画刷 / 颜色 / 几何
using Avalonia.Media.Imaging;  // 位图
using Avalonia.Threading;  // UI 线程调度
using CommunityToolkit.Mvvm.ComponentModel;  // MVVM 可观察对象
using ZincXManagerClient.Models;  // 客户端自身
using ZincXManagerClient.Modules;  // 客户端模块开关
using ZincXManagerShared.Account;  // 共享库
using ZincXManagerShared.Logging;  // 共享库
using ZincXManagerShared.Modules;  // 共享库:模块编号
using ZincXManagerShared.Network;  // 共享库

namespace ZincXManagerClient.ViewModels;

/// <summary>
/// 登录窗口视图模型:头像 + 账号 / 密码 / 权限等级 + 「7 天内免登录」「记住账号密码」。
///
/// <para>头像:输入账号名就先去本地缓存里找(avoids 未登录也能看到上次的头像);
/// 登录成功后向服务端要一次最新的,并存回本地缓存。</para>
/// </summary>
public partial class LoginViewModel : ViewModelBase, IDisposable
{
    private readonly ConnectViewModel _connect;
    private TaskCompletionSource<(bool Ok, string Text)>? _reply;
    private string _usedHash = "";

    /// <summary>自己建连接(默认)。</summary>
    public LoginViewModel() : this(new ConnectViewModel())
    {
    }

    /// <summary>复用外面已经建好、可能已经连上的连接(启动自检那条路)。</summary>
    public LoginViewModel(ConnectViewModel connect)
    {
        _connect = connect;
        _connect.PacketReceived += OnPacket;
        _connect.AccountReply += OnAccountReply;

        // 有记录就把账号、等级、两个勾选填上
        UserName = LoginRecord.User;
        Level = LoginRecord.Level;
        Remember = LoginRecord.Remember || LoginRecord.User.Length == 0;
        SavePassword = LoginRecord.SavePassword || LoginRecord.User.Length == 0;

        LoadCachedAvatar(UserName);

        // 服务端可能在登录窗已经开起来之后才下发开关名单(202 来晚了):跟着重算注册按钮的显隐
        ModuleState.Changed += OnModulesChanged;
    }

    /// <summary>
    /// 退订模块开关事件(窗口关掉时调)。<see cref="ModuleState.Changed"/> 是静态事件,
    /// 不退订的话这个视图模型会被一直钉在内存里。
    /// </summary>
    public void Dispose() => ModuleState.Changed -= OnModulesChanged;

    /// <summary>模块开关变了:注册入口要跟着显示 / 隐藏。</summary>
    private void OnModulesChanged()
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            OnPropertyChanged(nameof(CanRegister));
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(CanRegister)));
    }

    /// <summary>给主界面接着用的连接。</summary>
    public ConnectViewModel Connect => _connect;

    /// <summary>是否已登录成功。</summary>
    public bool IsLoggedIn { get; private set; }

    /// <summary>需要弹窗提示的错误(标题, 内容)—— 封号、连不上服务器这类都要让用户看见。</summary>
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

    /// <summary>服务端这条回执是不是「本服开了强制更新,客户端版本太旧」。</summary>
    /// <para>不能按 "err|" 开头判:OnAccountReply 已经把前缀剥掉、换成 "失败:" 了,这里只认记号本身。</para>
    private static bool IsUpdateRequired(string text)
        => text.Contains(Protocol.UpdateRequiredMarker, StringComparison.Ordinal);

    // ---------- 头像 ----------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar), nameof(AvatarHint))]
    public partial IImage? Avatar { get; set; }

    public bool HasAvatar => Avatar is not null;

    /// <summary>没有头像时画的那个首字(账户名第一个字)。</summary>
    public string UserInitial => UserName.Length > 0 ? UserName[..1].ToUpperInvariant() : "?";

    public string AvatarHint => HasAvatar ? "已载入头像" : "还没有头像(登录后可在 设置 → 账户与安全 里设置)";

    // ---------- 账号 ----------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserInitial))]
    public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string Level { get; set; } = Protocol.RegisterLevel;

    // 等级必须等 ItemsSource 的通知走完再复位:直接改的话,ComboBox 会先把"不在旧列表里"的值清成空
    partial void OnIsAdminAccountChanged(bool value) => ResetLevelDeferred();

    partial void OnAdminSideChanged(string value) => ResetLevelDeferred();

    /// <summary>延后一拍复位(等 Levels 的通知走完)。</summary>
    private void ResetLevelDeferred() => Dispatcher.UIThread.Post(ResetLevel, DispatcherPriority.Background);

    private void ResetLevel()
    {
        var list = Levels;
        if (Array.IndexOf(list, Level) < 0)
        {
            Level = list[0];
        }
    }
    /// <summary>连接方式(决定管理账号属于哪一端)。</summary>
    public string Mode { get; } = ClientConfig.GetOrEmpty("mode") is { Length: > 0 } m ? m : "SAOS";

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

    /// <summary>SAOS 才需要选"哪一端的管理账号"。</summary>
    public bool AdminSideVisible => IsAdminAccount && Mode == "SAOS";

    /// <summary>
    /// 管理账号不能在客户端注册;服务端把「账号注册」模块关掉或半加载时也不给注册。
    /// 半加载对注册一律不给:注册发生在登录之前,当时服务器还不知道来的是谁。
    /// </summary>
    public bool CanRegister => !IsAdminAccount && ModuleState.RegisterAllowed;

    /// <summary>提示语。</summary>
    public string LevelHint => IsAdminAccount
        ? "管理账号只能在服务端用 user reg 创建,这里只登录"
        : "玩家账号:优先在官方服务端注册 / 登录";

    public string[] Levels => AccountLevels.For(Mode, IsAdminAccount, AdminSide);

    /// <summary>7 天内免登录。</summary>
    [ObservableProperty] public partial bool Remember { get; set; } = true;

    /// <summary>记住账号密码(存的是密文)。</summary>
    [ObservableProperty] public partial bool SavePassword { get; set; } = true;

    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }

    /// <summary>账号名一变就去找本地缓存头像。</summary>
    partial void OnUserNameChanged(string value)
    {
        LoadCachedAvatar(value);

        // 上次登录的可能是管理账号(LAdmin 之类)。没勾「登录管理账号」时下拉里只有 Player,
        // 这时候把 LAdmin 回填进去会带着管理等级去登录 —— 服务端按「ZXMOS 管理账号」处理,
        // 官方端没连就报「官方服务端未连接,无法登录 ZXMOS 管理账号」,玩家会一头雾水。
        // 所以只有这个等级在当前下拉里可选时才回填。
        if (value.Trim().Equals(LoginRecord.User, StringComparison.OrdinalIgnoreCase)
            && Array.IndexOf(Levels, LoginRecord.Level) >= 0)
        {
            Level = LoginRecord.Level;
        }
    }

    /// <summary>从本地缓存读头像(登录前也能显示上次的)。</summary>
    public void LoadCachedAvatar(string user)
    {
        var bytes = AvatarStore.Load(user);
        Avatar = ToImage(bytes);
    }

    /// <summary>登录(密码在本机算成密文)。</summary>
    public Task<bool> LoginAsync()
    {
        var user = UserName.Trim();
        if (user.Length < 2 || Password.Length < 4)
        {
            Fail("输入不完整", "账号至少 2 个字符,密码至少 4 个字符");
            return Task.FromResult(false);
        }

        _usedHash = PasswordHash.Hash(user, Password);
        return LoginCoreAsync(user, _usedHash, Level, register: false, saveRecord: true);
    }

    /// <summary>注册(只能是 Player)。</summary>
    public Task<bool> RegisterAsync()
    {
        if (!ModuleState.RegisterAllowed)
        {
            // 兜底:服务器把注册模块关掉 / 半加载后注册入口已经隐藏,快捷键或自动化再点进来也不发注册包
            Logger.Log(LogLevel.Info, "[ZXMC] 账号注册模块不可用(已关闭或仅限管理员),不发注册包");
            return Task.FromResult(false);
        }

        var user = UserName.Trim();
        if (user.Length < 2 || Password.Length < 4)
        {
            Fail("输入不完整", "账号至少 2 个字符,密码至少 4 个字符");
            return Task.FromResult(false);
        }

        _usedHash = PasswordHash.Hash(user, Password);
        return LoginCoreAsync(user, _usedHash, Protocol.RegisterLevel, register: true, saveRecord: true);
    }

    /// <summary>用记住的密文免登录(7 天内的记录)。</summary>
    public Task<bool> AutoLoginAsync()
    {
        // 上次登录的是管理账号时不能静默顶用:那张登录窗没勾「登录管理账号」,
        // 拿管理等级去登录只会被服务端按官方账号处理然后失败。让用户自己勾一次。
        if (Array.IndexOf(Levels, LoginRecord.Level) < 0)
        {
            Message = $"上次登录的是管理账号({LoginRecord.Level}),请勾选「登录管理账号」后手动登录";
            return Task.FromResult(false);
        }

        Message = "正在用记住的账号登录…";
        return LoginCoreAsync(LoginRecord.User, LoginRecord.Hash, LoginRecord.Level, register: false, saveRecord: false);
    }

    /// <summary>真正走一次登录:发 6000 登录包,等 6001 回执。</summary>
    private async Task<bool> LoginCoreAsync(string user, string hash, string level, bool register, bool saveRecord)
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        _usedHash = hash;
        Message = register ? "正在注册…" : "正在连接数据源…";

        // 连接(数据源参数已经在 config.ini 里)
        if (!_connect.IsConnected)
        {
            await _connect.ConnectAsync();

            if (!await _connect.WaitConnectedAsync())
            {
                Fail("无法连接服务器", $"连不上 {_connect.Host}:{_connect.Port},请检查数据源地址与端口后重试。");
                IsBusy = false;
                return false;
            }
        }

        Message = register ? "正在注册…" : "正在登录…";
        _reply = new TaskCompletionSource<(bool, string)>();

        var type = register ? Protocol.AccountRegister : Protocol.AccountLogin;
        // 登录载荷第 4 段是"这条请求归哪一端"(zxms / zxmos);注册永远是玩家账号,交给 ZXMS 决定
        var side = AccountLevels.SideFor(Mode, IsAdminAccount, AdminSide);
        // 注册也要带机器码:服务端在注册阶段就能按机器码拦(注册载荷 = 名称|密文|机器码)
        // 登录附带挑战-响应:nonce 每次不同,proof = SHA256(密文 : nonce);密文本身仅用于转发给 ZXMOS(整包已加密)
        var nonce = ZincXManagerShared.Account.LoginProof.NewNonce();
        var proof = ZincXManagerShared.Account.LoginProof.Compute(hash, nonce);
        // 最后一段是本端版本号:服务端开了强制更新时会拿它判断要不要拒登录 / 踢下线
        var version = ZincXManagerClient.Services.UpdateService.CurrentVersion;
        var payload = register
            ? $"{user}|{hash}|{MachineCode.ValueForLogin}|{nonce}|{proof}|{version}"
            : $"{user}|{hash}|{level}|{side}|{MachineCode.ValueForLogin}||{nonce}|{proof}|{version}";

        bool sent;   // 登录包发出去了没;写包抛异常由下面的 catch 接住
        try
        {
            sent = await _connect.SendAsync(type, payload);
        }
        catch (Exception ex)
        {
            // 链路断了但连接对象还在时,WriteAsync 会抛 IOException(10054)。
            // 这里必须接住:异常沿着 async void 的点击事件一路冒到进程顶层,没有全局兜底就是整个客户端退出。
            // 接住后按普通失败提示,并复位 IsBusy,让「登录」按钮能再点。
            Logger.Log(LogLevel.Warning, $"[ZXMC] 登录包发送失败(连接已断开):{ex.Message}");
            Fail("连接已断开", "连接已断开,请重试。");
            IsBusy = false;
            return false;
        }

        if (!sent)
        {
            Fail("发送失败", "连接不可用(数据源已断开)");
            IsBusy = false;
            return false;
        }

        var finished = await Task.WhenAny(_reply.Task, Task.Delay(4000));
        if (finished != _reply.Task)
        {
            Fail("服务端没有回应", "请求超时(4 秒),请检查数据源地址或稍后重试");
            IsBusy = false;
            return false;
        }

        var (ok, text) = _reply.Task.Result;
        Message = text;
        IsBusy = false;

        if (!ok)
        {
            // 被封禁 / 密码错误 / 账号不存在:都弹窗告诉用户;
            // 服务端开了强制更新时标题改成「需要更新客户端」,别让人以为是账号问题
            Fail(IsUpdateRequired(text) ? "需要更新客户端" : register ? "注册失败" : "登录失败", Clean(text));
            return false;
        }

        IsLoggedIn = true;

        // 缓存凭据:断线重连后自动重登(只放内存,不落盘)
        ClientSession.LoginHash = hash;
        ClientSession.LoginLevel = level;
        ClientSession.LoginSide = side;

        if (saveRecord)
        {
            LoginRecord.Save(user, hash, level, Remember, SavePassword);
        }

        // 登录成功:向服务端要一次最新头像
        await _connect.SendAsync(Protocol.AccountAvatarReq, "");
        return true;
    }

    /// <summary>收到账号类回执(6001/6003)后更新界面状态。</summary>
    private void OnAccountReply(ushort type, string text)
    {
        // 负载:"ok|名称|ID|等级|完整ID" 或 "err|原因"
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
            ClientSession.FullId = parts.Length > 4 ? parts[4].Trim() : "";
            ClientConfig.Set("user", ClientSession.User);
            ClientConfig.Set("userid", ClientSession.UserId);
            ClientConfig.Set("level", ClientSession.Level);
            ClientConfig.Set("fullid", ClientSession.FullId);

            // 登录成功:顺手把服务端保留期内还没收到的消息补齐(505 → 逐条 504 → 506 收尾)
            _ = _connect.RequestHistoryAsync();

            // 直连官方服务端时,再要一张"你确实是官方服务端"的证明(5020 / 5021)
            _ = _connect.VerifyOfficialAsync();
        }

        _reply?.TrySetResult((ok, message));
    }

    /// <summary>收包:605 是头像(图片字节,或设置头像时的 "ok" 文本)。</summary>
    private void OnPacket(Packet packet)
    {
        if (packet.Type != Protocol.AccountAvatarAck)
        {
            return;
        }

        // 全是可打印文本且以 ok/err 开头 → 是"设置头像"的回执,不当图片处理
        var text = System.Text.Encoding.UTF8.GetString(packet.Data);   //包内容
        if (text.StartsWith("ok") || text.StartsWith("err"))
        {
            Message = text.StartsWith("ok") ? "头像已更新" : text;
            return;
        }

        if (packet.Data.Length == 0 || ClientSession.User.Length == 0)
        {
            Avatar = null;
            return;
        }

        // 缓存到本地(下次登录前也能显示)
        AvatarStore.Save(ClientSession.User, packet.Data, out _);
        Avatar = ToImage(packet.Data);
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
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[ZXMC] 头像解码失败: " + ex.Message);
            return null;
        }
    }
}
