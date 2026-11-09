using System;   // int.TryParse

namespace ZincXManagerShared.Chat;

/// <summary>
/// 510「聊天后端就绪」负载里「我是谁」的判定规则。
///
/// <para>抽成纯函数是为了能直接测:客户端那边(界面)本来就在别的程序集里,
/// 而这条规则一旦判错,算出的是服务端没建过的频道号,每条请求都 404 —— 现场极难看出。</para>
/// </summary>
public static class ChatHello
{
    /// <summary>负载里「自己的本服账号编号」是第几段(从 0 数,即第四段);老服务端没有这一段。</summary>
    public const int SelfCodeIndex = 3;

    /// <summary>
    /// 定「我是谁」:优先 510 第四段,其次令牌里签进去的编号,最后才退回会话里的编号。
    /// <para>为什么不能直接用会话编号:中转登录时它是 601 第三段的官方编号,
    /// 跟本服编号不是一回事 —— 拿它算私聊频道号会算出服务端没建的那个频道,永远 404。</para>
    /// </summary>
    /// <param name="parts">510 的负载分段(按 \t 切)。</param>
    /// <param name="token">510 的令牌(第二段)。</param>
    /// <param name="sessionUserId">会话里记着的账号编号,可能是空串或 null。</param>
    /// <returns>本服账号编号;一个都取不到时返回 0。</returns>
    public static int ResolveSelfCode(string[] parts, string token, string? sessionUserId)
    {
        if (parts.Length > SelfCodeIndex
            && int.TryParse(parts[SelfCodeIndex].Trim(), out var fromPayload)
            && fromPayload > 0)
        {
            return fromPayload;
        }

        if (ChatToken.TryReadUserId(token, out var signed) && int.TryParse(signed, out var fromToken) && fromToken > 0)
        {
            return fromToken;
        }

        return int.TryParse(sessionUserId, out var fromSession) && fromSession > 0 ? fromSession : 0;
    }
}
