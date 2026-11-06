using System.Collections.Concurrent;  // 并发字典

namespace ZincXManagerShared.Network;

/// <summary>
/// 失败次数窗口限流:同一个 key 在一段时间内失败太多次就先挡掉,成功一次就清零。
///
/// <para>为什么不用令牌桶:<see cref="RateLimiter"/> 会把正常登录也算进额度,
/// 网吧 / 学校 / 家庭 NAT 后面几十个人共用同一个外网 IP 时,好人先被自己人拖黑;
/// 只数失败就不一样 —— 正常玩家一次就成功,额度根本不动,爆破的人每次都在扣。</para>
///
/// <para>线程安全:计数用锁保护,可并发调用。</para>
/// </summary>
public sealed class FailureThrottle
{
    /// <summary>超过这么久没动静的 key 顺手清掉,免得爆破时 key 无限增长。</summary>
    private const long IdleCleanupMs = 30 * 60 * 1000;

    private readonly ConcurrentDictionary<string, Window> _windows = new();

    private readonly int _limit;   //一个窗口内最多失败几次

    private readonly int _windowSeconds;   //窗口长度

    private long _lastCleanup;   //上次清表的时刻

    /// <param name="limit">一个窗口内允许失败几次;小于等于 0 表示不限制。</param>
    /// <param name="windowSeconds">窗口长度(秒)。</param>
    public FailureThrottle(int limit, int windowSeconds)
    {
        _limit = limit;
        _windowSeconds = Math.Max(1, windowSeconds);
    }

    /// <summary>是否真的在限流(没配置时上层可以整段跳过)。</summary>
    public bool Enabled => _limit > 0;

    /// <summary>这个 key 现在是不是已经被挡了(只读,不扣额度)。</summary>
    /// <param name="key">限速维度(IP、IP+账号名……)。</param>
    public bool IsBlocked(string key)
    {
        if (_limit <= 0)
        {
            return false;
        }

        if (!_windows.TryGetValue(key, out var window))
        {
            return false;
        }

        lock (window)
        {
            if (Expired(window, Environment.TickCount64))
            {
                return false;
            }

            return window.Failures >= _limit;
        }
    }

    /// <summary>记一次失败。</summary>
    /// <param name="key">限速维度。</param>
    public void RecordFailure(string key)
    {
        if (_limit <= 0)
        {
            return;
        }

        var now = Environment.TickCount64;
        CleanupIfDue(now);

        var window = _windows.GetOrAdd(key, _ => new Window(now));
        lock (window)
        {
            if (Expired(window, now))
            {
                window.StartMs = now;
                window.Failures = 0;
            }

            window.Failures++;
            window.LastMs = now;
        }
    }

    /// <summary>成功了就清零:正常玩家不该被自己偶尔输错的那一次拖累。</summary>
    /// <param name="key">限速维度。</param>
    public void RecordSuccess(string key) => _windows.TryRemove(key, out _);

    /// <summary>窗口是否已经翻页。</summary>
    private bool Expired(Window window, long now)
        => now - window.StartMs >= _windowSeconds * 1000L;

    /// <summary>清一遍长时间没动静的 key;每 <see cref="IdleCleanupMs"/> 最多跑一次。</summary>
    private void CleanupIfDue(long now)
    {
        var last = Interlocked.Read(ref _lastCleanup);
        if (now - last < IdleCleanupMs)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastCleanup, now, last) != last)
        {
            return;
        }

        foreach (var pair in _windows)
        {
            if (now - pair.Value.LastMs > IdleCleanupMs)
            {
                _windows.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <summary>一个 key 的失败窗口。</summary>
    private sealed class Window
    {
        public Window(long now)
        {
            StartMs = now;
            LastMs = now;
        }

        /// <summary>窗口起点(毫秒)。</summary>
        public long StartMs { get; set; }

        /// <summary>窗口内失败了几次。</summary>
        public int Failures { get; set; }

        /// <summary>最近一次失败的时刻(清理用)。</summary>
        public long LastMs { get; set; }
    }
}
