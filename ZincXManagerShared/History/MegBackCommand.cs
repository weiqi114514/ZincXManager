using System;  // 基础类型
using System.Collections.Generic;  // 结果列表
using System.Globalization;  // 日期解析
using System.Linq;  // 过滤
using ZincXManagerShared.Logging;  // 共享库

namespace ZincXManagerShared.History;

/// <summary>
/// <c>msg backget</c> 的实现:读 Data/MegBack.txt,按条件过滤后打成列表。
/// <para>用法:</para>
/// <list type="bullet">
///   <item><c>msg backget</c> —— 最新 50 条</item>
///   <item><c>meg backget d &lt;起&gt; &lt;止&gt;</c> —— 按日期,写 26/10/23 或 26/10 或 26</item>
///   <item><c>meg backget t &lt;起&gt; &lt;止&gt;</c> —— 按时刻,写 10/3/23(时/分/秒)</item>
///   <item><c>meg backget m &lt;消息ID&gt;</c> —— 这条消息的全部返回</item>
///   <item><c>meg backget p &lt;玩家ID或名称&gt;</c> —— 这个玩家的全部返回</item>
/// </list>
/// </summary>
public static class MegBackCommand
{
    /// <summary>不带参数时默认返回多少条(实际值从 config 的 backgetcount 读,这里是兜底)。</summary>
    public const int DefaultCount = 50;

    /// <summary>用法说明(报错和 help 都用它)。</summary>
    public const string Usage =
        "msg backget | msg backget d <起> <止> | msg backget t <起> <止> | msg backget m <消息ID> | msg backget p <玩家ID/名称>";

    /// <summary>
    /// 执行查询并把结果打到日志。
    /// </summary>
    /// <param name="args">backget 之后的参数。</param>
    /// <param name="titleOf">按消息号取卡片标题(取不到返回空)。</param>
    /// <param name="playerIdOf">按名称/ID 找玩家账号 ID(找不到返回 0)。</param>
    /// <returns>是否执行成功(参数不对返回 false)。</returns>
    public static bool Run(string[] args, Func<int, string> titleOf, Func<string, int> playerIdOf,
        Func<int, string> nameOf, int defaultCount = DefaultCount)
    {
        var rows = Query(args, playerIdOf);
        if (rows is null)
        {
            return false;
        }

        Render(rows, titleOf, nameOf);
        return true;
    }

    /// <summary>
    /// 只做过滤,不输出:参数不对返回 null(调用方负责报用法)。
    /// </summary>
    public static List<MegBackEntry>? Query(string[] args, Func<string, int> playerIdOf, int defaultCount = DefaultCount)
    {
        var all = CardResponses.Load();
        List<MegBackEntry> rows;

        if (args.Length == 0)
        {
            rows = all.OrderByDescending(e => e.At).Take(Math.Max(1, defaultCount)).OrderBy(e => e.At).ToList();
        }
        else
        {
            switch (args[0].ToLowerInvariant())
            {
                case "d":
                {
                    if (args.Length < 3 || !TryParseDate(args[1], false, out var from) ||
                        !TryParseDate(args[2], true, out var to))
                    {
                        Logger.Log(LogLevel.Warning, "[消息] 用法:" + Usage);
                        return null;
                    }

                    rows = all.Where(e => e.At >= from && e.At <= to).OrderBy(e => e.At).ToList();
                    break;
                }

                case "t":
                {
                    if (args.Length < 3 || !TryParseTime(args[1], out var t1) || !TryParseTime(args[2], out var t2))
                    {
                        Logger.Log(LogLevel.Warning, "[消息] 用法:" + Usage);
                        return null;
                    }

                    // 起 > 止 当作跨零点(比如 22:00 到 02:00)
                    rows = all.Where(e => t1 <= t2
                            ? e.At.TimeOfDay >= t1 && e.At.TimeOfDay <= t2
                            : e.At.TimeOfDay >= t1 || e.At.TimeOfDay <= t2)
                        .OrderBy(e => e.At).ToList();
                    break;
                }

                case "m":
                {
                    if (args.Length < 2 || !int.TryParse(args[1], out var messageId))
                    {
                        Logger.Log(LogLevel.Warning, "[消息] 用法:" + Usage);
                        return null;
                    }

                    rows = all.Where(e => e.MessageId == messageId).OrderBy(e => e.At).ToList();
                    break;
                }

                case "p":
                {
                    if (args.Length < 2)
                    {
                        Logger.Log(LogLevel.Warning, "[消息] 用法:" + Usage);
                        return null;
                    }

                    var playerId = playerIdOf(args[1].Trim());
                    if (playerId <= 0)
                    {
                        Logger.Log(LogLevel.Warning, $"[消息] 找不到玩家 {args[1]}");
                        return null;
                    }

                    rows = all.Where(e => e.PlayerId == playerId).OrderBy(e => e.At).ToList();
                    break;
                }

                default:
                    Logger.Log(LogLevel.Warning, "[消息] 用法:" + Usage);
                    return null;
            }
        }

        return rows;
    }

    /// <summary>把结果打成文本行(命令输出和 -os 回包都用它)。</summary>
    public static List<string> RenderLines(List<MegBackEntry> rows, Func<int, string> titleOf, Func<int, string> nameOf)
    {
        var lines = new List<string>();
        if (rows.Count == 0)
        {
            lines.Add("没有符合条件的记录");
            return lines;
        }

        lines.Add($"megback:{rows.Count} 条");
        lines.Add("日期/时间            消息ID  玩家            标题            目标                 返回");

        foreach (var e in rows)
        {
            var title = e.MessageId > 0 ? titleOf(e.MessageId) : "";
            if (string.IsNullOrEmpty(title))
            {
                title = "-";
            }

            var player = e.PlayerId > 0 ? nameOf(e.PlayerId) : "";
            if (string.IsNullOrEmpty(player))
            {
                player = e.PlayerId > 0 ? "#" + e.PlayerId : "-";
            }

            lines.Add(string.Format("{0:yyyy/MM/dd HH:mm:ss}  {1,-6}  {2,-14}  {3,-14}  {4,-20}  {5}",
                e.At, e.MessageId, Fit(player, 14), Fit(title, 14), Fit(e.Target, 20), e.Content));
        }

        return lines;
    }

    /// <summary>把结果打成一张对齐的表。</summary>
    private static void Render(List<MegBackEntry> rows, Func<int, string> titleOf, Func<int, string> nameOf)
    {
        if (rows.Count == 0)
        {
            Logger.Log(LogLevel.Info, "[消息] megback:没有符合条件的记录");
            return;
        }

        Logger.Log(LogLevel.Info, $"[消息] megback:{rows.Count} 条");
        Logger.Log(LogLevel.Info, "日期/时间            消息ID  玩家            标题            目标                 返回");

        foreach (var e in rows)
        {
            var title = e.MessageId > 0 ? titleOf(e.MessageId) : "";
            if (string.IsNullOrEmpty(title))
            {
                title = "-";
            }

            var player = e.PlayerId > 0 ? nameOf(e.PlayerId) : "";
            if (string.IsNullOrEmpty(player))
            {
                player = e.PlayerId > 0 ? "#" + e.PlayerId : "-";
            }

            Logger.Log(LogLevel.Info,
                string.Format("{0:yyyy/MM/dd HH:mm:ss}  {1,-6}  {2,-14}  {3,-14}  {4,-20}  {5}",
                    e.At, e.MessageId, Fit(player, 14), Fit(title, 14), Fit(e.Target, 20), e.Content));
        }
    }

    /// <summary>按宽度截断(中文按 1 个字符算,够用就行)。</summary>
    private static string Fit(string text, int width)
        => text.Length <= width ? text : text[..(width - 1)] + "…";

    /// <summary>解析日期:支持 26 / 26/10 / 26/10/23(两位年份补成 2000+)。</summary>
    private static bool TryParseDate(string text, bool isEnd, out DateTime at)
    {
        at = DateTime.MinValue;
        var parts = text.Trim().Replace('-', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var year))
        {
            return false;
        }

        if (year < 100)
        {
            year += 2000;
        }

        var month = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : (isEnd ? 12 : 1);
        var day = parts.Length > 2 && int.TryParse(parts[2], out var d) ? d : (isEnd ? 31 : 1);

        month = Math.Clamp(month, 1, 12);
        day = Math.Clamp(day, 1, DateTime.DaysInMonth(year, month));

        at = new DateTime(year, month, day, isEnd ? 23 : 0, isEnd ? 59 : 0, isEnd ? 59 : 0);
        return true;
    }

    /// <summary>解析时刻:时分秒,支持 10 / 10/3 / 10/3/23。</summary>
    private static bool TryParseTime(string text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        var parts = text.Trim().Replace(':', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var hour))
        {
            return false;
        }

        var minute = parts.Length > 1 && int.TryParse(parts[1], out var mi) ? mi : 0;
        var second = parts.Length > 2 && int.TryParse(parts[2], out var se) ? se : 0;

        time = new TimeSpan(Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59), Math.Clamp(second, 0, 59));
        return true;
    }
}
