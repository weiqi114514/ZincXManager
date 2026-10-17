using System;
using System.Collections.Generic;
using ZincXManagerShared.Logging;

namespace ZincXManagerServer.Command;

/// <summary>
/// config 配置模式:输入 <c>config</c> 进入,**只能改现有项,不能增删**。
///
/// <list type="bullet">
///   <item>进入时打印配置表:键名 / 中文名 / 作用 / 类型 / 当前值;</item>
///   <item>设置写 <c>&lt;键名&gt; &lt;键值&gt;</c> 或 <c>&lt;中文名&gt; &lt;键值&gt;</c>;</item>
///   <item><c>exitc</c> 退出,回到正常命令模式;</item>
///   <item>在这里输入的东西不会被当成控制台命令,也不会被当成"账号名/密码"。</item>
/// </list>
/// </summary>
internal static class ConfigMode
{
    /// <summary>一个配置项(只描述;读写都走 Program)。</summary>
    internal sealed class Item
    {
        public string Key { get; init; } = "";
        public string Cn { get; init; } = "";
        public string Description { get; init; } = "";
        public string Type { get; init; } = "";
        public bool ReadOnly { get; init; }
    }

    internal static readonly Item[] Items =
    [
        new() { Key = "name",          Cn = "服务端名称",   Description = "玩家看到的名称(linkname=1 时跟账号名)", Type = "文本 1-32 字符" },
        new() { Key = "linkname",      Cn = "名称联动",     Description = "1 = 名称与官方账号名一致,0 = 分开设置",   Type = "1/0" },
        new() { Key = "connectOS",     Cn = "连接官方端",   Description = "启动时是否连 ZXMOS",                     Type = "1/0" },
        new() { Key = "czport",        Cn = "客户端端口",   Description = "ZXMC/ZXMM 接入端口(重启生效)",           Type = "1-65535" },
        new() { Key = "ozip",          Cn = "官方端地址",   Description = "ZXMOS 的 IP 或域名",                     Type = "IP 或域名" },
        new() { Key = "ozport",        Cn = "官方端端口",   Description = "ZXMOS 的端口",                           Type = "1-65535" },
        new() { Key = "osuser",        Cn = "官方账号名",   Description = "本端在 ZXMOS 上注册的账号(改名请用 osuser)", Type = "文本 2-32 字符" },
        new() { Key = "msgdays",       Cn = "消息保留天数", Description = "下发给客户端:本地留多久",                Type = "1-365" },
        new() { Key = "srvmsgdays",    Cn = "存档保留天数", Description = "本服存档留多久(补发用)",                 Type = "1-365" },
        new() { Key = "officialcheck", Cn = "对端校验",     Description = "1 = 对端不是官方就断开,0 = 只警告",       Type = "1/0" },
        new() { Key = "ozname",        Cn = "官方端名称",   Description = "ZXMOS 下发的名称(只读)",                 Type = "只读",       ReadOnly = true },
        new() { Key = "servercode",    Cn = "服务端码",     Description = "本实例的身份证(只读)",                   Type = "只读",       ReadOnly = true },
    ];

    private static bool _active;

    /// <summary>当前是否在配置模式里。</summary>
    internal static bool Active => _active;

    /// <summary>进入配置模式并打印配置表。</summary>
    internal static void Enter()
    {
        _active = true;
        Logger.Log(LogLevel.Info, Environment.NewLine + BuildTable() + Environment.NewLine +
            "已进入配置模式:输入 <键名|中文名> <键值> 修改,输入 exitc 退出");
    }

    /// <summary>退出配置模式。</summary>
    internal static void Exit()
    {
        _active = false;
        Logger.Log(LogLevel.Info, "已退出配置模式,回到正常命令模式");
    }

    /// <summary>
    /// 配置模式里的每一行都从这里走;返回 true = 已处理(不再当命令)。
    /// </summary>
    internal static bool Handle(string line)
    {
        if (!_active)
        {
            return false;
        }

        var text = line.Trim();

        if (text.Equals("exitc", StringComparison.OrdinalIgnoreCase))
        {
            Exit();
            return true;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            Logger.Log(LogLevel.Warning, "配置模式用法:<键名|中文名> <键值>;输入 exitc 退出");
            return true;
        }

        var name = parts[0];
        var value = string.Join(' ', parts[1..]);

        var item = Array.Find(Items, i =>
            i.Key.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            i.Cn.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            Logger.Log(LogLevel.Warning,
                $"没有这个配置项:{name}(只能改表里已有的,不能新增;输入 exitc 退出后可看配置表)");
            return true;
        }

        if (item.ReadOnly)
        {
            Logger.Log(LogLevel.Warning, $"{item.Key}({item.Cn})是只读项,不能改");
            return true;
        }

        var error = Program.SetConfigItem(item.Key, value);

        if (error.Length > 0)
        {
            Logger.Log(LogLevel.Warning, $"{item.Key}({item.Cn})设置失败:{error}");
            return true;
        }

        Logger.Log(LogLevel.Info, $"{item.Key}({item.Cn})已设置为 {Program.ConfigValue(item.Key)}");
        return true;
    }

    private static string BuildTable()
    {
        var lines = new List<string> { "配置项(只能改这些,不能增删):" };
        lines.Add($"  {"键名",-14}{"中文名",-14}{"类型",-16}{"作用",-34}当前值");
        lines.Add("  " + new string('-', 100));

        foreach (var item in Items)
        {
            var value = Program.ConfigValue(item.Key);
            lines.Add($"  {item.Key,-14}{item.Cn,-14}{item.Type,-16}{item.Description,-34}{value}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
