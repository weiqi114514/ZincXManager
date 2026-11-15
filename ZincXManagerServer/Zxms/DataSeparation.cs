using System;                                     // StringComparison
using System.Collections.Generic;                 // Dictionary / List / IReadOnlyDictionary / IReadOnlyList
using System.Globalization;                       // CultureInfo / NumberStyles
using System.Text;                                // StringBuilder
using ZincXManagerShared.FileIO;                  // ZFile
using ZincXManagerShared.Logging;                 // Logger / LogLevel
using ZincXManagerShared.Login;                   // LoginStore

namespace ZincXManager.Server.Zxms;

/// <summary>
/// 数据分离开关：决定哪些数据（金币、绑定表等）按服务器分区存放，并提供键号↔数据名↔文件路径的映射。
/// 配置在 config.ini 里：data-separation=0/1，data-separation-list=键号逗号列表。
/// </summary>
internal static class DataSeparation
{
    /// <summary>config.ini 里的总开关键名，0=全部数据共享，1=按 data-separation-list 分离。</summary>
    public const string KeyEnabled = "data-separation";

    /// <summary>config.ini 里要分离的键号列表键名，逗号分隔，键号见 countdataseparationkey.ini。</summary>
    public const string KeyList = "data-separation-list";

    /// <summary>键号→数据名对照表文件名，与 config.ini 同级（进程工作目录）。</summary>
    public const string KeyTableFile = "countdataseparationkey.ini";

    /// <summary>分区目录前缀，分区目录形如 Data/SS-2。</summary>
    internal const string ScopePrefix = "SS-";

    /// <summary>键表内容缺失时写出去的七行默认键表：键号、数据名、中文说明。</summary>
    private static readonly (int Key, string Name, string Comment)[] DefaultKeys =
    {
        (1, "coin", "金币"),
        (2, "bank", "银行"),
        (3, "shop", "商店"),
        (4, "bind", "绑定"),
        (5, "warehouse", "仓库"),
        (6, "qqbind", "QQ 绑定"),
        (7, "organization", "组织"),
    };

    private static bool _enabled;                                          //总开关是否打开
    private static readonly Dictionary<int, string> _keyTable = new();      //键号→数据名
    private static readonly List<int> _separated = new();                   //当前要分离的键号,按配置顺序

    /// <summary>
    /// 数据分离总开关是否打开；配置文件写了 0/1 以外的值时按关闭处理。
    /// </summary>
    public static bool Enabled => _enabled;

    /// <summary>
    /// 键号→数据名对照表（来自 countdataseparationkey.ini）。
    /// </summary>
    public static IReadOnlyDictionary<int, string> KeyTable => _keyTable;

    /// <summary>
    /// 当前要分离的键号列表，已按键表校验过（不存在的键号不会出现在这里）。
    /// </summary>
    public static IReadOnlyList<int> SeparatedKeys => _separated;

    /// <summary>
    /// 从 config.ini 载入开关与键号列表，并载入键表；缺失的配置项按默认值写回。
    /// </summary>
    /// <param name="config">已打开的 config.ini 句柄。</param>
    public static void Load(ZFile config)
    {
        LoadEnabled(config);
        LoadKeyTable();
        LoadSeparatedKeys(config);

        Logger.Log(
            LogLevel.Info,
            $"[数据分离] 加载完成 → {(_enabled ? "开启" : "关闭")}，分离键 [{(_separated.Count == 0 ? "无" : string.Join(",", _separated))}]");
    }

    /// <summary>
    /// 这份数据是否要按服务器分区存放；总开关关闭时一律返回 false。
    /// </summary>
    /// <param name="dataName">键表里的数据名，如 coin、bind。</param>
    public static bool IsSeparated(string dataName)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(dataName))
        {
            return false;
        }

        var name = dataName.Trim();
        foreach (var pair in _keyTable)
        {
            // 只认当前列表里的键号；键表里有但没列进列表的数据仍然共享。
            if (_separated.Contains(pair.Key) && string.Equals(pair.Value, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 这份数据在共享区的文件路径（相对进程工作目录）；本期没有独立文件的数据返回空串。
    /// </summary>
    /// <param name="dataName">键表里的数据名，如 bind。</param>
    public static string FileOf(string dataName)
    {
        if (string.IsNullOrWhiteSpace(dataName))
        {
            return "";
        }

        // 路径取自登录层常量，避免这里再写一份字面量、两边改漏。
        if (string.Equals(dataName.Trim(), "bind", StringComparison.OrdinalIgnoreCase))
        {
            return LoginStore.BindFileOf("");
        }

        return "";
    }

    /// <summary>
    /// 这份数据在某台服务器下的分区目录名（如 SS-2）；未分离或编号非法时返回空串。
    /// </summary>
    /// <param name="dataName">键表里的数据名，如 bind。</param>
    /// <param name="serverId">服务器编号。</param>
    public static string Scope(string dataName, int serverId)
    {
        if (serverId <= 0 || !IsSeparated(dataName))
        {
            return "";
        }

        return ScopePrefix + serverId.ToString(CultureInfo.InvariantCulture);
    }

    private static void LoadEnabled(ZFile config)
    {
        var text = config.MapGet(KeyEnabled).Trim();
        if (text.Length == 0)
        {
            config.WriteFileMap(KeyEnabled, 0);
            _enabled = false;
            Logger.Log(LogLevel.Info, $"[数据分离] {KeyEnabled} 未配置 → 已写入默认值 0（全部数据共享）");
            return;
        }

        if (text == "0" || text == "1")
        {
            _enabled = text == "1";
            return;
        }

        _enabled = false;
        Logger.Log(LogLevel.Warning, $"[数据分离] {KeyEnabled} 的值「{text}」不是 0/1 → 按 0（全部数据共享）处理");
    }

    private static void LoadKeyTable()
    {
        _keyTable.Clear();
        using var file = new ZFile();
        if (!file.FileOperate(KeyTableFile, "rwNFC"))
        {
            FillDefaultKeyTable();
            Logger.Log(LogLevel.Error, $"[数据分离] 打开键表 {KeyTableFile} 失败 → 使用内置默认键表（1-7）");
            return;
        }

        var lines = file.ReadFileTxt();
        if (lines.Count == 0)
        {
            // 首次部署：文件刚被创建，写一份带中文注释的默认键表再按它载入。
            file.WriteFileTxt(BuildDefaultKeyTableText(), false);
            FillDefaultKeyTable();
            Logger.Log(LogLevel.Info, $"[数据分离] 键表 {KeyTableFile} 不存在 → 已生成默认键表（1=coin 2=bank 3=shop 4=bind 5=warehouse 6=qqbind 7=organization）");
            return;
        }

        var lineNumber = 0;   //物理行号,坏行报错时好定位
        foreach (var raw in lines)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                continue;
            }

            var pos = line.IndexOf('=');
            if (pos <= 0 || pos == line.Length - 1)
            {
                Logger.Log(LogLevel.Warning, $"[数据分离] {KeyTableFile} 第 {lineNumber} 行不是「键号=数据名」 → 已跳过：{line}");
                continue;
            }

            if (!int.TryParse(line[..pos].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var key) || key <= 0)
            {
                Logger.Log(LogLevel.Warning, $"[数据分离] {KeyTableFile} 第 {lineNumber} 行键号「{line[..pos].Trim()}」不是大于 0 的整数 → 已跳过：{line}");
                continue;
            }

            var name = line[(pos + 1)..].Trim();
            if (name.Length == 0)
            {
                Logger.Log(LogLevel.Warning, $"[数据分离] {KeyTableFile} 第 {lineNumber} 行数据名为空 → 已跳过：{line}");
                continue;
            }

            if (_keyTable.ContainsKey(key))
            {
                Logger.Log(LogLevel.Warning, $"[数据分离] {KeyTableFile} 第 {lineNumber} 行键号 {key} 与前面的行重复 → 保留第一条，已跳过：{line}");
                continue;
            }

            _keyTable.Add(key, name);
        }

        if (_keyTable.Count == 0)
        {
            FillDefaultKeyTable();
            Logger.Log(LogLevel.Error, $"[数据分离] 键表 {KeyTableFile} 里没有一条有效行 → 使用内置默认键表（1-7）");
            return;
        }

        Logger.Log(LogLevel.Info, $"[数据分离] 读取键表 {KeyTableFile} → {_keyTable.Count} 个键");
    }

    private static void LoadSeparatedKeys(ZFile config)
    {
        _separated.Clear();
        var map = config.ReadFileMap();

        var hasList = map.ContainsKey(KeyList);
        var listText = hasList ? map[KeyList] : "";
        if (!hasList)
        {
            config.WriteFileMap(KeyList, "");
            Logger.Log(LogLevel.Info, $"[数据分离] {KeyList} 未配置 → 已写入空值（不分离任何数据）");
        }

        var dropped = 0;   //被剔除的项数,决定要不要回写干净值
        foreach (var raw in listText.Split(','))
        {
            var text = raw.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var key) || key <= 0)
            {
                dropped++;
                Logger.Log(LogLevel.Error, $"[数据分离] {KeyList} 里的「{text}」不是键号 → 已剔除");
                continue;
            }

            if (!_keyTable.ContainsKey(key))
            {
                dropped++;
                Logger.Log(LogLevel.Error, $"[数据分离] {KeyList} 里的键号 {key} 在键表 {KeyTableFile} 里不存在 → 已剔除");
                continue;
            }

            if (_separated.Contains(key))
            {
                dropped++;   //重复项不算错,但同样要去掉,回写时不保留
                continue;
            }

            _separated.Add(key);
        }

        if (dropped == 0)
        {
            return;
        }

        var clean = string.Join(",", _separated);
        if (config.WriteFileMap(KeyList, clean))
        {
            Logger.Log(LogLevel.Warning, $"[数据分离] {KeyList} 有 {dropped} 个无效项 → 已回写干净值「{clean}」");
        }
        else
        {
            Logger.Log(LogLevel.Error, $"[数据分离] {KeyList} 回写干净值失败 → 本次只按内存里的 {_separated.Count} 个键生效");
        }
    }

    private static void FillDefaultKeyTable()
    {
        foreach (var item in DefaultKeys)
        {
            _keyTable[item.Key] = item.Name;
        }
    }

    private static string BuildDefaultKeyTableText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 数据分离键表：一行一个键，格式 键号=数据名");
        builder.AppendLine("# 要在 config.ini 的 data-separation-list 里填键号（逗号分隔，如 1,4），键号的说明见每行上一行注释");
        foreach (var item in DefaultKeys)
        {
            builder.Append("# ").Append(item.Key).Append(" = ").AppendLine(item.Comment);
            builder.Append(item.Key.ToString(CultureInfo.InvariantCulture)).Append('=').AppendLine(item.Name);
        }

        //保留 AppendLine 给出的末尾换行,配合 WriteFileTxt(..., false) 让整份文件统一用 CRLF
        return builder.ToString();
    }
}
