using System;  // 基础类型
using System.Collections.Generic;  // 缓存
using System.Net;  // IPAddress
using System.Net.Http;  // HTTP 请求
using System.Text.Json;  // 解析回包
using System.Threading.Tasks;  // 异步
using ZincXManagerShared.Logging;  // 共享库

namespace ZincXManagerServer.Login;

/// <summary>
/// 玩家 IP 归属地查询:服务端查好再随登录请求带给客户端。
/// <para>内网地址直接返回"内网";查失败返回"未知"。结果缓存,同一个 IP 只查一次。</para>
/// </summary>
public static class GeoLookup
{
    // 查询接口(免费,无需 key)
    private const string Endpoint = "http://ip-api.com/json/{0}?lang=zh-CN&fields=status,country,regionName,city,isp";

    // 同一个 IP 只查一次
    private static readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    // 保护缓存
    private static readonly object _lock = new();

    // 复用一个 HttpClient
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>查归属地,形如 <c>中国 广东 深圳 电信</c>;内网返回"内网",失败返回"未知"。</summary>
    public static string Lookup(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return "未知";
        }

        if (!IPAddress.TryParse(ip, out var address))
        {
            return "未知";
        }

        // 内网 / 回环不查
        if (IPAddress.IsLoopback(address) || IsPrivate(address))
        {
            return "内网";
        }

        lock (_lock)
        {
            if (_cache.TryGetValue(ip, out var cached))
            {
                return cached;
            }
        }

        var text = "未知";
        try
        {
            var json = Http.GetStringAsync(string.Format(Endpoint, ip)).GetAwaiter().GetResult();
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
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[登录] 查 {ip} 归属地失败: {ex.Message}");
        }

        lock (_lock)
        {
            _cache[ip] = text;
        }

        return text;
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
