using System;  // 基础类型
using System.Collections.Generic;  // 返回列表
using System.Globalization;  // 解析日期时间
using System.IO;  // 读写文件
using System.Linq;  // 过滤
using System.Text;  // UTF-8
using ZincXManagerShared.Logging;  // 共享库

namespace ZincXManagerShared.History;

/// <summary>一条玩家返回记录。</summary>
public sealed class MegBackEntry
{
    /// <summary>发生时间。</summary>
    public DateTime At { get; init; }

    /// <summary>卡片对应的消息号(拿不到是 0)。</summary>
    public int MessageId { get; init; }

    /// <summary>造成这次返回的目标(公告 = 来源标签;推送 = @all / 玩家名 / @clients:子服)。</summary>
    public string Target { get; init; } = "";

    /// <summary>点卡片的玩家账号 ID。</summary>
    public int PlayerId { get; init; }

    /// <summary>玩家返回的内容(按钮文案,填空卡片带上填的内容)。</summary>
    public string Content { get; init; } = "";
}

/// <summary>
/// 玩家返回记录:玩家在卡片上点了什么,存到 <c>Data/MegBack.txt</c>。
/// <para>一行一条,格式:<c>[年/月/日][时/分/秒][消息ID][造成这次返回的目标][玩家ID] 返回内容</c>。</para>
/// <para>例:<c>[2026/10/24][15:45:12][12][@all][3] 是</c></para>
/// </summary>
public static class CardResponses
{
    /// <summary>记录文件(相对服务端工作目录)。</summary>
    public const string FilePath = "Data/MegBack.txt";

    // 保护文件写入的锁
    private static readonly object _lock = new();

    /// <summary>追加一条玩家返回。</summary>
    public static void Append(DateTime at, int messageId, string target, int playerId, string content)
    {
        var line = $"[{at:yyyy/MM/dd}][{at:HH:mm:ss}][{messageId}][{target}][{playerId}] {content}";

        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[消息] 写玩家返回记录失败: " + ex.Message);
            }
        }
    }

    /// <summary>读全部记录(格式不对的旧行跳过)。</summary>
    public static List<MegBackEntry> Load()
    {
        var list = new List<MegBackEntry>();

        lock (_lock)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return list;
                }

                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    if (TryParseLine(line, out var entry))
                    {
                        list.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[消息] 读玩家返回记录失败: " + ex.Message);
            }
        }

        return list;
    }

    /// <summary>解析一行;格式不对返回 false。</summary>
    public static bool TryParseLine(string line, out MegBackEntry entry)
    {
        entry = new MegBackEntry();

        if (string.IsNullOrWhiteSpace(line) || line[0] != '[')
        {
            return false;
        }

        var parts = line.Split(']', 6);   //拆分结果
        if (parts.Length < 6)
        {
            return false;
        }

        // parts: "[日期" "[时间" "[消息ID" "[目标" "[玩家ID" " 内容"
        var dateText = parts[0].TrimStart('[');
        var timeText = parts[1].TrimStart('[');
        var idText = parts[2].TrimStart('[');
        var target = parts[3].TrimStart('[');
        var playerText = parts[4].TrimStart('[');
        var content = parts[5].TrimStart(' ');

        if (!DateTime.TryParse($"{dateText} {timeText}", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var at))
        {
            return false;
        }

        entry = new MegBackEntry
        {
            At = at,
            MessageId = int.TryParse(idText, out var id) ? id : 0,
            Target = target,
            PlayerId = int.TryParse(playerText, out var pid) ? pid : 0,
            Content = content,
        };

        return true;
    }

    /// <summary>最新 n 条(按时间倒序取前 n,再按时间正序回来)。</summary>
    public static List<MegBackEntry> Latest(int n)
        => Load().OrderByDescending(e => e.At).Take(n).OrderBy(e => e.At).ToList();
}
