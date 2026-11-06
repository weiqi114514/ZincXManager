using System;                        // Uri / TimeSpan / int
using System.Collections.Generic;    // List
using System.Net.Http;               // HttpClient / StringContent
using System.Net.Http.Headers;       // AuthenticationHeaderValue
using System.Text;                   // Encoding
using System.Threading.Tasks;        // Task
using ZincXManagerShared.Account;    // Account
using ZincXManagerShared.Chat;       // ChatChannel:私聊频道号
using ZincXManagerShared.FileIO;     // ZFile:config.ini
using ZincXManagerShared.Friend;     // FriendStore / PlayerId
using ZincXManagerShared.Logging;    // Logger / LogLevel
using ZincXManagerServer.Chat;       // ChatHostRunner

namespace ZincXManagerServer;

/// <summary>
/// 让聊天后端认识「谁和谁是好友」:好友关系定了之后,把两个人的私聊频道建出来;
/// 好友关系解除时,再把频道撤掉。
///
/// <para>为什么在子服做:频道的成员关系只有本服知道(好友表在本服),客户端没法自己建频道,
/// 聊天后端也没有"好友"这个概念。</para>
/// </summary>
internal static partial class Program
{
    /// <summary>聊天后端挂载前缀(与 Chat 宿主的 RoutePrefix 一致)。</summary>
    private const string ChatRoutePrefix = "/chat";

    /// <summary>建频道 / 加成员的请求超时:本地进程,8 秒足够,卡住也不能拖着登录。</summary>
    private static readonly TimeSpan ChatCallTimeout = TimeSpan.FromSeconds(8);

    /// <summary>全服共用的连接池:每次登录都要对每个好友发几次请求,每次 new 会把本地端口耗光。</summary>
    private static readonly HttpClient ChatHttp = new() { Timeout = ChatCallTimeout };

    /// <summary>
    /// 玩家登录后,把他和本服好友之间的私聊频道都建一遍(幂等)。
    /// 只处理本服好友:跨服会话走 ZXMOS 中转,频道由各自的子服建。
    /// </summary>
    /// <param name="account">刚登录的账号。</param>
    /// <param name="config">本服配置(要拿本服的完整服ID)。</param>
    private static async Task EnsureChatChannelsAsync(Account account, ZFile config)
    {
        var host = _chatHost;
        if (host is null || !host.IsRunning)
        {
            return;
        }

        var self = LocalPlayerId(account, config);
        if (self.IsEmpty)
        {
            // 服ID 还没从官方端拿到:这时候算不出对方是不是本服,先不建,等下次登录再来
            return;
        }

        var friends = FriendStore.ReadFriends(self, out var readError);
        if (readError.Length > 0)
        {
            Logger.Log(LogLevel.Warning, $"[聊天] 读好友表失败,跳过频道准备:{readError}");
            return;
        }

        var baseUrl = $"http://127.0.0.1:{host.Port}";
        var created = 0;   //这次真建出来的频道数
        foreach (var friend in friends)
        {
            if (!PlayerId.TryParse(friend.Id, out var peer) || peer.IsEmpty)
            {
                continue;
            }

            if (!SameServer(peer.ServerId, self.ServerId))
            {
                continue;
            }

            if (await EnsureChatChannelAsync(baseUrl, host, account, self, peer, friend.Name))
            {
                created++;
            }
        }

        if (created > 0)
        {
            Logger.Log(LogLevel.Info, $"[聊天] {account.Name} 的 {created} 个本服好友会话已在聊天后端就绪");
        }
    }

    /// <summary>
    /// 刚成为好友的两个人补建私聊频道(幂等);跨服或聊天没开就直接返回。
    /// </summary>
    /// <param name="self">本端账号。</param>
    /// <param name="other">对方账号。</param>
    /// <param name="config">本服配置(要拿本服的完整服ID)。</param>
    private static async Task EnsureChatChannelForPairAsync(Account self, Account other, ZFile config)
    {
        var host = _chatHost;
        if (host is null || !host.IsRunning)
        {
            return;
        }

        var selfId = LocalPlayerId(self, config);
        var otherId = LocalPlayerId(other, config);
        if (selfId.IsEmpty || otherId.IsEmpty || !SameServer(selfId.ServerId, otherId.ServerId))
        {
            return;
        }

        var baseUrl = $"http://127.0.0.1:{host.Port}";
        await EnsureChatChannelAsync(baseUrl, host, self, selfId, otherId, other.Name);
    }

    /// <summary>
    /// 好友关系解除后撤销两人的私聊频道,让还开着的窗口发不出也读不到。
    ///
    /// <para>聊天后端没有「删成员」的端点,只能删整个频道;删频道又只有创建者能做,
    /// 而创建者是「先登录、先建频道」的那一侧,所以两边各试一次。频道不存在(404)也算撤销成功。</para>
    /// </summary>
    /// <param name="self">发起删除的一方(本服)。</param>
    /// <param name="peer">被删掉的好友(本服)。</param>
    public static async Task RevokeChatChannelForPairAsync(PlayerId self, PlayerId peer)
    {
        try
        {
            var host = _chatHost;
            if (host is null || !host.IsRunning || self.IsEmpty || peer.IsEmpty)
            {
                return;
            }

            var channel = ChatChannel.ForPair(self.Code, peer.Code);
            var baseUrl = $"http://127.0.0.1:{host.Port}";

            // 谁先登录谁建的频道,所以两侧的令牌都要试;账号在本服账号表里就能签,不看在不在线
            var accounts = new[] { Accounts.FindById(self.Code), Accounts.FindById(peer.Code) };
            var codes = new List<int>(accounts.Length);          //每次真正发出请求的状态码
            var bodies = new List<string>(accounts.Length);      //失败时的响应体,排错用
            for (var index = 0; index < accounts.Length; index++)
            {
                var account = accounts[index];
                if (account is null)
                {
                    continue;
                }

                var token = host.SignFor(account.Id.ToString(), account.Name, 3600);
                if (token.Length == 0)
                {
                    continue;
                }

                var (code, body) = await DeleteChatChannelAsync(token, baseUrl, channel);
                if (code is >= 200 and < 300)
                {
                    Logger.Log(LogLevel.Info, $"[聊天] 撤销频道 {channel}:成功(创建者={account.Id})");
                    return;
                }

                codes.Add(code);
                bodies.Add(body);
                if (code == 404)
                {
                    // 404 既可能是频道已经不在,也可能是「私聊对非成员隐藏」;留给另一侧确认
                    continue;
                }
            }

            if (codes.Count == 0)
            {
                Logger.Log(LogLevel.Warning, $"[聊天] 撤销频道 {channel} 失败:两侧都拿不到账号或令牌");
                return;
            }

            var allGone = true;
            foreach (var code in codes)
            {
                if (code != 404)
                {
                    allGone = false;
                }
            }

            if (allGone)
            {
                // 两侧都 404:频道确实不在(删过了)。但 404 也可能是「私聊对非成员隐藏」,
                // 所以这里不能报「成功」,只如实说没找到 —— 真留着的话下次删好友还会再试
                Logger.Log(LogLevel.Warning, $"[聊天] 撤销频道 {channel}:两侧都没有这个频道(可能早就删了,也可能是私聊对非成员隐藏,不当作成功)");
                return;
            }

            var detail = "";
            foreach (var body in bodies)
            {
                if (body.Length > 0)
                {
                    detail = body;
                    break;
                }
            }

            Logger.Log(LogLevel.Warning,
                $"[聊天] 撤销频道 {channel} 失败:两侧都没删成({string.Join(",", codes)}){(detail.Length > 0 ? ",响应:" + detail : "")}");
        }
        catch (Exception ex)
        {
            // fire-and-forget:调用方不 await,异常必须在这里收住
            Logger.Log(LogLevel.Warning, $"[聊天] 撤销私聊频道时出错:{ex.Message}");
        }
    }

    /// <summary>用一个身份删一次频道;HTTP 层异常也收在这里,返回 0 表示这次没问成。</summary>
    /// <param name="token">这个身份的聊天令牌。</param>
    /// <param name="baseUrl">聊天后端根地址。</param>
    /// <param name="channel">频道号。</param>
    /// <returns>状态码与响应体(只留前 200 字符)。</returns>
    private static async Task<(int Code, string Body)> DeleteChatChannelAsync(string token, string baseUrl, string channel)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Delete, $"{baseUrl}{ChatRoutePrefix}/api/channels/{channel}", token);
            using var response = await ChatHttp.SendAsync(request);
            var code = (int)response.StatusCode;
            var body = code is >= 200 and < 300 ? "" : await response.Content.ReadAsStringAsync();
            return (code, body.Length > 200 ? body[..200] : body);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[聊天] 删除频道 {channel} 的请求失败:{ex.Message}");
            return (0, ex.Message);
        }
    }

    /// <summary>
    /// 拼一个带令牌的请求。令牌必须跟着请求走,不能设到共享 HttpClient 的 DefaultRequestHeaders 上 ——
    /// 那是全进程共用的一份,两个玩家同时建频道时会互相覆盖,变成拿 A 的令牌读 B 的频道。
    /// </summary>
    private static HttpRequestMessage NewRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        if (token.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    /// <summary>
    /// 建一个私聊频道并把对方拉进来;已经建过就只补成员。
    /// </summary>
    /// <param name="baseUrl">聊天后端根地址,如 <c>http://127.0.0.1:21300</c>。</param>
    /// <param name="host">聊天宿主句柄(用它签令牌)。</param>
    /// <param name="account">本端账号(签令牌用)。</param>
    /// <param name="self">本端完整ID。</param>
    /// <param name="peer">对方完整ID。</param>
    /// <param name="peerName">对方名字(频道显示名用)。</param>
    /// <returns>频道可用返回 true。</returns>
    private static async Task<bool> EnsureChatChannelAsync(
        string baseUrl,
        ChatHostRunner host,
        Account account,
        PlayerId self,
        PlayerId peer,
        string peerName)
    {
        var channel = ChatChannel.ForPair(self.Code, peer.Code);
        var token = host.SignFor(account.Id.ToString(), account.Name, 3600);
        if (token.Length == 0)
        {
            return false;
        }

        // 对方账号可能还没镜像出来;没有就只做「建频道 + 拉对方」这一半
        var peerAccount = Accounts.FindById(peer.Code);
        var peerToken = peerAccount is null ? "" : host.SignFor(peerAccount.Id.ToString(), peerAccount.Name, 3600);

        var channelBody = $"{{\"channelId\":\"{channel}\",\"name\":\"{Escape(account.Name)} 与 {Escape(peerName)}\",\"isPrivate\":true}}";
        using (var content = new StringContent(channelBody, Encoding.UTF8, "application/json"))
        {
            using var request = NewRequest(HttpMethod.Post, $"{baseUrl}{ChatRoutePrefix}/api/channels", token);
            request.Content = content;
            using var response = await ChatHttp.SendAsync(request);
            var code = (int)response.StatusCode;
            // 409 = 频道已存在,这对我们是正常情况。别的 4xx 说明令牌坏了或请求拼错了,
            // 当成功会一路走到「私聊频道就绪」,而实际上一句都发不出去
            if (code is < 200 or >= 300 && code != 409)
            {
                var body = "";
                try
                {
                    body = await response.Content.ReadAsStringAsync();
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Debug, $"[聊天] 读建频道失败的响应体出错: {ex.Message}");
                }

                Logger.Log(LogLevel.Warning,
                    $"[聊天] 建频道 {channel} 失败:HTTP {code}{(body.Length > 0 ? ",响应:" + Shorten(body) : "")}");
                return false;
            }
        }

        await AddMemberAsync(baseUrl, channel, peer.Code.ToString(), peer.Full, token);
        if (peerToken.Length > 0)
        {
            // 对方上次已经建好了频道,只是没把本端拉进来 —— 用对方的令牌补一次
            await AddMemberAsync(baseUrl, channel, self.Code.ToString(), self.Full, peerToken);
        }

        // 聊天后端对「本来就已在频道里」回 500,不能拿状态码当结论;以对方能不能读为准。
        // 对方账号还没镜像到本服时没有令牌可验,这时候不能宣称「就绪」——下次登录会再建一次
        if (peerToken.Length == 0)
        {
            Logger.Log(LogLevel.Warning, $"[聊天] {channel} 建好了,但 {peer.Full} 的账号还没在本服落地,没法验证对方能不能读,这次不算就绪");
            return false;
        }

        if (!await CanReadAsync(peerToken, baseUrl, channel))
        {
            Logger.Log(LogLevel.Warning, $"[聊天] {peer.Full} 还读不到 {channel},这次没算就绪");
            return false;
        }

        Logger.Log(LogLevel.Info, $"[聊天] 私聊频道就绪:{channel}({self.Full} ↔ {peer.Full})");
        return true;
    }

    /// <summary>把一个成员加进频道;已经在了就不管(聊天后端会回 500/409)。</summary>
    /// <param name="baseUrl">聊天后端根地址。</param>
    /// <param name="channel">频道号。</param>
    /// <param name="userId">要加进去的账号编号。</param>
    /// <param name="label">日志里怎么称呼这个人。</param>
    /// <param name="token">用谁的身份去加(必须是频道成员才有权加人)。</param>
    private static async Task AddMemberAsync(string baseUrl, string channel, string userId, string label, string token)
    {
        var memberBody = $"{{\"userId\":\"{userId}\"}}";
        using var content = new StringContent(memberBody, Encoding.UTF8, "application/json");
        using var request = NewRequest(HttpMethod.Post, $"{baseUrl}{ChatRoutePrefix}/api/channels/{channel}/members", token);
        request.Content = content;
        using var response = await ChatHttp.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            // 409 / 500 基本都是「本来就是成员」,真正的结论由 CanReadAsync 判
            Logger.Log(LogLevel.Debug, $"[聊天] 把 {label} 加进 {channel} 回 HTTP {(int)response.StatusCode}");
        }
    }

    /// <summary>用某个人的令牌读一次历史,能读到就算这个人是频道成员。</summary>
    private static async Task<bool> CanReadAsync(string token, string baseUrl, string channel)
    {
        using var request = NewRequest(HttpMethod.Get, $"{baseUrl}{ChatRoutePrefix}/api/channels/{channel}/messages?limit=1", token);
        using var response = await ChatHttp.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    /// <summary>把名字里的控制字符与引号转义掉,别把 JSON 拼坏(账号名是用户起的,可能是任何字符)。</summary>
    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var one in text)
        {
            switch (one)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (one < ' ')
                    {
                        builder.Append("\\u").Append(((int)one).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(one);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>响应体只留前 120 字进日志,别把整页 HTML 塞进去。</summary>
    private static string Shorten(string body)
    {
        var text = body.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return text.Length <= 120 ? text : text[..120] + "…";
    }
}
