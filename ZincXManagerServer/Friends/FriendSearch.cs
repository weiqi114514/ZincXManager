using System.Text;                     // Encoding:读包负载
using ZincXManagerShared.Account;       // Account:本服账号
using ZincXManagerShared.FileIO;        // ZFile:config.ini
using ZincXManagerShared.Friend;        // PlayerId / FriendCodec / SearchHit
using ZincXManagerShared.Logging;       // Logger / LogLevel
using ZincXManagerShared.Network;       // Protocol / ServerboundPacket

namespace ZincXManagerServer;

/// <summary>
/// 搜好友(663 / 664)。
///
/// <para>规则(定死的,别改):关键词按最后一个横线切出编码段,能转成数字就按编号找,否则按名字找;
/// 名字必须<b>完全一致</b>,不做前缀/模糊。只查账号表,不看在不在线。跨服的人靠全网账号表补。</para>
/// </summary>
internal static partial class Program
{
    /// <summary>663 入口。</summary>
    private static async Task HandleFriendSearchAsync(ServerboundPacket packet, ZFile config)
    {
        var keyword = Encoding.UTF8.GetString(packet.Data).Trim();   //玩家输入的原样
        var self = PlayerOf(packet.Connection.Id, config);           //搜索者
        var hits = new List<SearchHit>();

        // 没登录就不给搜:否则任何连上来的连接都能按名字/编号把本服和全网账号表枚举一遍
        if (self.IsEmpty)
        {
            await packet.Connection.SendAsync(Protocol.FriendSearchReply, FriendCodec.BuildSearchHits(hits), packet.Id);
            Logger.Log(LogLevel.Warning, $"[好友] 未登录的连接要搜好友,已回空结果");
            return;
        }

        if (keyword.Length > 0)
        {
            if (PlayerId.TryParse(keyword, out var direct))
            {
                CollectDirectHits(direct, self, config, hits);
            }
            else
            {
                CollectNamedHits(keyword, self, config, hits);
            }
        }

        await packet.Connection.SendAsync(Protocol.FriendSearchReply, FriendCodec.BuildSearchHits(hits), packet.Id);

        // 关键词清掉换行,否则一条日志被拆成好几行
        var logKeyword = keyword.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        if (logKeyword.Length > 64)
        {
            logKeyword = logKeyword[..64] + "…";
        }

        Logger.Log(LogLevel.Info,
            $"[好友] {(self.IsEmpty ? "(未登录)" : self.Full)} 搜好友:\"{logKeyword}\",结果 {hits.Count} 条");
    }

    /// <summary>
    /// 玩家直接输了完整ID(或至少是「服ID-编号」的形状)。
    /// 本服认识的给真名和等级;不认识的只知道编号 → 按占位回,成不成由对方服决定。
    /// </summary>
    private static void CollectDirectHits(PlayerId direct, PlayerId self, ZFile config, List<SearchHit> hits)
    {
        if (!direct.IsEmpty && direct == self)
        {
            return;   // 搜自己不算命中
        }

        var isLocal = SameServer(direct.ServerId, SelfServerId(config));
        if (isLocal)
        {
            var account = Accounts.FindById(direct.Code);
            if (account is not null)
            {
                hits.Add(new SearchHit(direct.Full, account.Name, account.Level));
                return;
            }
        }

        // 本服没有 / 属于别服:查全网账号表,查得到就给真实名字和等级
        if (NetworkAccounts.TryGetValue(direct.Full, out var known))
        {
            hits.Add(new SearchHit(direct.Full, known.Name, known.Level));
            return;
        }

        // 哪儿都不认识:只回地址,名字显示成 #编号
        hits.Add(new SearchHit(direct.Full, "#" + direct.Code, FriendCodec.Unknown));
    }

    /// <summary>
    /// 玩家输的是编号或名字:先查本服账号表,再拿全网账号表补跨服的人。
    /// </summary>
    private static void CollectNamedHits(string keyword, PlayerId self, ZFile config, List<SearchHit> hits)
    {
        var wantCode = ParseTrailingCode(keyword);   // 只认最后一段编码,比如 "ZXMS-x-1-5" → 5
        var localHits = 0;

        foreach (var account in Accounts.All)
        {
            if (hits.Count >= Protocol.FriendSearchLimit)
            {
                return;
            }

            if (IsSelf(account, self, config) || !Matches(account.Name, account.Id, keyword, wantCode))
            {
                continue;
            }

            hits.Add(new SearchHit(LocalPlayerId(account, config).Full, account.Name, account.Level));
            localHits++;
        }

        CollectNetworkHits(keyword, wantCode, localHits, self, hits);
    }

    /// <summary>全网账号表补漏:跨服的人只有这张表里有。跳过本服已命中的编号,免得同一个人出两行。</summary>
    private static void CollectNetworkHits(string keyword, int wantCode, int localHits, PlayerId self, List<SearchHit> hits)
    {
        if (localHits > 0 && wantCode <= 0)
        {
            return;   // 按名字搜到本服的人就够了,不必再翻全网表
        }

        foreach (var account in NetworkAccounts.Values.OrderBy(one => one.Code).ThenBy(one => one.ServerId, StringComparer.OrdinalIgnoreCase))
        {
            if (hits.Count >= Protocol.FriendSearchLimit)
            {
                return;
            }

            var id = PlayerIdOf(account);
            if (id.IsEmpty || id == self || !Matches(account.Name, account.Code, keyword, wantCode))
            {
                continue;
            }

            // 本服已经命中过同一个编号(可能是同一台的镜像账号),不再重复
            if (localHits > 0 && hits.Any(hit => hit.Id.EndsWith("-" + account.Code, StringComparison.Ordinal)))
            {
                continue;
            }

            hits.Add(new SearchHit(id.Full, account.Name, account.Level));
        }
    }

    /// <summary>关键词是否命中这个账号:编号相等,或名字完全一致(忽略大小写)。</summary>
    private static bool Matches(string name, int code, string keyword, int wantCode)
    {
        if (wantCode > 0 && code == wantCode)
        {
            return true;
        }

        return string.Equals(name, keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>这个账号是不是搜索者自己。</summary>
    private static bool IsSelf(Account account, PlayerId self, ZFile config)
    {
        if (self.IsEmpty)
        {
            return false;
        }

        return SameServer(self.ServerId, SelfServerId(config)) && account.Id == self.Code;
    }

    /// <summary>取关键词里最后一个横线之后的编码;不是数字返回 0。</summary>
    private static int ParseTrailingCode(string keyword)
    {
        var dash = keyword.LastIndexOf('-');
        var tail = dash >= 0 ? keyword[(dash + 1)..] : keyword;
        return int.TryParse(tail, out var code) ? code : 0;
    }
}
