using System;  // 临时目录 / 异常
using System.Collections.Generic;  // List<SuiteResult>
using System.IO;  // 文件读写与目录
using System.Linq;  // 绑定表的行处理与顺序断言
using System.Threading.Tasks;  // Task
using ZincXManagerShared.Account;  // WhitelistStore:白名单存储
using ZincXManagerShared.Login;  // LoginStore:持久登录表

namespace ZincXManagerTests;

/// <summary>套件 16:白名单表 + 持久登录表 + 绑定表(纯存储单测,不起进程)</summary>
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
            Binds(r, root);
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

    /// <summary>
    /// 绑定表:一个账号能挂多个玩家、一个玩家只归一个账号、上限拦"新挂一个"、解绑按玩家名逐个来。
    /// <para>LoginStore 的绑定表是静态缓存,整套进程只读一次盘,所以这里所有断言都在同一个临时目录里跑。</para>
    /// </summary>
    private static void Binds(SuiteResult r, string root)
    {
        var old = Directory.GetCurrentDirectory();
        var dir = Path.Combine(root, "binds");
        Directory.CreateDirectory(dir);
        Directory.SetCurrentDirectory(dir);
        try
        {
            var saved = Path.Combine(dir, LoginStore.BindFile);   //绑定表落盘位置

            // ---------- ① 空表 ----------
            r.Add("一开始没绑定", LoginStore.PlayerOf(1).Length == 0);
            r.Add("空表 PlayersOf 是空列表", LoginStore.PlayersOf(1).Count == 0);
            r.Add("空表 BindCount 是 0", LoginStore.BindCount(1) == 0);
            r.Add("空表 AccountOf 是 0", LoginStore.AccountOf("Alice") == 0);
            r.Add("空表 CheckBindLimit 放行", LoginStore.CheckBindLimit(1, "Alice", 1).Length == 0);

            // ---------- ② 上限 1:绑一个成功,第二个被拦 ----------
            r.Add("上限 1 · 绑第一个成功", LoginStore.Bind(1, "Alice", 1, out var e1), e1);
            r.Add("PlayerOf 拿到第一个", LoginStore.PlayerOf(1) == "Alice", LoginStore.PlayerOf(1));
            r.Add("BindCount 是 1", LoginStore.BindCount(1) == 1);

            var limit = LoginStore.CheckBindLimit(1, "Alice2", 1);   //上限 1 的预检
            r.Add("上限 1 · 预检拦住第二个", limit.Length > 0, limit);
            r.Add("拦的理由说得清(带上限与已绑玩家)", limit.Contains("上限 1") && limit.Contains("Alice"), limit);
            r.Add("上限 1 · 真绑也被拦", !LoginStore.Bind(1, "Alice2", 1, out var e2) && e2.Contains("上限"), e2);
            r.Add("被拦后账号名下还是 1 个", LoginStore.BindCount(1) == 1);

            // ---------- ③ 上限 3:再挂两个,第三个还能挂 ----------
            r.Add("上限 3 · 挂第二个", LoginStore.Bind(1, "Bob", 3, out var e3), e3);
            r.Add("上限 3 · 挂第三个", LoginStore.Bind(1, "Carol", 3, out var e4), e4);
            r.Add("三个都在(BindCount=3)", LoginStore.BindCount(1) == 3, LoginStore.BindCount(1).ToString());
            var mine = LoginStore.PlayersOf(1);   //这个账号名下的玩家
            r.Add("绑定顺序保持 Alice→Bob→Carol",
                mine.SequenceEqual(new[] { "Alice", "Bob", "Carol" }), string.Join(" , ", mine));
            r.Add("PlayerOf 仍返回第一个", LoginStore.PlayerOf(1) == "Alice", LoginStore.PlayerOf(1));

            var full = LoginStore.CheckBindLimit(1, "Dave", 3);   //挂满之后的预检
            r.Add("上限 3 挂满后拦第四个", full.Length > 0 && full.Contains("上限 3"), full);
            r.Add("多个玩家时理由写个数", full.Contains("3 个玩家"), full);

            // ---------- ④ 重复绑同一个玩家算成功,不占名额也不改顺序 ----------
            r.Add("重复绑已在自己名下的玩家算成功", LoginStore.Bind(1, "bob", 3, out var e5), e5);
            r.Add("大小写不同也认成同一个", LoginStore.BindCount(1) == 3, LoginStore.BindCount(1).ToString());
            r.Add("重复绑不挪顺序", LoginStore.PlayersOf(1)[1] == "Bob", string.Join(" , ", LoginStore.PlayersOf(1)));

            // ---------- ⑤ 玩家唯一归一个账号 ----------
            r.Add("别的账号抢已绑玩家被拒",
                !LoginStore.Bind(2, "Alice", 5, out var e6) && e6.Contains("已经被账号 1"), e6);
            r.Add("抢绑失败后账号 2 仍是空的", LoginStore.BindCount(2) == 0);
            r.Add("AccountOf 认大小写", LoginStore.AccountOf("alice") == 1, LoginStore.AccountOf("alice").ToString());
            r.Add("AccountOf 未绑玩家返回 0", LoginStore.AccountOf("Nobody") == 0);

            r.Add("账号 2 绑自己的玩家", LoginStore.Bind(2, "Zed", 5, out var e7), e7);
            r.Add("两个账号各归各的",
                LoginStore.AccountOf("Alice") == 1 && LoginStore.AccountOf("Zed") == 2);

            // ---------- ⑥ 落盘:一个玩家一行,同一账号的多行挨在一起 ----------
            var text = File.Exists(saved) ? File.ReadAllText(saved) : "";
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().TrimStart('\uFEFF')).ToList();   //去掉 BOM 的行
            r.Add("绑定表一个玩家一行(共 4 行)", lines.Count == 4, "行数=" + lines.Count);
            r.Add("头三行属于账号 1", lines.Take(3).All(l => l.StartsWith("1=", StringComparison.Ordinal)), text.Trim());
            r.Add("最后一行属于账号 2", lines.LastOrDefault() == "2=Zed", text.Trim());

            // ---------- ⑦ AllBinds 一個玩家一行,同账号多行 ----------
            var all = LoginStore.AllBinds();   //全部绑定
            r.Add("AllBinds 一个玩家一行", all.Count == 4, all.Count.ToString());
            r.Add("AllBinds 按账号 ID 排序",
                all.Select(p => p.Key).SequenceEqual(new[] { 1, 1, 1, 2 }), string.Join(",", all.Select(p => p.Key)));

            // ---------- ⑧ 按玩家名解绑:只解这一个 ----------
            r.Add("解 Bob", LoginStore.Unbind(1, "Bob", out var removedBob), removedBob);
            r.Add("解掉的正是 Bob", removedBob == "Bob", removedBob);
            r.Add("解完还剩 2 个", LoginStore.BindCount(1) == 2, LoginStore.BindCount(1).ToString());
            r.Add("解完 AccountOf(Bob) 归 0", LoginStore.AccountOf("Bob") == 0);
            r.Add("解大小写不同的名字也认", LoginStore.Unbind(1, "carol", out var removedCarol), removedCarol);
            r.Add("解一个不属于自己的名字失败", !LoginStore.Unbind(1, "Zed", out _), "账号 1 把账号 2 的玩家解掉了");
            r.Add("解不存在的名字失败", !LoginStore.Unbind(1, "Nobody", out _));
            r.Add("清空所有绑定后账号 1 只剩 Alice", LoginStore.BindCount(1) == 1);

            // ---------- ⑨ UnbindAll:整条账号清掉 ----------
            var all2 = LoginStore.UnbindAll(1);   //账号 1 名下全解
            r.Add("UnbindAll 返回被解的玩家", all2.Count == 1 && all2[0] == "Alice", string.Join(" , ", all2));
            r.Add("UnbindAll 后账号 1 归零", LoginStore.BindCount(1) == 0 && LoginStore.PlayerOf(1).Length == 0);
            r.Add("UnbindAll 不动别的账号", LoginStore.AccountOf("Zed") == 2);
            r.Add("再 UnbindAll 空账号返回空", LoginStore.UnbindAll(1).Count == 0);

            var afterAll = File.Exists(saved) ? File.ReadAllText(saved) : "";
            r.Add("整条账号被解后表里没它的行",
                afterAll.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .All(l => !l.Trim().TrimStart('\uFEFF').StartsWith("1=", StringComparison.Ordinal)), afterAll.Trim());

            // ---------- ⑩ 上限 0 = 不限 ----------
            r.Add("上限 0 · 连挂三个都成功",
                LoginStore.Bind(3, "P1", 0, out _) && LoginStore.Bind(3, "P2", 0, out _) && LoginStore.Bind(3, "P3", 0, out _));
            r.Add("上限 0 · BindCount 是 3", LoginStore.BindCount(3) == 3, LoginStore.BindCount(3).ToString());
            r.Add("上限 0 · 预检永远放行", LoginStore.CheckBindLimit(3, "P4", 0).Length == 0);

            // ---------- ⑪ 参数不合法 ----------
            r.Add("账号 ID 0 绑不了", !LoginStore.Bind(0, "Q", 1, out var e8) && e8.Contains("不合法"), e8);
            r.Add("空玩家名绑不了", !LoginStore.Bind(1, "   ", 1, out var e9) && e9.Contains("不合法"), e9);
            r.Add("空玩家名解不了", !LoginStore.Unbind(1, "", out _));
            r.Add("玩家名前后空格自动去掉",
                LoginStore.Bind(4, "  Spacey  ", 1, out _) && LoginStore.PlayerOf(4) == "Spacey", LoginStore.PlayerOf(4));
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
