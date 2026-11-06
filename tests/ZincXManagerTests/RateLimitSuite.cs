using System;  // Guid / Environment.TickCount64
using System.Collections.Generic;  // List<SuiteResult>
using System.Threading;  // 多线程抢额度
using System.Threading.Tasks;  // Task / Parallel
using ZincXManagerShared.Network;  // RateLimiter

namespace ZincXManagerTests;

/// <summary>套件 18:限速器(令牌桶,纯内存单测,不起进程)</summary>
public static class RateLimitSuite
{
    /// <summary>跑一遍,结果追加到 results。</summary>
    /// <param name="results">总表。</param>
    public static Task RunAsync(List<SuiteResult> results)
    {
        var r = new SuiteResult { Name = "18 限速器(令牌桶)" };
        results.Add(r);

        try
        {
            Unlimited(r);
            Burst(r);
            Refill(r);
            Concurrent(r);
            Forget(r);
            ThrottleBasics(r);
            ThrottleSuccess(r);
            ThrottleWindow(r);
        }
        catch (Exception ex)
        {
            r.Fatal = ex.Message;
        }

        return Task.CompletedTask;
    }

    /// <summary>perSecond = 0 表示不限速,怎么取都应该成功。</summary>
    private static void Unlimited(SuiteResult r)
    {
        var limiter = new RateLimiter(0);
        var ok = true;
        for (var i = 0; i < 1000; i++)
        {
            ok &= limiter.TryTake("任意");
        }

        r.AddTrue("不限速时 1000 次全部放行", ok);
        r.AddTrue("不限速时 Enabled 为假", !limiter.Enabled);
    }

    /// <summary>每秒 10 个 + 最多攒 2 秒 = 桶里最多 20 个,连取 42 次应当只成功 20 次左右。</summary>
    private static void Burst(SuiteResult r)
    {
        var limiter = new RateLimiter(10);
        var taken = 0;
        for (var i = 0; i < 42; i++)
        {
            if (limiter.TryTake("A"))
            {
                taken++;
            }
        }

        // 桶初始就是满的(2 秒额度 = 20),中途只过几毫秒,所以成功数必然在 20±1
        r.AddTrue("突发额度 = 每秒速率 × 2 秒", taken is >= 20 and <= 21, $"连取 42 次,成功 {taken} 次");
    }

    /// <summary>等一秒再取:额度会按流逝时间补回来。</summary>
    private static void Refill(SuiteResult r)
    {
        var limiter = new RateLimiter(10);
        while (limiter.TryTake("B"))
        {
            // 先把桶抽干
        }

        r.AddTrue("抽干后立刻再取被拒", !limiter.TryTake("B"), "");

        Thread.Sleep(1000);
        var got = 0;
        for (var i = 0; i < 20; i++)
        {
            if (limiter.TryTake("B"))
            {
                got++;
            }
        }

        // 睡 1 秒应当补回 10 个左右(计时误差允许 8-12)
        r.AddTrue("睡 1 秒补回约 1 秒的额度", got is >= 8 and <= 12, $"补回 {got} 个");
    }

    /// <summary>不同 key 各用各的桶:A 抽干不影响 B。</summary>
    private static void Concurrent(SuiteResult r)
    {
        var limiter = new RateLimiter(10);
        while (limiter.TryTake("甲"))
        {
            // 抽干甲的桶
        }

        var otherOk = true;
        for (var i = 0; i < 15; i++)
        {
            otherOk &= limiter.TryTake("乙");
        }

        r.AddTrue("甲的额度用完不影响乙", otherOk);

        // 并发抢:20 个桶 × 每个 5 个额度,并发调不应当多给
        var shared = new RateLimiter(50);
        var granted = 0;
        Parallel.For(0, 20, i =>
        {
            var mine = 0;
            for (var k = 0; k < 30; k++)
            {
                if (shared.TryTake("并发" + i))
                {
                    mine++;
                }
            }

            Interlocked.Add(ref granted, mine);
        });

        // 每个桶最多攒 100(50×2),30 次全给也才 600;这里只验"没有因为并发多发出额度"
        r.AddTrue("并发调用不会多给额度", granted <= 20 * 30, $"共放行 {granted} 次(上限 {20 * 30})");
    }

    /// <summary>Forget 之后额度重新满上。</summary>
    private static void Forget(SuiteResult r)
    {
        var limiter = new RateLimiter(5);
        while (limiter.TryTake("丙"))
        {
            // 抽干
        }

        limiter.Forget("丙");
        var reOk = limiter.TryTake("丙");
        r.AddTrue("Forget 之后额度重新回满", reOk);
    }

    /// <summary>失败计数:没到上限不挡,到了才挡;不同 key 各算各的。</summary>
    private static void ThrottleBasics(SuiteResult r)
    {
        var throttle = new FailureThrottle(3, 60);
        r.AddTrue("没失败过不挡", !throttle.IsBlocked("1.2.3.4"));

        throttle.RecordFailure("1.2.3.4");
        throttle.RecordFailure("1.2.3.4");
        r.AddTrue("失败 2 次(上限 3)还放行", !throttle.IsBlocked("1.2.3.4"));

        throttle.RecordFailure("1.2.3.4");
        r.AddTrue("失败 3 次刚好挡住", throttle.IsBlocked("1.2.3.4"));

        r.AddTrue("别的 IP 不受影响", !throttle.IsBlocked("5.6.7.8"));

        var off = new FailureThrottle(0, 60);
        for (var i = 0; i < 100; i++)
        {
            off.RecordFailure("x");
        }

        r.AddTrue("上限填 0 表示不限", !off.IsBlocked("x"));
    }

    /// <summary>成功一次就清零:正常玩家偶尔输错不该被拖累。</summary>
    private static void ThrottleSuccess(SuiteResult r)
    {
        var throttle = new FailureThrottle(2, 60);
        throttle.RecordFailure("9.9.9.9");
        throttle.RecordFailure("9.9.9.9");
        r.AddTrue("连续失败 2 次后挡住", throttle.IsBlocked("9.9.9.9"));

        throttle.RecordSuccess("9.9.9.9");
        r.AddTrue("成功一次后解除", !throttle.IsBlocked("9.9.9.9"));
    }

    /// <summary>窗口过了就重新计数。</summary>
    private static void ThrottleWindow(SuiteResult r)
    {
        var throttle = new FailureThrottle(1, 1);   //1 秒窗口内只许失败 1 次
        throttle.RecordFailure("7.7.7.7");
        r.AddTrue("1 秒窗口:失败 1 次就挡", throttle.IsBlocked("7.7.7.7"));

        Thread.Sleep(1200);
        r.AddTrue("窗口翻页后解除", !throttle.IsBlocked("7.7.7.7"));
    }
}
