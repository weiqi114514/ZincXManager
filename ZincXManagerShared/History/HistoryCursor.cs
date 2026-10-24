using System;  // 基础类型 / 常用静态方法
using System.Collections.Generic;  // 泛型集合(账号 → 消息号)
using System.IO;  // 读写游标文件
using System.Text.Json;  // JSON 序列化
using ZincXManagerShared.Logging;  // 共享库

namespace ZincXManagerShared.History;

/// <summary>
/// 补发游标:按账号记住"已经补发到第几条消息"。
/// <para>作用:客户端删掉的消息不会因为重新登录又被补发回来 —— 补过就不再补。</para>
/// <para>存盘:服务端工作目录下的 <see cref="FilePath"/>(账号 → 消息号)。</para>
/// </summary>
public static class HistoryCursor
{
    /// <summary>游标文件(相对服务端工作目录)。</summary>
    public const string FilePath = "history_cursor.json";

    // 保护 _map 的锁
    private static readonly object _lock = new();

    // 账号 → 已补发到的消息号;null 表示还没读盘
    private static Dictionary<string, int>? _map;

    /// <summary>取某账号已补发到的消息号;没有记录返回 0。</summary>
    public static int Get(string account)
    {
        lock (_lock)
        {
            return Load_NoLock().TryGetValue(account, out var id) ? id : 0;
        }
    }

    /// <summary>把游标推进到 id(只前进不后退),有变化才落盘。</summary>
    public static void Advance(string account, int id)
    {
        lock (_lock)
        {
            var map = Load_NoLock();
            if (map.TryGetValue(account, out var old) && old >= id)
            {
                return;
            }

            map[account] = id;
            Save_NoLock(map);
        }
    }

    /// <summary>清掉某账号的游标(下次登录重拉全部)。</summary>
    public static void Reset(string account)
    {
        lock (_lock)
        {
            var map = Load_NoLock();
            if (map.Remove(account))
            {
                Save_NoLock(map);
            }
        }
    }

    /// <summary>读游标表(已读过就直接用内存里的)。</summary>
    private static Dictionary<string, int> Load_NoLock()
    {
        if (_map is not null)
        {
            return _map;
        }

        _map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(FilePath))
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(FilePath));
                if (map is not null)
                {
                    _map = new Dictionary<string, int>(map, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[消息] 读补发游标失败,当作空的: " + ex.Message);
        }

        return _map;
    }

    /// <summary>落盘游标表(写失败只记日志,不影响补发)。</summary>
    private static void Save_NoLock(Dictionary<string, int> map)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(map));
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[消息] 写补发游标失败: " + ex.Message);
        }
    }
}
