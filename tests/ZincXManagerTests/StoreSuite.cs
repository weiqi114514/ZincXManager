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
}
