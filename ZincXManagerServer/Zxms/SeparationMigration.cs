using System;                                     // Console / Environment / StringComparison
using System.Collections.Generic;                 // List / IReadOnlyList
using System.Globalization;                       // CultureInfo
using System.IO;                                  // Path / File / Directory / FileInfo / FileStream
using System.Text;                                // StringBuilder
using ZincXManagerShared.FileIO;                  // ZFile
using ZincXManagerShared.Logging;                 // Logger / LogLevel

namespace ZincXManager.Server.Zxms;

/// <summary>
/// 数据分离迁移：拿上次的状态快照和本次配置比对，只对"新增分离"和"移出分离"的数据动手。
/// 0→1 时把共享区文件复制进 Data/SS-编号/，1→0 时把分区文件复制回共享区，复制不删原件。
/// </summary>
internal static class SeparationMigration
{
    /// <summary>迁移状态快照路径，记录上次生效的开关与键号列表。</summary>
    public const string StateFile = "Data/separation-state.ini";

    /// <summary>分区目录的父目录，分区目录形如 Data/SS-2。</summary>
    private const string PartitionRoot = "Data";

    private const string KeyEnabled = "enabled";   //快照里的总开关键名
    private const string KeyKeys = "keys";         //快照里的键号列表键名

    /// <summary>扫描到的一份分区副本。</summary>
    private sealed class PartitionCopy
    {
        /// <summary>分区目录里的服务器编号。</summary>
        public int ServerId { get; init; }

        /// <summary>副本文件路径。</summary>
        public string Path { get; init; } = "";

        /// <summary>账号数，读不到时是 ?。</summary>
        public string Accounts { get; init; } = "?";
    }

    /// <summary>
    /// 比对状态快照与当前配置，执行本次新增/移出的迁移，最后写回快照。
    /// </summary>
    /// <param name="config">已打开的 config.ini 句柄，仅在配置尚未加载时用于补加载。</param>
    public static void Run(ZFile config)
    {
        // 列服务器要从登记表来；Program 若漏调 Servers.Load，这里补一次，免得静默不迁移。
        if (Servers.All.Count == 0)
        {
            Logger.Log(LogLevel.Warning, $"[数据分离] 登记表还没载入 → 先读一次 {Servers.FileName}");
            Servers.Load();
        }

        if (DataSeparation.KeyTable.Count == 0)
        {
            Logger.Log(LogLevel.Warning, "[数据分离] 分离配置还没载入 → 先补一次 DataSeparation.Load");
            DataSeparation.Load(config);
        }

        var currentEnabled = DataSeparation.Enabled;
        var currentKeys = DataSeparation.SeparatedKeys;

        var state = ReadState();
        if (!state.Ok)
        {
            // 首次运行没有可比对的历史，只落快照：此时共享区文件按现状继续共享，不打扰运维。
            WriteState(currentEnabled, currentKeys);
            Logger.Log(LogLevel.Info, $"[数据分离] 首次启动（没有快照）→ 只记录当前状态 {Describe(currentEnabled, currentKeys)}，不迁移");
            return;
        }

        var added = new List<int>();       //新增分离:共享区要复制到分区
        var removed = new List<int>();     //移出分离:分区要复制回共享区

        if (!state.Enabled && currentEnabled)
        {
            added.AddRange(currentKeys);
            Logger.Log(LogLevel.Info, $"[数据分离] 总开关由 0 改为 1 → {added.Count} 个键要从共享区迁到分区");
        }
        else if (state.Enabled && !currentEnabled)
        {
            removed.AddRange(state.Keys);
            Logger.Log(LogLevel.Info, $"[数据分离] 总开关由 1 改为 0 → {removed.Count} 个键要从分区合回共享区");
        }
        else if (state.Enabled && currentEnabled)
        {
            foreach (var key in currentKeys)
            {
                if (!state.Keys.Contains(key))
                {
                    added.Add(key);
                }
            }

            foreach (var key in state.Keys)
            {
                if (!ContainsKey(currentKeys, key))
                {
                    removed.Add(key);
                }
            }

            if (added.Count > 0 || removed.Count > 0)
            {
                Logger.Log(LogLevel.Info, $"[数据分离] 分离键有变化 → 新增 [{string.Join(",", added)}]，移出 [{string.Join(",", removed)}]");
            }
        }

        if (added.Count == 0 && removed.Count == 0)
        {
            Logger.Log(LogLevel.Info, $"[数据分离] 分离状态与上次一致（{Describe(currentEnabled, currentKeys)}）→ 不需要迁移");
            return;
        }

        if (added.Count > 0 && Servers.All.Count == 0)
        {
            // 不写快照：等登记表补齐后下次启动仍能重问，不会把这次机会吞掉。
            Logger.Log(LogLevel.Warning, $"[数据分离] 有 {added.Count} 个键要迁到分区，但登记表 {Servers.FileName} 里没有一台服务器 → 本次不迁移，补好登记表后重启再试");
            return;
        }

        foreach (var key in added)
        {
            MigrateToServer(key);
        }

        foreach (var key in removed)
        {
            MigrateToShared(key);
        }

        WriteState(currentEnabled, currentKeys);
        Logger.Log(LogLevel.Info, $"[数据分离] 迁移检查完成 → 迁入分区 {added.Count} 个键，合回共享区 {removed.Count} 个键，快照已更新");
    }

    /// <summary>
    /// 0→1：共享区文件存在且非空时，问归到哪台服务器，再复制到该服务器分区。
    /// </summary>
    private static void MigrateToServer(int key)
    {
        if (!DataSeparation.KeyTable.TryGetValue(key, out var dataName))
        {
            Logger.Log(LogLevel.Warning, $"[数据分离] 键 {key} 不在键表 {DataSeparation.KeyTableFile} 里 → 无法定位数据，未迁移");
            return;
        }

        var sharedPath = DataSeparation.FileOf(dataName);
        if (sharedPath.Length == 0)
        {
            Logger.Log(LogLevel.Info, $"[数据分离] 键 {key}({dataName}) 本期没有独立数据文件 → 不需要迁移");
            return;
        }

        if (!IsUsableFile(sharedPath))
        {
            Logger.Log(LogLevel.Info, $"[数据分离] 共享区 {sharedPath} 不存在或为空 → 键 {key}({dataName}) 不需要迁移");
            return;
        }

        var prompt = new StringBuilder();
        prompt.Append("原来共享的 ").Append(sharedPath).Append(" 要归到哪台服务器？").Append(Environment.NewLine);
        foreach (var item in Servers.All)
        {
            prompt.Append("  ").Append(item.Id).Append(' ').Append(item.Name).Append(Environment.NewLine);
        }

        prompt.Append("  输入服务器编号，直接回车 = 不迁移");

        var answer = Ask(prompt.ToString());
        if (answer.Length == 0)
        {
            Logger.Log(LogLevel.Warning, $"[数据分离] 没有选择服务器 → 共享区 {sharedPath} 保持原状，未迁移");
            return;
        }

        if (!int.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var serverId) || !Servers.TryGet(serverId, out var entry))
        {
            Logger.Log(LogLevel.Warning, $"[数据分离] 输入的服务器编号「{answer}」不在登记表 {Servers.FileName} 里 → 共享区 {sharedPath} 保持原状，未迁移");
            return;
        }

        Copy(sharedPath, PartitionPath(sharedPath, entry.Id));
    }

    /// <summary>
    /// 1→0：扫分区副本；0 份不动，1 份直接复制回共享区，多份问保留哪一份。
    /// </summary>
    private static void MigrateToShared(int key)
    {
        if (!DataSeparation.KeyTable.TryGetValue(key, out var dataName))
        {
            Logger.Log(LogLevel.Warning, $"[数据分离] 键 {key} 已不在键表 {DataSeparation.KeyTableFile} 里 → 无法定位它的文件，未合并");
            return;
        }

        var sharedPath = DataSeparation.FileOf(dataName);
        if (sharedPath.Length == 0)
        {
            Logger.Log(LogLevel.Info, $"[数据分离] 键 {key}({dataName}) 本期没有独立数据文件 → 不需要合并");
            return;
        }

        var fileName = Path.GetFileName(sharedPath);
        var copies = ScanPartitions(fileName);
        if (copies.Count == 0)
        {
            Logger.Log(LogLevel.Info, $"[数据分离] 没有找到 {fileName} 的分区副本 → 键 {key}({dataName}) 不需要合并");
            return;
        }

        if (copies.Count == 1)
        {
            Copy(copies[0].Path, sharedPath);
            return;
        }

        var options = new List<string>();   //按提示编号顺序记可选路径,编号从 1 开始
        var prompt = new StringBuilder();
        prompt.Append(fileName).Append(" 在共享区和 ").Append(copies.Count).Append(" 个分区都有，保留哪一份？").Append(Environment.NewLine);

        if (IsUsableFile(sharedPath))
        {
            options.Add(sharedPath);
            prompt.Append("   ").Append(options.Count).Append(" 共享区 ").Append(sharedPath)
                .Append('(').Append(CountAccounts(sharedPath)).Append(" 个账号)").Append(Environment.NewLine);
        }

        foreach (var copy in copies)
        {
            options.Add(copy.Path);
            var name = Servers.TryGet(copy.ServerId, out var entry) ? entry.Name : "未登记";
            prompt.Append("   ").Append(options.Count).Append(' ').Append(name).Append(' ').Append(copy.Path)
                .Append('(').Append(copy.Accounts).Append(" 个账号)").Append(Environment.NewLine);
        }

        prompt.Append("  输入编号，直接回车 = 不合并");

        var answer = Ask(prompt.ToString());
        if (answer.Length == 0)
        {
            Logger.Log(LogLevel.Warning, $"[数据分离] 没有选择保留哪一份 → {fileName} 保持现状，未合并");
            return;
        }

        if (!int.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pick) || pick < 1 || pick > options.Count)
        {
            Logger.Log(LogLevel.Warning, $"[数据分离] 输入的编号「{answer}」不在 1-{options.Count} 之间 → {fileName} 保持现状，未合并");
            return;
        }

        var chosen = options[pick - 1];
        if (string.Equals(chosen, sharedPath, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Log(LogLevel.Info, $"[数据分离] 选中的就是共享区 {sharedPath} 本身 → 不需要复制");
            return;
        }

        Copy(chosen, sharedPath);
    }

    /// <summary>
    /// 扫 Data/SS-编号/ 下的同名文件，按服务器编号排序返回。
    /// </summary>
    private static List<PartitionCopy> ScanPartitions(string fileName)
    {
        var result = new List<PartitionCopy>();
        if (!Directory.Exists(PartitionRoot))
        {
            return result;
        }

        foreach (var dir in Directory.EnumerateDirectories(PartitionRoot, DataSeparation.ScopePrefix + "*"))
        {
            var name = Path.GetFileName(dir);
            // 只认 SS-<数字>：Data 下别的目录（备份、临时）不参与合并。
            if (!int.TryParse(name[DataSeparation.ScopePrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var serverId) || serverId <= 0)
            {
                Logger.Log(LogLevel.Warning, $"[数据分离] 目录 {dir} 名字不是 {DataSeparation.ScopePrefix}<编号> → 已忽略");
                continue;
            }

            var path = Path.Combine(dir, fileName);
            if (!IsUsableFile(path))
            {
                Logger.Log(LogLevel.Warning, $"[数据分离] 分区副本 {path} 不存在或为空 → 不作为合并来源");
                continue;
            }

            result.Add(new PartitionCopy { ServerId = serverId, Path = path, Accounts = CountAccounts(path) });
        }

        result.Sort(static (a, b) => a.ServerId.CompareTo(b.ServerId));   //按编号排,便于对照登记表核对
        return result;
    }

    /// <summary>
    /// 统计一份绑定表里的账号数（非空、非 # 行）；读不到时返回 ?。
    /// </summary>
    private static string CountAccounts(string path)
    {
        try
        {
            var count = 0;
            // 共享读：登录层可能正在写这个文件，不能把它挡住。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var text = line.Trim();
                if (text.Length == 0 || text[0] == '#')
                {
                    continue;
                }

                count++;
            }

            return count.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[数据分离] 统计 {path} 的账号数失败 → 记为 ?：{ex.Message}");
            return "?";
        }
    }

    /// <summary>
    /// 文件存在且非空才算可迁移，空文件迁过去只会覆盖出空数据。
    /// </summary>
    private static bool IsUsableFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[数据分离] 检查 {path} 是否可迁移失败 → 按不存在处理：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 复制文件并建好父目录，复制不删原件；失败只记日志不抛，避免一次迁移失败拖垮启动。
    /// </summary>
    private static void Copy(string src, string dst)
    {
        try
        {
            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(src, dst, true);
            Logger.Log(LogLevel.Info, $"[数据分离] 从 {src} 复制到 {dst}");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, $"[数据分离] 从 {src} 复制到 {dst} 失败 → {ex.Message}");
        }
    }

    /// <summary>
    /// 分区文件路径：Data/SS-编号/共享区文件名，与 DataSeparation.Scope 保持同一套规则。
    /// </summary>
    private static string PartitionPath(string sharedPath, int serverId)
        => Path.Combine(PartitionRoot, DataSeparation.ScopePrefix + serverId.ToString(CultureInfo.InvariantCulture), Path.GetFileName(sharedPath));

    /// <summary>
    /// 打印问题并读一行回答；没有可交互的 stdin 或读到 EOF 时返回空串，由调用方按"不迁移"处理。
    /// </summary>
    private static string Ask(string prompt)
    {
        Logger.Log(LogLevel.Info, "[数据分离] " + prompt);

        if (Console.IsInputRedirected)
        {
            Logger.Log(LogLevel.Warning, "[数据分离] 当前没有可交互的 stdin → 按默认处理，不迁移");
            return "";
        }

        string? answer;
        try
        {
            answer = Console.ReadLine();
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[数据分离] 读取输入失败 → 按默认处理，不迁移：{ex.Message}");
            return "";
        }

        if (answer == null)
        {
            Logger.Log(LogLevel.Warning, "[数据分离] 输入已结束（EOF）→ 按默认处理，不迁移");
            return "";
        }

        return answer.Trim();
    }

    /// <summary>
    /// 读状态快照；没有快照或读不出 enabled 时 Ok 为 false，调用方按首次启动处理。
    /// </summary>
    private static (bool Ok, bool Enabled, List<int> Keys) ReadState()
    {
        if (!File.Exists(StateFile))
        {
            return (false, false, new List<int>());
        }

        using var file = new ZFile();
        if (!file.FileOperate(StateFile, "rwNFC"))
        {
            Logger.Log(LogLevel.Warning, $"[数据分离] 打开状态快照 {StateFile} 失败 → 按首次启动处理，本次会重写快照");
            return (false, false, new List<int>());
        }

        var map = file.ReadFileMap();
        if (!map.TryGetValue(KeyEnabled, out var enabledText))
        {
            Logger.Log(LogLevel.Warning, $"[数据分离] 状态快照 {StateFile} 里没有 {KeyEnabled} → 按首次启动处理，本次会重写快照");
            return (false, false, new List<int>());
        }

        var text = enabledText.Trim();
        if (text != "0" && text != "1")
        {
            Logger.Log(LogLevel.Warning, $"[数据分离] 状态快照 {StateFile} 的 {KeyEnabled} 是「{text}」不是 0/1 → 按首次启动处理，本次会重写快照");
            return (false, false, new List<int>());
        }

        var keys = new List<int>();
        if (map.TryGetValue(KeyKeys, out var keysText))
        {
            foreach (var raw in keysText.Split(','))
            {
                var item = raw.Trim();
                if (item.Length == 0)
                {
                    continue;
                }

                if (int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var key) && key > 0)
                {
                    if (!keys.Contains(key))
                    {
                        keys.Add(key);
                    }
                }
                else
                {
                    Logger.Log(LogLevel.Warning, $"[数据分离] 状态快照 {StateFile} 里的键号「{item}」无效 → 已忽略");
                }
            }
        }

        return (true, text == "1", keys);
    }

    /// <summary>
    /// 写回状态快照：本次生效的开关与键号列表。
    /// </summary>
    private static void WriteState(bool enabled, IReadOnlyList<int> keys)
    {
        using var file = new ZFile();
        if (!file.FileOperate(StateFile, "rwNFC"))
        {
            Logger.Log(LogLevel.Error, $"[数据分离] 打开状态快照 {StateFile} 失败 → 下次启动仍按旧快照比对，可能重复询问");
            return;
        }

        file.WriteFileMap(KeyEnabled, enabled ? "1" : "0");
        file.WriteFileMap(KeyKeys, string.Join(",", keys));
        Logger.Log(LogLevel.Info, $"[数据分离] 状态快照已写入 {StateFile} → {Describe(enabled, keys)}");
    }

    private static bool ContainsKey(IReadOnlyList<int> keys, int key)
    {
        foreach (var item in keys)
        {
            if (item == key)
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(bool enabled, IReadOnlyList<int> keys)
        => (enabled ? "开启" : "关闭") + "，键 [" + (keys.Count == 0 ? "无" : string.Join(",", keys)) + "]";
}
