using System;   // RandomNumberGenerator / Convert / int
using System.IO;   // Path
using System.Threading.Tasks;   // Task
using ZincXManagerShared.Account;   // Account
using ZincXManagerShared.FileIO;   // ZFile
using ZincXManagerShared.Logging;   // Logger / LogLevel
using ZincXManagerShared.Network;   // Protocol / TcpConnection / SendAsync 扩展

namespace ZincXManagerServer;   // 与 Program 同一个分部类

using ZincXManagerServer.Chat;   // ChatHostRunner

/// <summary>
/// 把聊天宿主接进子服启动流程:读配置、拉起独立进程、登记退出时收尾。
/// </summary>
internal static partial class Program
{
    /// <summary>配置键:是否启用聊天后端(默认 1)。</summary>
    private const string KeyChatEnabled = "chatenabled";

    /// <summary>配置键:聊天宿主监听端口(默认 czport+300)。</summary>
    private const string KeyChatPort = "chatport";

    /// <summary>配置键:与聊天宿主共享的签名密钥(缺失时自动生成并写回)。</summary>
    private const string KeyChatSecret = "chatsecret";

    /// <summary>配置键:聊天宿主可执行文件(默认运行目录下 ZincXManagerChat.dll)。</summary>
    private const string KeyChatExe = "chatexe";

    /// <summary>聊天宿主与客户端端口的固定偏移:21000 → 21300。</summary>
    private const int ChatPortOffset = 300;

    /// <summary>自动生成的密钥长度(字符)。</summary>
    private const int GeneratedSecretLength = 32;

    /// <summary>聊天宿主进程句柄;没启用时为 null。</summary>
    private static ChatHostRunner? _chatHost;

    /// <summary>
    /// 按配置拉起聊天宿主。失败只记日志,不影响子服主流程。
    /// </summary>
    /// <param name="config">已加载的 config.ini。</param>
    internal static void StartChatHost(ZFile config)
    {
        if (config.MapGet(KeyChatEnabled).Trim() == "0")
        {
            Logger.Log(LogLevel.Info, "[聊天] 已按配置关闭(chatenabled=0),好友聊天不可用");
            return;
        }

        var port = ResolveChatPort(config);
        var secret = ResolveChatSecret(config);
        var executable = config.MapGet(KeyChatExe).Trim();   //配置值
        if (executable.Length == 0)
        {
            executable = Path.Combine(AppContext.BaseDirectory, "ZincXManagerChat.dll");
        }

        var data = Path.Combine(AppContext.BaseDirectory, "Data", "chat");
        var runner = new ChatHostRunner();
        if (!runner.Start(executable, port, data, secret))
        {
            Logger.Log(LogLevel.Error, $"[聊天] 聊天宿主启动失败:起不来或端口 {port} 没在听 {executable}(好友聊天不可用)");
            return;
        }

        _chatHost?.Dispose();   // 换新的之前先把旧的收掉,免得留下没人管的宿主进程
        _chatHost = runner;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _chatHost?.Dispose();
        Logger.Log(LogLevel.Info, $"[聊天] 聊天宿主已拉起:端口 {port},数据 {data}");
    }

    /// <summary>
    /// 聊天端口:优先读 chatport,缺省用客户端端口 +300;写回配置,便于外网放行。
    /// </summary>
    private static int ResolveChatPort(ZFile config)
    {
        var raw = config.MapGet(KeyChatPort).Trim();   //配置值
        if (int.TryParse(raw, out var value) && value > 0 && value <= 65535)
        {
            return value;
        }

        var fallback = (int.TryParse(_czport, out var cz) ? cz : 25566) + ChatPortOffset;
        if (fallback > 65535)
        {
            fallback = 65535 - ChatPortOffset;
        }
        config.WriteFileMap(KeyChatPort, fallback.ToString());
        Logger.Log(LogLevel.Info, $"[聊天]{KeyChatPort} 未配置,已写入默认值 {fallback}(客户端端口 +{ChatPortOffset})");
        return fallback;
    }

    /// <summary>
    /// 签名密钥:缺失时随机生成并写回配置;已有但太短则重新生成(短密钥等于没签)。
    /// </summary>
    private static string ResolveChatSecret(ZFile config)
    {
        var secret = config.MapGet(KeyChatSecret).Trim();   //配置值
        if (secret.Length >= 16)
        {
            return secret;
        }

        secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(GeneratedSecretLength / 2));
        config.WriteFileMap(KeyChatSecret, secret);
        Logger.Log(LogLevel.Info, $"[聊天]{KeyChatSecret} 缺失或过短,已生成新的签名密钥");
        return secret;
    }

    /// <summary>给客户端签一张聊天令牌;聊天没开时返回空串。</summary>
    /// <param name="userId">账号编号(字符串形式,就是令牌里的身份)。</param>
    /// <param name="displayName">账号名(只用于显示)。</param>
    internal static string SignChatToken(string userId, string displayName)
        => _chatHost?.SignFor(userId, displayName, ChatTokenLifetimeSeconds) ?? "";

    /// <summary>
    /// 令牌有效期。别设太长:令牌是无状态 HMAC,删好友、踢人、改密码都吊销不了它,
    /// 一旦被人从内存或日志里捞走,在这个时长内就是完全权限。
    /// </summary>
    private const int ChatTokenLifetimeSeconds = 3600;

    /// <summary>
    /// 中转登录(账号由官方端校验)之后补发 510。这条路上本地账号是「按需镜像」的,
    /// 镜像可能还没落地,所以给最多 2 秒等它出现;等不到就只记一条日志。
    /// </summary>
    /// <param name="connection">刚登录成功的这条连接。</param>
    /// <param name="name">账号名。</param>
    /// <param name="config">本服配置。</param>
    internal static async Task SendChatHelloByNameAsync(TcpConnection connection, string name, ZFile config)
    {
        if (_chatHost is null || !_chatHost.IsRunning)
        {
            return;
        }

        var account = Accounts.Find(name);
        for (var waited = 0; account is null && waited < 2000; waited += 100)
        {
            await Task.Delay(100);
            account = Accounts.Find(name);
        }

        if (account is null)
        {
            Logger.Log(LogLevel.Warning, $"[聊天] {name} 登录成功但本地账号还没镜像出来,这次没发聊天令牌");
            return;
        }

        await SendChatHelloAsync(connection, account, config);
    }

    /// <summary>
    /// 登录成功后把聊天后端的端口与令牌发给客户端(510),并把好友私聊频道准备好。
    /// 聊天没开就什么都不发,客户端会一直走原来的 400 直发。
    /// </summary>
    /// <param name="connection">刚登录成功的这条连接。</param>
    /// <param name="account">登录的账号。</param>
    /// <param name="config">本服配置。</param>
    internal static async Task SendChatHelloAsync(TcpConnection connection, Account account, ZFile config)
    {
        var host = _chatHost;
        if (host is null || !host.IsRunning)
        {
            return;
        }

        var token = host.SignFor(account.Id.ToString(), account.Name, ChatTokenLifetimeSeconds);
        if (token.Length == 0)
        {
            return;
        }

        var selfServerId = SelfServerId(config);
        await connection.SendAsync(Protocol.ChatHello, $"{host.Port}\t{token}\t{selfServerId}");
        Logger.Log(LogLevel.Info,
            $"[聊天] 已把聊天后端端口 {host.Port} 与令牌发给 {account.Name}(本服ID={selfServerId})");

        // 建频道要发 HTTP,不能占着登录这条线,丢后台
        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureChatChannelsAsync(account, config);
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, $"[聊天] 准备频道失败:{ex.Message}");
            }
        });
    }
}

