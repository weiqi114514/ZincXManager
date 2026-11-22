using System;  // Func / StringComparer
using System.Collections.Generic;  // Dictionary / List
using System.Linq;  // LINQ 查询
using System.Text;  // StringBuilder:拼清单
using System.Threading.Tasks;  // Task
using ZincXManagerShared.FileIO;  // ZFile:读写配置
using ZincXManagerShared.Logging;  // Logger / LogLevel
using ZincXManagerShared.Modules;  // IModule / ModuleRules / ModuleModes
using ZincXManagerShared.Network;  // ServerboundPacket

namespace ZincXManagerServer.Modules;

/// <summary>
/// 模块注册表：按 config.ini 的 modules 键决定每个模块这次启动装到什么程度。
/// <para>三态：开(全装配) / 半加载(只留控制台命令,客户端包一律回 err) / 关(连类都不 new)。</para>
/// <para>关键约束：模块类放在 factory 里，先用纯数据判断开关，确认要装才 new。
/// 关掉的模块构造函数不执行、OnLoad 不调用、命令与包处理器一概不登记。
/// 改开关要重启 ZXMS 才生效，运行期不热更。</para>
/// </summary>
internal static class ModuleRegistry
{
    /// <summary>配置里存模块名单的键名。</summary>
    internal const string KeyModules = ModuleModes.KeyModules;

    /// <summary>modules 写这个值时表示「一个都不关、也不半加载」；空值表示「还没配过,按默认写」。</summary>
    internal const string NoneToken = ModuleModes.NoneToken;

    /// <summary>
    /// 服务端实现了模块类的那部分模块：编号 + 构造函数。
    /// 只放服务端真有事要做的；纯客户端入口不在这个表里，但仍然能写进禁用名单。
    /// 开关判断只读 ModuleCatalog 的纯数据，确认要装才调用 Create。
    /// </summary>
    private static readonly (string Id, Func<ServerModule> Create)[] _implemented =
    [
        (ModuleIds.NavFriends, static () => new FriendsModule()),
        (ModuleIds.WidgetBind, static () => new BindModule()),
        (ModuleIds.FuncBan, static () => new BanModule()),
        (ModuleIds.AdminWhitelist, static () => new WhitelistModule()),
        (ModuleIds.AdminZxmAccount, static () => new AccountModule()),
        (ModuleIds.AdminZxmRegister, static () => new RegisterModule()),
        (ModuleIds.ManageServer, static () => new ServerInfoModule()),
        (ModuleIds.Message, static () => new MessageModule()),
        (ModuleIds.Console, static () => new ConsoleModule()),
        (ModuleIds.ServerList, static () => new ServerListModule()),
        (ModuleIds.LoginConfirm, static () => new LoginConfirmModule()),
        (ModuleIds.Chat, static () => new ChatModule()),
        (ModuleIds.ForceUpdate, static () => new ForceUpdateModule()),
        (ModuleIds.DataSeparation, static () => new DataSeparationModule()),
    ];

    private static readonly List<ServerModule> _loaded = [];   //装进进程的模块(含半加载)
    private static readonly Dictionary<string, ServerModule> _loadedById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<ushort, ServerModule> _packetTable = [];   //包类型 -> 归属模块(半加载不进来)
    private static readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);   //完全关闭
    private static readonly HashSet<string> _half = new(StringComparer.OrdinalIgnoreCase);   //半加载
    private static readonly HashSet<string> _pendingOff = new(StringComparer.OrdinalIgnoreCase);   //写进配置、还没重启的关闭名单
    private static readonly HashSet<string> _pendingHalf = new(StringComparer.OrdinalIgnoreCase);   //写进配置、还没重启的半加载名单
    private static ModuleContext? _context;

    /// <summary>加载是否已经跑过(防止重复加载)。</summary>
    internal static bool Ready { get; private set; }

    /// <summary>装配时交给模块的那份上下文(核心分发把包转给模块时要用它)。</summary>
    internal static ModuleContext? Context => _context;

    /// <summary>配置里完全关闭的模块编号(只读快照，含纯客户端入口)。</summary>
    internal static IReadOnlyCollection<string> Disabled => _disabled;

    /// <summary>本次启动半加载的模块编号(只读快照)。</summary>
    internal static IReadOnlyCollection<string> HalfIds => _half;

    /// <summary>已装配的模块(只读快照，含半加载)。</summary>
    internal static IReadOnlyList<ServerModule> LoadedModules => _loaded;

    /// <summary>按编号查已装配的模块；没装配返回 null。</summary>
    internal static ServerModule? Find(string id)
        => _loadedById.TryGetValue(id, out var module) ? module : null;

    /// <summary>这个模块是否已经装进进程(半加载也算装配)。</summary>
    internal static bool IsLoaded(string id) => _loadedById.ContainsKey(id);

    /// <summary>这个模块这次是半加载：命令能用，客户端发来的包一律不接。</summary>
    internal static bool IsHalf(string id) => _half.Contains(id);

    /// <summary>
    /// 这个模块这次是不是「完整开着」：装进来了、而且不是半加载。
    /// 需要判断「该功能现在对客户端是否真的在生效」时用它(半加载对客户端等于关闭)。
    /// </summary>
    internal static bool IsOn(string id) => _loadedById.ContainsKey(id) && !_half.Contains(id);

    /// <summary>这个模块是否在完全关闭名单里(纯客户端入口也能查到)。</summary>
    internal static bool IsDisabled(string id) => _disabled.Contains(id);

    /// <summary>按包类型查归属模块；核心包、模块未装配或半加载都返回 false。</summary>
    internal static bool TryGetModule(ushort type, out ServerModule module)
        => _packetTable.TryGetValue(type, out module!);

    // ---------- 加载 ----------

    /// <summary>
    /// 读 modules 键、迁移旧开关、父关子关、按三态装配模块、建包分发表。
    /// 必须在 StartChatHost 之前调用：聊天宿主是否拉起由 ChatModule 决定。
    /// </summary>
    internal static void Load(ZFile config, ModuleContext context)
    {
        if (Ready)
        {
            return;
        }

        _context = context;

        var raw = config.MapGet(KeyModules).Trim();   //配置值
        if (raw.Length == 0)
        {
            raw = WriteDefaults(config);
        }

        ModuleModes.Parse(raw, _disabled, _half);

        //先把"文件里写下的名单"留一份:后面 DropLocked / ApplyParentRule 会往里加连带项
        var explicitOff = new HashSet<string>(_disabled, StringComparer.OrdinalIgnoreCase);
        var explicitHalf = new HashSet<string>(_half, StringComparer.OrdinalIgnoreCase);

        var cleaned = DropUnknown();
        cleaned |= DropLocked();
        if (cleaned)
        {
            //清单里有错的编号就顺手改回文件,否则每次启动都重复同一条告警
            var fixedText = ModuleModes.Encode(_disabled, _half);
            config.WriteFileMap(KeyModules, fixedText);
            Logger.Log(LogLevel.Info, $"[模块] 已回写清洗后的名单:{KeyModules}={fixedText}");
            explicitOff.IntersectWith(_disabled);
            explicitHalf.IntersectWith(_half);
        }

        ApplyParentRule();

        //"配置里写的名单"和"正在生效的名单"不是一回事:父模块关掉会把它下面一串子模块也带进 _disabled,
        //那些是连带出来的,没写进文件。pending 记的是配置文件里的原话,否则任何一次 module 命令
        //都会把整串连带项展开写进文件,以后单独打开父模块时子模块还留在关里。
        _pendingOff.Clear();
        _pendingOff.UnionWith(explicitOff);
        _pendingHalf.Clear();
        _pendingHalf.UnionWith(explicitHalf);

        foreach (var (id, create) in _implemented)
        {
            if (_disabled.Contains(id))
            {
                continue;
            }

            //半加载也要 new：类在,控制台命令才有人认领;包处理体也登记进来,
            //但分发时只对管理账号放行(玩家发来的包一律回 err)。
            if (_half.Contains(id))
            {
                var halfModule = create();
                _loaded.Add(halfModule);
                _loadedById[id] = halfModule;

                foreach (var type in halfModule.Packets)
                {
                    _packetTable[type] = halfModule;
                }

                Logger.Log(LogLevel.Info,
                    $"[模块] 已半加载 {halfModule.Title}({id}):客户端包只放行管理账号,玩家发来的回 err;" +
                    $"控制台命令 {halfModule.Commands.Count} 条照常可用");
                continue;
            }

            var module = create();
            module.OnLoad(context);
            _loaded.Add(module);
            _loadedById[id] = module;

            foreach (var type in module.Packets)
            {
                _packetTable[type] = module;
            }

            Logger.Log(LogLevel.Info,
                $"[模块] 已加载 {module.Title}({id}),接管 {module.Packets.Count} 个包、{module.Commands.Count} 条命令");
        }

        Ready = true;
        Logger.Log(LogLevel.Info, $"[模块] 装配完成:加载 {_loaded.Count} 个"
            + (_half.Count > 0 ? $"(其中半加载 {_half.Count} 个:{string.Join(",", SortedHalf())})" : "")
            + $",关闭 {_disabled.Count} 个"
            + (_disabled.Count > 0 ? "(关闭:" + string.Join(",", Sorted()) + ")" : "")
            + ";改开关要重启 ZXMS 才生效");
    }

    /// <summary>给命令处理器用的门禁:命令属于完全关闭的模块时给出明确理由(半加载放行)。</summary>
    internal static string? CommandBlocked(string command)
    {
        var owner = ModuleRules.CommandOf(command);   //归属模块编号
        if (owner.Length == 0)
        {
            return null;
        }

        if (IsLoaded(owner))
        {
            return null;
        }

        return $"[模块] 该功能已关闭:{TitleOf(owner)}({owner}) 重启前无法使用";
    }

    /// <summary>查模块的中文名；不认识的编号原样返回。</summary>
    internal static string TitleOf(string id) => ModuleCatalog.TitleOf(id);

    /// <summary>进程退出时对已装配的模块做收尾(半加载没跑过 OnLoad,也就不收尾)。</summary>
    internal static void UnloadAll()
    {
        foreach (var module in _loaded)
        {
            if (_half.Contains(module.Id))
            {
                continue;
            }

            try
            {
                module.OnUnload();
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, $"[模块] {module.Title} 收尾失败: {ex.Message}");
            }
        }
    }

    // ---------- module 命令用 ----------

    /// <summary>列出全部模块的开关与装配状态。</summary>
    internal static string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine("模块清单(半加载 = 只留控制台命令,客户端包一律回 err;关闭 = 启动时不装配;改完要重启 ZXMS):");
        sb.AppendLine($"  {"编号",-22}{"名称",-22}{"状态",-12}说明");

        foreach (var def in ModuleCatalog.All)
        {
            var on = IsLoaded(def.Id);                    //这次启动装进来了没有
            var halfNow = on && IsHalf(def.Id);           //装进来但只是半加载
            var nextOff = PendingOff(def.Id);             //配置里是不是关着(重启后生效)
            var nextHalf = !nextOff && PendingHalf(def.Id);//配置里是不是半加载(重启后生效)
            //纯客户端入口在服务端永远不装配,"关(下次开)"那格对它没有意义,只看配置
            var state = !HasImplementation(def.Id)
                ? (nextOff ? "关" : (nextHalf ? "半加载" : "开"))
                : (on, halfNow) switch
                {
                    (true, false) => nextOff ? "开(下次关)" : (nextHalf ? "开(下次半加载)" : "开"),
                    (true, true) => nextOff ? "半加载(下次关)" : (nextHalf ? "半加载" : "半加载(下次开)"),
                    _ => nextOff ? "关" : (nextHalf ? "关(下次半加载)" : "关(下次开)"),
                };

            var note = def.Locked
                ? "锁死,不能关闭"
                : (HasImplementation(def.Id) ? "服务端有实现" : "仅客户端入口");

            if (!HasImplementation(def.Id) && nextHalf)
            {
                note += ",半加载对它等同关闭";
            }

            if (HasImplementation(def.Id) && nextHalf)
            {
                note += ",半加载只给管理账号用";
            }

            if (def.Id == ModuleIds.ForceUpdate || def.Id == ModuleIds.DataSeparation)
            {
                note += ",默认关";
            }

            var module = Find(def.Id);
            if (module is not null)
            {
                if (halfNow)
                {
                    note = $"半加载:只留 {module.Commands.Count} 条命令,包一律回 err";
                }
                else if (module.Packets.Count > 0)
                {
                    note = $"接管 {module.Packets.Count} 个包、{module.Commands.Count} 条命令";
                }
            }

            sb.AppendLine($"  {def.Id,-22}{def.Title,-22}{state,-12}{note}");
        }

        sb.AppendLine();
        sb.AppendLine($"本次启动装配 {_loaded.Count} 个模块(其中半加载 {_half.Count} 个);" +
            "改开关后执行 module on/off/half/full 再重启 ZXMS 才生效");
        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// 开关一个模块:只写配置,不碰运行期状态。给了 <see cref="SetMode"/> 之后它退化成二态写法。
    /// 返回 (是否写成功, 给控制台的说明)。
    /// </summary>
    internal static (bool Ok, string Message) SetEnabled(ZFile config, string id, bool on)
        => SetMode(config, id, on ? ModuleMode.On : ModuleMode.Off);

    /// <summary>
    /// 把一个模块设成指定三态:只写配置,不碰运行期状态;重启 ZXMS 才生效。
    /// 校验(编号是否存在、是否锁死、父模块是否关着)都在这儿做。
    /// </summary>
    /// <param name="config">config.ini 句柄。</param>
    /// <param name="id">模块编号。</param>
    /// <param name="mode">开 / 半加载 / 关。</param>
    internal static (bool Ok, string Message) SetMode(ZFile config, string id, ModuleMode mode)
    {
        var target = ModuleCatalog.Find(id);   //模块声明
        if (target is null)
        {
            return (false, $"[模块] 没有这个模块:{id} 输入 module list 看清单");
        }

        if (mode != ModuleMode.On && target.Locked)
        {
            var what = mode == ModuleMode.Half ? "半加载" : "关闭";
            return (false, $"[模块] {target.Title} 是锁死的,不能{what}");
        }

        if (mode != ModuleMode.Off && target.Parent.Length > 0 && PendingOff(target.Parent))
        {
            return (false, $"[模块] 父模块「{TitleOf(target.Parent)}」已关闭,先打开它");
        }

        //以配置里的名单为准改写,写完就变成显式清单
        var off = new HashSet<string>(_pendingOff, StringComparer.OrdinalIgnoreCase);
        var half = new HashSet<string>(_pendingHalf, StringComparer.OrdinalIgnoreCase);
        off.Remove(target.Id);
        half.Remove(target.Id);

        switch (mode)
        {
            case ModuleMode.Half:
                half.Add(target.Id);
                break;
            case ModuleMode.Off:
                off.Add(target.Id);
                foreach (var childId in ModuleCatalog.ChildrenOf(target.Id))
                {
                    Logger.Log(LogLevel.Info,
                        $"[模块] 父模块「{target.Title}」关闭后,子模块「{TitleOf(childId)}」下次启动不再装配");
                }

                break;
        }

        //父模块关着时子模块本来就跟关:这种"连带"不写进文件,免得以后单独打开父模块时子模块还留在关里
        PruneImplied(off, half);

        config.WriteFileMap(KeyModules, ModuleModes.Encode(off, half));

        //配置改了但还没重启:记下来,module list 就能显示“开(下次关)”“半加载(下次开)”这些
        _pendingOff.Clear();
        _pendingOff.UnionWith(off);
        _pendingHalf.Clear();
        _pendingHalf.UnionWith(half);

        var word = mode switch
        {
            ModuleMode.Half => "半加载",
            ModuleMode.Off => "关闭",
            _ => "开启",
        };

        return (true, $"[模块] 已写入:{target.Title}({target.Id}) {word},重启 ZXMS 后生效");
    }

    /// <summary>配置里这个模块是不是关着(父模块关了,子模块跟着算关;重启后生效)。</summary>
    /// <param name="id">模块编号。</param>
    private static bool PendingOff(string id) => PendingAny(id, _pendingOff);

    /// <summary>
    /// 把「父模块关着才成立的关闭/半加载」从名单里去掉:父子都写进文件是多余的,而且有害 ——
    /// 以后单独 module on 打开父模块时,那串子模块还留在关里,看起来像"打开父模块没生效"。
    /// </summary>
    /// <param name="off">关闭名单(就地修改)。</param>
    /// <param name="half">半加载名单(就地修改)。</param>
    private static void PruneImplied(HashSet<string> off, HashSet<string> half)
    {
        //父模块关着 → 子模块那一条关/半加载都是冗余的,删掉
        foreach (var id in off.ToArray())
        {
            if (AncestorIn(id, off))
            {
                off.Remove(id);
            }
        }

        //父模块关着时半加载不成立(父不开,子没有"只留命令"可言);
        //父模块只是半加载则不影响子模块 —— 半加载不往下传
        foreach (var id in half.ToArray())
        {
            if (AncestorIn(id, off))
            {
                half.Remove(id);
            }
        }
    }

    /// <summary>沿着父链往上查,看有没有任一祖先在给定名单里(不含自己)。</summary>
    /// <param name="id">模块编号。</param>
    /// <param name="set">要查的名单。</param>
    private static bool AncestorIn(string id, HashSet<string> set)
    {
        var cur = ModuleCatalog.ParentOf(id);   //先跳到父模块
        for (var guard = 0; guard < 16 && cur.Length > 0; guard++)
        {
            if (set.Contains(cur))
            {
                return true;
            }

            cur = ModuleCatalog.ParentOf(cur);
        }

        return false;
    }

    /// <summary>配置里这个模块是不是半加载(父模块关着时不算;重启后生效)。</summary>
    /// <param name="id">模块编号。</param>
    private static bool PendingHalf(string id) => PendingAny(id, _pendingHalf);

    /// <summary>沿着父链往上查,看自己或任一祖先是不是在给定名单里。</summary>
    /// <param name="id">模块编号。</param>
    /// <param name="set">要查的名单。</param>
    private static bool PendingAny(string id, HashSet<string> set)
    {
        var cur = id;   //当前往上看的那一层
        for (var guard = 0; guard < 16 && cur.Length > 0; guard++)
        {
            if (set.Contains(cur))
            {
                return true;
            }

            cur = ModuleCatalog.ParentOf(cur);
        }

        return false;
    }

    // ---------- 解析与迁移 ----------

    /// <summary>按模块全集顺序列出当前关闭名单，只用于日志。</summary>
    private static IEnumerable<string> Sorted()
        => ModuleCatalog.All.Select(def => def.Id).Where(_disabled.Contains);

    /// <summary>按模块全集顺序列出当前半加载名单，只用于日志。</summary>
    private static IEnumerable<string> SortedHalf()
        => ModuleCatalog.All.Select(def => def.Id).Where(_half.Contains);

    /// <summary>服务端有没有为这个编号写模块类。</summary>
    private static bool HasImplementation(string id)
    {
        foreach (var (implId, _) in _implemented)
        {
            if (string.Equals(implId, id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 首次启动写默认开关,并顺手把旧键的值迁进来。
    /// 旧键优先(升级上来的部署行为不变),没有旧键才用模块自带的默认值。
    /// 半加载没有旧键可迁:默认名单里只有「开」与「关」两种。
    /// </summary>
    private static string WriteDefaults(ZFile config)
    {
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var half = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var def in ModuleCatalog.All)
        {
            var off = !def.DefaultOn;

            //旧键有值就按旧键来:chatenabled 只有 0 才算关,其它两个只有 1 才算开
            if (def.LegacyKey.Length > 0)
            {
                var legacy = config.MapGet(def.LegacyKey).Trim();   //旧开关的值
                if (legacy.Length > 0)
                {
                    off = string.Equals(def.LegacyKey, "chatenabled", StringComparison.OrdinalIgnoreCase)
                        ? legacy == "0"
                        : legacy != "1";
                }
            }

            if (off)
            {
                disabled.Add(def.Id);
            }
        }

        var text = ModuleModes.Encode(disabled, half);
        config.WriteFileMap(KeyModules, text);
        Logger.Log(LogLevel.Info,
            $"[模块] 首次启动:按默认与旧开关写入 {KeyModules}={text}" +
            "(旧键 chatenabled / forceupdate / data-separation 保留但不再读取)");
        return text;
    }

    /// <summary>剔除模块全集里没有的编号(两份名单都查)。返回是否真的剔掉了东西(调用方据此回写配置)。</summary>
    private static bool DropUnknown()
    {
        var dropped = false;   //剔掉过没有

        foreach (var set in new[] { _disabled, _half })
        {
            var bad = set.Where(id => !ModuleCatalog.Exists(id)).ToList();
            foreach (var id in bad)
            {
                set.Remove(id);
                dropped = true;
                Logger.Log(LogLevel.Warning, $"[模块] {KeyModules} 里有不认识的模块:{id},已忽略");
            }
        }

        return dropped;
    }

    /// <summary>剔除锁死不能关闭的编号(半加载也一并去掉)。返回是否真的剔掉了东西。</summary>
    private static bool DropLocked()
    {
        var dropped = false;   //剔掉过没有
        foreach (var def in ModuleCatalog.All)
        {
            if (!def.Locked)
            {
                continue;
            }

            var hit = _disabled.Remove(def.Id);
            hit |= _half.Remove(def.Id);
            if (hit)
            {
                dropped = true;
                Logger.Log(LogLevel.Warning, $"[模块] {def.Title}({def.Id}) 不能关闭,已从禁用名单里去掉");
            }
        }

        return dropped;
    }

    /// <summary>父模块关着就把子模块一起关掉(半加载的子模块也改成关:父不开,子没有半加载一说)。</summary>
    private static void ApplyParentRule()
    {
        foreach (var def in ModuleCatalog.All)
        {
            if (def.Parent.Length == 0 || !_disabled.Contains(def.Parent))
            {
                continue;
            }

            _half.Remove(def.Id);
            if (_disabled.Add(def.Id))
            {
                Logger.Log(LogLevel.Info,
                    $"[模块] 父模块「{TitleOf(def.Parent)}」已关闭,连带关闭「{def.Title}」({def.Id})");
            }
        }
    }
}
