using System;                        // DateTime / TimeSpan
using System.Collections.Generic;    // List
using System.Net.Http;               // HttpClient / HttpRequestMessage
using System.Net.Http.Headers;       // AuthenticationHeaderValue
using System.Text;                   // Encoding
using System.Text.Json;              // JsonDocument:聊天后端回的是 JSON
using System.Threading.Tasks;        // Task
using ZincXManagerShared.Logging;    // Logger / LogLevel

namespace ZincXManagerClient.Chat;

/// <summary>聊天后端返回的一条消息,只留界面要用的字段。</summary>
/// <param name="Id">消息编号(去重用)。</param>
/// <param name="SenderId">发送者账号编号。</param>
/// <param name="Text">正文。</param>
/// <param name="At">发送时间(本地时区)。</param>
internal sealed record ChatLine(string Id, string SenderId, string Text, DateTime At);

/// <summary>
/// 直接找聊天后端要数据:历史、发送、标记已读。走 HTTP,不用长连接——
/// 客户端是 NativeAOT 交付,SignalR 那条路编不进去,轮询已经够用。
/// </summary>
internal static class ChatApi
{
    /// <summary>请求超时:本地局域网,10 秒收不到就当断线。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>全客户端共用的连接池:聊天窗 1.5 秒轮询一次,每次新建 HttpClient 会把本地端口耗光。</summary>
    private static readonly HttpClient Shared = new() { Timeout = Timeout };

    /// <summary>拉某个频道最近的消息(时间正序)。失败返回空表,不抛。</summary>
    /// <param name="channelId">频道号。</param>
    /// <param name="limit">最多几条。</param>
    internal static async Task<List<ChatLine>> FetchAsync(string channelId, int limit)
    {
        var lines = new List<ChatLine>();
        if (!ChatBridge.Ready)
        {
            return lines;
        }

        try
        {
            var http = Shared;
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{ChatBridge.BaseUrl}/api/channels/{channelId}/messages?limit={limit}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ChatBridge.Token);

            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                NoteStatus("拉历史", (int)response.StatusCode);
                return lines;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return lines;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                var line = ReadLine(element);
                if (line is not null)
                {
                    lines.Add(line);
                }
            }

            // 后端按什么顺序给不保证,统一按时间正序(气泡从上到下)
            lines.Sort((left, right) => left.At.CompareTo(right.At));
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[聊天] 拉历史出错:{ex.Message}");
        }

        return lines;
    }

    /// <summary>发一条文本消息;成功返回消息编号,失败返回空串。</summary>
    /// <param name="channelId">频道号。</param>
    /// <param name="text">正文。</param>
    internal static async Task<string> SendAsync(string channelId, string text)
    {
        if (!ChatBridge.Ready)
        {
            return "";
        }

        try
        {
            var http = Shared;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ChatBridge.BaseUrl}/messages")
            {
                Content = new StringContent(
                    $"{{\"channelId\":\"{channelId}\",\"payload\":\"{Escape(text)}\",\"contentType\":\"Text\"}}",
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ChatBridge.Token);

            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Log(LogLevel.Warning, $"[聊天] 发消息失败:{channelId} HTTP {(int)response.StatusCode}");
                return "";
            }

            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("messageId", out var id) ? id.GetString() ?? "" : "";
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[聊天] 发消息出错:{ex.Message}");
            return "";
        }
    }

    /// <summary>告诉后端「这个频道我读完了」;失败只记日志(未读数会不准,但不影响收发)。</summary>
    /// <param name="channelId">频道号。</param>
    internal static async Task MarkReadAsync(string channelId)
    {
        if (!ChatBridge.Ready)
        {
            return;
        }

        try
        {
            var http = Shared;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ChatBridge.BaseUrl}/api/channels/{channelId}/read");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ChatBridge.Token);
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Log(LogLevel.Warning, $"[聊天] 标记已读失败:{channelId} HTTP {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[聊天] 标记已读出错:{ex.Message}");
        }
    }

    /// <summary>自己的每个频道的未读数;失败返回空表。</summary>
    internal static async Task<Dictionary<string, int>> UnreadAsync()
    {
        var map = new Dictionary<string, int>();
        if (!ChatBridge.Ready)
        {
            return map;
        }

        try
        {
            var http = Shared;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ChatBridge.BaseUrl}/api/channels/mine?limit=100");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ChatBridge.Token);
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                NoteStatus("拉未读", (int)response.StatusCode);
                return map;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return map;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } channel)
                {
                    continue;
                }

                var unread = element.TryGetProperty("unreadCount", out var count) && count.TryGetInt32(out var value) ? value : 0;
                map[channel] = unread;
            }
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[聊天] 拉未读出错:{ex.Message}");
        }

        return map;
    }

    /// <summary>某个频道最新的一条;没聊过返回 null。</summary>
    /// <param name="channelId">频道号。</param>
    internal static async Task<ChatLine?> LastAsync(string channelId)
    {
        var lines = await FetchAsync(channelId, 1);
        return lines.Count > 0 ? lines[^1] : null;
    }

    /// <summary>把 JSON 里的一条解析成 <see cref="ChatLine"/>;字段不全就当没有。</summary>
    private static ChatLine? ReadLine(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = element.TryGetProperty("messageId", out var idText) ? idText.GetString() ?? "" : "";
        var sender = element.TryGetProperty("senderId", out var senderText) ? senderText.GetString() ?? "" : "";
        var payload = element.TryGetProperty("payload", out var payloadText) ? payloadText.GetString() ?? "" : "";
        if (id.Length == 0 || payload.Length == 0)
        {
            return null;
        }

        var at = DateTime.Now;
        if (element.TryGetProperty("createdAtUtc", out var time) && time.TryGetDateTime(out var utc))
        {
            at = utc.ToLocalTime();
        }

        return new ChatLine(id, sender, payload, at);
    }

    /// <summary>把正文转成 JSON 字符串内容(不带外层引号)。</summary>
    /// <param name="text">原文。</param>
    private static string Escape(string text)
    {
        // 客户端是 NativeAOT,JsonSerializer 的反射序列化被裁掉了,自己转义
        var builder = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
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
                    if (ch < ' ')
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>记一次失败;401/403 说明令牌不能用了,顺手让桥接退回「等 510」,下次登录重新拿。</summary>
    /// <param name="what">在做的事,进日志用。</param>
    /// <param name="code">HTTP 状态码。</param>
    private static void NoteStatus(string what, int code)
    {
        if (code is 401 or 403)
        {
            ChatBridge.MarkExpired();
            return;
        }

        Logger.Log(LogLevel.Warning, $"[聊天] {what}失败:HTTP {code}");
    }
}
