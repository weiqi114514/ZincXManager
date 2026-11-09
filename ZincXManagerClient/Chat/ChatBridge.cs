using System;   // Action:int
using ZincXManagerShared.Chat;   // ChatHello:从 510 里认出自己是哪个本服编号
using ZincXManagerShared.Logging;   // Logger / LogLevel

namespace ZincXManagerClient.Chat;

/// <summary>
/// 聊天后端的「门牌」:地址、令牌、我是谁。登录成功后由服务端的 510 填进来。
///
/// <para>填进来之前 <see cref="Ready"/> 是 false,聊天窗口会先挂起,等 <see cref="Configured"/> 再开工——
/// 这样「登录前就点开聊天窗」也不会白屏。</para>
/// </summary>
internal static class ChatBridge
{
    /// <summary>令牌本体(交给聊天后端做鉴权),没拿到时是空串。</summary>
    internal static string Token { get; private set; } = "";

    /// <summary>聊天后端根地址,如 <c>http://127.0.0.1:21300/chat</c>。</summary>
    internal static string BaseUrl { get; private set; } = "";

    /// <summary>本服完整ID(<c>ZXMS-登录名-编号</c>);用来判断对方是不是本服的人。</summary>
    internal static string ServerId { get; private set; } = "";

    /// <summary>自己的账号编号。</summary>
    internal static int SelfCode { get; private set; }

    /// <summary>地址与令牌都齐了才算能用。</summary>
    internal static bool Ready => Token.Length > 0 && BaseUrl.Length > 0 && SelfCode > 0;

    /// <summary>拿到 510 时触发;已经拿到过就不再触发。</summary>
    internal static event Action? Configured;

    /// <summary>
    /// 处理 510:负载 <c>端口\ttoken\t本服完整ID\t自己的账号编号</c>(第四段是 b0.3.2a 新加的,老服务端没有)。
    /// </summary>
    /// <param name="payload">服务端发来的原文。</param>
    internal static void Configure(string payload)
    {
        var parts = payload.Split('\t');
        if (parts.Length < 2 || !int.TryParse(parts[0].Trim(), out var port) || port <= 0)
        {
            // 这份负载里可能有令牌,不能整份打出来
            Logger.Log(LogLevel.Warning, $"[聊天] 510 负载看不懂({parts.Length} 段),忽略");
            return;
        }

        var host = ClientSession.Connect?.Host ?? "127.0.0.1";
        var token = parts[1].Trim();
        var serverId = parts.Length > 2 ? parts[2].Trim() : "";
        // 取值顺序在 Shared 里定,测试那边直接钉的就是同一条规则
        var selfCode = ChatHello.ResolveSelfCode(parts, token, ClientSession.UserId);
        if (selfCode <= 0)
        {
            Logger.Log(LogLevel.Warning, "[聊天] 还没有自己的账号编号,510 先不接");
            return;
        }

        var baseUrl = $"http://{host}:{port}/chat";

        // 登录路径与主窗分派都会收到 510,同一份就不要再触发一次
        if (token == Token && baseUrl == BaseUrl && selfCode == SelfCode)
        {
            return;
        }

        Token = token;
        ServerId = serverId;
        SelfCode = selfCode;
        BaseUrl = baseUrl;
        Logger.Log(LogLevel.Info, $"[聊天] 聊天后端已就绪:{BaseUrl}(我是 {SelfCode},本服 {ServerId})");
        Configured?.Invoke();
    }

    /// <summary>退出登录时清干净,避免换了账号还用上一个人的令牌。</summary>
    internal static void Reset()
    {
        Token = "";
        BaseUrl = "";
        ServerId = "";
        SelfCode = 0;
        Configured = null;
    }

    /// <summary>
    /// 聊天后端回 401/403:令牌过期或宿主换了密钥。把本端状态清掉,
    /// 界面会退回「等 510」,下次登录/重连拿到新令牌再开工 —— 不这么做界面只会一直空着。
    /// </summary>
    internal static void MarkExpired()
    {
        if (!Ready)
        {
            return;
        }

        Logger.Log(LogLevel.Warning, "[聊天] 聊天令牌已失效(401/403),等下次登录重新下发");
        Reset();
    }
}
