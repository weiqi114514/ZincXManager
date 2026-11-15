using System;                              // Environment / StringComparison / TimeSpan
using System.Collections.Generic;          // List
using System.Globalization;                // CultureInfo / NumberStyles
using System.Threading;                    // CancellationTokenSource
using ZincXManagerServer.Zxms;             // ConsoleRouter / ZxmmEntry / ZxmmRegistry
using ZincXManagerShared.Command;          // CommandHandler
using ZincXManagerShared.Logging;          // Logger / LogLevel

namespace ZincXManagerServer.Command;

/// <summary>
/// zxmm 配置模式：改 <c>Data/ZXMMC.dat</c> 里的连接表（一台 ZXMM 怎么连、控制台走哪条路）。
///
/// <list type="bullet">
///   <item><c>zxmm config</c> 打印连接表，输入编号或名称选中一台后进编辑模式；</item>
///   <item><c>zxmm config &lt;编号|名称&gt;</c> 直接进编辑模式；</item>
///   <item>编辑模式里每行 <c>&lt;键|中文名&gt; &lt;值&gt;</c>，<c>info</c> 看全部字段，<c>exitc</c> 落盘并退出；</item>
///   <item>把 <c>consolemode</c> 改成 1（直连 RCON）而 RCON 还是默认值时，额外问一次「保留默认 / 让我来改」。</item>
/// </list>
///
/// <para>整段交互都是状态机：命令循环已经是 <see cref="Console"/> 驱动的，
/// 再调一层 <c>ReadLine</c> 会把后面的命令一起吃掉，所以每一行都从 <see cref="Handle"/> 进。</para>
/// </summary>
internal static class ZxmmConfigMode
{
    /// <summary>当前停在哪个输入位；每一行按它分派。</summary>
    private enum State
    {
        Idle,
        Select,
        Edit,
        RconChoice,
        RconHost,
        RconPort,
        RconPassword,
        RconConfirm,
    }

    /// <summary>编辑模式认的一个键（英文键与中文名等价）。</summary>
    private sealed class Field
    {
        public string Key { get; init; } = "";
        public string Cn { get; init; } = "";

        /// <summary>允许把值设成空串（「留空 = 跟 ZXMM 主机」这种）。</summary>
        public bool AllowEmpty { get; init; }
    }

    // 编辑模式能改的键;顺序就是 info 与提示里的顺序
    private static readonly Field[] Fields =
    [
        new() { Key = "name",         Cn = "名称" },
        new() { Key = "host",         Cn = "主机" },
        new() { Key = "port",         Cn = "端口" },
        new() { Key = "token",        Cn = "令牌",       AllowEmpty = true },
        new() { Key = "consolemode",  Cn = "控制台模式" },
        new() { Key = "rconhost",     Cn = "RCON主机",   AllowEmpty = true },
        new() { Key = "rconport",     Cn = "RCON端口" },
        new() { Key = "rconpassword", Cn = "RCON密码",   AllowEmpty = true },
    ];

    // 正常命令模式里的命令词:编辑模式下输入这些要明确拒绝,而不是当成未知键
    private static readonly string[] Commands =
    {
        "help", "echo", "exit", "config", "net", "user", "wl", "bind", "unbind",
        "name", "osuser", "srv", "msg", "msgdays", "srvmsgdays", "file", "log", "zxmm",
    };

    // 写 server.properties 最多等多久:ZXMM 掉线时不能把命令线程永远挂着
    private const int RconWriteTimeoutSeconds = 20;

    // 是否处于本模式。
    private static bool _active;

    // 当前停在哪个输入位。
    private static State _state = State.Idle;

    // 正在编辑的那台服。
    private static ZxmmEntry? _entry;

    // 命令层实例(挂/摘拦截器用),由 RegisterZxmsCommands 注入。
    private static CommandHandler? _cmd;

    // 进来之前命令层挂的拦截器与配置模式判定,退出时原样还回去。
    private static Func<string, bool>? _previousInterceptor;
    private static Func<bool>? _previousIsConfigMode;
    private static Func<string, bool>? _previousMaskInput;

    // 二级流程里攒的三项,确认后才落盘。
    private static string _pendingRconHost = "";
    private static int _pendingRconPort;
    private static string _pendingRconPassword = "";

    /// <summary>当前是否在本模式里。</summary>
    internal static bool Active => _active;

    /// <summary>接线：拿到命令层实例，进模式时才能挂 / 摘拦截器。</summary>
    /// <param name="cmd">宿主建的命令处理器。</param>
    internal static void Attach(CommandHandler cmd) => _cmd = cmd;

    /// <summary>
    /// 进入 zxmm 配置模式：给了参数就直接选中那台，没给就打印连接表等人选。
    /// </summary>
    /// <param name="args">命令后面的参数；只有第一个（编号或名称）有效。</param>
    internal static void Enter(string[] args)
    {
        if (_cmd is null)
        {
            Logger.Log(LogLevel.Warning, "[ZXMM] 命令层没接上,进不了 zxmm 配置模式(程序内部错误)");
            return;
        }

        if (_active)
        {
            Logger.Log(LogLevel.Warning, "[ZXMM] 已经在 zxmm 配置模式里了:输入 exitc 退出");
            return;
        }

        ZxmmEntry? entry = null;   //一开始就指定的那台,没给参数时为 null
        if (args.Length > 0)
        {
            var key = args[0].Trim();
            entry = ZxmmRegistry.Find(key);
            if (entry is null)
            {
                // 找不到就回命令层:不挂拦截器,免得之后每行都被吞掉
                Logger.Log(LogLevel.Warning,
                    $"[ZXMM] 没有这台服:{key}(输入 zxmm config 先看连接表,或 zxmm config <编号|名称>)");
                return;
            }
        }
        else if (ZxmmRegistry.All.Count == 0)
        {
            Logger.Log(LogLevel.Warning,
                $"[ZXMM] 连接表 {ZxmmRegistry.FileName} 里一台服都没有:先在控制台用 zxmm connect 之前把它登记进表");
            return;
        }

        _active = true;
        _previousInterceptor = _cmd.LineInterceptor;
        _previousIsConfigMode = _cmd.IsConfigMode;
        _previousMaskInput = _cmd.MaskInput;

        // 先问本模式,没接管再放给原来那个(配置模式)
        _cmd.LineInterceptor = line => Handle(line) || (_previousInterceptor?.Invoke(line) ?? false);
        _cmd.IsConfigMode = () => _active || (_previousIsConfigMode?.Invoke() ?? false);

        // 输 RCON 密码那一行要脱敏:命令层在把行交给拦截器之前就回显了
        _cmd.MaskInput = line => IsSensitiveLine(line) || (_previousMaskInput?.Invoke(line) ?? false);

        if (entry is null)
        {
            _state = State.Select;
            Logger.Log(LogLevel.Info, Environment.NewLine + BuildTable() + Environment.NewLine +
                "输入编号或名称选择一台,输入 exitc 退出");
            return;
        }

        Select(entry);
    }

    /// <summary>
    /// 这一行会不会是密码:二级询问的密码步骤整行都是密码,
    /// 编辑模式下 "rconpassword xxx" 这种整行里就带着密码。
    /// 命令层在把行交给拦截器之前就回显了,所以这里必须先把行判掉。
    /// </summary>
    private static bool IsSensitiveLine(string line)
    {
        if (_state == State.RconPassword)
        {
            return true;
        }

        if (!_active || _state != State.Edit)
        {
            return false;
        }

        var space = line.IndexOf(' ');
        if (space <= 0)
        {
            return false;
        }

        var head = line[..space].Trim(); // 这一行的键名部分
        return head.Equals("rconpassword", StringComparison.OrdinalIgnoreCase)
            || head.Equals("RCON密码", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>退出本模式，把拦截器还回命令层。</summary>
    internal static void Exit()
    {
        _active = false;
        _state = State.Idle;
        _entry = null;
        ClearPendingRcon();

        if (_cmd is not null)
        {
            _cmd.LineInterceptor = _previousInterceptor;
            _cmd.IsConfigMode = _previousIsConfigMode;
            _cmd.MaskInput = _previousMaskInput;
        }

        _previousInterceptor = null;
        _previousIsConfigMode = null;
        _previousMaskInput = null;

        Logger.Log(LogLevel.Info, "[ZXMM] 已退出 zxmm 配置模式,回到正常命令模式");
    }

    /// <summary>本模式里的每一行都从这里走;返回 true = 已处理(不再当命令)。</summary>
    /// <param name="line">命令线程读到的原始一行。</param>
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

        if (_state == State.Select)
        {
            return HandleSelect(text);
        }

        var entry = _entry;   //正在编辑的那台
        if (entry is null)
        {
            // 状态与对象对不上(理论上不会),按退出处理,别把命令层卡死
            Exit();
            return true;
        }

        return _state switch
        {
            State.Edit => HandleEdit(entry, text),
            State.RconChoice => HandleRconChoice(entry, text),
            State.RconHost => HandleRconHost(entry, text),
            State.RconPort => HandleRconPort(entry, text),
            State.RconPassword => HandleRconPassword(entry, text),
            State.RconConfirm => HandleRconConfirm(entry, text),
            _ => true,
        };
    }

    // ---------- 选择模式 ----------

    /// <summary>选择模式：输入编号或名称选中一台。</summary>
    private static bool HandleSelect(string text)
    {
        if (IsExit(text))
        {
            Exit();
            return true;
        }

        var entry = ZxmmRegistry.Find(text);
        if (entry is null)
        {
            Logger.Log(LogLevel.Warning,
                $"[ZXMM] 没有这台服:{text}(输入编号或名称;输入 exitc 退出)");
            return true;
        }

        Select(entry);
        return true;
    }

    /// <summary>选中一台，进编辑模式。</summary>
    private static void Select(ZxmmEntry entry)
    {
        _entry = entry;
        _state = State.Edit;

        Logger.Log(LogLevel.Info, Environment.NewLine +
            $"已选中 {entry.Id} {entry.Name}({entry.Host}:{entry.Port},{ModeText(entry.ConsoleMode)})" + Environment.NewLine +
            "逐项修改:输入 <键|中文名> <值>;可改 name/名称 host/主机 port/端口 token/令牌 " +
            "consolemode/控制台模式 rconhost/RCON主机 rconport/RCON端口 rconpassword/RCON密码" + Environment.NewLine +
            "info 查看这台服的全部字段,exitc 保存并退出");
    }

    // ---------- 编辑模式 ----------

    /// <summary>编辑模式：<c>&lt;键&gt; &lt;值&gt;</c> / info / exitc。</summary>
    private static bool HandleEdit(ZxmmEntry entry, string text)
    {
        if (IsExit(text))
        {
            // 编辑模式下退出就是「确认改完」:必须落盘,否则改的全丢
            ZxmmRegistry.Save();
            Exit();
            return true;
        }

        if (text.Equals("info", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("查看", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Log(LogLevel.Info, DescribeEntry(entry));
            return true;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);   //拆分结果
        var name = parts.Length > 0 ? parts[0] : "";
        var value = parts.Length > 1 ? string.Join(' ', parts[1..]) : "";

        var field = Array.Find(Fields, f =>
            f.Key.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            f.Cn.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (field is null)
        {
            if (Array.Exists(Commands, c => c.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                Logger.Log(LogLevel.Warning,
                    $"[ZXMM] 编辑模式下不能用命令 {name}:先输入 exitc 退出,再执行它");
            }
            else
            {
                Logger.Log(LogLevel.Warning,
                    $"[ZXMM] 没有这个键:{name}(可改 name/host/port/token/consolemode/rconhost/rconport/rconpassword," +
                    "info 看字段,exitc 退出)");
            }

            return true;
        }

        if (value.Length == 0 && !field.AllowEmpty)
        {
            Logger.Log(LogLevel.Warning, $"[ZXMM] 用法:<键|中文名> <值>;{field.Key}({field.Cn})不能留空");
            return true;
        }

        Apply(entry, field, value);
        return true;
    }

    /// <summary>把一个键的值校验后写进内存（不落盘，exitc 才写）。</summary>
    private static void Apply(ZxmmEntry entry, Field field, string value)
    {
        var text = value.Trim();

        switch (field.Key)
        {
            case "name":
                if (!CheckText(field, text))
                {
                    return;
                }

                entry.Name = text;
                Logger.Log(LogLevel.Info, $"[ZXMM] {entry.Id} 的名称已设为 {text}");
                return;

            case "host":
                if (!CheckText(field, text))
                {
                    return;
                }

                entry.Host = text;
                Logger.Log(LogLevel.Info,
                    $"[ZXMM] {entry.Id} {entry.Name} 的 ZXMM 主机已设为 {text}(RCON 主机留空时也跟着它)");
                return;

            case "port":
                if (!TryParsePort(text, out var port))
                {
                    Logger.Log(LogLevel.Warning, $"[ZXMM] 端口 {text} 不在 1-65535");
                    return;
                }

                entry.Port = port;
                Logger.Log(LogLevel.Info, $"[ZXMM] {entry.Id} {entry.Name} 的 ZXMM 端口已设为 {port}");
                return;

            case "token":
                if (!CheckText(field, text))
                {
                    return;
                }

                entry.Token = text;
                Logger.Log(LogLevel.Info, text.Length == 0
                    ? $"[ZXMM] {entry.Id} {entry.Name} 的令牌已清空(两边都留空 = 不校验)"
                    : $"[ZXMM] {entry.Id} {entry.Name} 的令牌已设置({text.Length} 位)");
                return;

            case "consolemode":
                ApplyConsoleMode(entry, text);
                return;

            case "rconhost":
                if (!CheckText(field, text))
                {
                    return;
                }

                entry.RconHost = text;
                Logger.Log(LogLevel.Info, text.Length == 0
                    ? $"[ZXMM] {entry.Id} {entry.Name} 的 RCON 主机已清空(跟 ZXMM 主机 {entry.Host})"
                    : $"[ZXMM] {entry.Id} {entry.Name} 的 RCON 主机已设为 {text}");
                return;

            case "rconport":
                if (!TryParsePort(text, out var rconPort))
                {
                    Logger.Log(LogLevel.Warning, $"[ZXMM] RCON 端口 {text} 不在 1-65535");
                    return;
                }

                entry.RconPort = rconPort;
                Logger.Log(LogLevel.Info, $"[ZXMM] {entry.Id} {entry.Name} 的 RCON 端口已设为 {rconPort}");
                return;

            case "rconpassword":
                if (!CheckText(field, text))
                {
                    return;
                }

                entry.RconPassword = text;
                Logger.Log(LogLevel.Info,
                    $"[ZXMM] {entry.Id} {entry.Name} 的 RCON 密码{PasswordText(text)}(日志里不记原文)");
                return;

            default:
                Logger.Log(LogLevel.Warning, $"[ZXMM] 没有这个键:{field.Key}");
                return;
        }
    }

    /// <summary>控制台模式 0/1；改成 RCON 而 RCON 还是默认值时，问「保留默认 / 让我来改」。</summary>
    private static void ApplyConsoleMode(ZxmmEntry entry, string text)
    {
        if (text is not ("0" or "1"))
        {
            Logger.Log(LogLevel.Warning, $"[ZXMM] 控制台模式只能是 0(ZXMM 桥接) 或 1(直连 RCON),收到 {text}");
            return;
        }

        var mode = text == "1" ? ZxmmRegistry.ConsoleModeRcon : ZxmmRegistry.ConsoleModeBridge;   //模式编号
        entry.ConsoleMode = mode;

        if (mode == ZxmmRegistry.ConsoleModeBridge)
        {
            Logger.Log(LogLevel.Info,
                $"[ZXMM] {entry.Id} {entry.Name} 的控制台模式已记为 ZXMM 桥接(0),exitc 时写回连接表");
            return;
        }

        // 密码空 + 端口还是默认 = 服务端配置文件里没配过 RCON,先问用户怎么办
        if (entry.RconPassword.Length == 0 && entry.RconPort == ZxmmRegistry.DefaultRconPort)
        {
            _state = State.RconChoice;
            Logger.Log(LogLevel.Info, Environment.NewLine +
                $"这台服的 RCON 还是默认配置(RCON端口 {ZxmmRegistry.DefaultRconPort},密码空)。" + Environment.NewLine +
                "[1] 保留默认 —— 只把模式记下来,你自己去改 server.properties" + Environment.NewLine +
                "[2] 让我来改 —— 依次输入 IP / 端口 / 密码,确认后经 ZXMM 写进那台服的 server.properties" + Environment.NewLine +
                "输入 1 或 2(exitc 放弃):");
            return;
        }

        Logger.Log(LogLevel.Info,
            $"[ZXMM] {entry.Id} {entry.Name} 的控制台模式已记为直连 RCON(1):{entry.RconAddress}," +
            $"密码{PasswordText(entry.RconPassword)},exitc 时写回连接表");
    }

    // ---------- 把 RCON 写进 server.properties 的二级流程 ----------

    /// <summary>问「保留默认 / 让我来改」。</summary>
    private static bool HandleRconChoice(ZxmmEntry entry, string text)
    {
        if (IsExit(text))
        {
            AbandonRcon(entry, "已放弃写 server.properties");
            return true;
        }

        if (text == "1")
        {
            _state = State.Edit;
            Logger.Log(LogLevel.Info,
                "[ZXMM] 模式已记为 RCON,记得重启那台 MC 服并手动配 server.properties" +
                "(enable-rcon=true / rcon.port / rcon.password)");
            return true;
        }

        if (text == "2")
        {
            _state = State.RconHost;
            Logger.Log(LogLevel.Info,
                $"[ZXMM] RCON 主机(留空 = 跟 ZXMM 主机 {entry.Host}):");
            return true;
        }

        Logger.Log(LogLevel.Warning, "[ZXMM] 输入 1 或 2(1 = 保留默认,2 = 让我来改;exitc 放弃)");
        return true;
    }

    /// <summary>RCON 主机：留空表示与 ZXMM 同一台机器。</summary>
    private static bool HandleRconHost(ZxmmEntry entry, string text)
    {
        if (IsExit(text))
        {
            AbandonRcon(entry, "已放弃写 server.properties");
            return true;
        }

        if (!CheckText(null, text))
        {
            return true;
        }

        _pendingRconHost = text;
        _state = State.RconPort;
        Logger.Log(LogLevel.Info,
            $"[ZXMM] RCON 端口(1-65535,直接回车用默认 {ZxmmRegistry.DefaultRconPort}):");
        return true;
    }

    /// <summary>RCON 端口：直接回车用 25575。</summary>
    private static bool HandleRconPort(ZxmmEntry entry, string text)
    {
        if (IsExit(text))
        {
            AbandonRcon(entry, "已放弃写 server.properties");
            return true;
        }

        var port = ZxmmRegistry.DefaultRconPort;   //回车 = 用 MC 的默认 rcon.port
        if (text.Length > 0 && !TryParsePort(text, out port))
        {
            Logger.Log(LogLevel.Warning, $"[ZXMM] RCON 端口 {text} 不在 1-65535,重新输入(直接回车用 {ZxmmRegistry.DefaultRconPort})");
            return true;
        }

        _pendingRconPort = port;
        _state = State.RconPassword;
        Logger.Log(LogLevel.Info,
            "[ZXMM] RCON 密码(必填;命令层会把这行原样回显,介意就输入 exitc 改成自己配):");
        return true;
    }

    /// <summary>RCON 密码：只进内存，屏幕与日志里都不出现原文。</summary>
    private static bool HandleRconPassword(ZxmmEntry entry, string text)
    {
        if (IsExit(text))
        {
            AbandonRcon(entry, "已放弃写 server.properties");
            return true;
        }

        if (text.Length == 0)
        {
            Logger.Log(LogLevel.Warning, "[ZXMM] RCON 密码不能为空(空密码等于 MC 那边不启 RCON),重新输入");
            return true;
        }

        if (!CheckText(null, text))
        {
            return true;
        }

        _pendingRconPassword = text;
        _state = State.RconConfirm;

        var host = _pendingRconHost.Length == 0 ? entry.Host : _pendingRconHost;   //留空 = 跟 ZXMM 主机
        Logger.Log(LogLevel.Info, Environment.NewLine +
            $"即将写入 {entry.Id} {entry.Name} 的 server.properties:" + Environment.NewLine +
            $"  RCON 地址: {host}:{_pendingRconPort}" +
            (_pendingRconHost.Length == 0 ? "(留空,跟 ZXMM 主机)" : "") + Environment.NewLine +
            $"  密码: {PasswordText(_pendingRconPassword)}" + Environment.NewLine +
            "确认请输入 y(其它任意输入放弃):");
        return true;
    }

    /// <summary>二次确认：y 才真写；失败不落盘，回编辑模式让用户重试。</summary>
    private static bool HandleRconConfirm(ZxmmEntry entry, string text)
    {
        if (IsExit(text) || !text.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            AbandonRcon(entry, "已放弃写 server.properties(那台服没动)");
            return true;
        }

        var host = _pendingRconHost.Length == 0 ? entry.Host : _pendingRconHost;   //实际用来连的地址,只进日志
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(RconWriteTimeoutSeconds));
            var (ok, result) = ConsoleRouter
                .WriteRconConfigAsync(entry.Id, host, _pendingRconPort, _pendingRconPassword, cts.Token)
                .GetAwaiter().GetResult();

            if (ok)
            {
                // 写成功了才把三项记进连接表:失败时保持原样,用户能再试
                entry.RconHost = _pendingRconHost;
                entry.RconPort = _pendingRconPort;
                entry.RconPassword = _pendingRconPassword;
                ZxmmRegistry.Save();
                Logger.Log(LogLevel.Info,
                    $"[ZXMM] 已写入 server.properties,重启 MC 服生效({result};密码 {_pendingRconPassword.Length} 位)");
            }
            else
            {
                Logger.Log(LogLevel.Warning,
                    $"[ZXMM] 写 server.properties 失败:{result}(连接表没改,可输入 consolemode 1 重来)");
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log(LogLevel.Warning,
                $"[ZXMM] 写 server.properties 超时({RconWriteTimeoutSeconds} 秒没回),连接表没改");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[ZXMM] 写 server.properties 出错:{ex.Message}(连接表没改)");
        }

        ClearPendingRcon();
        _state = State.Edit;
        return true;
    }

    /// <summary>放弃二级流程：回编辑模式（控制台模式仍是 RCON，只是不自动写配置）。</summary>
    private static void AbandonRcon(ZxmmEntry entry, string reason)
    {
        ClearPendingRcon();
        _state = State.Edit;
        Logger.Log(LogLevel.Warning,
            $"[ZXMM] {reason};模式仍是 RCON(1),exitc 时只把模式写回连接表");
    }

    /// <summary>清掉二级流程里攒的三项（密码不留内存）。</summary>
    private static void ClearPendingRcon()
    {
        _pendingRconHost = "";
        _pendingRconPort = 0;
        _pendingRconPassword = "";
    }

    // ---------- 展示 ----------

    /// <summary>这台服的全部字段（密码只显示位数）。</summary>
    private static string DescribeEntry(ZxmmEntry entry)
    {
        var lines = new List<string>
        {
            $"{entry.Id} {entry.Name}:",
            $"  name        = {entry.Name}",
            $"  host        = {entry.Host}",
            $"  port        = {entry.Port}",
            $"  token       = {(entry.Token.Length == 0 ? "(空,不校验令牌)" : $"{entry.Token.Length} 位(已隐藏)")}",
            $"  consolemode = {entry.ConsoleMode} ({ModeText(entry.ConsoleMode)})",
            $"  rconhost    = {(entry.RconHost.Length == 0 ? $"(空,跟 ZXMM 主机 {entry.Host})" : entry.RconHost)}",
            $"  rconport    = {entry.RconPort}",
            $"  rconpassword= {PasswordText(entry.RconPassword)}",
            $"  (只读)RCON 地址 = {entry.RconAddress}",
        };

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>连接表表格：编号 / 名称 / ZXMM 地址 / 控制台模式 / RCON 地址。</summary>
    private static string BuildTable()
    {
        var lines = new List<string> { $"ZXMM 连接表({ZxmmRegistry.FileName}):" };
        lines.Add($"  {Pad("编号", 6)}{Pad("名称", 22)}{Pad("主机:端口", 22)}{Pad("控制台模式", 14)}RCON 地址");
        lines.Add("  " + new string('-', 84));

        foreach (var entry in ZxmmRegistry.All)
        {
            lines.Add($"  {Pad(entry.Id.ToString(CultureInfo.InvariantCulture), 6)}" +
                      $"{Pad(entry.Name, 22)}" +
                      $"{Pad($"{entry.Host}:{entry.Port}", 22)}" +
                      $"{Pad(ModeText(entry.ConsoleMode), 14)}" +
                      $"{entry.RconAddress}{(entry.RconPassword.Length == 0 ? "(密码空)" : "")}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>控制台模式的可读名。</summary>
    private static string ModeText(int mode)
        => mode == ZxmmRegistry.ConsoleModeRcon ? "RCON(1)" : "ZXMM桥接(0)";

    /// <summary>密码只报状态与位数，绝不回显原文。</summary>
    private static string PasswordText(string password)
        => password.Length == 0 ? "空" : $"{password.Length} 位(已隐藏)";

    // ---------- 校验 ----------

    /// <summary>文本类字段：不许带竖线（连接表是竖线分隔的，带进去会把一行拆坏）。</summary>
    /// <param name="field">正在改的键；传 null 时只做竖线检查（二级流程里的输入）。</param>
    /// <param name="text">已 Trim 的值。</param>
    private static bool CheckText(Field? field, string text)
    {
        if (text.Contains('|'))
        {
            Logger.Log(LogLevel.Warning, $"[ZXMM] 值里不能有竖线 |(连接表用竖线分段),这次没改");
            return false;
        }

        if (field is not null && text.Length == 0)
        {
            Logger.Log(LogLevel.Warning, $"[ZXMM] {field.Key}({field.Cn})不能留空");
            return false;
        }

        return true;
    }

    /// <summary>端口必须是 1-65535 的十进制整数。</summary>
    private static bool TryParsePort(string text, out int port)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
           && port is >= 1 and <= 65535;

    private static bool IsExit(string text)
        => text.Equals("exitc", StringComparison.OrdinalIgnoreCase);

    /// <summary>按显示宽度算字符串占几列（中文算 2 列）。</summary>
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
}
