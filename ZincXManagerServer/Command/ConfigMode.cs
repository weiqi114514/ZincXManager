using System;  // 基础类型 / 常用静态方法
using System.Collections.Generic;  // 泛型集合
using ZincXManagerShared.Logging;  // 共享库

namespace ZincXManagerServer.Command;

/// <summary>
/// config 配置模式:输入 <c>config</c> 进入,**只能改现有项,不能增删**。
///
/// <list type="bullet">
///   <item>进入时打印配置表:中文名 / 键名 / 当前值 / 类型 / 作用;</item>
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

    // 该端可配置项的清单(配置模式表格用它)。
    internal static readonly Item[] Items =
    [
        new() { Key = "name",          Cn = "服务端名称",   Description = "玩家看到的名称(linkname=1 时跟账号名)", Type = "文本 1-32 字符" },
        new() { Key = "linkname",      Cn = "名称联动",     Description = "1 = 名称与官方账号名一致,0 = 分开设置",   Type = "1/0" },
        new() { Key = "connectOS",     Cn = "连接官方端",   Description = "启动时是否连 ZXMOS",                     Type = "1/0" },
        new() { Key = "czport",        Cn = "客户端端口",   Description = "ZXMC/ZXMM 接入端口(重启生效)",           Type = "1-65535" },
        new() { Key = "ozip",          Cn = "官方端地址",   Description = "ZXMOS 的 IP 或域名",                     Type = "IP 或域名" },
        new() { Key = "ozport",        Cn = "官方端端口",   Description = "ZXMOS 的端口",                           Type = "1-65535" },
        new() { Key = "osuser",        Cn = "官方账号名",   Description = "本端在 ZXMOS 上注册的账号(改名请用 osuser)", Type = "文本 2-32 字符" },
        new() { Key = "squarename",    Cn = "广场名称",     Description = "客户端「服务器广场」卡片上显示的名字",     Type = "文本 1-24 字符" },
        new() { Key = "msgdays",       Cn = "消息保留天数", Description = "下发给客户端:本地留多久",                Type = "1-365" },
        new() { Key = "srvmsgdays",    Cn = "存档保留天数", Description = "本服存档留多久(补发用)",                 Type = "1-365" },
        new() { Key = "officialcheck", Cn = "对端校验",     Description = "1 = 对端不是官方就断开,0 = 只警告",       Type = "1/0" },
        new() { Key = "ozname",        Cn = "官方端名称",   Description = "ZXMOS 下发的名称(只读)",                 Type = "只读",       ReadOnly = true },
        new() { Key = "servercode",    Cn = "服务端码",     Description = "本实例的身份证(只读)",                   Type = "只读",       ReadOnly = true },
        new() { Key = "oshash",        Cn = "官方账号密文", Description = "本端在 ZXMOS 上的账号密文(下次连接生效)",  Type = "64 位十六进制" },
        new() { Key = "configver",     Cn = "配置版本",     Description = "配置文件结构版本(只读)",                 Type = "只读",       ReadOnly = true },
    ];

    /// <summary>正常命令模式里的命令词:配置模式下输入这些要明确拒绝,而不是当成配置项。</summary>
    private static readonly string[] Commands =
    {
        "help", "echo", "exit", "info", "infopc", "msgdays", "srvmsgdays", "name",
        "notice", "push", "osuser", "srv", "user", "ban", "file", "net", "config",
    };

    // 是否处于配置模式。
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
        if (text.Length == 0)
        {
            return true;
        }

        if (text.Equals("exitc", StringComparison.OrdinalIgnoreCase))
        {
            Exit();
            return true;
        }


        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length > 0 ? parts[0] : "";
        var value = parts.Length > 1 ? string.Join(' ', parts[1..]) : "";

        // 配置项优先(config 模式下 msgdays 之类既是命令又是配置项,按配置项处理)
        var item = Array.Find(Items, i =>
            i.Key.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            i.Cn.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            if (Array.Exists(Commands, c => c.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                Logger.Log(LogLevel.Warning,
                    $"配置模式下不能用命令 {name}:先输入 exitc 退出配置模式,再执行它");
            }
            else
            {
                Logger.Log(LogLevel.Warning,
                    $"没有这个配置项:{name}(只能改表里已有的,不能新增;输入 exitc 退出后可看配置表)");
            }

            return true;
        }

        if (item.ReadOnly)
        {
            Logger.Log(LogLevel.Warning, $"{item.Key}({item.Cn})是只读项,不能改");
            return true;
        }

        if (value.Length == 0)
        {
            Logger.Log(LogLevel.Warning, "配置模式用法:<键名|中文名> <键值>;输入 exitc 退出");
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

    /// <summary>按显示宽度算字符串占几列(中文算 2 列)。</summary>
    private static int DisplayWidth(string s)
    {
        var w = 0;
        foreach (var c in s) { w += c >= 0x2E80 ? 2 : 1; }
        return w;
    }
/// <summary>按显示宽度右侧补空格。</summary>

    private static string Pad(string s, int width)
    {
        var pad = width - DisplayWidth(s);
        return pad > 0 ? s + new string(' ', pad) : s;
    /// <summary>把三列排成对齐的表格文本。</summary>
    }

    private static string BuildTable()
    {
        var lines = new List<string> { "配置项(只能改这些,不能增删):" };
        lines.Add($"  {Pad("中文名", 16)}{Pad("键名", 14)}{Pad("当前值", 34)}{Pad("类型", 18)}作用");
        lines.Add("  " + new string('-', 100));

        foreach (var item in Items)
        {
            var value = Program.ConfigValue(item.Key);
            lines.Add($"  {Pad(item.Cn, 16)}{Pad(item.Key, 14)}{Pad(value, 34)}{Pad(item.Type, 18)}{item.Description}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
