using System;  // 基础类型
using System.IO;  // 写文件
using System.Text;  // UTF-8
using ZincXManagerShared.Logging;  // 共享库

namespace ZincXManagerShared.History;

/// <summary>
/// 玩家返回记录:玩家在卡片上点了什么,单独存到 <c>Data/MegBack.txt</c>。
/// <para>一行一条,格式:<c>[年/月/日][时/分/秒][消息ID][造成这次返回的目标] 返回内容</c>。</para>
/// <para>例:<c>[2026/10/24][15:45:12][12][@all] 是</c></para>
/// </summary>
public static class CardResponses
{
    /// <summary>记录文件(相对服务端工作目录)。</summary>
    public const string FilePath = "Data/MegBack.txt";

    // 保护文件写入的锁
    private static readonly object _lock = new();

    /// <summary>追加一条玩家返回。</summary>
    /// <param name="at">发生时间。</param>
    /// <param name="messageId">原卡片的消息号(拿不到就 0)。</param>
    /// <param name="target">造成这次返回的目标(公告写 notice,推送写 @all / 玩家名 / @clients:子服)。</param>
    /// <param name="content">玩家返回的内容(点的按钮文案,填空卡片带上填的内容)。</param>
    public static void Append(DateTime at, int messageId, string target, string content)
    {
        var line = $"[{at:yyyy/MM/dd}][{at:HH:mm:ss}][{messageId}][{target}] {content}";

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
}
