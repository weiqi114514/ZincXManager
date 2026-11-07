using System;  // Guid / DateTime / 异常
using System.Collections.Generic;  // List<SuiteResult>
using System.IO;  // 临时目录与文件读写
using System.Threading.Tasks;  // Task
using ZincXManagerShared.Friend;  // PlayerId / FriendCodec / FriendStore / ChatLog:好友功能的存储与编解码
using ZincXManagerShared.Network;  // FriendRelayAction:615 中转的动作常量

namespace ZincXManagerTests;

/// <summary>套件 17:好友功能(编解码 + 好友存档 + 聊天存档,纯存储单测,不起进程)</summary>
public static class FriendSuite
{
    /// <summary>跑一遍,结果追加到 results。</summary>
    public static Task RunAsync(List<SuiteResult> results, string ws)
    {
        var r = new SuiteResult { Name = "17 好友功能(编解码/好友档/聊天档)" };
        results.Add(r);

        var root = Path.Combine(ws, "_zxt", "friend-" + Guid.NewGuid().ToString("N")[..6]);
        var oldRoot = FriendStore.RootDirectory;
        Directory.CreateDirectory(root);

        try
        {
            // 把存储根指到临时目录,免得污染仓库里的 User/Friend
            FriendStore.RootDirectory = root;
            PlayerIdPart(r);
            CodecPart(r);
            FriendStorePart(r);
            ChatLogPart(r);
        }
        catch (Exception ex)
        {
            r.Fatal = ex.Message;
        }
        finally
        {
            FriendStore.RootDirectory = oldRoot;
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

    /// <summary>完整玩家 ID 的拼法、拆解与拒绝规则。</summary>
    private static void PlayerIdPart(SuiteResult r)
    {
        var child = new PlayerId("ZXMS-testserver-1", 14);
        r.Add("完整ID拼成 服ID-编号", child.Full == "ZXMS-testserver-1-14", child.Full);
        r.Add("编号为 0 时没有完整ID", new PlayerId("ZXMS-x-1", 0).Full == "" && new PlayerId("ZXMS-x-1", 0).IsEmpty);
        r.Add("子服ID按登录名与官方分配号拼", PlayerId.ChildServerId("testserver", 1) == "ZXMS-testserver-1", PlayerId.ChildServerId("testserver", 1));

        r.Add("拆子服ID", PlayerId.TryParse("ZXMS-testserver-1-14", out var p1) && p1.ServerId == "ZXMS-testserver-1" && p1.Code == 14, p1.Full);
        r.Add("拆官方服ID", PlayerId.TryParse("ZXMOS-5", out var p2) && p2.ServerId == "ZXMOS" && p2.Code == 5, p2.Full);
        r.Add("没有横线的不认", !PlayerId.TryParse("relay03", out _));
        r.Add("末段不是数字的不认", !PlayerId.TryParse("ZXMS-testserver-1-abc", out _));
        r.Add("末段是 0 的不认", !PlayerId.TryParse("ZXMS-testserver-1-0", out _));
        r.Add("不是 ZXMS-/ZXMOS 打头的不认", !PlayerId.TryParse("ACME-1-2", out _));
        r.Add("空串不认", !PlayerId.TryParse("", out _));

        r.Add("认子服ID本身", PlayerId.IsServerId("ZXMS-testserver2-2"));
        // 服ID与玩家ID形状一样(都是 ZXMS-名字-编号[-编号]),这个函数只看「最后一段是数字」,
        // 所以完整玩家ID也会被认成服ID。它只给 TryParse 校验前缀用,拆解另有 PlayerId.TryParse。
        r.Add("完整玩家ID在形状上也满足服ID(已知的宽口径)", PlayerId.IsServerId("ZXMS-testserver-1-14"));
        r.Add("ZXMOS 是合法服ID(官方端自己)", PlayerId.IsServerId(PlayerId.OfficialServerId));
        r.Add("没有编号的不是服ID", !PlayerId.IsServerId("ZXMS-testserver") && !PlayerId.IsServerId("testserver") && !PlayerId.IsServerId(""));
    }

    /// <summary>各个负载的拼装与解析(含旧格式兼容与脏行计数)。</summary>
    private static void CodecPart(SuiteResult r)
    {
        // 好友列表:8 段(最后一段是最后一条消息的时间)
        var list = new List<FriendEntry>
        {
            new("ZXMS-testserver2-2-13", "zeta2", "小z", true, "ZXMS-testserver2-2", 3, "在吗", 1762400000),
            new("ZXMS-testserver-1-5", "", "", false, "ZXMS-testserver-1"),
        };
        var text = FriendCodec.BuildFriendList(list);
        var back = FriendCodec.ParseFriendList(text, out var bad);
        r.Add("好友列表 8 段往返不丢字段", bad == 0 && back.Count == 2
            && back[0].Id == "ZXMS-testserver2-2-13" && back[0].Name == "zeta2" && back[0].Remark == "小z"
            && back[0].Online && back[0].Unread == 3 && back[0].Preview == "在吗", text);
        r.Add("最后一条消息的时间能往返", back.Count == 2 && back[0].LastAt == 1762400000 && back[1].LastAt == 0);
        r.Add("没名字的空段也认", back.Count == 2 && back[1].Name == "" && back[1].Remark == "" && !back[1].Online);

        // 旧格式:7 段(没有时间)、5 段(没有未读数与最后一条)、4 段(没有名字)
        var seven = "ZXMS-a-1-2\tname\t备注\t1\tZXMS-a-1\t0\t在吗";
        var backSeven = FriendCodec.ParseFriendList(seven, out var badSeven);
        r.Add("7 段旧格式能读,时间按 0", badSeven == 0 && backSeven.Count == 1 && backSeven[0].Preview == "在吗" && backSeven[0].LastAt == 0);
        var five = "ZXMS-a-1-2\tname\t备注\t1\tZXMS-a-1";
        var backFive = FriendCodec.ParseFriendList(five, out var badFive);
        r.Add("5 段旧格式能读,未读数按 0", badFive == 0 && backFive.Count == 1 && backFive[0].Unread == 0 && backFive[0].Preview == "");
        var four = "ZXMS-a-1-2\t\t备注\t0\tZXMS-a-1";
        r.Add("4 段旧格式能读,名字留空", FriendCodec.ParseFriendList(four, out _).Count == 1);
        r.Add("段数不够的行被计数跳过", FriendCodec.ParseFriendList("ZXMS-a-1-2\tname\n\n", out var badShort).Count == 0 && badShort == 1, badShort.ToString());

        // 搜索命中:3 段,竖线分隔
        var hits = FriendCodec.ParseSearchHits(FriendCodec.BuildSearchHits([new SearchHit("ZXMS-a-1-2", "weiqi2", "Player")]), out _);
        r.Add("搜索结果 3 段往返", hits.Count == 1 && hits[0].Id == "ZXMS-a-1-2" && hits[0].Name == "weiqi2" && hits[0].Level == "Player");

        // 全网账号表:4 段 / 3 段首段是完整ID / 3 段首段是数字 / 多于 4 段
        // 614 的字段分隔符是竖线(见 FriendCodec.BuildNetworkAccounts),不是 tab
        var fourSeg = "ZXMS-testserver2-2|13|zeta2|Player";
        var threeFull = "ZXMS-testserver-1-14|relay03|Player";
        var threeLegacy = "5|fr01|Player";
        var dirty = "ZXMS-x-1|9|名字里带了|竖线|Player";
        var accounts = FriendCodec.ParseNetworkAccounts(
            string.Join("\n", [fourSeg, threeFull, threeLegacy, dirty]), out var badAccounts);
        r.Add("4 段带归属服能读", accounts.Exists(a => a.ServerId == "ZXMS-testserver2-2" && a.Code == 13 && a.Name == "zeta2"));
        r.Add("3 段首段是完整ID也能读", accounts.Exists(a => a.ServerId == "ZXMS-testserver-1" && a.Code == 14 && a.Name == "relay03"));
        r.Add("3 段没有归属服时按编号认", accounts.Exists(a => a.ServerId == "" && a.Code == 5 && a.Name == "fr01"));
        r.Add("多于 4 段的脏行被丢弃并计数", badAccounts == 1 && !accounts.Exists(a => a.Name.Contains("竖线")), $"脏行 {badAccounts}");

        // 私聊
        var msg = FriendCodec.BuildFriendMessage("ZXMS-a-1-2", "m1", "你好|带竖线");
        r.Add("私聊上行是 tab 三段", FriendCodec.TryParseFriendMessage(msg, out var toId, out var msgId, out var body)
            && toId == "ZXMS-a-1-2" && msgId == "m1" && body == "你好|带竖线", msg);
        r.Add("旧版两段私聊也认", FriendCodec.TryParseFriendMessage("ZXMS-a-1-2\t正文", out var to2, out var id2, out var body2)
            && to2 == "ZXMS-a-1-2" && id2 == "" && body2 == "正文");
        r.Add("转给对端的是竖线两段", FriendCodec.TryParseIncomingMessage(FriendCodec.BuildIncomingMessage("ZXMS-b-2-1", "在吗"), out var from3, out var body3)
            && from3 == "ZXMS-b-2-1" && body3 == "在吗");

        // 回执
        r.Add("回执带序号", FriendCodec.TryParseMessageAck(FriendCodec.BuildMessageAck("m1", 3, false), out var ackId, out var ackSeq, out var dup)
            && ackId == "m1" && ackSeq == 3 && !dup);
        r.Add("重发回执标 dup", FriendCodec.TryParseMessageAck(FriendCodec.BuildMessageAck("m1", 3, true), out _, out _, out var dup2) && dup2);
        r.Add("失败回执不是 ok", !FriendCodec.TryParseMessageAck("err|对方不在线", out _, out _, out _));

        // 历史与已读
        var history = FriendCodec.BuildHistoryReply([new ChatLine(1, new DateTime(2026, 11, 2, 19, 2, 28), true, "第一条")]);
        var lines = FriendCodec.ParseHistoryReply(history, out var badHistory);
        r.Add("聊天记录往返保住序号与方向", badHistory == 0 && lines.Count == 1 && lines[0].Seq == 1 && lines[0].Incoming && lines[0].Text == "第一条", history);
        r.Add("拉历史的负载是 tab 两段", FriendCodec.TryParseHistoryQuery(FriendCodec.BuildHistoryQuery("ZXMS-a-1-2", 7), out var peer, out var after)
            && peer == "ZXMS-a-1-2" && after == 7);
        r.Add("已读标记往返", FriendCodec.TryParseReadMark(FriendCodec.BuildReadMark("ZXMS-a-1-2", 4), out _, out var readSeq) && readSeq == 4);
        r.Add("送达/已读回执往返", FriendCodec.TryParseReceipt(FriendCodec.BuildReceipt("ZXMS-a-1-2", "m1", "delivered"), out var rPeer, out var rKey, out var rState)
            && rPeer == "ZXMS-a-1-2" && rKey == "m1" && rState == "delivered");

        // 申请与中转
        r.Add("好友申请不带备注", FriendCodec.TryParseFriendRequest(FriendCodec.BuildFriendRequest("ZXMS-a-1-2", "我是"), out var reqTo, out var reqMsg, out var noRemark)
            && reqTo == "ZXMS-a-1-2" && reqMsg == "我是" && noRemark.Length == 0);
        r.Add("好友申请带备注", FriendCodec.TryParseFriendRequest(FriendCodec.BuildFriendRequest("ZXMS-a-1-2", "我是", "老王"), out _, out _, out var reqRemark) && reqRemark == "老王");
        r.Add("旧格式两段申请也能解析", FriendCodec.TryParseFriendRequest("ZXMS-a-1-2\t我是谁", out _, out var oldMsg, out var oldRemark) && oldMsg == "我是谁" && oldRemark.Length == 0);
        r.Add("备注里的竖线保留", FriendCodec.TryParseFriendRequest(FriendCodec.BuildFriendRequest("ZXMS-a-1-2", "我是", "主T|奶位"), out _, out _, out var pipeRemark) && pipeRemark == "主T|奶位");
        r.Add("失败回执三段解析", FriendCodec.TryParseMessageError(FriendCodec.BuildMessageError("abc123", "你们还不是好友"), out var errId, out var errReason)
            && errId == "abc123" && errReason == "你们还不是好友");
        r.Add("两段 err 不误判成三段", !FriendCodec.TryParseMessageError("err|请先登录", out _, out _));
        r.Add("回答同意/拒绝", FriendCodec.TryParseFriendAnswer(FriendCodec.BuildFriendAnswer("ZXMS-a-1-2", true), out _, out var agree) && agree
            && FriendCodec.TryParseFriendAnswer(FriendCodec.BuildFriendAnswer("ZXMS-a-1-2", false), out _, out var refuse) && !refuse);

        // 614 里「不属于任何子服」的账号用「-编号」表示,负号是标记不是编号
        var minusRows = FriendCodec.ParseNetworkAccounts("-13|阿丽丝|5", out var minusBad);
        r.Add("614 的 -13 不再当脏行丢掉", minusBad == 0 && minusRows.Count == 1 && minusRows[0].ServerId.Length == 0
            && minusRows[0].Code == 13 && minusRows[0].Name == "阿丽丝", minusRows.Count.ToString());

        var relay = new FriendRelayPacket("ZXMS-a-1-2", "ZXMS-b-2-3", Protocol.FriendRelayAction.Message, "m1|正文里也有|竖线");
        r.Add("中转包正文里的竖线不破坏分段", FriendCodec.TryParseRelay(FriendCodec.BuildRelay(relay), out var back1)
            && back1.FromId == "ZXMS-a-1-2" && back1.ToId == "ZXMS-b-2-3"
            && back1.Action == Protocol.FriendRelayAction.Message && back1.Extra == "m1|正文里也有|竖线");

        // 头像:空图与有图
        var emptyAck = FriendCodec.BuildAvatarRelayAck("relay03", null, "ZXMS-b-2-13");
        r.Add("空头像中继回执能读出「没有」", FriendCodec.TryParseAvatarRelayAck(emptyAck, out var aName, out var aImage, out var aWho)
            && aName == "relay03" && aImage.Length == 0 && aWho == "ZXMS-b-2-13");
        var bytes = new byte[] { 1, 2, 3, 4 };
        r.Add("有头像中继回执能还原字节", FriendCodec.TryParseAvatarRelayAck(FriendCodec.BuildAvatarRelayAck("relay03", bytes, "ZXMS-b-2-13"), out _, out var aImage2, out _)
            && aImage2.Length == 4 && aImage2[3] == 4);
        r.Add("要头像的负载三段", FriendCodec.TryParseAvatarRelay(FriendCodec.BuildAvatarRelay("ZXMS-testserver-1", "relay03", "ZXMS-b-2-13"), out var target, out var account, out var who)
            && target == "ZXMS-testserver-1" && account == "relay03" && who == "ZXMS-b-2-13");
        r.Add("头像回执空图", FriendCodec.TryParseAvatarAck(FriendCodec.BuildAvatarAck("relay03", null), out var cName, out var cImage) && cName == "relay03" && cImage.Length == 0);
    }

    /// <summary>好友关系、好友申请、已读游标的落盘。</summary>
    private static void FriendStorePart(SuiteResult r)
    {
        var me = new PlayerId("ZXMS-testserver-1", 14);
        var peer = new PlayerId("ZXMS-testserver2-2", 13);

        r.Add("没有档时好友列表是空的", FriendStore.ReadFriends(me, out _).Count == 0);
        r.Add("没有档时也不是好友", !FriendStore.AreFriends(me, peer));

        r.Add("加好友成功", FriendStore.AddFriend(me, peer.Full, "小z", false, "ZXMS-testserver2-2", out var e1), e1);
        r.Add("加完就是好友", FriendStore.AreFriends(me, peer));
        r.Add("反向还没写,不算好友(关系由调用方各写一次)", !FriendStore.AreFriends(peer, me));
        r.Add("好友列表存成 5 列以上且名字留空", File.ReadAllText(FriendStore.FriendsPathOf(me)).Contains(peer.Full) && FriendStore.ReadFriends(me, out _)[0].Name == "");

        r.Add("重复加只更新备注", FriendStore.AddFriend(me, peer.Full, "改过的备注", true, "ZXMS-testserver2-2", out var e2)
            && e2.Length == 0 && FriendStore.ReadFriends(me, out _).Count == 1
            && FriendStore.ReadFriends(me, out _)[0].Remark == "改过的备注");

        r.Add("删好友成功", FriendStore.RemoveFriend(me, peer.Full, out var e3), e3);
        r.Add("删完就不是好友了", !FriendStore.AreFriends(me, peer));
        r.Add("删不存在的好友也算成功", FriendStore.RemoveFriend(me, peer.Full, out var e4) && e4.Length == 0);

        // 没有归属服的账号落在 Local 目录
        var noServer = new PlayerId("", 12);
        FriendStore.AddFriend(noServer, peer.Full, "", false, "ZXMS-testserver2-2", out _);
        r.Add("没有归属服时目录名是 Local", FriendStore.DirectoryOf(noServer).EndsWith(Path.Combine("Local", "12")), FriendStore.DirectoryOf(noServer));

        // 好友申请:同一个人的旧申请被替换
        r.Add("加申请成功", FriendStore.AppendRequest(me, new FriendRequestEntry(peer.Full, "第一次", "2026-11-02 18:00:00"), out var e5), e5);
        FriendStore.AppendRequest(me, new FriendRequestEntry(peer.Full, "第二次", "2026-11-02 18:05:00"), out _);
        var requests = FriendStore.ReadRequests(me, out _);
        r.Add("同一个人的申请只留最新一条", requests.Count == 1 && requests[0].Message == "第二次", requests.Count.ToString());
        r.Add("申请里的时间原样留着", requests[0].Time == "2026-11-02 18:05:00");
        r.Add("删申请成功", FriendStore.RemoveRequest(me, peer.Full, out var e6) && e6.Length == 0 && FriendStore.ReadRequests(me, out _).Count == 0);

        // 已读游标:只往后不改小
        r.Add("没读过时游标是 0", FriendStore.ReadCursor(me, peer.Full) == 0);
        FriendStore.WriteCursor(me, peer.Full, 3);
        r.Add("写游标后能读回", FriendStore.ReadCursor(me, peer.Full) == 3);
        FriendStore.WriteCursor(me, peer.Full, 1);
        r.Add("游标不会被改小", FriendStore.ReadCursor(me, peer.Full) == 3);
        FriendStore.WriteCursor(me, peer.Full, 9);
        r.Add("游标能往后推", FriendStore.ReadCursor(me, peer.Full) == 9);
        FriendStore.WriteCursor(me, "ZXMS-other-1-1", 2);
        r.Add("另一个好友的游标不受影响", FriendStore.ReadCursor(me, peer.Full) == 9 && FriendStore.ReadCursor(me, "ZXMS-other-1-1") == 2);

        // 待生效备注(申请时填的,同意那一刻才落档)
        r.Add("没写过的待生效备注取出来是空", FriendStore.TakePendingRemark(me, peer.Full).Length == 0);
        FriendStore.SetPendingRemark(me, peer.Full, "申请时起的名");
        r.Add("待生效备注能取回", FriendStore.TakePendingRemark(me, peer.Full) == "申请时起的名");
        r.Add("取走就删,第二次取是空", FriendStore.TakePendingRemark(me, peer.Full).Length == 0);
        FriendStore.SetPendingRemark(me, peer.Full, "第一个");
        FriendStore.SetPendingRemark(me, peer.Full, "第二个");
        r.Add("同一目标的备注被覆盖", FriendStore.TakePendingRemark(me, peer.Full) == "第二个");
        FriendStore.SetPendingRemark(me, "ZXMS-other-1-1", "别人");
        r.Add("不同目标的备注互不影响", FriendStore.TakePendingRemark(me, peer.Full).Length == 0 && FriendStore.TakePendingRemark(me, "ZXMS-other-1-1") == "别人");
        FriendStore.SetPendingRemark(me, "ZXMS-other-1-1", "");
        r.Add("备注清空等于删掉这条", FriendStore.TakePendingRemark(me, "ZXMS-other-1-1").Length == 0 && !File.Exists(FriendStore.PendingRemarksPathOf(me)));
    }

    /// <summary>聊天记录的序号、增量读取、单行化。</summary>
    private static void ChatLogPart(SuiteResult r)
    {
        var me = new PlayerId("ZXMS-testserver-1", 14);
        var peer = "ZXMS-testserver2-2-13";
        var t0 = new DateTime(2026, 11, 2, 19, 2, 28);

        r.Add("没有聊天文件时序号是 0", ChatLog.LastSeq(me, peer) == 0);

        r.Add("第一条是 out,分到序号 1", ChatLog.Append(me, peer, false, "第一条", t0, out var s1, out var e1) && s1 == 1, e1);
        r.Add("第二条是 in,分到序号 2", ChatLog.Append(me, peer, true, "第二条", t0.AddMinutes(1), out var s2, out _) && s2 == 2);
        r.Add("第三条分到序号 3", ChatLog.Append(me, peer, false, "第三条", t0.AddMinutes(2), out var s3, out _) && s3 == 3);
        r.Add("写三条后最后序号是 3", ChatLog.LastSeq(me, peer) == 3);

        var all = ChatLog.Read(me, peer, 0, 0, out var readError);
        r.Add("全读拿到三条且方向正确", readError.Length == 0 && all.Count == 3 && !all[0].Incoming && all[1].Incoming && all[2].Text == "第三条");

        var after1 = ChatLog.Read(me, peer, 1, 0, out _);
        r.Add("从序号 1 之后增量读只拿后两条", after1.Count == 2 && after1[0].Seq == 2, string.Join(",", after1.ConvertAll(one => one.Seq)));
        var afterAll = ChatLog.Read(me, peer, 3, 0, out _);
        r.Add("读到最新号之后再拉是空的", afterAll.Count == 0);
        var lastOne = ChatLog.Read(me, peer, 0, 1, out _);
        r.Add("只要最近 1 条时给的是最后一条", lastOne.Count == 1 && lastOne[0].Seq == 3 && lastOne[0].Text == "第三条");

        ChatLog.Append(me, peer, false, "换行\n和\t制表符", t0.AddMinutes(3), out var s4, out _);
        var withNewline = ChatLog.Read(me, peer, 3, 0, out _);
        r.Add("正文里的换行与制表符被压成空格", s4 == 4 && withNewline.Count == 1 && withNewline[0].Text == "换行 和 制表符", withNewline[0].Text);

        r.Add("聊天记录落在账号目录的 Ch 下", ChatLog.PathOf(me, peer).Replace('\\', '/').EndsWith("/ZXMS-testserver-1/14/Ch/ZXMS-testserver2-2-13.dat"), ChatLog.PathOf(me, peer));
        r.Add("两个账号的聊天记录不串", ChatLog.LastSeq(new PlayerId("ZXMS-testserver-1", 1), peer) == 0);

        var timeLine = File.ReadAllLines(ChatLog.PathOf(me, peer))[0].Split('\t');
        r.Add("落盘是 时间 / 方向 / 正文 三段", timeLine.Length == 3 && timeLine[0] == "2026-11-02 19:02:28" && timeLine[1] == "out", string.Join("|", timeLine));
    }
}

