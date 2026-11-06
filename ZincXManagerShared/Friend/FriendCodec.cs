using System.Globalization;   // CultureInfo:格式化时间戳

namespace ZincXManagerShared.Friend;

/// <summary>好友列表里的一条(651 的一行)。</summary>
/// <param name="Id">对方完整ID。</param>
/// <param name="Name">对方账户名(头像按它找;跨服也要有,否则只能画问号)。</param>
/// <param name="Remark">我给对方起的备注(空 = 显示名字)。</param>
/// <param name="Online">当前是否在线。</param>
/// <param name="ServerName">对方所在服的显示名(本服为空)。</param>
public sealed record FriendEntry(string Id, string Name, string Remark, bool Online, string ServerName, int Unread = 0, string Preview = "");

/// <summary>别人发给我的一条好友申请(657 的一行)。</summary>
/// <param name="FromId">申请者完整ID。</param>
/// <param name="Message">验证消息。</param>
/// <param name="Time">申请时间(文本,服务端原样存)。</param>
public sealed record FriendRequestEntry(string FromId, string Message, string Time);

/// <summary>搜索命中的一条(664 的一行)。</summary>
/// <param name="Id">完整ID;本服不认识的账号也照样给地址。</param>
/// <param name="Name">名字;<c>#编号</c> = 只知道编号、不知道名字。</param>
/// <param name="Level">等级;<c>-</c> = 不知道。</param>
public sealed record SearchHit(string Id, string Name, string Level);

/// <summary>全网账号表里的一条(614 的一行)。</summary>
/// <param name="ServerId">归属服ID;空 = 该账号直连官方端注册,不属于任何子服。</param>
/// <param name="Code">账号在该归属服里的本地编号。</param>
/// <param name="Name">账号名。</param>
/// <param name="Level">等级。</param>
public sealed record NetworkAccount(string ServerId, int Code, string Name, string Level);

/// <summary>跨服中转包里剥出来的四段(615)。</summary>
/// <param name="FromId">来源完整ID。</param>
/// <param name="ToId">目标完整ID。</param>
/// <param name="Action">动作,见 <see cref="Network.Protocol.FriendRelayAction"/>。</param>
/// <param name="Extra">附加内容(验证消息等,可能为空也可能含竖线)。</param>
public sealed record FriendRelayPacket(string FromId, string ToId, string Action, string Extra);

/// <summary>
/// 好友相关负载的拼装与解析,全部是纯函数:进字符串、出 record。
///
/// <para>放一处的理由:各 handler 里不许再出现 <c>Split('|')</c>。
/// 格式一旦只在一个文件里,改格式就只改这里,不会漏掉某个分支。</para>
///
/// <para>约定:列表类负载「一行一条、<c>\n</c> 分隔」;行内字段优先用 <c>\t</c>,
/// 因为玩家的名字和聊天正文里可能有竖线,用竖线当分隔符迟早错位。
/// 例外是要跟旧版本对齐的几处(614 / 648 / 649),那几处固定用竖线。</para>
/// </summary>
public static class FriendCodec
{
    /// <summary>行分隔符。</summary>
    public const char LineSeparator = '\n';

    /// <summary>字段分隔符(好友列表/申请列表/私聊正文)。</summary>
    public const char FieldSeparator = '\t';

    /// <summary>旧格式用的竖线(614 / 648 / 649 / 664)。</summary>
    public const char PipeSeparator = '|';

    /// <summary>「不知道」占位符(等级、来源服名)。</summary>
    public const string Unknown = "-";

    // ---------------- 651 好友列表 ----------------

    /// <summary>把好友列表拼成 651 的负载:<c>完整ID\t名字\t备注\t在线\t来源服\t未读数\t最后一条</c>。</summary>
    public static string BuildFriendList(IEnumerable<FriendEntry> entries)
    {
        var lines = new List<string>();
        foreach (var one in entries)
        {
            lines.Add(string.Join(
                FieldSeparator,
                one.Id,
                Clean(one.Name),
                CleanRemark(one.Remark),
                one.Online ? "1" : "0",
                Clean(one.ServerName),
                one.Unread.ToString(CultureInfo.InvariantCulture),
                Clean(one.Preview)));
        }

        return string.Join(LineSeparator, lines);
    }

    /// <summary>
    /// 解析 651 的负载;坏行计数通过 <paramref name="badLines"/> 交回,由调用方记日志。
    /// 兼容 5 段(没有未读与摘要)和 4 段(连名字都没有)的旧格式,那时未读按 0、摘要按空。
    /// </summary>
    public static List<FriendEntry> ParseFriendList(string? payload, out int badLines)
    {
        var list = new List<FriendEntry>();
        badLines = 0;
        foreach (var line in SplitLines(payload))
        {
            var parts = line.Split(FieldSeparator);
            if (parts.Length < 4)
            {
                badLines++;
                continue;
            }

            var entry = parts.Length switch
            {
                >= 7 => new FriendEntry(parts[0].Trim(), parts[1], parts[2], parts[3] == "1", parts[4], ParseInt(parts[5]), parts[6]),
                6 => new FriendEntry(parts[0].Trim(), parts[1], parts[2], parts[3] == "1", parts[4], ParseInt(parts[5]), ""),
                5 => new FriendEntry(parts[0].Trim(), parts[1], parts[2], parts[3] == "1", parts[4], 0, ""),
                _ => new FriendEntry(parts[0].Trim(), "", parts[1], parts[2] == "1", parts[3], 0, ""),
            };
            list.Add(entry);
        }

        return list;
    }

    /// <summary>把一段文本当整数读,读不出来算 0。</summary>
    private static int ParseInt(string text) =>
        int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    // ---------------- 657 好友申请 ----------------

    /// <summary>把申请列表拼成 657 的负载。</summary>
    public static string BuildFriendRequests(IEnumerable<FriendRequestEntry> entries)
    {
        var lines = new List<string>();
        foreach (var one in entries)
        {
            lines.Add(string.Join(FieldSeparator, one.FromId, Clean(one.Message), Clean(one.Time)));
        }

        return string.Join(LineSeparator, lines);
    }

    /// <summary>解析 657 的负载。</summary>
    public static List<FriendRequestEntry> ParseFriendRequests(string? payload, out int badLines)
    {
        var list = new List<FriendRequestEntry>();
        badLines = 0;
        foreach (var line in SplitLines(payload))
        {
            var parts = line.Split(FieldSeparator, 3);
            if (parts.Length < 2)
            {
                badLines++;
                continue;
            }

            list.Add(new FriendRequestEntry(parts[0].Trim(), parts[1], parts.Length >= 3 ? parts[2] : ""));
        }

        return list;
    }

    // ---------------- 664 搜索结果 ----------------

    /// <summary>把一条搜索结果拼成 664 的一行(竖线,跟旧版本对齐)。</summary>
    public static string BuildSearchHit(SearchHit hit) =>
        string.Join(PipeSeparator, hit.Id, Clean(hit.Name), Clean(hit.Level));

    /// <summary>把搜索结果拼成 664 的负载。</summary>
    public static string BuildSearchHits(IEnumerable<SearchHit> hits) =>
        string.Join(LineSeparator, hits.Select(BuildSearchHit));

    /// <summary>解析 664 的负载(客户端用)。</summary>
    public static List<SearchHit> ParseSearchHits(string? payload, out int badLines)
    {
        var list = new List<SearchHit>();
        badLines = 0;
        foreach (var line in SplitLines(payload))
        {
            var parts = line.Split(PipeSeparator);
            if (parts.Length < 2)
            {
                badLines++;
                continue;
            }

            list.Add(new SearchHit(parts[0].Trim(), parts[1], parts.Length >= 3 ? parts[2] : Unknown));
        }

        return list;
    }

    // ---------------- 614 全网账号表 ----------------

    /// <summary>把全网账号表拼成 614 的负载(4 段,竖线)。</summary>
    public static string BuildNetworkAccounts(IEnumerable<NetworkAccount> accounts) =>
        string.Join(LineSeparator, accounts.Select(one => string.Join(PipeSeparator, one.ServerId, one.Code.ToString(CultureInfo.InvariantCulture), Clean(one.Name), Clean(one.Level))));

    /// <summary>
    /// 解析 614 的负载。兼容三种:
    /// 4 段 <c>归属服ID|编号|名称|等级</c>,
    /// 3 段 <c>完整ID|名称|等级</c>(官方端现在发的格式,首段是 <c>ZXMS-x-1-13</c> 或 <c>-13</c>),
    /// 3 段 <c>编号|名称|等级</c>(最老的格式,没有归属服)。
    /// 段数比 4 多 = 名字里带了竖线,丢弃并计数,不做「取前四段」的猜测。
    /// </summary>
    public static List<NetworkAccount> ParseNetworkAccounts(string? payload, out int badLines)
    {
        var list = new List<NetworkAccount>();
        badLines = 0;
        foreach (var line in SplitLines(payload))
        {
            var parts = line.Split(PipeSeparator);
            if (parts.Length is < 3 or > 4)
            {
                badLines++;
                continue;
            }

            var serverId = "";
            var codeText = parts[0];
            var name = parts[1];
            var level = parts[2];
            if (parts.Length == 4)
            {
                serverId = parts[0].Trim();
                codeText = parts[1];
                name = parts[2];
                level = parts[3];
            }
            else if (codeText.TrimStart().StartsWith('-'))
            {
                // 官方端用 -13 表示「账号 13,不属于任何子服」;负号是标记不是编号,取绝对值
                if (!int.TryParse(codeText, out var bare) || bare >= 0)
                {
                    badLines++;
                    continue;
                }

                serverId = "";
                codeText = (-bare).ToString(CultureInfo.InvariantCulture);
            }
            else if (!int.TryParse(codeText, out _))
            {
                // 首段不是数字:说明它是完整ID,拆出「服ID + 编号」;拆不出来就当脏行
                if (!PlayerId.TryParse(codeText, out var fullId) || fullId.IsEmpty)
                {
                    badLines++;
                    continue;
                }

                serverId = fullId.ServerId;
                codeText = fullId.Code.ToString(CultureInfo.InvariantCulture);
            }

            if (!int.TryParse(codeText, out var code) || code <= 0 || name.Length == 0)
            {
                badLines++;
                continue;
            }

            list.Add(new NetworkAccount(serverId, code, name, level));
        }

        return list;
    }

    // ---------------- 613 上报 ----------------

    /// <summary>把本服账号表拼成 613 的负载(3 段,竖线)。</summary>
    public static string BuildUserListUp(IEnumerable<(int Id, string Name, string Level)> accounts) =>
        string.Join(LineSeparator, accounts.Select(one => string.Join(PipeSeparator, one.Id.ToString(CultureInfo.InvariantCulture), Clean(one.Name), Clean(one.Level))));

    // ---------------- 400 私聊 ----------------

    /// <summary>把私聊拼成 400 的负载:<c>目标完整ID\t消息编号\t正文</c>(正文可含竖线和 tab,不受影响)。</summary>
    public static string BuildFriendMessage(string toId, string msgId, string text) =>
        $"{toId}{FieldSeparator}{Clean(msgId)}{FieldSeparator}{text}";

    /// <summary>
    /// 解析 400 的负载;返回 false = 缺目标 ID(正文空不空由调用方自己判)。
    /// 2 段的旧格式也认(那时没有消息编号,消息编号给空串,重发去重就失效,但消息照收)。
    /// </summary>
    public static bool TryParseFriendMessage(string? payload, out string toId, out string msgId, out string text)
    {
        toId = "";
        msgId = "";
        text = "";
        var raw = payload ?? "";
        var parts = raw.Split(FieldSeparator, 3);
        if (parts.Length < 2 || parts[0].Trim().Length == 0)
        {
            return false;
        }

        toId = parts[0].Trim();
        if (parts.Length == 2)
        {
            text = parts[1].Trim();
            return true;
        }

        msgId = parts[1].Trim();
        text = parts[2];
        return true;
    }

    /// <summary>服务端转发给收件人时的负载:<c>发送者完整ID|正文</c>。</summary>
    public static string BuildIncomingMessage(string fromId, string text) => $"{fromId}{PipeSeparator}{text}";

    /// <summary>解析服务端转来的 400(收到别人的私聊):<c>发送者完整ID|正文</c>。</summary>
    public static bool TryParseIncomingMessage(string? payload, out string fromId, out string text)
    {
        fromId = "";
        text = "";
        var raw = payload ?? "";
        var cut = raw.IndexOf(PipeSeparator);
        if (cut <= 0)
        {
            return false;
        }

        fromId = raw[..cut].Trim();
        text = raw[(cut + 1)..];
        return fromId.Length > 0;
    }

    // ---------------- 401 私聊回执 ----------------

    /// <summary>拼 401 的成功回执:<c>ok|消息编号|序号</c>,重发时多一段 <c>dup</c>。</summary>
    public static string BuildMessageAck(string msgId, int seq, bool duplicate) =>
        duplicate
            ? $"ok{PipeSeparator}{Clean(msgId)}{PipeSeparator}{seq}{PipeSeparator}dup"
            : $"ok{PipeSeparator}{Clean(msgId)}{PipeSeparator}{seq}";

    /// <summary>解析 401:<c>ok|消息编号|序号[|dup]</c>。</summary>
    public static bool TryParseMessageAck(string? payload, out string msgId, out int seq, out bool duplicate)
    {
        msgId = "";
        seq = 0;
        duplicate = false;
        var parts = (payload ?? "").Split(PipeSeparator);
        if (parts.Length < 3 || parts[0] != "ok")
        {
            return false;
        }

        msgId = parts[1].Trim();
        _ = int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seq);
        duplicate = parts.Length >= 4 && parts[3].Trim() == "dup";
        return msgId.Length > 0;
    }

    /// <summary>拼 401 的失败回执:<c>err|消息编号|原因</c>;拿不到编号的场景(未登录等)由调用方自己拼两段 <c>err|原因</c>。</summary>
    public static string BuildMessageError(string msgId, string reason) =>
        $"err{PipeSeparator}{Clean(msgId)}{PipeSeparator}{(reason ?? "").Trim()}";

    /// <summary>
    /// 解析 401 的三段失败回执;两段的旧格式 <c>err|原因</c> 不归这里管(编号给空串,返回 false)。
    /// </summary>
    public static bool TryParseMessageError(string? payload, out string msgId, out string reason)
    {
        msgId = "";
        reason = "";
        var parts = (payload ?? "").Split(PipeSeparator, 3);
        if (parts.Length < 3 || parts[0] != "err")
        {
            return false;
        }

        msgId = parts[1].Trim();
        reason = parts[2].Trim();
        return msgId.Length > 0 && reason.Length > 0;
    }

    // ---------------- 402 / 403 聊天记录 ----------------

    /// <summary>拼 402 的负载:<c>对方完整ID\t起点序号</c>(0 = 从头要)。</summary>
    public static string BuildHistoryQuery(string peerId, int afterSeq) =>
        $"{peerId}{FieldSeparator}{afterSeq.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>解析 402;起点序号缺省按 0。</summary>
    public static bool TryParseHistoryQuery(string? payload, out string peerId, out int afterSeq)
    {
        var parts = (payload ?? "").Split(FieldSeparator);
        peerId = parts.Length > 0 ? parts[0].Trim() : "";
        afterSeq = 0;
        if (parts.Length > 1)
        {
            _ = int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out afterSeq);
        }

        return peerId.Length > 0;
    }

    /// <summary>把聊天记录拼成 403:每行 <c>序号\t时间\tdirection\t正文</c>。</summary>
    public static string BuildHistoryReply(IEnumerable<ChatLine> lines)
    {
        var rows = new List<string>();
        foreach (var one in lines)
        {
            rows.Add(string.Join(
                FieldSeparator,
                one.Seq.ToString(CultureInfo.InvariantCulture),
                one.Time.ToString(ChatLog.TimeFormat, CultureInfo.InvariantCulture),
                one.Incoming ? "in" : "out",
                one.Text));
        }

        return string.Join(LineSeparator, rows);
    }

    /// <summary>解析 403;坏行计数通过 <paramref name="badLines"/> 交回。</summary>
    public static List<ChatLine> ParseHistoryReply(string? payload, out int badLines)
    {
        var list = new List<ChatLine>();
        badLines = 0;
        foreach (var line in SplitLines(payload))
        {
            var parts = line.Split(FieldSeparator, 4);
            if (parts.Length < 4
                || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq)
                || !DateTime.TryParseExact(parts[1].Trim(), ChatLog.TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                badLines++;
                continue;
            }

            list.Add(new ChatLine(seq, time, parts[2] == "in", parts[3]));
        }

        return list;
    }

    // ---------------- 404 / 405 回执与已读 ----------------

    /// <summary>拼 404 的负载:<c>对端完整ID\t消息编号或序号\t状态</c>,状态 = <c>delivered</c> / <c>read</c>。</summary>
    public static string BuildReceipt(string peerId, string key, string state) =>
        $"{peerId}{FieldSeparator}{Clean(key)}{FieldSeparator}{state}";

    /// <summary>解析 404。</summary>
    public static bool TryParseReceipt(string? payload, out string peerId, out string key, out string state)
    {
        var parts = (payload ?? "").Split(FieldSeparator);
        peerId = parts.Length > 0 ? parts[0].Trim() : "";
        key = parts.Length > 1 ? parts[1].Trim() : "";
        state = parts.Length > 2 ? parts[2].Trim() : "";
        return peerId.Length > 0 && state.Length > 0;
    }

    /// <summary>拼 405 的负载:<c>对端完整ID\t已读到第几号</c>。</summary>
    public static string BuildReadMark(string peerId, int seq) =>
        $"{peerId}{FieldSeparator}{seq.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>解析 405。</summary>
    public static bool TryParseReadMark(string? payload, out string peerId, out int seq)
    {
        var parts = (payload ?? "").Split(FieldSeparator);
        peerId = parts.Length > 0 ? parts[0].Trim() : "";
        seq = 0;
        if (parts.Length > 1)
        {
            _ = int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seq);
        }

        return peerId.Length > 0;
    }

    // ---------------- 654 / 658 好友申请 ----------------

    /// <summary>
    /// 拼 654 的负载:<c>目标完整ID\t验证消息\t备注</c>(消息里可以随便带竖线)。
    /// 备注是申请人自己给对方起的名字,被申请人看不到;统一写三段,不填就是空串。
    /// </summary>
    public static string BuildFriendRequest(string targetId, string message, string remark = "") =>
        $"{targetId}{FieldSeparator}{Clean(message)}{FieldSeparator}{CleanRemark(remark)}";

    /// <summary>
    /// 解析 654 的负载;返回 false = 缺目标 ID。
    /// 2 段的旧格式也认(那时没有备注,备注给空串)。
    /// </summary>
    public static bool TryParseFriendRequest(string? payload, out string targetId, out string message, out string remark)
    {
        targetId = "";
        message = "";
        remark = "";
        var parts = (payload ?? "").Split(FieldSeparator, 3);
        if (parts.Length < 2 || parts[0].Trim().Length == 0)
        {
            return false;
        }

        targetId = parts[0].Trim();
        message = parts[1].Trim();
        remark = parts.Length > 2 ? parts[2].Trim() : "";
        return true;
    }

    /// <summary>拼 658 的负载:<c>申请者完整ID\tyes|no</c>。</summary>
    public static string BuildFriendAnswer(string fromId, bool agree) => $"{fromId}{FieldSeparator}{(agree ? "yes" : "no")}";

    /// <summary>解析 658 的负载;返回 false = 缺申请者 ID。</summary>
    public static bool TryParseFriendAnswer(string? payload, out string fromId, out bool agree)
    {
        fromId = "";
        agree = false;
        var raw = payload ?? "";
        var cut = raw.IndexOf(FieldSeparator);
        if (cut <= 0)
        {
            return false;
        }

        fromId = raw[..cut].Trim();
        agree = string.Equals(raw[(cut + 1)..].Trim(), "yes", StringComparison.OrdinalIgnoreCase);
        return fromId.Length > 0;
    }

    // ---------------- 615 跨服中转 ----------------

    /// <summary>把跨服中转包拼成 615 的负载;附加段放最后,里面再有竖线也不影响前面三段。</summary>
    public static string BuildRelay(FriendRelayPacket packet) =>
        string.Join(PipeSeparator, packet.FromId, packet.ToId, packet.Action, packet.Extra ?? "");

    /// <summary>解析 615 的负载(只切前 3 个竖线)。</summary>
    public static bool TryParseRelay(string? payload, out FriendRelayPacket packet)
    {
        packet = new FriendRelayPacket("", "", "", "");
        var parts = (payload ?? "").Split(PipeSeparator, 4);
        if (parts.Length < 3)
        {
            return false;
        }

        packet = new FriendRelayPacket(parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), parts.Length >= 4 ? parts[3] : "");
        return packet.FromId.Length > 0 && packet.ToId.Length > 0 && packet.Action.Length > 0;
    }

    /// <summary>跨服私聊的附带段:<c>消息编号|正文</c>(编号可能为空,正文里的竖线不影响前面)。</summary>
    public static string BuildRelayedMessage(string msgId, string text) =>
        $"{Clean(msgId)}{PipeSeparator}{text}";

    // ---------------- 648 / 649 跨服头像 ----------------
    /// <summary>拼 648 的负载:<c>目标服务端ID|账户名|请求者完整ID</c>。</summary>
    public static string BuildAvatarRelay(string targetServerId, string accountName, string requesterId) =>
        string.Join(PipeSeparator, targetServerId, Clean(accountName), requesterId);

    /// <summary>解析 648 的负载。</summary>
    public static bool TryParseAvatarRelay(string? payload, out string targetServerId, out string accountName, out string requesterId)
    {
        targetServerId = "";
        accountName = "";
        requesterId = "";
        var parts = (payload ?? "").Split(PipeSeparator);
        if (parts.Length < 3)
        {
            return false;
        }

        targetServerId = parts[0].Trim();
        accountName = parts[1].Trim();
        requesterId = parts[2].Trim();
        return accountName.Length > 0;
    }

    /// <summary>拼 649 的负载:<c>账户名|base64|请求者完整ID</c>(base64 为空 = 那边也没有)。</summary>
    public static string BuildAvatarRelayAck(string accountName, byte[]? image, string requesterId) =>
        string.Join(PipeSeparator, Clean(accountName), Convert.ToBase64String(image ?? []), requesterId);

    /// <summary>解析 649 的负载(客户端/发起服两边都用这个,别各写一份)。</summary>
    public static bool TryParseAvatarRelayAck(string? payload, out string accountName, out byte[] image, out string requesterId)
    {
        accountName = "";
        image = [];
        requesterId = "";
        var parts = (payload ?? "").Split(PipeSeparator, 3);
        if (parts.Length < 2)
        {
            return false;
        }

        accountName = parts[0].Trim();
        requesterId = parts.Length >= 3 ? parts[2].Trim() : "";
        if (parts[1].Length > 0)
        {
            try
            {
                image = Convert.FromBase64String(parts[1]);
            }
            catch (FormatException)
            {
                // base64 坏了就当没有图,不抛给上层:头像拿不到不该让整条链路失败。
                image = [];
            }
        }

        return accountName.Length > 0;
    }

    // ---------------- 608 头像回执 ----------------

    /// <summary>拼 608 的负载:<c>账户名|base64</c>。</summary>
    public static string BuildAvatarAck(string accountName, byte[]? image) =>
        string.Join(PipeSeparator, Clean(accountName), Convert.ToBase64String(image ?? []));

    /// <summary>解析 608 的负载。</summary>
    public static bool TryParseAvatarAck(string? payload, out string accountName, out byte[] image)
    {
        accountName = "";
        image = [];
        var parts = (payload ?? "").Split(PipeSeparator, 2);
        if (parts.Length < 2)
        {
            return false;
        }

        accountName = parts[0].Trim();
        if (parts[1].Length > 0)
        {
            try
            {
                image = Convert.FromBase64String(parts[1]);
            }
            catch (FormatException)
            {
                image = [];
            }
        }

        return accountName.Length > 0;
    }

    // ---------------- 工具 ----------------

    /// <summary>按行切开,丢掉空行;空负载返回空集合。</summary>
    private static string[] SplitLines(string? payload) =>
        (payload ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Split(LineSeparator)
            .Where(line => line.Length > 0)
            .ToArray();

    /// <summary>把字段里的分隔符与换行换成空格,免得拼出来的行错位。</summary>
    private static string Clean(string? text) =>
        (text ?? "").Replace(PipeSeparator, ' ').Replace(FieldSeparator, ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();

    /// <summary>
    /// 备注专用:只洗掉会撑破「一行一条、tab 分列」的字符,竖线保留 ——
    /// 备注是玩家自己写的,「主T|奶位」这种写法不该被悄悄改成空格。
    /// </summary>
    private static string CleanRemark(string? text) =>
        (text ?? "").Replace(FieldSeparator, ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
}
