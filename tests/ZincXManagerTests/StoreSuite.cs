using System;  // 临时目录 / 异常
using System.Collections.Generic;  // List<SuiteResult>
using System.IO;  // 文件读写与目录
using System.Threading.Tasks;  // Task
using ZincXManagerShared.Account;  // WhitelistStore:白名单存储
using ZincXManagerShared.Login;  // LoginStore:持久登录表

namespace ZincXManagerTests;

/// <summary>套件 16:白名单表 + 持久登录表(纯存储单测,不起进程)</summary>
public static class StoreSuite
{
    /// <summary>跑一遍,结果追加到 results</summary>
    public static Task RunAsync(List<SuiteResult> results, string ws)
    {
        var r = new SuiteResult { Name = "16 白名单与持久登录(存储单测)" };
        results.Add(r);

        var root = Path.Combine(ws, "_zxt", "store-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        try
        {
            WhiteList(r, root);
            Persistent(r, root);
            AccountIds(r, root);
            AccountNames(r, root);
        }
        catch (Exception ex)
        {
            r.Fatal = ex.Message;
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // 临时目录删不掉不影响结论
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>白名单的增删查、重启后还在、开关文件</summary>
    private static void WhiteList(SuiteResult r, string root)
    {
        var file = Path.Combine(root, "wl", "Whitelist.txt");
        var store = new WhitelistStore(file);

        r.Add("没建文件时默认关闭", !store.Enabled);
        r.Add("空表查不到任何键", !store.Contains("ZXMTestPlayer"));

        r.Add("加玩家名", store.Add("ZXMTestPlayer", out var e1), e1);
        r.Add("加账号名", store.Add("testplayer", out var e2), e2);
        r.Add("加账号 ID", store.Add("5", out var e3), e3);
        r.Add("重复加报已存在", !store.Add("ZXMTestPlayer", out var e4) && e4.Contains("已经"), e4);
        r.Add("空键被拒", !store.Add("   ", out var e5) && e5.Contains("不能为空"), e5);

        r.Add("三条都在", store.All.Length == 3, string.Join(" , ", store.All));
        r.Add("按长度排序(ID 在前)", store.All[0] == "5", string.Join(" , ", store.All));

        // 控制台/玩家名大小写不统一也要命中
        r.Add("查大小写不敏感", store.Contains("zxmtestplayer"));
        r.Add("查带空格自动 trim", store.Contains("  testplayer  "));
        r.Add("查空串返回 false", !store.Contains(""));
        r.Add("查 null 返回 false", !store.Contains(null));

        // 重新 new = 模拟 ZXMS 重启
        var reload = new WhitelistStore(file);
        r.Add("重启后条目还在", reload.All.Length == 3, string.Join(" , ", reload.All));
        r.Add("重启后仍能命中", reload.Contains("ZXMTestPlayer"));

        reload.SetEnabled(true);
        r.Add("开关文件写 1", File.ReadAllText(reload.EnabledPath).Trim() == "1");
        var afterOn = new WhitelistStore(file);
        r.Add("重启后开关仍是开", afterOn.Enabled);
        afterOn.SetEnabled(false);
        r.Add("关掉后重启是关", !new WhitelistStore(file).Enabled);

        // 手改文件:注释与空行要跳过
        File.WriteAllText(file, "\uFEFF# 注释行\n\nZXMTestPlayer\n  testplayer  \n# 尾巴\n");
        var manual = new WhitelistStore(file);
        r.Add("读盘跳过注释与空行", manual.All.Length == 2, string.Join(" , ", manual.All));

        r.Add("删存在的条目", manual.Remove("ZXMTestPlayer"));
        r.Add("删不存在的条目返回 false", !manual.Remove("nobody"));
        r.Add("删完只剩 1 条且落盘", new WhitelistStore(file).All.Length == 1);
    }

    /// <summary>持久登录表:同玩家同 IP 才免确认</summary>
    private static void Persistent(SuiteResult r, string root)
    {
        // PersistentFile 是相对路径常量,靠切工作目录把它关进临时目录
        var old = Directory.GetCurrentDirectory();
        var dir = Path.Combine(root, "persist");
        Directory.CreateDirectory(dir);
        Directory.SetCurrentDirectory(dir);
        try
        {
            var saved = Path.Combine(dir, LoginStore.PersistentFile);
            r.Add("一开始没有持久登录", !LoginStore.HasPersistentLogin("ZXMTestPlayer", "1.2.3.4"));

            LoginStore.AddPersistentLogin("ZXMTestPlayer", "1.2.3.4");
            r.Add("同玩家同 IP 免确认", LoginStore.HasPersistentLogin("ZXMTestPlayer", "1.2.3.4"));
            r.Add("玩家名大小写不敏感", LoginStore.HasPersistentLogin("zxmtestplayer", "1.2.3.4"));
            r.Add("换了 IP 不免确认(要重新点头)", !LoginStore.HasPersistentLogin("ZXMTestPlayer", "5.6.7.8"));
            r.Add("传空 IP 不校验(兼容旧数据)", LoginStore.HasPersistentLogin("ZXMTestPlayer", ""));
            r.Add("别的玩家没有记录", !LoginStore.HasPersistentLogin("Other", "1.2.3.4"));

            // 同玩家换 IP 再登一次:只留最新一条,旧 IP 立即失效
            LoginStore.AddPersistentLogin("ZXMTestPlayer", "5.6.7.8");
            r.Add("换 IP 后新 IP 生效", LoginStore.HasPersistentLogin("ZXMTestPlayer", "5.6.7.8"));
            r.Add("换 IP 后旧 IP 失效", !LoginStore.HasPersistentLogin("ZXMTestPlayer", "1.2.3.4"));
            var lines = File.ReadAllLines(saved);
            r.Add("同一玩家只留一行", lines.Length == 1, "行数=" + lines.Length);

            // 两个玩家互不影响
            LoginStore.AddPersistentLogin("Other", "9.9.9.9");
            r.Add("第二个玩家写入后仍各自命中",
                LoginStore.HasPersistentLogin("ZXMTestPlayer", "5.6.7.8") && LoginStore.HasPersistentLogin("Other", "9.9.9.9"));

            LoginStore.RemovePersistentLogin("ZXMTestPlayer");
            r.Add("删掉后不再免确认", !LoginStore.HasPersistentLogin("ZXMTestPlayer", "5.6.7.8"));
            r.Add("删一个不动另一个", LoginStore.HasPersistentLogin("Other", "9.9.9.9"));
        }
        finally
        {
            Directory.SetCurrentDirectory(old);
        }
    }

    /// <summary>账号编号只增不回收:删掉最大号再建一个,新号不能拿回旧号(好友档与聊天频道都按编号指认人)。</summary>
    private static void AccountIds(SuiteResult r, string root)
    {
        var dir = Path.Combine(root, "accounts");
        var store = new AccountStore(dir);
        var hash = ZincXManagerShared.Account.PasswordHash.Hash("tester", "123456");

        r.Add("建第一个账号", store.Add("tester", hash, "Player", out var first, out var e1), e1);
        r.Add("第一个账号编号是 1", first is { Id: 1 }, first?.Id.ToString() ?? "null");

        store.Add("second", hash, "Player", out var second, out _);
        r.Add("第二个账号编号是 2", second is { Id: 2 }, second?.Id.ToString() ?? "null");

        r.Add("删掉最大的那个", store.Remove("second"), "Remove(second)");
        store.Add("third", hash, "Player", out var third, out _);
        r.Add("新建的不会捡回被删的编号 2", third is { Id: 3 }, third?.Id.ToString() ?? "null");

        // 重启后水位不能退回去,否则下一次重建号又会撞车
        var reload = new AccountStore(dir);
        reload.Add("fourth", hash, "Player", out var fourth, out _);
        r.Add("重启后继续往上加(编号 4)", fourth is { Id: 4 }, fourth?.Id.ToString() ?? "null");
        r.Add("编号水位落了盘", File.Exists(Path.Combine(dir, AccountStore.NextIdFileName)),
            Path.Combine(dir, AccountStore.NextIdFileName));
    }

    /// <summary>账号名不能带协议分隔符:名字要拼进 648/649 的竖线负载和官方端的 名称.dat。</summary>
    private static void AccountNames(SuiteResult r, string root)
    {
        var dir = Path.Combine(root, "names");
        var store = new AccountStore(dir);
        var hash = ZincXManagerShared.Account.PasswordHash.Hash("tester", "123456");

        r.Add("正常名字能通过", AccountStore.IsNameTextOk("阿丽丝", out _), "阿丽丝");
        r.Add("带竖线的名字不行", !AccountStore.IsNameTextOk("a|b", out var pipeError), pipeError);
        r.Add("竖线的报错说得清", pipeError.Contains("符号"), pipeError);
        r.Add("带 tab 的名字不行", !AccountStore.IsNameTextOk("a\tb", out _), "a\\tb");
        r.Add("带换行的名字不行", !AccountStore.IsNameTextOk("a\nb", out _), "a\\nb");
        r.Add("带斜杠的名字不行", !AccountStore.IsNameTextOk("a/b", out _), "a/b");
        r.Add("一个字的名字不行", !AccountStore.IsNameTextOk("a", out _), "a");
        r.Add("超长的名字不行", !AccountStore.IsNameTextOk(new string('a', 25), out _), "25 个 a");

        r.Add("建号时挡住竖线名", !store.Add("bad|name", hash, "Player", out _, out var addError), addError);
        r.Add("建号的报错说得清", addError.Contains("符号"), addError);
        r.Add("被挡住的名字没落盘", store.Find("bad|name") is null, "Find(bad|name)");
        r.Add("建号时挡住换行名", !store.Add("bad\nname", hash, "Player", out _, out _), "bad\\nname");

        store.Add("goodname", hash, "Player", out _, out _);
        r.Add("改名时挡住竖线", !store.Rename("goodname", "bad|again", hash, out _, out var renameError), renameError);
        r.Add("改名被挡后原名还在", store.Find("goodname") is not null, "Find(goodname)");
    }
}
