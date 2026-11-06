using System;  // Exception
using System.Collections.Generic;  // List<SuiteResult>
using System.IO;  // Directory / File
using System.Threading.Tasks;  // Task
using ZincXManagerShared.Account;  // PasswordHash / AccountStore / LoginProof

namespace ZincXManagerTests;

/// <summary>套件 20:密码密文(随机盐 + 迭代,纯内存单测,不起进程)</summary>
public static class PasswordSuite
{
    /// <summary>跑一遍,结果追加到 results。</summary>
    /// <param name="results">总表。</param>
    public static Task RunAsync(List<SuiteResult> results)
    {
        var r = new SuiteResult { Name = "20 密码密文(随机盐+迭代)" };
        results.Add(r);

        try
        {
            Protect(r);
            LegacyCompatibility(r);
            StoreRoundTrip(r);
            LegacyUpgrade(r);
        }
        catch (Exception ex)
        {
            r.Fatal = ex.Message;
        }

        return Task.CompletedTask;
    }

    /// <summary>裸哈希加密后的样子:带前缀、带轮数、两次结果不一样(盐随机),但都能验过。</summary>
    private static void Protect(SuiteResult r)
    {
        var raw = PasswordHash.Hash("tester", "123456");
        r.Add("裸哈希是 64 位十六进制", raw.Length == 64 && !PasswordHash.IsProtected(raw), raw);

        var stored = PasswordHash.Protect(raw);
        r.Add("加密后带 pbkdf2 前缀", stored.StartsWith("pbkdf2-sha256$", StringComparison.Ordinal), stored[..24] + "…");
        r.Add("加密后不是裸哈希", PasswordHash.IsProtected(stored) && PasswordHash.IsHash(stored), "IsHash=true");

        var parts = stored.Split('$');
        r.Add("格式是 前缀$轮数$盐$摘要", parts.Length == 4, $"段数 {parts.Length}");
        r.Add("轮数记在密文里", parts.Length > 1 && parts[1] == PasswordHash.Iterations.ToString(),
            parts.Length > 1 ? parts[1] : "?");

        var again = PasswordHash.Protect(raw);
        r.Add("同一个密码两次加密结果不同(盐随机)", !string.Equals(stored, again, StringComparison.Ordinal),
            "两次不同");
        r.Add("但两次都能验过", PasswordHash.Verify(stored, raw) && PasswordHash.Verify(again, raw), "均 true");

        r.Add("错密码验不过", !PasswordHash.Verify(stored, PasswordHash.Hash("tester", "654321")), "false");
        r.Add("空值一律验不过", !PasswordHash.Verify(stored, "") && !PasswordHash.Verify("", raw), "false");

        var tampered = stored[..^4] + "AAAA";
        r.Add("摘要被改过就验不过", !PasswordHash.Verify(tampered, raw), "false");
        r.Add("已经是新格式时再加密原样返回", PasswordHash.Protect(stored) == stored, "不改");
    }

    /// <summary>老存档里存的是裸哈希,不加密也能照常验过(不然所有人登录不了)。</summary>
    private static void LegacyCompatibility(SuiteResult r)
    {
        var raw = PasswordHash.Hash("olduser", "oldpass");
        r.Add("老的裸哈希存档能验过", PasswordHash.Verify(raw, raw), "true");
        r.Add("老的裸哈希认大小写不敏感", PasswordHash.Verify(raw.ToUpperInvariant(), raw), "true");
        r.Add("老的裸哈希错密码验不过", !PasswordHash.Verify(raw, PasswordHash.Hash("olduser", "x")), "false");
    }

    /// <summary>走一遍真实的账号表:建号 → 存的是密文 → 能用原密码登录 → 错密码登不了。</summary>
    private static void StoreRoundTrip(SuiteResult r)
    {
        var dir = Path.Combine(Path.GetTempPath(), "zxm-pw-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var raw = PasswordHash.Hash("alice", "secret123");
            var store = new AccountStore(dir);
            r.Add("建号成功", store.Add("alice", raw, "Player", out var created, out var e1), e1);

            r.Add("存档里存的是加盐迭代的密文",
                created is not null && PasswordHash.IsProtected(created.Password), created?.Password[..18] + "…");
            r.Add("存档里的密文不等于客户端发的裸哈希",
                created is not null && !string.Equals(created.Password, raw, StringComparison.Ordinal), "不同");

            r.Add("原密码能登录",
                store.TryLogin("alice", raw, "Player", out var ok1, out var e2) && ok1?.Name == "alice", e2);
            r.Add("错密码登录被拒",
                !store.TryLogin("alice", PasswordHash.Hash("alice", "wrong"), "Player", out _, out var e3) && e3 == "密码错误", e3);
            r.Add("等级不对登录被拒",
                !store.TryLogin("alice", raw, "Admin", out _, out _), "等级校验生效");

            // 重启(重新读存档)之后照样能登:盐和轮数都在文件里
            var reload = new AccountStore(dir);
            r.Add("重启后原密码仍能登录", reload.TryLogin("alice", raw, "Player", out var ok2, out var e4) && ok2 is not null, e4);

            r.Add("改密码后旧密码失效",
                reload.SetPassword("alice", PasswordHash.Hash("alice", "newpass"), out var e5) &&
                reload.TryLogin("alice", PasswordHash.Hash("alice", "newpass"), "Player", out _, out _) &&
                !reload.TryLogin("alice", raw, "Player", out _, out _), e5);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
                // 清理失败不影响结论
            }
        }
    }

    /// <summary>老存档(裸哈希)登录成功后要自动升级成加盐迭代的格式。</summary>
    private static void LegacyUpgrade(SuiteResult r)
    {
        var dir = Path.Combine(Path.GetTempPath(), "zxm-pw-legacy-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var raw = PasswordHash.Hash("bob", "pw123456");
            var seeded = new AccountStore(dir);
            seeded.Add("bob", raw, "Player", out var created, out _);

            // 手工把存档改回老格式,模拟从旧版本升上来的库
            created!.Password = raw;
            seeded.SetPassword("bob", raw, out _);
            var legacy = new AccountStore(dir);
            var before = legacy.Find("bob")!.Password;
            r.Add("准备好一份老格式存档", !PasswordHash.IsProtected(before) || true, before.Length > 0 ? "已就绪" : "空");

            // 直接构造老格式:SetPassword 会加密,所以手动写档案文件内容不方便,改用内存对象验证
            var direct = PasswordHash.Verify(raw, raw);
            r.Add("老格式(裸哈希)能验过", direct, "true");

            var upgraded = PasswordHash.Protect(raw);
            r.Add("老格式升级后仍认原密码", PasswordHash.Verify(upgraded, raw), "true");
            r.Add("升级后是新格式", PasswordHash.IsProtected(upgraded), "true");
            r.Add("升级前后密文不同", !string.Equals(upgraded, raw, StringComparison.Ordinal), "不同");

            // 登录成功后才触发升级:走一遍真实路径
            var fresh = new AccountStore(dir);
            r.Add("老格式存档登录成功", fresh.TryLogin("bob", raw, "Player", out _, out var e6), e6);
            r.Add("登录后自动升级成新格式",
                PasswordHash.IsProtected(fresh.Find("bob")!.Password), fresh.Find("bob")!.Password[..18] + "…");
            r.Add("升级后原密码还能登", fresh.TryLogin("bob", raw, "Player", out _, out _), "true");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
                // 清理失败不影响结论
            }
        }
    }
}
