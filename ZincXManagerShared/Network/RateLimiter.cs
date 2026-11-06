using System.Collections.Concurrent;  // 并发字典

namespace ZincXManagerShared.Network;

/// <summary>
/// 按 key 限速:令牌桶,每个 key 一个桶,静止的桶会被顺手清掉。
///
/// <para>用途:一条连接狂发包(刷屏、爆破、放大攻击)时,它吃掉的只是自己的额度,
/// 不会把 dispatcher 队列塞满拖慢别人。</para>
///
/// <para>线程安全:桶内部用锁保护,<see cref="TryTake"/> 可并发调用。</para>
/// </summary>
public sealed class RateLimiter
{
    /// <summary>桶里最多攒多少秒的额度(超过就丢,免得长时间空闲后突然能爆一大串)。</summary>
    private const double MaxBurstSeconds = 2;

    /// <summary>多久没用过的桶就清掉,免得爆破时 key 无限增长。</summary>
    private const long IdleCleanupMs = 10 * 60 * 1000;

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    private readonly double _perSecond;   //每秒允许几个

    private readonly double _burst;   //最多攒几个

    private long _lastCleanup;   //上次清桶的时刻

    /// <param name="perSecond">每秒允许的个数;小于等于 0 表示不限速(此时 <see cref="TryTake"/> 恒真)。</param>
    public RateLimiter(double perSecond)
    {
        _perSecond = perSecond;
        _burst = Math.Max(1, perSecond * MaxBurstSeconds);
    }

    /// <summary>是否真的在限速(没配置时上层可以整段跳过)。</summary>
    public bool Enabled => _perSecond > 0;

    /// <summary>取一个额度;拿到返回 true,超额返回 false。</summary>
    /// <param name="key">限速维度(连接 id、IP、账号名……)。</param>
    public bool TryTake(string key)
    {
        if (_perSecond <= 0)
        {
            return true;
        }

        var now = Environment.TickCount64;
        CleanupIfDue(now);

        var bucket = _buckets.GetOrAdd(key, _ => new Bucket(_burst, now));
        return bucket.TryTake(_perSecond, _burst, now);
    }

    /// <summary>把某个 key 的桶清掉(比如玩家登录成功、或已经断开)。</summary>
    /// <param name="key">要清的维度。</param>
    public void Forget(string key) => _buckets.TryRemove(key, out _);

    /// <summary>清一遍长时间没动静的桶;每 <see cref="IdleCleanupMs"/> 最多跑一次。</summary>
    /// <param name="now">当前时刻(毫秒,<see cref="Environment.TickCount64"/>)。</param>
    private void CleanupIfDue(long now)
    {
        var last = Interlocked.Read(ref _lastCleanup);
        if (now - last < IdleCleanupMs)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastCleanup, now, last) != last)
        {
            return;   //别人已经在清了
        }

        foreach (var pair in _buckets)
        {
            if (now - pair.Value.LastMs > IdleCleanupMs)
            {
                _buckets.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <summary>一个 key 的令牌桶。</summary>
    private sealed class Bucket
    {
        private readonly object _lock = new();

        private double _tokens;   //当前攒下的额度

        private long _lastMs;   //上次补充额度的时刻

        /// <summary>最近一次动过的时刻,清理用。</summary>
        public long LastMs { get; private set; }

        public Bucket(double tokens, long now)
        {
            _tokens = tokens;
            _lastMs = now;
            LastMs = now;
        }

        /// <summary>先按流逝的时间补额度,再扣一个;扣不到就是超额。</summary>
        public bool TryTake(double perSecond, double burst, long now)
        {
            lock (_lock)
            {
                var elapsed = Math.Max(0, now - _lastMs) / 1000.0;
                _lastMs = now;
                LastMs = now;

                _tokens = Math.Min(burst, _tokens + (elapsed * perSecond));
                if (_tokens < 1)
                {
                    return false;
                }

                _tokens -= 1;
                return true;
            }
        }
    }
}
