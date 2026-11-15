using System;                                // Array / Environment / Exception
using System.Collections.Generic;            // List / IReadOnlyList
using System.Globalization;                  // CultureInfo / NumberStyles
using ZincXManagerShared.FileIO;             // ZFile
using ZincXManagerShared.Logging;            // Logger / LogLevel

namespace ZincXManager.Server.Zxms;

/// <summary>
/// 一台登记在册的服务器：编号、显示名、接入端口与专用通道令牌。
/// </summary>
internal sealed class ServerEntry
{
    /// <summary>服务器编号，大于 0 且全局唯一，客户端与运维命令都用它指名服务器。</summary>
    public int Id { get; init; }

    /// <summary>服务器显示名称，日志与弹窗里给人看。</summary>
    public string Name { get; init; } = "";

    /// <summary>该服 ZXMS 接入端口（1-65535）。</summary>
    public int Port { get; init; }

    /// <summary>ZXMS↔ZXMM 专用通道令牌，空串表示该服不启用专用通道。</summary>
    public string Token { get; init; } = "";
}

/// <summary>
/// 服务器登记表 Data/Servers.txt 的读取与查询；表只在启动时载入一次，运行期只读。
/// </summary>
internal static class Servers
{
    /// <summary>登记表路径，格式一行一台：编号|名称|端口|令牌。</summary>
    public const string FileName = "Data/Servers.txt";

    private static readonly string Header = string.Join(
        Environment.NewLine,
        "# 服务器登记表：一行一台，格式 编号|名称|端口|令牌",
        "# 编号大于 0 且唯一；端口 1-65535；令牌可留空（留空则不启用专用通道）",
        "# 以 # 开头的行为注释") + Environment.NewLine;

    private static IReadOnlyList<ServerEntry> _all = Array.Empty<ServerEntry>();   //已载入的登记表(不可变)

    /// <summary>
    /// 全部已登记的服务器，按文件里的顺序排列；尚未载入时为空。
    /// </summary>
    public static IReadOnlyList<ServerEntry> All => _all;

    /// <summary>
    /// 按编号查一台服务器。
    /// </summary>
    /// <param name="id">服务器编号。</param>
    /// <param name="entry">查到时为对应登记项，查不到为 null。</param>
    /// <returns>查到返回 true。</returns>
    public static bool TryGet(int id, out ServerEntry entry)
    {
        foreach (var item in _all)
        {
            if (item.Id == id)
            {
                entry = item;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    /// <summary>
    /// 登记表里的第一台服务器；没有任何登记时为 null。单服部署下它就是唯一那台。
    /// </summary>
    public static ServerEntry? Default => _all.Count > 0 ? _all[0] : null;

    /// <summary>
    /// 读取登记表；表不存在时只写注释头，格式错误的行跳过并记 Warn 日志。
    /// </summary>
    public static void Load()
    {
        var list = new List<ServerEntry>();

        try
        {
            using var file = new ZFile();
            if (!file.FileOperate(FileName, "rwNFC"))
            {
                _all = Array.Empty<ServerEntry>();
                Logger.Log(LogLevel.Error, $"[DATA] 打开登记表 {FileName} 失败 → 按没有登记服务器处理");
                return;
            }

            var lines = file.ReadFileTxt();
            if (lines.Count == 0)
            {
                // 首次部署：表是刚创建的空文件，写注释头让运维知道该填什么，用完再关。
                file.WriteFileTxt(Header, false);
                _all = Array.Empty<ServerEntry>();
                Logger.Log(LogLevel.Info, $"[DATA] 登记表 {FileName} 不存在或为空 → 已写入注释头，请按「编号|名称|端口|令牌」登记服务器");
                return;
            }

            var lineNumber = 0;   //文件里的物理行号,报错时好定位
            foreach (var raw in lines)
            {
                lineNumber++;
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var parts = line.Split('|');
                if (parts.Length < 3 || parts.Length > 4)
                {
                    Logger.Log(LogLevel.Warning, $"[DATA] {FileName} 第 {lineNumber} 行字段数 {parts.Length} 不是 3-4 段 → 已跳过：{line}");
                    continue;
                }

                if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
                {
                    Logger.Log(LogLevel.Warning, $"[DATA] {FileName} 第 {lineNumber} 行编号「{parts[0].Trim()}」不是大于 0 的整数 → 已跳过：{line}");
                    continue;
                }

                if (!int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port < 1 || port > 65535)
                {
                    Logger.Log(LogLevel.Warning, $"[DATA] {FileName} 第 {lineNumber} 行端口「{parts[2].Trim()}」不在 1-65535 → 已跳过：{line}");
                    continue;
                }

                var name = parts[1].Trim();
                if (name.Length == 0)
                {
                    Logger.Log(LogLevel.Warning, $"[DATA] {FileName} 第 {lineNumber} 行名称为空 → 已跳过：{line}");
                    continue;
                }

                var duplicated = false;
                foreach (var item in list)
                {
                    if (item.Id == id)
                    {
                        duplicated = true;
                        break;
                    }
                }

                if (duplicated)
                {
                    Logger.Log(LogLevel.Warning, $"[DATA] {FileName} 第 {lineNumber} 行编号 {id} 与前面的登记重复 → 保留第一条，已跳过：{line}");
                    continue;
                }

                var token = parts.Length == 4 ? parts[3].Trim() : "";   //令牌可以整段省略
                list.Add(new ServerEntry { Id = id, Name = name, Port = port, Token = token });
            }
        }
        catch (Exception ex)
        {
            _all = Array.Empty<ServerEntry>();
            Logger.Log(LogLevel.Error, $"[DATA] 读取登记表 {FileName} 异常 → 按没有登记服务器处理：{ex.Message}");
            return;
        }

        _all = list.AsReadOnly();
        if (list.Count == 0)
        {
            Logger.Log(LogLevel.Warning, $"[DATA] 读取登记表 {FileName} → 0 台，请按「编号|名称|端口|令牌」补登");
        }
        else
        {
            Logger.Log(LogLevel.Info, $"[DATA] 读取登记表 {FileName} → {list.Count} 台，默认 {list[0].Id} {list[0].Name} 端口 {list[0].Port}");
        }
    }
}
