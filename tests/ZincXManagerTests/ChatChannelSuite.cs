using System;  // DateTime / Uri
using System.Collections.Generic;  // Dictionary:配置项
using System.IO;  // File / Directory:清掉模板里的开发机数据
using System.Linq;  // TakeLast:取日志最后几行当证据
using System.Net.Http;  // HttpClient:直接调聊天宿主
using System.Net.Http.Headers;  // AuthenticationHeaderValue:Bearer 令牌
using System.Text;  // Encoding:包负载
using System.Threading.Tasks;  // Task
using ZincXManagerShared.Account;  // PasswordHash
using ZincXManagerShared.Chat;  // ChatToken / ChatChannel
using ZincXManagerShared.Friend;  // FriendCodec / PlayerId
using ZincXManagerShared.Network;  // Protocol:510 / 654 / 658

namespace ZincXManagerTests;

/// <summary>
/// 套件 23:聊天频道号一致(510 第四段)与好友私聊真收发。
///
/// <para>要钉死的是现场那个 bug:中转登录时 601 第三段是<b>官方编号</b>,本服编号是另一个数;
/// 客户端拿错的那个算频道号,就会算出服务端没建的频道,每条请求都 404。</para>
///
/// <para>所以这里真起 ZXMOS + ZXMS + 聊天宿主,让两个官方账号走中转登录加好友,
/// 断言 510 第四段 = 自己令牌里签的编号,并且用这两张令牌能在同一个频道里<b>真的发一条、读回来</b>。</para>
/// </summary>
public static class ChatChannelSuite
{
    /// <summary>跑一遍,结果追加到 results。</summary>
    /// <param name="results">总表。</param>
    /// <param name="ws">测试工作区。</param>
    /// <param name="debugMs">ZXMS 开发态输出目录。</param>
    /// <param name="debugOs">ZXMOS 开发态输出目录。</param>
    /// <param name="debugChat">聊天宿主开发态输出目录。</param>
    public static async Task RunAsync(List<SuiteResult> results, string ws, string debugMs, string debugOs, string debugChat)
    {
        var r = new SuiteResult { Name = "23 聊天频道号一致(510 第四段 + 私聊真收发)" };

        const int chatPort = 21794;   // 聊天宿主端口
        const int ozport = 21795;   // 官方服务端端口
        const int czport = 21796;   // 子服客户端接入端口
        const int zxmmPort = 21797;   // 假 ZXMM 端口

        // 两个进程都靠这个放行口跳过 official.lic;退出时恢复原值
        var previousBypass = Environment.GetEnvironmentVariable("ZXM_ALLOW_UNOFFICIAL");
        Environment.SetEnvironmentVariable("ZXM_ALLOW_UNOFFICIAL", "1", EnvironmentVariableTarget.Process);

        try
        {
            // ---------- 纯计算的两条:频道号拼法与「从令牌里读编号」 ----------
            r.Add("频道号把两个编号按大小排:2 与 22 → dm-2-22",
                ChatChannel.ForPair(22, 2) == "dm-2-22", ChatChannel.ForPair(22, 2));
            var probeToken = ChatToken.Sign("0123456789abcdef0123456789abcdef", "7", "某人", 60);   //临时令牌
            r.Add("从令牌 payload 里能读回签发时的编号",
                ChatToken.TryReadUserId(probeToken, out var signedUserId) && signedUserId == "7", signedUserId);
            r.Add("畸形令牌读不出编号(不许猜)",
                !ChatToken.TryReadUserId("garbage", out _) && !ChatToken.TryReadUserId("", out _), "");

            // ---------- 三级兜底:510 第四段 → 令牌 → 会话 ----------
            var secret = "0123456789abcdef0123456789abcdef";   //与聊天宿主共享的密钥
            var token7 = ChatToken.Sign(secret, "7", "某人", 60);   //第四段缺失时靠它认出我是 7
            var tokenNan = token7[..token7.IndexOf('.')];   //只有 payload 没有签名,用来造「读不出编号」

            r.Add("第一级:第四段有就用它,压过令牌与会话",
                ChatHello.ResolveSelfCode(new[] { "21300", token7, "ZXMS-x-1-7", "9" }, token7, "3") == 9, "第四段 9");
            r.Add("第一级:第四段是 0 或负数当没有,继续往下退",
                ChatHello.ResolveSelfCode(new[] { "21300", token7, "ZXMS-x-1-7", "0" }, token7, "3") == 7
                && ChatHello.ResolveSelfCode(new[] { "21300", token7, "ZXMS-x-1-7", "-4" }, token7, "3") == 7, "令牌 7");
            r.Add("第一级:第四段不是数字时不猜,退到令牌",
                ChatHello.ResolveSelfCode(new[] { "21300", token7, "ZXMS-x-1-7", "abc" }, token7, "3") == 7, "令牌 7");
            r.Add("第一级:第四段带空格也能认",
                ChatHello.ResolveSelfCode(new[] { "21300", token7, "ZXMS-x-1-7", " 11 " }, token7, "") == 11, "11");

            r.Add("第二级:老服务端只有三段 → 读令牌里的编号",
                ChatHello.ResolveSelfCode(new[] { "21300", token7, "ZXMS-x-1-7" }, token7, "") == 7, "令牌 7");
            r.Add("第二级:令牌比会话优先(中转登录的会话编号是官方编号,不能用)",
                ChatHello.ResolveSelfCode(new[] { "21300", token7, "ZXMS-x-1-7" }, token7, "4") == 7, "令牌 7 压过会话 4");
            r.Add("第二级:令牌读不出编号才退到会话",
                ChatHello.ResolveSelfCode(new[] { "21300", tokenNan, "ZXMS-x-1-7" }, tokenNan, "4") == 4, "会话 4");
            r.Add("第二级:空令牌也不抛,直接退到会话",
                ChatHello.ResolveSelfCode(new[] { "21300", "", "ZXMS-x-1-7" }, "", "5") == 5, "会话 5");

            r.Add("第三级:三段都取不到(会话空)就返回 0",
                ChatHello.ResolveSelfCode(new[] { "21300", tokenNan, "ZXMS-x-1-7" }, tokenNan, "") == 0, "0");
            r.Add("第三级:会话是 null 也不抛,返回 0",
                ChatHello.ResolveSelfCode(new[] { "21300", tokenNan, "ZXMS-x-1-7" }, tokenNan, null) == 0, "0");
            r.Add("第三级:会话不是数字返回 0",
                ChatHello.ResolveSelfCode(new[] { "21300", tokenNan, "ZXMS-x-1-7" }, tokenNan, "ZXMS-x-1-9") == 0, "0");
            r.Add("三级都取不到时频道号算不出来,Ready 必然 false(不许拿 0 去拼 dm-0-x)",
                ChatHello.ResolveSelfCode(new[] { "21300", "", "ZXMS-x-1-7" }, "", null) == 0, "");

            // ---------- 官方服务端 ----------
            var osDir = Svc.PrepareBin(debugOs, Path.Combine(ws, "_zxt", "chat-channel-os"));
            foreach (var staleDir in new[] { "User", "Data" })
            {
                var path = Path.Combine(osDir, staleDir);
                if (Directory.Exists(path)) { Directory.Delete(path, true); }
            }

            var staleLic = Path.Combine(osDir, "official.lic");
            if (File.Exists(staleLic)) { File.Delete(staleLic); }

            using var os = Svc.Start(Path.Combine(osDir, "ZincXManagerOServer.exe"), osDir, new Dictionary<string, string>
            {
                ["port"] = ozport.ToString(),
                ["name"] = "CSharp-聊天频道官方端",
            });

            r.Add("ZXMOS 监听 " + ozport, os.WaitPort(ozport, 25), LastLines(os, 2));

            // ---------- 子服(连官方端 + 开聊天宿主) ----------
            var msDir = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "chat-channel-ms"));
            foreach (var stale in new[] { "PersistentLogin.txt", "loginlog.log", "BindUser.txt" })
            {
                var path = Path.Combine(msDir, stale);
                if (File.Exists(path)) { File.Delete(path); }
            }

            foreach (var staleDir in new[] { "Data", "User" })
            {
                var path = Path.Combine(msDir, staleDir);
                if (Directory.Exists(path)) { Directory.Delete(path, true); }
            }

            var chatExe = Path.Combine(debugChat, "ZincXManagerChat.exe");   //聊天宿主可执行文件
            if (!File.Exists(chatExe))
            {
                r.Fatal = "聊天宿主没构建:" + chatExe;
                results.Add(r);
                return;
            }

            using var fake = new FakeZxmm(zxmmPort);
            fake.Start();

            const string osUser = "ZXMS-Chat-Channel";   // 子服在官方端的服务端账号名
            var osHash = PasswordHash.Hash(osUser, "pw123456");   // 服务端账号密文
            var serverCode = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
                .ToLowerInvariant();   // 本实例服务端码

            using var ms = Svc.Start(Path.Combine(msDir, "ZincXManagerServer.exe"), msDir, new Dictionary<string, string>
            {
                ["configver"] = "2",
                ["connectOS"] = "1",
                ["linkname"] = "0",
                ["name"] = "CSharp-聊天频道子服",
                ["czport"] = czport.ToString(),
                ["ozip"] = "127.0.0.1",
                ["ozport"] = ozport.ToString(),
                ["osuser"] = osUser,
                ["oshash"] = osHash,
                ["oslevel"] = "Admin",
                ["servercode"] = serverCode,
                ["zxmmapi"] = "127.0.0.1:" + zxmmPort,
                ["chatport"] = chatPort.ToString(),
                ["chatexe"] = chatExe,
                ["chatsecret"] = "0123456789abcdef0123456789abcdef",
            });

            var msUp = ms.WaitPort(czport, 25);
            r.Add("ZXMS 监听 " + czport, msUp, LastLines(ms, 2));
            r.Add("ZXMS 连上假 ZXMM", await fake.WaitConnectedAsync(15000), fake.Dump());
            r.Add("ZXMS 在官方端认证通过", await WaitOutAsync(ms, "[ZXMOS] 账号认证通过", 25000), LastLines(ms, 3));
            r.Add("聊天宿主被拉起(端口 " + chatPort + ")",
                await WaitOutAsync(ms, "聊天宿主已拉起:端口 " + chatPort, 15000), LastLines(ms, 3));

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            r.Add("聊天宿主在听(能力查询 200)",
                await GetStatusAsync(http, $"http://127.0.0.1:{chatPort}/chat/api/capabilities", "") == 200, "");

            // ---------- 先把本服编号推后:现场那个 bug 只在「本服编号 ≠ 官方编号」时才现形 ----------
            // 本地账号按注册顺序从 1 排,官方端那边也是从 1 排;先占掉 3 个本地编号,
            // 后面两个官方账号镜像进来就是 4、5,而它们的官方编号还是 1、2 —— 两个数必然不同。
            for (var index = 1; index <= 3; index++)
            {
                using var filler = await Probe.ConnectAsync("127.0.0.1", czport, "ZXMC");   //只用来注册本地账号
                await filler.RegisterAsync("localonly" + index, "pw123456", "MACH-FILL-" + index);
            }

            // ---------- 两个官方账号经中转登录:各拿一份 510 ----------
            using var one = await Probe.ConnectAsync("127.0.0.1", czport, "OS");
            var oneReg = await one.RegisterAsync("chatone", "pw123456", "MACH-CHAT-1");
            var oneLogin = await one.LoginAsync("chatone", "pw123456", "MACH-CHAT-1", "Player", "zxmos");
            one.StartReader();
            r.Add("账号一 经中转注册并登录", oneReg && oneLogin, one.LastAck ?? "");
            var helloOne = await one.TakeQueuedAsync(Protocol.ChatHello, 15000);   //账号一的 510
            r.Add("账号一 收到 510", helloOne is not null, LastLines(ms, 3));

            using var two = await Probe.ConnectAsync("127.0.0.1", czport, "OS");
            var twoReg = await two.RegisterAsync("chattwo", "pw123456", "MACH-CHAT-2");
            var twoLogin = await two.LoginAsync("chattwo", "pw123456", "MACH-CHAT-2", "Player", "zxmos");
            two.StartReader();
            r.Add("账号二 经中转注册并登录", twoReg && twoLogin, two.LastAck ?? "");
            var helloTwo = await two.TakeQueuedAsync(Protocol.ChatHello, 15000);   //账号二的 510
            r.Add("账号二 收到 510", helloTwo is not null, LastLines(ms, 3));

            // 510 只留四段:端口 / 令牌 / 本服完整ID / 自己的本服编号
            var partsOne = (helloOne ?? "").Split('\t');   //账号一 510 的四段
            var partsTwo = (helloTwo ?? "").Split('\t');   //账号二 510 的四段
            r.Add("510 是四段(端口/令牌/服ID/编号)", partsOne.Length >= 4, "段数=" + partsOne.Length);
            if (partsOne.Length < 4 || partsTwo.Length < 4)
            {
                r.Note("510 段数不对,后面的频道检查没法做");
                results.Add(r);
                return;
            }

            var tokenOne = partsOne[1];   //账号一的聊天令牌
            var tokenTwo = partsTwo[1];   //账号二的聊天令牌
            var selfIdOne = int.TryParse(partsOne[3], out var codeOne) ? codeOne : 0;   //账号一的本服编号
            var selfIdTwo = int.TryParse(partsTwo[3], out var codeTwo) ? codeTwo : 0;   //账号二的本服编号
            var serverId = partsOne[2];   //本服完整服ID

            // 601 的第三段是官方编号(现场 bug 里客户端拿的就是它)
            var officialOne = (one.LastAck ?? "").Split('|') is { Length: > 2 } ackOne ? ackOne[2] : "";   //账号一的官方编号

            r.Add("第四段是自己的本服编号(>0)", selfIdOne > 0 && selfIdTwo > 0, $"一号={selfIdOne} 二号={selfIdTwo}");
            r.Add("第四段与令牌里签进去的编号一致",
                ChatToken.TryReadUserId(tokenOne, out var signedOne) && signedOne == selfIdOne.ToString(),
                $"{signedOne} vs {selfIdOne}");
            r.Add("第三段是本服完整服ID", PlayerId.IsServerId(serverId), serverId);
            // 两个编号恰好相等也算正常(本服编号和官方编号各自从 1 排),这里只记录不判定
            r.Note($"本次环境:官方编号 {officialOne} / 本服编号 {selfIdOne}");
            r.Add("两个账号编号不同", selfIdOne != selfIdTwo, $"{selfIdOne} / {selfIdTwo}");

            var fullOne = $"{serverId}-{selfIdOne}";   //账号一的完整ID
            var fullTwo = $"{serverId}-{selfIdTwo}";   //账号二的完整ID

            // ---------- 加好友:一号申请 → 二号同意 ----------
            await one.SendAsync(40, Protocol.FriendRequest,
                Encoding.UTF8.GetBytes(FriendCodec.BuildFriendRequest(fullTwo, "来自一号的申请", "")));
            var requestAck = await one.TakeQueuedAsync(Protocol.FriendRequestAck, 10000);   //655 回执
            r.Add("一号发好友申请成功", requestAck?.StartsWith("ok|") == true, requestAck ?? "没收到");
            var pushed = await two.TakeQueuedAsync(Protocol.FriendRequestsReply, 8000);   //657 推送
            if (pushed is null) { pushed = await two.TakeQueuedAsync(Protocol.FriendEvent, 3000); }
            r.Add("二号收到申请推送", pushed is not null, pushed ?? LastLines(ms, 3));

            await two.SendAsync(41, Protocol.FriendAnswer,
                Encoding.UTF8.GetBytes(FriendCodec.BuildFriendAnswer(fullOne, true)));
            var answerAck = await two.TakeQueuedAsync(Protocol.FriendAnswerAck, 10000);   //659 回执
            r.Add("二号同意好友", answerAck?.StartsWith("ok|") == true, answerAck ?? "没收到");

            // ---------- 服务端按两人的本服编号建频道 ----------
            var channel = ChatChannel.ForPair(selfIdOne, selfIdTwo);   //两人之间的私聊频道号
            r.Add("服务端按本服编号建好私聊频道 " + channel,
                await WaitOutAsync(ms, "私聊频道就绪:" + channel, 20000), LastLines(ms, 4));

            // 用两边令牌各读一次:读得到才算真进了频道(频道不存在 / 不是成员时都是 404)
            r.Add("一号的令牌能读 " + channel,
                await GetStatusAsync(http, $"http://127.0.0.1:{chatPort}/chat/api/channels/{channel}/messages?limit=1", tokenOne) == 200, "");
            r.Add("二号的令牌能读 " + channel,
                await GetStatusAsync(http, $"http://127.0.0.1:{chatPort}/chat/api/channels/{channel}/messages?limit=1", tokenTwo) == 200, "");

            // ---------- 真发一条,再读回来 ----------
            const string body = "chat-channel-suite-hello";   //发送的正文
            var sent = await PostAsync(http, $"http://127.0.0.1:{chatPort}/chat/messages", tokenOne,
                $"{{\"channelId\":\"{channel}\",\"payload\":\"{body}\",\"contentType\":\"Text\"}}");
            // 聊天宿主新建消息回 201,不能拿 200 当唯一成功码
            r.Add("一号在 " + channel + " 里发消息成功", sent is 200 or 201, "HTTP " + sent);

            var readBack = await GetBodyAsync(http,
                $"http://127.0.0.1:{chatPort}/chat/api/channels/{channel}/messages?limit=5", tokenTwo);
            r.Add("二号能读到一号发的这条", readBack.Contains(body), readBack.Length > 200 ? readBack[..200] : readBack);

            // 账号编号对不上时(现场那个 bug 算出来的频道)必须读不到,不能假成功
            var wrongChannel = ChatChannel.ForPair(selfIdOne == 1 ? 9001 : 1, selfIdTwo);   //故意用错编号拼的频道
            r.Add("编号算错的频道读不到(不是 200)",
                await GetStatusAsync(http, $"http://127.0.0.1:{chatPort}/chat/api/channels/{wrongChannel}/messages?limit=1", tokenTwo) != 200,
                wrongChannel);
        }
        catch (Exception ex)
        {
            r.Fatal = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZXM_ALLOW_UNOFFICIAL", previousBypass, EnvironmentVariableTarget.Process);
            results.Add(r);
        }
    }

    /// <summary>发一个 GET,只回状态码(连不上回 -1)。</summary>
    private static async Task<int> GetStatusAsync(HttpClient http, string url, string token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (token.Length > 0)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await http.SendAsync(request);
            return (int)response.StatusCode;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>发一个 GET,回正文(失败回空串)。</summary>
    private static async Task<string> GetBodyAsync(HttpClient http, string url, string token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request);
            return await response.Content.ReadAsStringAsync();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>发一个 JSON POST,只回状态码(连不上回 -1)。</summary>
    private static async Task<int> PostAsync(HttpClient http, string url, string token, string json)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request);
            return (int)response.StatusCode;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>等进程输出里出现某段文字。</summary>
    private static async Task<bool> WaitOutAsync(Svc svc, string text, int timeoutMs)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (svc.Out.Contains(text)) { return true; }
            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>取输出最后几行,失败时当证据。</summary>
    private static string LastLines(Svc svc, int count)
    {
        var lines = svc.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", lines.TakeLast(count).Select(l => l.Trim()));
    }
}
