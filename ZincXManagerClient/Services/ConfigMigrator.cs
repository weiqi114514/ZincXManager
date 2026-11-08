using System;  // StringComparison
using System.Linq;  // Any
using ZincXManagerClient.Models;  // MirrorItem
using ZincXManagerShared.Logging;  // Logger / LogLevel

namespace ZincXManagerClient.Services;

/// <summary>
/// 客户端 config.ini 的版本迁移:老配置缺了新版本才有的默认项时补上,
/// <b>用户已经填过的值一个字都不动</b>(顺序、启停状态、已有的测速参数全部保留)。
///
/// <para>版本号存在 config.ini 的 <c>configver</c> 里,缺键视为版本 1。
/// 迁移只做一次:版本已经是当前值就直接返回,不写文件、不打日志。</para>
/// </summary>
internal static class ConfigMigrator
{
    /// <summary>配置键:配置文件结构版本。</summary>
    private const string KeyConfigVer = "configver";

    /// <summary>当前配置版本:2 起镜像清单含 sixyin、<c>update.speed.*</c> 五个键齐全。</summary>
    private const int ConfigVerNow = 2;

    /// <summary>要补进镜像清单的那一条:地址。</summary>
    private const string SixyinUrl = "https://gh.sixyin.com/";

    /// <summary>要补进镜像清单的那一条:名字。</summary>
    private const string SixyinName = "sixyin";

    /// <summary>
    /// 按版本号把老配置补齐。必须在界面起来之前调用(界面各处读配置时就不用再各自兜底)。
    /// </summary>
    public static void Migrate()
    {
        var raw = ClientConfig.GetOrEmpty(KeyConfigVer).Trim();   // 当前版本号;空 = 老配置
        var from = raw.Length == 0 ? "1" : raw;

        if (int.TryParse(raw, out var version) && version >= ConfigVerNow)
        {
            return;
        }

        var note = EnsureSixyinMirror();
        MirrorSpeedConfig.EnsureDefaults();
        ClientConfig.Set(KeyConfigVer, ConfigVerNow.ToString());

        Logger.Log(LogLevel.Info, $"[CONFIG]配置已从版本 {from} 迁移到 {ConfigVerNow}:{note}");
    }

    /// <summary>
    /// 把 sixyin 追加进用户已有的镜像清单,返回这次对清单做了什么(写日志用)。
    /// </summary>
    private static string EnsureSixyinMirror()
    {
        var raw = ClientConfig.GetOrEmpty(MirrorSettingsStore.ConfigKey);   // 用户清单原文
        if (raw.Length == 0)
        {
            // 没有清单 = 走内置默认清单,里面本来就带 sixyin,不必落盘
            return "配置里没有镜像清单,继续用内置默认清单(含 sixyin)";
        }

        var items = MirrorSettingsStore.Parse(raw);
        if (items.Count == 0)
        {
            return "配置里的镜像清单解析为空,保持原样";
        }

        // 已经手动加过(地址或名字对得上)就不重复加,用户写的地址也保持原样
        var exists = items.Any(i =>
            string.Equals(i.Url.Trim().TrimEnd('/'), SixyinUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Name.Trim(), SixyinName, StringComparison.OrdinalIgnoreCase));
        if (exists)
        {
            return "镜像清单已有 sixyin,保持原样";
        }

        items.Add(new MirrorItem { Url = SixyinUrl, Name = SixyinName, Enabled = true });
        MirrorSettingsStore.Save(items);
        return $"镜像清单补入 {SixyinName}({SixyinUrl}),原有条目顺序与启停不变";
    }
}
