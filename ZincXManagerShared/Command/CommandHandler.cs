using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using ZincXManagerShared.FileIO;
using ZincXManagerShared.Logging;

namespace ZincXManagerShared.Command;

/// <summary>
/// 控制台命令处理器：支持分支命令。
/// file 分支的命令名直接对应 ZFile 的方法名（全小写，无驼峰），
/// 参数顺序和方法签名一致，每个命令只调用一次对应方法。
/// 所有 file 命令共用一个 ZFile 实例。
/// 日志等级：
///   Info    —— 正常反馈
///   Warning —— 不造成故障但是有问题，如输入参数错误
///   Error   —— 系统出错
/// </summary>
//创建指令流程
//位置RegisterBuiltInCommands 格式：Register("命令名", 处理方法, "用途", "用法");
//单个命令无分支写在顶层命令注释下，有分支写在底部 格式：private void 处理方法(string[] args) { ... }
public class CommandHandler
{
    private readonly Dictionary<string, Action<string[]>> _commands
        = new(StringComparer.OrdinalIgnoreCase);

    // 命令描述：命令名 -> 用途说明（help 列表用）
    private readonly Dictionary<string, string> _descriptions
        = new(StringComparer.OrdinalIgnoreCase);

    // 命令用法：命令名 -> 用法格式（help <命令> 用）
    private readonly Dictionary<string, string> _usages
        = new(StringComparer.OrdinalIgnoreCase);

    private Thread? _thread;
    private volatile bool _running;

    // file 分支共用的文件实例（整个 file 命令只用这一个）
    private readonly ZFile _file = new ZFile();

    public CommandHandler()
    {
        RegisterBuiltInCommands();
    }

    // ---------- 对外接口 ----------

    /// <summary>
    /// 注册命令。
    /// </summary>
    /// <param name="name">命令名（可带空格表示分支，如 "user add"）</param>
    /// <param name="handler">处理函数</param>
    /// <param name="description">用途说明（help 列表显示）</param>
    /// <param name="usage">用法格式（help &lt;命令&gt; 显示），空则默认取 name</param>
    public void Register(string name,
                         Action<string[]> handler,
                         string description = "",
                         string usage = "")
    {
        var key = name.Trim();
        _commands[key] = handler;
        _descriptions[key] = description;
        _usages[key] = string.IsNullOrWhiteSpace(usage) ? key : usage;
    }

    public void Start()
    {
        if (_running) return;

        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "CommandListener"
        };
        _thread.Start();

        Logger.Log(LogLevel.Info, "[Command] 命令监听已启动，输入 help 查看可用命令");
    }

    public void Stop() => _running = false;

    // ---------- 线程主循环 ----------

    private void Loop()
    {
        while (_running)
        {
            string? input = Console.ReadLine();
            if (input == null) break;

            input = input.Trim();
            if (input.Length == 0) continue;

            Logger.Log(LogLevel.Info, $"[Command] > {input}");

            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Dispatch(parts);
        }
    }

    // ---------- 命令分发：最长前缀匹配 ----------

    private void Dispatch(string[] parts)
    {
        for (int take = parts.Length; take >= 1; take--)
        {
            var key = string.Join(' ', parts[0..take]);

            if (_commands.TryGetValue(key, out var handler))
            {
                var args = parts.Length > take ? parts[take..] : Array.Empty<string>();
                try
                {
                    handler(args);
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Error,
                        $"[Command] 执行命令 '{key}' 出错: {ex.Message}");
                }
                return;
            }
        }

        var prefix = parts[0];
        var subs = _commands.Keys
            .Where(k => k.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (subs.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"'{prefix}' 的子命令:");
            foreach (var s in subs)
                sb.AppendLine("    " + s);
            Logger.Log(LogLevel.Warning, sb.ToString().TrimEnd('\r', '\n'));
        }
        else
        {
            Logger.Log(LogLevel.Warning, $"[Command] 未知命令: {prefix} 输入 help 查看可用命令");
        }
    }

    // ---------- 内置命令注册 ----------

    private void RegisterBuiltInCommands()
    {
        // 顶层
        Register("help", CmdHelp, "显示帮助",
                 "help [命令]");
        Register("exit", CmdExit, "退出程序",
                 "exit");
        Register("echo", CmdEcho, "回显文本",
                 "echo <文本>");
        Register("info", CmdInfo, "打印程序信息",
                 "info");

        // file 分支：命令名 = ZFile 方法名小写，参数顺序 = 方法签名
        Register("file fileoperate", CmdFileFileOperate,
                 "打开/创建/删除/替换文件",
                 "file fileoperate <fileName> <mode>   (mode: rwNFC | del | replaceFile)");
        Register("file closefile", CmdFileCloseFile,
                 "关闭当前文件",
                 "file closefile");
        Register("file readfiletxt", CmdFileReadFileTxt,
                 "读取当前文件为行列表",
                 "file readfiletxt");
        Register("file readfilemap", CmdFileReadFileMap,
                 "读取当前文件为键值对",
                 "file readfilemap");
        Register("file readfilebin", CmdFileReadFileBin,
                 "读取当前文件为二进制",
                 "file readfilebin");
        Register("file readdir", CmdFileReadDir,
                 "列出目录",
                 "file readdir <dirPath>");
        Register("file writefiletxt", CmdFileWriteFileTxt,
                 "追加写 / 从第 line 行覆盖写",
                 "file writefiletxt <txt> <endl> [line]\n        endl: true/false 或 1/0");
        Register("file writefilemap", CmdFileWriteFileMap,
                 "写键值对",
                 "file writefilemap <key> <value>");
        Register("file mapget", CmdFileMapGet,
                 "读一个键值",
                 "file mapget <key> [def]");
        Register("file txtget", CmdFileTxtGet,
                 "读第 n 行",
                 "file txtget <n>");

        // log 分支
        Register("log write", CmdLogWrite, "写一条日志（可指定等级）",
                 "log write [等级] <文本>    (等级: Debug|Trace|Info|Warning|Error|Fatal)");
        Register("log level", CmdLogLevel, "查看/切换日志等级门槛（低于此等级不输出）",
                 "log level [Debug|Trace|Info|Warning|Error|Fatal]");

        // user 分支
        Register("user add", CmdUserAdd, "添加用户",
                 "user add <用户名>");
        Register("user del", CmdUserDel, "删除用户",
                 "user del <用户名>");
        Register("user list", CmdUserList, "列出用户",
                 "user list");
    }

    // ---------- 顶层命令 ----------

    // help：无参列出全部；带参显示该命令的用法
    private void CmdHelp(string[] args)
    {
        // help <命令>：显示该命令用法
        if (args.Length > 0)
        {
            var name = string.Join(' ', args);

            // 精确匹配
            if (_commands.ContainsKey(name))
            {
                ShowCommandDetail(name);
                return;
            }

            // 前缀匹配（用户可能只写 "file"，想把所有 file 命令列出来）
            var matched = _commands.Keys
                .Where(k => k.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)
                         || k.Equals(name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k)
                .ToList();

            if (matched.Count == 1)
            {
                ShowCommandDetail(matched[0]);
                return;
            }
            if (matched.Count > 1)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"'{name}' 相关的命令:");
                foreach (var k in matched)
                {
                    var u = _usages.TryGetValue(k, out var us) ? us : k;
                    sb.AppendLine("    " + u);
                }
                Logger.Log(LogLevel.Info, sb.ToString().TrimEnd('\r', '\n'));
                return;
            }

            Logger.Log(LogLevel.Warning, $"未知命令: {name}");
            return;
        }

        // help 无参：列出全部
        var sbAll = new StringBuilder();
        sbAll.AppendLine("可用命令:");

        // 顶层命令
        var topLevel = _commands.Keys
            .Where(k => !k.Contains(' '))
            .OrderBy(k => k)
            .ToList();

        foreach (var k in topLevel)
        {
            var desc = _descriptions.TryGetValue(k, out var d) ? d : "";
            sbAll.AppendLine($"  {k,-20}{desc}");
        }

        // 分支命令，按前缀分组
        var branches = _commands.Keys
            .Where(k => k.Contains(' '))
            .GroupBy(k => k.Split(' ')[0])
            .OrderBy(g => g.Key);

        foreach (var g in branches)
        {
            sbAll.AppendLine($"  {g.Key} <子命令>:");
            foreach (var k in g.OrderBy(x => x))
            {
                var sub = k[(g.Key.Length + 1)..];
                var desc = _descriptions.TryGetValue(k, out var d) ? d : "";
                sbAll.AppendLine($"      {sub,-24}{desc}");
            }
        }

        sbAll.AppendLine();
        sbAll.AppendLine("输入 help <命令> 查看具体用法");

        Logger.Log(LogLevel.Info, sbAll.ToString().TrimEnd('\r', '\n'));
    }

    // 显示单个命令的详细信息
    private void ShowCommandDetail(string name)
    {
        var usage = _usages.TryGetValue(name, out var u) ? u : name;
        var desc = _descriptions.TryGetValue(name, out var d) ? d : "";

        var sb = new StringBuilder();
        sb.AppendLine($"命令: {name}");
        if (!string.IsNullOrEmpty(desc))
            sb.AppendLine($"用途: {desc}");
        sb.AppendLine($"用法: {usage}");

        Logger.Log(LogLevel.Info, sb.ToString().TrimEnd('\r', '\n'));
    }

    private void CmdExit(string[] args)
    {
        Logger.Log(LogLevel.Info, "[Command] 收到退出命令，正在退出...");
        Environment.Exit(0);
    }

    private void CmdEcho(string[] args)
    {
        Logger.Log(LogLevel.Info, string.Join(' ', args));
    }

    private void CmdInfo(string[] args)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"\n进程 ID    : {Environment.ProcessId}");
        sb.AppendLine($".NET 版本  : {Environment.Version}");
        sb.AppendLine($"当前目录   : {Environment.CurrentDirectory}");
        sb.AppendLine($"日志文件   : {ZFile.logF.CurrentFile}");
        sb.AppendLine($"当前文件   : {(_file.IsOpen ? _file.CurrentFile : "(未打开)")}");

        Logger.Log(LogLevel.Info, sb.ToString().TrimEnd('\r', '\n'));
    }

    // ---------- file 命令：方法一一映射 ----------

    private void CmdFileFileOperate(string[] args)
    {
        if (args.Length < 2)
        {
            Logger.Log(LogLevel.Warning, "用法: file fileoperate <fileName> <mode>");
            return;
        }

        var fileName = args[0];
        var mode = args[1];

        if (_file.FileOperate(fileName, mode))
            Logger.Log(LogLevel.Info, $"fileoperate 成功: {fileName} (mode={mode})");
        else
            Logger.Log(LogLevel.Error, $"fileoperate 失败: {fileName} (mode={mode})");
    }

    private void CmdFileCloseFile(string[] args)
    {
        if (_file.CloseFile())
            Logger.Log(LogLevel.Info, "closefile 成功");
    }

    private void CmdFileReadFileTxt(string[] args)
    {
        if (!RequireOpen()) return;

        var lines = _file.ReadFileTxt();
        if (lines.Count == 0)
        {
            Logger.Log(LogLevel.Info, "(空文件)");
            return;
        }
        Logger.Log(LogLevel.Info, string.Join('\n', lines));
    }

    private void CmdFileReadFileMap(string[] args)
    {
        if (!RequireOpen()) return;

        var map = _file.ReadFileMap();
        if (map.Count == 0)
        {
            Logger.Log(LogLevel.Info, "(空 map)");
            return;
        }

        var sb = new StringBuilder();
        foreach (var kv in map)
            sb.AppendLine($"{kv.Key} = {kv.Value}");
        Logger.Log(LogLevel.Info, sb.ToString().TrimEnd('\r', '\n'));
    }

    private void CmdFileReadFileBin(string[] args)
    {
        if (!RequireOpen()) return;

        if (!_file.TryReadFileBin(out var data))
        {
            Logger.Log(LogLevel.Error, "readfilebin 失败: 文件读取错误(详见日志)");
            return;
        }
        if (string.IsNullOrEmpty(data))
        {
            Logger.Log(LogLevel.Info, "(空文件)");
            return;
        }
        Logger.Log(LogLevel.Info, $"已读取 {data.Length} 字节");
    }

    private void CmdFileReadDir(string[] args)
    {
        if (args.Length < 1)
        {
            Logger.Log(LogLevel.Warning, "用法: file readdir <dirPath>");
            return;
        }

        var text = _file.ReadDir(args[0]);
        if (string.IsNullOrEmpty(text))
        {
            Logger.Log(LogLevel.Warning, $"(空目录或不存在: {args[0]})");
            return;
        }
        Logger.Log(LogLevel.Info, text.TrimEnd('\r', '\n'));
    }

    private void CmdFileWriteFileTxt(string[] args)
    {
        if (!RequireOpen()) return;

        if (args.Length == 2)
        {
            var txt = args[0];
            if (!TryParseBool(args[1], out var endl))
            {
                Logger.Log(LogLevel.Warning, "endl 必须是 true/false 或 1/0");
                return;
            }
            if (_file.WriteFileTxt(txt, endl))
                Logger.Log(LogLevel.Info, $"writefiletxt 成功 (txt={txt}, endl={endl})");
            else
                Logger.Log(LogLevel.Error, "writefiletxt 失败");
        }
        else if (args.Length == 3)
        {
            var txt = args[0];
            if (!TryParseBool(args[1], out var endl))
            {
                Logger.Log(LogLevel.Warning, "endl 必须是 true/false 或 1/0");
                return;
            }
            if (!int.TryParse(args[2], out var line) || line < 1)
            {
                Logger.Log(LogLevel.Warning, "line 必须是 >=1 的整数");
                return;
            }
            if (_file.WriteFileTxt(txt, endl, line))
                Logger.Log(LogLevel.Info, $"writefiletxt 成功 (txt={txt}, endl={endl}, line={line})");
            else
                Logger.Log(LogLevel.Error, "writefiletxt 失败");
        }
        else
        {
            Logger.Log(LogLevel.Warning, "用法: file writefiletxt <txt> <endl> [line]");
        }
    }

    private void CmdFileWriteFileMap(string[] args)
    {
        if (!RequireOpen()) return;
        if (args.Length < 2)
        {
            Logger.Log(LogLevel.Warning, "用法: file writefilemap <key> <value>");
            return;
        }

        var key = args[0];
        var value = string.Join(' ', args[1..]);

        if (_file.WriteFileMap(key, value))
            Logger.Log(LogLevel.Info, $"writefilemap 成功 ({key}={value})");
        else
            Logger.Log(LogLevel.Error, "writefilemap 失败");
    }

    private void CmdFileMapGet(string[] args)
    {
        if (!RequireOpen()) return;
        if (args.Length < 1)
        {
            Logger.Log(LogLevel.Warning, "用法: file mapget <key> [def]");
            return;
        }

        var key = args[0];
        var def = args.Length > 1 ? string.Join(' ', args[1..]) : "";

        var v = _file.MapGet(key, def);
        Logger.Log(LogLevel.Info, $"{key} = {v}");
    }

    private void CmdFileTxtGet(string[] args)
    {
        if (!RequireOpen()) return;
        if (args.Length < 1 || !int.TryParse(args[0], out var n) || n < 1)
        {
            Logger.Log(LogLevel.Warning, "用法: file txtget <n>");
            return;
        }

        var text = _file.TxtGet(n);
        if (string.IsNullOrEmpty(text))
            Logger.Log(LogLevel.Warning, $"第 {n} 行为空或不存在");
        else
            Logger.Log(LogLevel.Info, text);
    }

    // ---------- 辅助 ----------

    private bool RequireOpen()
    {
        if (!_file.IsOpen)
        {
            Logger.Log(LogLevel.Warning, "当前没有打开的文件，请先 file fileoperate <fileName> rwNFC");
            return false;
        }
        return true;
    }

    private static bool TryParseBool(string s, out bool result)
    {
        if (bool.TryParse(s, out result)) return true;
        if (s == "1") { result = true; return true; }
        if (s == "0") { result = false; return true; }
        result = false;
        return false;
    }

    private static bool TryParseLevel(string s, out LogLevel level)
    {
        // 只接受明确的等级名，不接受随便的字符串
        return Enum.TryParse<LogLevel>(s, ignoreCase: true, out level)
               && Enum.IsDefined(typeof(LogLevel), level);
    }

    // ---------- 分支：log ----------

    private void CmdLogWrite(string[] args)
    {
        if (args.Length < 1)
        {
            Logger.Log(LogLevel.Warning,
                "用法: log write [等级] <文本>    (等级: Debug|Trace|Info|Warning|Error|Fatal)");
            return;
        }

        // 默认等级 Info
        var level = LogLevel.Info;
        var textStart = 0;

        // 第一个参数如果是合法等级，就当作等级，剩余部分当文本
        if (args.Length >= 2 && TryParseLevel(args[0], out var lv))
        {
            level = lv;
            textStart = 1;
        }

        var text = string.Join(' ', args[textStart..]);
        if (string.IsNullOrEmpty(text))
        {
            Logger.Log(LogLevel.Warning,
                "用法: log write [等级] <文本>");
            return;
        }

        Logger.Log(level, "[用户] " + text);
    }

    private void CmdLogLevel(string[] args)
    {
        if (args.Length < 1)
        {
            Logger.Log(LogLevel.Info,
                $"当前日志等级门槛: {Logger.GetName(Logger.GetLevel())}");
            Logger.Log(LogLevel.Info,
                "用法: log level <Debug|Trace|Info|Warning|Error|Fatal>");
            return;
        }

        if (TryParseLevel(args[0], out var lv))
        {
            Logger.SetLevel(lv);
            Logger.Log(LogLevel.Info, $"日志等级门槛已切换为: {Logger.GetName(lv)}");
        }
        else
        {
            Logger.Log(LogLevel.Warning,
                "无效等级，可选: Debug/Trace/Info/Warning/Error/Fatal");
        }
    }

    // ---------- 分支：user ----------

    private static readonly List<string> _users = new();

    private void CmdUserAdd(string[] args)
    {
        if (args.Length < 1)
        {
            Logger.Log(LogLevel.Warning, "用法: user add <用户名>");
            return;
        }
        if (_users.Contains(args[0]))
        {
            Logger.Log(LogLevel.Warning, $"用户已存在: {args[0]}");
            return;
        }
        _users.Add(args[0]);
        Logger.Log(LogLevel.Info, $"已添加用户: {args[0]}");
    }

    private void CmdUserDel(string[] args)
    {
        if (args.Length < 1)
        {
            Logger.Log(LogLevel.Warning, "用法: user del <用户名>");
            return;
        }
        if (_users.Remove(args[0]))
            Logger.Log(LogLevel.Info, $"已删除用户: {args[0]}");
        else
            Logger.Log(LogLevel.Warning, $"用户不存在: {args[0]}");
    }

    private void CmdUserList(string[] args)
    {
        if (_users.Count == 0)
        {
            Logger.Log(LogLevel.Info, "(无用户)");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("用户列表:");
        foreach (var u in _users)
            sb.AppendLine("  " + u);
        Logger.Log(LogLevel.Info, sb.ToString().TrimEnd('\r', '\n'));
    }
}