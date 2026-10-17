using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ZincXManagerClient.Models;
using ZincXManagerShared.Account;
using ZincXManagerShared.Logging;
using ZincXManagerShared.Network;

namespace ZincXManagerClient.ViewModels;

/// <summary>
/// 登录窗口视图模型:头像 + 账号 / 密码 / 权限等级 + 「7 天内免登录」「记住账号密码」。
///
/// <para>头像:输入账号名就先去本地缓存里找(avoids 未登录也能看到上次的头像);
/// 登录成功后向服务端要一次最新的,并存回本地缓存。</para>
/// </summary>
public partial class LoginViewModel : ViewModelBase
{
    private readonly ConnectViewModel _connect = new();
    private TaskCompletionSource<(bool Ok, string Text)>? _reply;
    private string _usedHash = "";

    public LoginViewModel()
    {
        _connect.PacketReceived += OnPacket;
        _connect.AccountReply += OnAccountReply;

        // 有记录就把账号、等级、两个勾选填上
        UserName = LoginRecord.User;
        Level = LoginRecord.Level;
        Remember = LoginRecord.Remember || LoginRecord.User.Length == 0;
        SavePassword = LoginRecord.SavePassword || LoginRecord.User.Length == 0;

        LoadCachedAvatar(UserName);
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

    // ---------- 头像 ----------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar), nameof(AvatarHint))]
    public partial IImage? Avatar { get; set; }

    public bool HasAvatar => Avatar is not null;

    public string AvatarHint => HasAvatar ? "已载入头像" : "还没有头像(登录后可在 设置 → 账户与安全 里设置)";

    // ---------- 账号 ----------
    [ObservableProperty] public partial string UserName { get; set; } = "";
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

    /// <summary>管理账号不能在客户端注册。</summary>
    public bool CanRegister => !IsAdminAccount;

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

        if (value.Trim().Equals(LoginRecord.User, StringComparison.OrdinalIgnoreCase))
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
        Message = "正在用记住的账号登录…";
        return LoginCoreAsync(LoginRecord.User, LoginRecord.Hash, LoginRecord.Level, register: false, saveRecord: false);
    }

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
        var payload = register
            ? $"{user}|{hash}|{MachineCode.Value}|{nonce}|{proof}"
            : $"{user}|{hash}|{level}|{side}|{MachineCode.Value}|{nonce}|{proof}";

        if (!await _connect.SendAsync(type, payload))
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
            // 被封禁 / 密码错误 / 账号不存在:都弹窗告诉用户
            Fail(register ? "注册失败" : "登录失败", Clean(text));
            return false;
        }

        IsLoggedIn = true;

        if (saveRecord)
        {
            LoginRecord.Save(user, hash, level, Remember, SavePassword);
        }

        // 登录成功:向服务端要一次最新头像
        await _connect.SendAsync(Protocol.AccountAvatarReq, "");
        return true;
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

            // 登录成功:顺手把服务端保留期内还没收到的消息补齐(505 → 逐条 504 → 506 收尾)
            _ = _connect.RequestHistoryAsync();

            // 直连官方服务端时,再要一张"你确实是官方服务端"的证明(520 / 521)
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
        var text = System.Text.Encoding.UTF8.GetString(packet.Data);
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
