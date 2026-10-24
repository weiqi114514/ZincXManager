using System;  // 基础类型 / 常用静态方法
using System.Collections.Generic;  // 泛型集合
using System.IO;  // 读写清单文件
using System.Linq;  // 拼接输出
using ZincXManagerShared.Logging;  // 共享库

namespace ZincXManagerShared.History;

/// <summary>
/// 已发消息清单:每个用户一个文件 <c>User/Message/{用户ID}.txt</c>,内容是一行空格分隔的消息号。
/// <para>流程:服务端准备消息列表 → 查这里剔除发过的 → 发出去 → 客户端回执(507) → 才写进这里。</para>
/// <para>写进这里之后,这条消息对该用户就算"已发",以后补发不会再给。</para>
/// </summary>
public static class SentMessages
{
    /// <summary>清单目录(相对服务端工作目录)。</summary>
    public const string RootDir = "User/Message";

    // 保护 _cache 的锁
    private static readonly object _lock = new();

    // 用户ID → 已发消息号集合(内存缓存,避免每次读盘)
    private static readonly Dictionary<int, HashSet<int>> _cache = new();

    /// <summary>这个用户是不是已经收过这条消息。</summary>
    public static bool Contains(int userId, int messageId)
    {
        lock (_lock)
        {
            return Load(userId).Contains(messageId);
        }
    }

    /// <summary>把一批消息号记为已发(来自客户端回执);有新的才落盘。</summary>
    public static void Add(int userId, IEnumerable<int> messageIds)
    {
        lock (_lock)
        {
            var set = Load(userId);
            var changed = false;
            foreach (var id in messageIds)
            {
                if (id > 0 && set.Add(id))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                Save(userId, set);
            }
        }
    }

    /// <summary>取这个用户已发的全部消息号(调试/统计用)。</summary>
    public static IReadOnlyCollection<int> All(int userId)
    {
        lock (_lock)
        {
            return Load(userId).ToArray();
        }
    }

    /// <summary>清掉某个用户的已发记录(下次会重新补发全部)。</summary>
    public static void Clear(int userId)
    {
        lock (_lock)
        {
            _cache.Remove(userId);
            try
            {
                var path = PathOf(userId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[消息] 清已发清单失败: " + ex.Message);
            }
        }
    }

    /// <summary>某用户清单文件路径。</summary>
    private static string PathOf(int userId) => Path.Combine(RootDir, userId + ".txt");

    /// <summary>读清单(读过就缓存);一行空格分隔。</summary>
    private static HashSet<int> Load(int userId)
    {
        if (_cache.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        var set = new HashSet<int>();
        try
        {
            var path = PathOf(userId);
            if (File.Exists(path))
            {
                foreach (var token in File.ReadAllText(path).Split(new[] { ' ', '\t', '\r', '\n' },
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(token, out var id) && id > 0)
                    {
                        set.Add(id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[消息] 读已发清单失败,当作空的: " + ex.Message);
        }

        _cache[userId] = set;
        return set;
    }

    /// <summary>落盘:一行,空格分隔(用户要求的一行内记录)。</summary>
    private static void Save(int userId, HashSet<int> set)
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            var line = string.Join(" ", set.OrderBy(x => x));
            File.WriteAllText(PathOf(userId), line);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[消息] 写已发清单失败: " + ex.Message);
        }
    }
}
