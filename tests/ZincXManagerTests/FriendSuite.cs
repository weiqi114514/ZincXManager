using System;  // 临时目录 / 异常
using System.Collections.Generic;  // List<SuiteResult>
using System.IO;  // 文件读写与目录
using System.Threading.Tasks;  // Task
using ZincXManagerShared.Account;  // PlayerId / FriendStore:玩家 ID 与好友存储

namespace ZincXManagerTests;

/// <summary>套件 17:玩家 ID 解析 + 好友列表存储(纯存储单测,不起进程)</summary>
public static class FriendSuite
{
    /// <summary>跑一遍,结果追加到 results</summary>
    public static Task RunAsync(List<SuiteResult> results, string ws)
    {
        var r = new SuiteResult { Name = "17 好友存储(PlayerId + FriendStore)" };
        results.Add(r);

        var root = Path.Combine(ws, "_zxt", "friend-" + Guid.NewGuid().ToString("N")[..6]);
        var old = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(root);
        Directory.SetCurrentDirectory(root);
        try
        {
            PlayerIdCases(r);
            ListCases(r);
            AccountIdCases(r);
        }
        catch (Exception ex)
        {
            r.Fatal = ex.Message;
        }
        finally
        {
            Directory.SetCurrentDirectory(old);
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

    /// <summary>完整 ID 的拼装与解析</summary>
    private static void PlayerIdCases(SuiteResult r)
    {
        var child = PlayerId.Parse("ZXMS-ZES-1-14");
        r.Add("子服 ID 解析出服务端与编码", child.ServerId == "ZXMS-ZES-1" && child.Code == 14, child.Full);
        r.Add("子服 ID 拼回原样", child.Full == "ZXMS-ZES-1-14", child.Full);

        var official = PlayerId.Parse("ZXMOS-5");
        r.Add("官方 ID 解析", official.ServerId == "ZXMOS" && official.Code == 5, official.Full);

        r.Add("服务端 ID 拼装", PlayerId.ChildServerId("ZES", 1) == "ZXMS-ZES-1", PlayerId.ChildServerId("ZES", 1));
        r.Add("自身不相等则不同", PlayerId.Parse("ZXMS-ZES-1-1") != PlayerId.Parse("ZXMS-HT-2-1"));

        r.Add("空串解析失败", !PlayerId.TryParse("", out _));
        r.Add("null 解析失败", !PlayerId.TryParse(null, out _));
        r.Add("没有编码段失败", !PlayerId.TryParse("ZXMOS-", out _));
        r.Add("编码非数字失败", !PlayerId.TryParse("ZXMOS-abc", out _));
        r.Add("编码为 0 失败", !PlayerId.TryParse("ZXMOS-0", out _));
        r.Add("前缀不认识失败", !PlayerId.TryParse("FOO-1", out _));
        r.Add("子服缺编号失败", !PlayerId.TryParse("ZXMS-ZES-", out _));
        r.Add("服务端 ID 校验:官方", PlayerId.IsServerId("ZXMOS"));
        r.Add("服务端 ID 校验:子服", PlayerId.IsServerId("ZXMS-ZES-1"));
        r.Add("服务端 ID 校验:子服缺编号", !PlayerId.IsServerId("ZXMS-ZES"));
        r.Add("服务端 ID 校验:乱写", !PlayerId.IsServerId("ZXM"));
    }

    /// <summary>好友列表的落盘、读取、增删</summary>
    private static void ListCases(SuiteResult r)
    {
        var me = PlayerId.Parse("ZXMS-ZES-1-14");
        var other = PlayerId.Parse("ZXMS-HT-2-23");
        var official = PlayerId.Parse("ZXMOS-5");

        r.Add("没建文件时是空列表", FriendStore.ReadList(me).Count == 0);

        FriendStore.WriteList(me, new[] { other, official });
        var list = FriendStore.ReadList(me);
        r.Add("写两条读回两条", list.Count == 2, string.Join(" , ", list));
        r.Add("读回顺序与写入一致", list[0] == other && list[1] == official, string.Join(" , ", list));

        var path = FriendStore.ListPathOf(me);
        r.Add("文件路径是 User/Friend/<服务端>/<编码>/FriendList.dat",
            path.Replace('\\', '/').EndsWith("User/Friend/ZXMS-ZES-1/14/FriendList.dat", StringComparison.Ordinal),
            path);
        r.Add("落盘是 CRLF 且两行", File.ReadAllText(path) == "ZXMS-HT-2 23\r\nZXMOS 5\r\n", File.ReadAllText(path).Replace("\r", "\\r").Replace("\n", "\\n"));

        // 手改文件:空行、行首尾空格、注释都要容忍
        File.WriteAllText(path, "# 手改的\n\n  ZXMS-HT-2   23  \nZXMOS 5\n\n");
        var manual = FriendStore.ReadList(me);
        r.Add("读盘跳过空行与注释、容忍空格", manual.Count == 2, string.Join(" , ", manual));

        r.Add("加已有好友返回 false", !FriendStore.Add(me, other));
        var third = PlayerId.Parse("ZXMS-RAINSERVER-3-1");
        r.Add("加新好友返回 true", FriendStore.Add(me, third));
        r.Add("加完是三条", FriendStore.ReadList(me).Count == 3);
        r.Add("不能把自己加成好友", !FriendStore.Add(me, me));

        r.Add("删好友返回 true", FriendStore.Remove(me, third));
        r.Add("删不存在的好友返回 false", !FriendStore.Remove(me, third));
        r.Add("删完回到两条", FriendStore.ReadList(me).Count == 2);

        r.Add("互查好友关系", FriendStore.AreFriends(me, other));
        r.Add("反查也应命中(存储层不做双向保证)", FriendStore.AreFriends(other, me) == false);

        // 别人看到的是空列表:每个账户一份
        r.Add("另一个账户的列表是空的", FriendStore.ReadList(other).Count == 0);

        // 聊天记录路径按对方完整 ID 命名
        var chat = FriendStore.ChatPathOf(me, other).Replace('\\', '/');
        r.Add("聊天文件是 Ch/<对方完整ID>.dat", chat.EndsWith("User/Friend/ZXMS-ZES-1/14/Ch/ZXMS-HT-2-23.dat", StringComparison.Ordinal), chat);

        // 写入时去重:同一行给两遍只留一条
        FriendStore.WriteList(me, new[] { other, other, official });
        r.Add("写入去重", FriendStore.ReadList(me).Count == 2, string.Join(" , ", FriendStore.ReadList(me)));
    }

    /// <summary>账号编码对齐官方 ID:按官方号建账号、改号成功/冲突失败、好友目录跟着搬</summary>
    private static void AccountIdCases(SuiteResult r)
    {
        var store = new AccountStore("users-align.ini");   //临时账号表
        r.Add("自增分配第一个账号 ID=1",
            store.Add("localone", new string('a', 64), "Player", out var first, out _) && first!.Id == 1, first?.Id.ToString());
        r.Add("自增分配第二个账号 ID=2",
            store.Add("localtwo", new string('b', 64), "Player", out var second, out _) && second!.Id == 2, second?.Id.ToString());

        // 官方账号:ID 由 ZXMOS 分配,镜像时照抄
        r.Add("按官方分配号建账号",
            store.Add("officialone", new string('c', 64), "Player", "", 7, out var official, out _) && official!.Id == 7, official?.Id.ToString());
        r.Add("官方号已被占用时拒绝",
            !store.Add("officialtwo", new string('d', 64), "Player", "", 7, out _, out var idError), idError);

        // 已有账号改号:以官方为准
        r.Add("对齐到新 ID 成功", store.AlignId("localtwo", 9, out var before, out _) && before == 2, before.ToString());
        r.Add("对齐后按新 ID 找得到", store.FindById(9)?.Name == "localtwo");
        r.Add("对齐后旧 ID 查不到", store.FindById(2) is null);
        r.Add("已经一致时直接成功", store.AlignId("localtwo", 9, out _, out _));
        r.Add("对齐到被占用的 ID 失败", !store.AlignId("localtwo", 1, out _, out var alignError), alignError);
        r.Add("对齐不存在的账号失败", !store.AlignId("nobody", 3, out _, out _));

        // 改号后好友目录要跟着走,不然等于换人
        var server = "ZXMS-ZES-1";   //服务端 ID
        var oldId = PlayerId.Parse(server + "-14");
        FriendStore.WriteList(oldId, new[] { PlayerId.Parse("ZXMS-HT-2-23") });
        r.Add("改号前旧目录有数据", FriendStore.ReadList(oldId).Count == 1);
        r.Add("搬好友目录返回 true", FriendStore.MoveAccountDir(server, 14, 15));
        r.Add("新编码读得到原来的一行", FriendStore.ReadList(PlayerId.Parse(server + "-15")).Count == 1);
        r.Add("旧编码已空", FriendStore.ReadList(oldId).Count == 0);

        // 目标编码已有数据时不许覆盖
        FriendStore.WriteList(PlayerId.Parse(server + "-16"), new[] { PlayerId.Parse("ZXMOS-5") });
        r.Add("目标已有数据时拒绝搬", !FriendStore.MoveAccountDir(server, 15, 16));
        r.Add("目标数据没被覆盖", FriendStore.ReadList(PlayerId.Parse(server + "-16"))[0] == PlayerId.Parse("ZXMOS-5"));
        r.Add("没有老数据时返回 true", FriendStore.MoveAccountDir(server, 99, 100));
        r.Add("编码相同返回 true", FriendStore.MoveAccountDir(server, 16, 16));
    }
}
