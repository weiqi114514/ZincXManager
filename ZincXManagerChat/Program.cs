using System.Globalization;   // 时间戳与文化无关的数字格式
using System.Security.Cryptography;   // HMAC-SHA256 签名
using System.Text;   // 文本编码
using BareChat;   // IChatAuthProvider
using BareChat.Core.Domain;   // ChatUserContext
using ZincXManagerShared.Chat;   // ChatToken 验签
using BareChat.Extensions;   // AddBareChat / UseBareChat
using Microsoft.AspNetCore.Http;   // HttpContext

namespace ZincXManagerChat;   // 聊天宿主:现成后端(BareChat) + 我们的账号校验

/// <summary>
/// 聊天宿主进程入口:把 BareChat 挂在 Kestrel 上,账号用子服与自己共享的密钥校验。
/// 只负责「存消息、推消息」,谁能不能聊天由子服在频道成员里决定。
/// </summary>
internal static class Program
{
    /// <summary>启动参数:监听端口(默认 21300)。</summary>
    private const string ArgPort = "--port";

    /// <summary>启动参数:数据目录(chat.db 与图片都放这里)。</summary>
    private const string ArgData = "--data";

    /// <summary>启动参数:与子服共享的签名密钥,令牌由子服签发。</summary>
    private const string ArgSecret = "--secret";

    /// <summary>签名密钥的环境变量名(子服拉起宿主时用这个传,避免密钥出现在命令行里)。</summary>
    private const string ArgSecretEnv = "ZXM_CHAT_SECRET";

    /// <summary>令牌有效期上限(秒):子服签发时自己也会卡,这里只防畸形值。</summary>
    private const long MaxTokenLifetimeSeconds = 7 * 24 * 3600;

    /// <summary>进程退出码:参数不对。</summary>
    private const int ExitBadArgs = 2;

    /// <summary>
    /// 解析参数、装配 ASP.NET Core 宿主并阻塞运行。
    /// </summary>
    private static int Main(string[] args)
    {
        var port = ReadIntArg(args, ArgPort, 21300);
        var data = ReadStringArg(args, ArgData, "Data/chat");
        // 优先读环境变量:命令行参数在进程列表里人人可见,等于把密钥挂在外面
        var secret = Environment.GetEnvironmentVariable(ArgSecretEnv) ?? "";
        if (secret.Length < 16)
        {
            secret = ReadStringArg(args, ArgSecret, "");
        }

        if (secret.Length < 16)
        {
            Console.Error.WriteLine($"[Error] [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Chat] 缺少签名密钥(环境变量 {ArgSecretEnv} 或 {ArgSecret}),拒绝启动");
            return ExitBadArgs;
        }

        Directory.CreateDirectory(data);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // 不把启动参数喂给配置源:那会把密钥塞进配置树,任何能 dump 配置的地方都能看到
            Args = [],
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddBareChat(options =>
        {
            options.DataPath = data;
            options.RoutePrefix = "/chat";
        });
        // 必须在 AddBareChat 之后注册:它自己会注册一个读 HttpContext.User 的默认实现,后注册的才生效。
        builder.Services.AddSingleton<IChatAuthProvider>(_ => new TokenChatAuthProvider(secret));

        var app = builder.Build();
        app.UseRouting();
        app.UseBareChat();

        var url = $"http://127.0.0.1:{port}";
        app.Urls.Add(url);
        Console.WriteLine($"[Info] [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Chat] 聊天宿主启动: {url}/chat 数据={Path.GetFullPath(data)}");
        app.Run();
        return 0;
    }

    /// <summary>读整数参数,缺失或非法时用默认值。</summary>
    private static int ReadIntArg(string[] args, string name, int fallback)
    {
        var raw = ReadStringArg(args, name, "");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 && value <= 65535
            ? value
            : fallback;
    }

    /// <summary>读字符串参数,支持 <c>--name value</c> 与 <c>--name=value</c> 两种写法。</summary>
    private static string ReadStringArg(string[] args, string name, string fallback)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == name && index + 1 < args.Length)
            {
                return args[index + 1];
            }
            if (args[index].StartsWith(name + "=", StringComparison.Ordinal))
            {
                return args[index][(name.Length + 1)..];
            }
        }
        return fallback;
    }
}

/// <summary>
/// 令牌校验:子服签发「用户编号 + 显示名 + 过期时刻 + HMAC」,这里只验签,不查账号表。
/// 好处是聊天宿主不需要碰子服的账号目录,也就不越权。
/// </summary>
internal sealed class TokenChatAuthProvider : IChatAuthProvider
{
    /// <summary>与子服共享的密钥。</summary>
    private readonly string _secret;   // 只用来验签,不落盘不打日志

    /// <summary>用共享密钥构造校验器。</summary>
    internal TokenChatAuthProvider(string secret) => _secret = secret;

    /// <summary>从请求里取令牌并验签;任何一步不成立都当匿名,由 BareChat 回 401。</summary>
    public Task<ChatUserContext> ResolveUserAsync(HttpContext context)
    {
        var token = context.Request.Query["access_token"].ToString();
        if (token.Length == 0)
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = header[7..].Trim();
            }
        }
        return Task.FromResult(Verify(token));
    }

    /// <summary>验签并解析令牌,失败返回匿名。</summary>
    private ChatUserContext Verify(string token)
    {
        if (!ChatToken.TryVerify(_secret, token, out var userId, out var displayName))
        {
            return ChatUserContext.Anonymous;
        }
        return new ChatUserContext
        {
            UserId = userId,
            DisplayName = displayName,
            IsAuthenticated = true
        };
    }
}


