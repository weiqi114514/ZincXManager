using System;  // 基础类型 / 常用静态方法
using System.Collections.Generic;  // 泛型集合
using System.Linq;  // LINQ 查询
using ZincXManagerServer.Modules;  // ModuleRegistry:模块开关
using ZincXManagerShared.Logging;  // 共享库
using ZincXManagerShared.Modules;  // ModuleCatalog:模块清单

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

        /// <summary>允许把值设成空串(只对「留空 = 用默认」这类项有意义)。</summary>
        public bool AllowEmpty { get; init; }
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
        new() { Key = "backgetcount",  Cn = "返回查询条数", Description = "msg backget 最多查几条玩家返回记录",     Type = "1-1000" },
        new() { Key = "packetrate",    Cn = "包速率上限",   Description = "每连接每秒最多几个包,超出的直接丢(0=不限)", Type = "0-10000" },
        new() { Key = "loginrate",     Cn = "登录失败上限", Description = "同一 IP 5 分钟内最多失败几次登录/注册(0=不限)", Type = "0-10000" },
        new() { Key = "officialcheck", Cn = "对端校验",     Description = "1 = 对端不是官方就断开,0 = 只警告",       Type = "1/0" },
        new() { Key = "forceupdate",   Cn = "强制更新",     Description = "1 = 旧版 ZXMC 拒绝登录并踢下线,0 = 不检查", Type = "1/0" },
        new() { Key = "minversion",    Cn = "最低版本",     Description = "强制更新的门槛版本,留空 = 跟本端版本一致",   Type = "版本号如 b0.3.2a", AllowEmpty = true },
        new() { Key = "machine.require", Cn = "机器码必填", Description = "1 = 取不到主板序列号就拒绝登录/注册;0 = 允许登录但不记录机器码,机器码封禁对这类账号无效", Type = "1/0" },
        new() { Key = "bindmax",       Cn = "绑定上限",     Description = "一个 ZXM 账号最多绑几个 MC 玩家",          Type = "0-1000,0=不限" },
        new() { Key = "persistdays",   Cn = "免确认天数",   Description = "同玩家同 IP 在这几天内进服不用再确认,0 = 不限期", Type = "0-3650" },
        new() { Key = "confirmalways", Cn = "每次都要确认", Description = "1 = 不看持久登录表,每次进服都弹窗;开启时清空已记的免确认", Type = "1/0" },
        new() { Key = "data-separation", Cn = "多服数据分离", Description = "1 = 按 MC 服分目录存数据,0 = 全部共享;改完要重启", Type = "1/0" },
        new() { Key = "data-separation-list", Cn = "分离数据键", Description = "逗号分隔的键号,见 countdataseparationkey.ini,如 4 = bind;改完要重启", Type = "如 4,1", AllowEmpty = true },
        new() { Key = "modules", Cn = "模块开关", Description = "禁用名单,逗号分隔;项后加 :half = 半加载(只留服务端命令,客户端全隐藏);none = 全开;改完要重启", Type = "如 wd.bind:half", AllowEmpty = true },
        new() { Key = "ozname",        Cn = "官方端名称",   Description = "ZXMOS 下发的名称(只读)",                 Type = "只读",       ReadOnly = true },
        new() { Key = "servercode",    Cn = "服务端码",     Description = "本实例的身份证(只读)",                   Type = "只读",       ReadOnly = true },
        new() { Key = "oshash",        Cn = "官方账号密文", Description = "本端在 ZXMOS 上的账号密文(下次连接生效)",  Type = "64 位十六进制" },
        new() { Key = "configver",     Cn = "配置版本",     Description = "配置文件结构版本(只读)",                 Type = "只读",       ReadOnly = true },
    ];

    /// <summary>正常命令模式里的命令词:配置模式下输入这些要明确拒绝,而不是当成配置项。</summary>
    private static readonly string[] Commands =
    {
        "help", "echo", "exit", "info", "infopc", "msgdays", "srvmsgdays", "name",
        "msg", "osuser", "srv", "user", "ban", "file", "net", "config", "module",
    };

    // 是否处于配置模式。
    private static bool _active;

    /// <summary>当前是否在配置模式里。</summary>
    internal static bool Active => _active;

    /// <summary>进入配置模式并打印配置表。</summary>
    internal static void Enter()
    {
        _active = true;
        // 多行内容由 LogLayout.NewLine 从第二行开始,这里不再手工补前导换行
        Logger.Log(LogLevel.Info,
            BuildTable() + "\n" +
            "已进入配置模式:输入 <键名|中文名> <键值> 修改,输入 exitc 退出",
            LogLayout.NewLine);
    }

    /// <summary>退出配置模式。</summary>
    internal static void Exit()
    {
        _active = false;
        Logger.Log(LogLevel.Info, "已退出配置模式,回到正常命令模式");
    }

    /// <summary>配置模式里的每一行都从这里走;返回 true = 已处理(不再当命令)。</summary>
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


        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);   //拆分结果
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

        // 空值只有「留空 = 用默认」这类项允许(minversion 清空就是回到本端版本)
        if (value.Length == 0 && !item.AllowEmpty)
        {
            Logger.Log(LogLevel.Warning, "配置模式用法:<键名|中文名> <键值>;输入 exitc 退出");
            return true;
        }

        // 模块开关不在 Program 的配置表里:整份禁用名单归模块层管(module on/off 写的也是这一项)
        if (item.Key.Equals(ModuleRegistry.KeyModules, StringComparison.OrdinalIgnoreCase))
        {
            SetModules(value);
            return true;
        }

        var error = Program.SetConfigItem(item.Key, value);
        if (error.Length > 0)
        {
            Logger.Log(LogLevel.Warning, $"{item.Key}({item.Cn})设置失败:{error}");
            return true;
        }

        var shown = Program.ConfigValue(item.Key);   //设置后的值
        Logger.Log(LogLevel.Info, $"{item.Key}({item.Cn})已设置为 {(shown.Length > 0 ? shown : "空")}");
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
    }

    /// <summary>把三列排成对齐的表格文本。</summary>
    private static string BuildTable()
    {
        var lines = new List<string> { "配置项(只能改这些,不能增删):" };
        lines.Add($"  {Pad("中文名", 16)}{Pad("键名", 17)}{Pad("当前值", 34)}{Pad("类型", 18)}作用");
        lines.Add("  " + new string('-', 103));

        foreach (var item in Items)
        {
            var value = CurrentValue(item);   //当前值
            lines.Add($"  {Pad(item.Cn, 16)}{Pad(item.Key, 17)}{Pad(value, 34)}{Pad(item.Type, 18)}{item.Description}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 配置表里显示的当前值。模块开关读 config.ini 的 modules:
    /// Program 的配置表里没有这一项,直接问它会得到空串。
    /// </summary>
    private static string CurrentValue(Item item)
    {
        if (item.Key.Equals(ModuleRegistry.KeyModules, StringComparison.OrdinalIgnoreCase))
        {
            var modules = Program.ModuleConfig?.MapGet(ModuleRegistry.KeyModules).Trim() ?? "";   //配置值
            return modules.Length > 0 ? modules : "(未配置)";
        }

        return Program.ConfigValue(item.Key);
    }

    /// <summary>
    /// 改模块开关(modules):只写 config.ini,运行期不热更 —— 和 module on/off/half/full 写的是同一项。
    /// 值 = 逗号分隔的模块编号,项后加 :half 是半加载(只留控制台命令、客户端包一律回 err);
    /// none = 一个都不关;清空 = 下次启动按默认与旧开关重新生成。
    /// </summary>
    private static void SetModules(string value)
    {
        var config = Program.ModuleConfig;   //config.ini 句柄
        if (config is null)
        {
            Logger.Log(LogLevel.Warning, "[模块] 配置还没加载完,现在改不了模块开关");
            return;
        }

        var text = value.Trim();
        if (text.Length == 0)
        {
            config.WriteFileMap(ModuleRegistry.KeyModules, "");
            Logger.Log(LogLevel.Info, "[模块] 模块开关已清空:下次启动 ZXMS 时按默认与旧开关重新生成");
            return;
        }

        if (string.Equals(text, ModuleRegistry.NoneToken, StringComparison.OrdinalIgnoreCase))
        {
            config.WriteFileMap(ModuleRegistry.KeyModules, ModuleRegistry.NoneToken);
            Logger.Log(LogLevel.Info, "[模块] 模块开关已设为 none(一个都不关),重启 ZXMS 后生效");
            return;
        }

        //先按和启动时完全相同的规则解析(分隔符、:half 后缀、同一编号两边都有以关为准)
        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);    //完全关闭
        var half = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   //半加载
        ModuleModes.Parse(text, off, half);

        foreach (var rawId in off.Concat(half).ToArray())
        {
            var def = ModuleCatalog.Find(rawId);   //模块声明
            if (def is null)
            {
                Logger.Log(LogLevel.Warning, $"[模块] 没有这个模块:{rawId}(输入 exitc 退出后用 module list 看清单)");
                return;
            }

            if (def.Locked)
            {
                Logger.Log(LogLevel.Warning, $"[模块] {def.Title}({def.Id}) 是锁死的,不能关闭;这次没有写入任何改动");
                return;
            }

            //半加载的父模块关着也说不通:子模块整块都不装配了,没有"只留命令"可言
            if (half.Contains(def.Id) && def.Parent.Length > 0 && off.Contains(def.Parent))
            {
                Logger.Log(LogLevel.Warning,
                    $"[模块] 父模块「{ModuleCatalog.TitleOf(def.Parent)}」已关闭,不能把 {def.Title}({def.Id}) 设成半加载;" +
                    "这次没有写入任何改动");
                return;
            }
        }

        if (off.Count == 0 && half.Count == 0)
        {
            Logger.Log(LogLevel.Warning,
                "[模块] 用法:模块开关 <编号,...>,项后加 :half 表示半加载,或 none;例:模块开关 nav.friends,wd.bind:half");
            return;
        }

        //按模块全集顺序写,和 module on/off/half 写出来的格式保持一致
        var encoded = ModuleModes.Encode(off, half);
        config.WriteFileMap(ModuleRegistry.KeyModules, encoded);
        Logger.Log(LogLevel.Info,
            $"[模块] 模块开关已写入:{encoded}(关闭 {off.Count} 个,半加载 {half.Count} 个),重启 ZXMS 后生效");
    }
}
