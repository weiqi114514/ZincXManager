using System;
using System.Collections.Generic;
using System.Linq;
using ZincXManagerClient.Models;
using ZincXManagerShared.Logging;

namespace ZincXManagerClient.Services;

/// <summary>镜像清单的读写:存成 url|名称|启用;url|名称|启用</summary>
public static class MirrorSettingsStore
{
    /// <summary>配置键</summary>
    private const string Key = "update.mirrors";

    /// <summary>默认清单(镜像只放一个常用的,其余留空给用户加)</summary>
    public static List<MirrorItem> Default => new()
    {
        new MirrorItem { Url = "https://gh-proxy.com/", Name = "gh-proxy", Enabled = true },
        new MirrorItem { Url = "https://ghfast.top/", Name = "ghfast", Enabled = false },
        new MirrorItem { Url = "https://ghproxy.net/", Name = "ghproxy", Enabled = false },
    };

    /// <summary>读清单;旧版 update.mirror(分号分隔)会自动迁移过来</summary>
    public static List<MirrorItem> Load()
    {
        var raw = ClientConfig.GetOrEmpty(Key);
        if (raw.Length > 0)
        {
            var list = Parse(raw);
            if (list.Count > 0)
            {
                return list;
            }
        }

        // 旧配置迁移:分号/逗号分隔的纯地址
        var legacy = ClientConfig.GetOrEmpty("update.mirror");
        if (legacy.Length > 0)
        {
            var items = legacy
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(url => new MirrorItem { Url = url, Name = "", Enabled = true })
                .ToList();

            if (items.Count > 0)
            {
                return items;
            }
        }

        return Default;
    }

    /// <summary>写清单</summary>
    public static void Save(IEnumerable<MirrorItem> items)
    {
        ClientConfig.Set(Key, Serialize(items));
    }

    /// <summary>序列化成 url|名称|启用;…</summary>
    public static string Serialize(IEnumerable<MirrorItem> items)
    {
        var parts = items
            .Where(i => i.Url.Trim().Length > 0)
            .Select(i => $"{i.Url.Trim()}|{i.Name.Trim()}|{(i.Enabled ? 1 : 0)}");
        return string.Join(";", parts);
    }

    /// <summary>解析</summary>
    public static List<MirrorItem> Parse(string raw)
    {
        var list = new List<MirrorItem>();
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = part.Split('|');
            if (fields.Length == 0 || fields[0].Trim().Length == 0)
            {
                continue;
            }

            list.Add(new MirrorItem
            {
                Url = fields[0].Trim(),
                Name = fields.Length > 1 ? fields[1].Trim() : "",
                Enabled = fields.Length <= 2 || fields[2].Trim() == "1",
            });
        }

        return list;
    }

    /// <summary>当前生效的镜像地址(按顺序,只算启用的)</summary>
    public static string[] EnabledUrls() =>
        Load()
            .Where(i => i.Enabled && i.Url.Trim().Length > 0)
            .Select(i => i.Url.Trim())
            .ToArray();
}
