using System;  // Action / StringComparison
using System.Collections.Generic;  // HashSet
using ZincXManagerShared.Logging;  // Logger
using ZincXManagerShared.Modules;  // ModuleCatalog / ModuleModes / ModuleRules
using ZincXManagerShared.Network;  // Protocol.IsManagementLevel:判是不是管理账号

namespace ZincXManagerClient.Modules;

/// <summary>
/// 客户端的模块开关状态:每个模块三种状态 —— 开、半加载、关。
///
/// <para><b>开关从哪来</b>:服务端 202 配置包里的 <c>modules=</c> 行(逗号分隔的编号,
/// <c>none</c> 表示一个都没关),半加载的编号带 <c>:half</c> 尾巴(也接受单独一行 <c>half=</c>)。
/// 收到就记进客户端 config 的 <c>modules</c> 键,下次启动还没连上服务端时也能先按上次的名单
/// 把该藏的东西藏起来(<see cref="LoadFromCache"/>)。开关要重启 ZXMS 才生效,
/// 客户端只在收到 202 的那一刻重算一次。</para>
///
/// <para><b>半加载只给管理员</b>:半加载的意思是「玩家用不了,服主还能用」——
/// 玩家账号登录时入口、卡片、窗口全都不显示,相关的包也不收不发;
/// 管理账号(<c>LAdmin</c> / <c>HAdmin</c> / <c>System</c>)登录时入口照常显示、包照常收发,
/// 因为服务端那边本来就只对管理账号放行。所以 <see cref="IsOn"/> 看身份:
/// 玩家恒 false,管理员算 true;想知道"是半加载还是彻底关掉"用 <see cref="IsHalf"/>。</para>
/// </summary>
public static class ModuleState
{
    /// <summary>config 里存名单的键名(与 202 的行名一致)。</summary>
    private const string ConfigKey = ModuleModes.KeyModules;

    /// <summary>202 里另一行:半加载名单(值同样是逗号分隔的编号)。</summary>
    private const string HalfKey = "half";

    /// <summary>名单里允许的分隔符(逗号为主,其余几种是为了少踩服务端手写配置的坑)。</summary>
    private static readonly char[] Separators = [',', ';', '|', ' ', '\t', '\r', '\n'];

    /// <summary>彻底关掉的模块编号(大小写不敏感)。</summary>
    private static readonly HashSet<string> _off = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>半加载的模块编号:服务端控制台还能用,客户端这边当关掉处理。</summary>
    private static readonly HashSet<string> _half = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>名单变了:界面据此重算入口显隐、收掉已经关掉功能的窗口。</summary>
    public static event Action? Changed;

    /// <summary>
    /// 当前登录账号是不是管理员(<c>LAdmin</c> / <c>HAdmin</c> / <c>System</c>)。
    /// 半加载只对管理员有效,所以每次现查,不在静态字段里缓存 —— 换账号登录不会读到上一个账号的等级。
    /// </summary>
    public static bool IsAdminAccount => Protocol.IsManagementLevel(ClientConfig.GetOrEmpty("level"));

    /// <summary>
    /// 这个模块现在能不能用。半加载看身份:玩家当关掉(入口全不显示),管理员当开着(照常操作)。
    /// 父模块关掉或半加载时子模块跟着走(用户在服务端只处理了大类,下面那些也得藏起来);
    /// 锁死的模块(<c>nav.settings</c>)恒开。不认识的编号按开着处理。
    /// </summary>
    public static bool IsOn(string id)
    {
        var mode = ModeOf(id);
        if (mode == ModuleMode.On)
        {
            return true;
        }

        // 半加载:服务端只对管理账号放行,客户端也就只给管理账号显示入口
        return mode == ModuleMode.Half && IsAdminAccount;
    }

    /// <summary>这个模块是不是半加载(服务端只对管理账号生效,玩家看不到入口)。</summary>
    internal static bool IsHalf(string id) => ModeOf(id) == ModuleMode.Half;

    /// <summary>
    /// 客户端还能不能自助注册。注册发生在登录之前,服务端拿不到账号等级,
    /// 没法确认来的是不是管理员;客户端这边本来也没有给管理账号用的注册入口。
    /// 所以「账号注册」半加载时一律不给注册(服务端控制台的 <c>user reg</c> 照常可用),
    /// 只有完整开启才显示注册按钮。
    /// </summary>
    public static bool RegisterAllowed => ModeOf(ModuleIds.AdminZxmRegister) == ModuleMode.On;

    /// <summary>
    /// 查某个编号现在的状态:自己或任意一层父模块被关掉就是关,否则被半加载就是半加载,
    /// 都没有就是开。锁死模块恒开。
    /// </summary>
    public static ModuleMode ModeOf(string id)
    {
        var start = id.Trim();
        if (start.Length == 0)
        {
            return ModuleMode.On;   // 空编号 = 不受开关控制的核心功能
        }

        if (ModuleCatalog.IsLocked(start))
        {
            return ModuleMode.On;
        }

        var half = false;
        var current = start;
        for (var guard = 0; guard < 8 && current.Length > 0; guard++)   // guard:清单写错成环也不会死循环
        {
            if (_off.Contains(current))
            {
                return ModuleMode.Off;   // 关掉优先:关的父模块下面不能因为半加载又漏出来
            }

            if (!half && _half.Contains(current))
            {
                half = true;
            }

            current = ModuleCatalog.ParentOf(current);
        }

        return half ? ModuleMode.Half : ModuleMode.On;
    }

    /// <summary>
    /// 这个包现在能不能收:包归属的模块关掉就返回 false。
    /// 半加载的模块对管理员放行(要收 err 回包和操作结果),对玩家按关掉处理。
    /// </summary>
    public static bool PacketAllowed(ushort type)
    {
        var owner = ModuleRules.PacketOf(type);
        return owner.Length == 0 || IsOn(owner);
    }

    /// <summary>
    /// 这个包现在还能不能发。半加载的模块玩家一律不许发 —— 该功能在玩家这边完全不存在;
    /// 管理账号照常发(服务端那边只对管理账号放行)。
    /// 彻底关掉的模块不在这里挡:服务端会回 <c>err|该功能已关闭:…</c>,那句话要留着给用户看
    /// (否则点了没反应,用户以为客户端卡了)。
    /// </summary>
    public static bool SendAllowed(ushort type)
    {
        var owner = ModuleRules.PacketOf(type);
        return owner.Length == 0 || !IsHalf(owner) || IsAdminAccount;
    }

    /// <summary>
    /// 解析 202 的服务端配置包,认 <c>modules=</c> 和 <c>half=</c> 两行:
    /// 值是逗号隔开的编号,半加载的编号带 <c>:half</c> 尾巴,<c>none</c>(或空)表示没有。
    /// 两行都没有就什么都不做 —— 老服务端不下发名单,客户端保持原样。
    /// </summary>
    public static void Configure(string payload)
    {
        string? modules = null;
        string? half = null;

        foreach (var rawLine in payload.Split('\n'))
        {
            var parts = rawLine.Trim().Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var key = parts[0].Trim();
            if (key.Equals(ConfigKey, StringComparison.OrdinalIgnoreCase))
            {
                modules = parts[1];
            }
            else if (key.Equals(HalfKey, StringComparison.OrdinalIgnoreCase))
            {
                half = parts[1];
            }
        }

        if (modules is null && half is null)
        {
            return;   // 老服务端不带这两行:保持原样
        }

        Apply(modules ?? "", half ?? "", persist: true, notify: true);
    }

    /// <summary>
    /// 启动时读上次记下的名单(还没连上服务端也能先把该藏的藏起来)。
    /// 不写文件、不通知:这一刻界面还没建好,由调用方接着重算一次。
    /// </summary>
    public static void LoadFromCache() => Apply(ClientConfig.GetOrEmpty(ConfigKey), "", persist: false, notify: false);

    /// <summary>换成新的名单:清掉旧的、装新的,需要时写回 config 并通知界面。</summary>
    /// <param name="modulesValue">202 的 <c>modules=</c> 值(逗号分隔,<c>:half</c> 表半加载)。</param>
    /// <param name="halfValue">202 的 <c>half=</c> 值(逗号分隔的半加载编号,没这一行就给空)。</param>
    /// <param name="persist">要不要写回 config。</param>
    /// <param name="notify">要不要触发 <see cref="Changed"/>。</param>
    private static void Apply(string modulesValue, string halfValue, bool persist, bool notify)
    {
        // 共用契约那套解析:modules= 一行里带 :half 的进半加载集合,同一编号既关又半加载时"关"优先
        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var half = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ModuleModes.Parse(modulesValue, off, half);

        if (halfValue.Trim().Length > 0)
        {
            // half= 是另一行:那里列出的编号本来就是半加载,不用再带 :half 尾巴。
            // 所以这里不能直接套 ModuleModes.Parse —— 那套规则里裸编号等于"完全关闭",
            // 会把半加载错读成关闭(关闭优先,半加载条目就全丢了)。
            foreach (var item in halfValue.Split(Separators))
            {
                var id = item.Trim();
                if (id.Length == 0 || id.Equals(ModuleModes.NoneToken, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 有人在 half= 里又写了 :half 也照收,免得服务端换个写法客户端就认不出来
                if (id.EndsWith(ModuleModes.HalfSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    id = id[..^ModuleModes.HalfSuffix.Length].Trim();
                }

                half.Add(id);
            }
        }

        _off.Clear();
        _half.Clear();

        foreach (var id in off)
        {
            if (Accepted(id, "关闭"))
            {
                _off.Add(id);
            }
        }

        foreach (var id in half)
        {
            if (!_off.Contains(id) && Accepted(id, "半加载"))
            {
                _half.Add(id);
            }
        }

        if (persist)
        {
            // 存一份合并写法(关掉的写编号,半加载的写 :half),下次启动 LoadFromCache 原样读回来
            ClientConfig.Set(ConfigKey, ModuleModes.Encode(_off, _half));
        }

        Logger.Log(LogLevel.Info, "[ZXMC] 模块开关:" + Describe());

        if (notify)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>名单里这一项能不能生效:不认识的编号和锁死模块都不要,并落一条日志说明为什么。</summary>
    /// <param name="id">名单里的编号。</param>
    /// <param name="what">调用方在做什么("关闭" / "半加载"),只用于日志。</param>
    private static bool Accepted(string id, string what)
    {
        if (!ModuleCatalog.Exists(id))
        {
            Logger.Log(LogLevel.Warning, $"[ZXMC] 模块名单里有不认识的编号,已忽略:{id}");
            return false;
        }

        if (ModuleCatalog.IsLocked(id))
        {
            Logger.Log(LogLevel.Warning, $"[ZXMC] 模块「{ModuleCatalog.TitleOf(id)}」锁死不能{what},已忽略:{id}");
            return false;
        }

        return true;
    }

    /// <summary>名单的一句话说明(按清单顺序,日志每次一致)。</summary>
    private static string Describe()
    {
        var off = new List<string>();
        var half = new List<string>();
        foreach (var def in ModuleCatalog.All)
        {
            if (_off.Contains(def.Id))
            {
                off.Add(def.Title);
            }
            else if (_half.Contains(def.Id))
            {
                half.Add(def.Title);
            }
        }

        if (off.Count == 0 && half.Count == 0)
        {
            return "全部开启";
        }

        var text = off.Count == 0 ? "" : $"关闭 {off.Count} 项:{string.Join("、", off)}";
        if (half.Count > 0)
        {
            var halfText = $"半加载 {half.Count} 项:{string.Join("、", half)}";
            return text.Length == 0 ? halfText : text + ";" + halfText;
        }

        return text;
    }
}
