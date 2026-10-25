using System;  // 基础类型
using System.Collections.Generic;  // 缓存
using System.Net;  // IPAddress
using System.Net.Http;  // HTTP 请求
using System.Text.Json;  // 解析回包
using System.Threading;  // 超时令牌
using System.Threading.Tasks;  // 异步
using ZincXManagerShared.Logging;  // 共享库

namespace ZincXManagerServer.Login;

/// <summary>
/// 玩家 IP 归属地查询:服务端查好再随登录请求带给客户端。
/// <para>内网地址直接返回"内网";查失败返回"未知"。结果缓存,同一个 IP 只查一次。</para>
/// <para>查询有**硬上限 1.2 秒**:它在发 621 弹窗之前跑,查太久玩家就得干等。</para>
/// </summary>
public static class GeoLookup
{
    // 查询接口(免费,无需 key)
    private const string Endpoint = "http://ip-api.com/json/{0}?lang=zh-CN&fields=status,country,regionName,city,isp";

    // 同一个 IP 只查一次
    private static readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    // 保护缓存
    private static readonly object _lock = new();

    // 复用一个 HttpClient(端到端超时另由 LookupAsync 的令牌控制)
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// 查归属地,形如 <c>中国 广东 深圳 电信</c>;内网返回"内网",失败返回"未知"。
    /// 超过 <paramref name="timeoutMs"/> 毫秒直接当"未知",不拖住登录弹窗。
    /// </summary>
    public static async Task<string> LookupAsync(string ip, int timeoutMs = 1200)
    {
        var address = Normalize(ip);
        if (address.Length == 0 || !IPAddress.TryParse(address, out var parsed))
        {
            return "未知";
        }

        // 内网 / 回环不查
        if (IPAddress.IsLoopback(parsed) || IsPrivate(parsed))
        {
            return "内网";
        }

        lock (_lock)
        {
            if (_cache.TryGetValue(address, out var cached))
            {
                return cached;
            }
        }

        var text = "未知";
        try
        {
            using var cts = new CancellationTokenSource(Math.Max(300, timeoutMs));
            var json = await Http.GetStringAsync(string.Format(Endpoint, address), cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status) && status.GetString() == "success")
            {
                var parts = new List<string>();
                foreach (var key in new[] { "country", "regionName", "city", "isp" })
                {
                    if (root.TryGetProperty(key, out var value))
                    {
                        var part = value.GetString();
                        if (!string.IsNullOrWhiteSpace(part))
                        {
                            parts.Add(part);
                        }
                    }
                }

                text = parts.Count > 0 ? string.Join(" ", parts) : "未知";
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log(LogLevel.Warning, $"[登录] 查 {address} 归属地超时({timeoutMs}ms),先按未知显示");
            return text;   // 超时不进缓存:下次还能再查一次
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[登录] 查 {address} 归属地失败: {ex.Message}");
        }

        lock (_lock)
        {
            _cache[address] = text;
        }

        return text;
    }

    /// <summary>
    /// 把 ZXMM 报上来的地址压成纯 IP。
    /// <para>老版本模组报的是 <c>/1.2.3.4:5678</c>(SocketAddress 的 toString),
    /// 直接丢给 IPAddress.TryParse 只会得到"未知",所以这里先剥掉斜杠和端口。</para>
    /// </summary>
    public static string Normalize(string? ip)
    {
        var text = (ip ?? "").Trim();
        if (text.Length == 0)
        {
            return "";
        }

        // /1.2.3.4:5678 → 1.2.3.4:5678
        text = text.TrimStart('/');

        // IPv6 带端口的写法是 [::1]:5678
        if (text.StartsWith('['))
        {
            var end = text.IndexOf(']');
            return end > 1 ? text[1..end] : text;
        }

        // 只有一个冒号、而且冒号后面全是数字 → 那是端口,不是 IPv6
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out _))
        {
            text = text[..colon];
        }

        return text.Trim();
    }

    /// <summary>是不是内网地址(10./172.16-31./192.168./169.254.)。</summary>
    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        return bytes[0] == 10
               || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
               || (bytes[0] == 192 && bytes[1] == 168)
               || (bytes[0] == 169 && bytes[1] == 254);
    }
}
