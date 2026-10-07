using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZincXManagerShared.Command;
using ZincXManagerShared.FileIO;
using ZincXManagerShared.Logging;
using ZincXManagerShared.Network;

namespace ZincXManagerOServer;

internal static class Program
{
    // 默认监听端口(config.ini 里没写 port 时的兜底值)
    private const string DefaultPort = "25565";

    // ---------- 与 ZXMS(子服务端)约定的消息类型 ----------
    private const ushort SOnline = 100;   // ZXMS → ZXMOS:子服务端上线(握手)
    private const ushort SBack = 101;     // ZXMOS → ZXMS:收到

    // ---------- 与 ZXMC(客户端)约定的消息类型 ----------
    private const ushort Conline = 300;   // ZXMC → ZXMOS:客户端上线(握手)
    private const ushort CBack = 301;     // ZXMOS → ZXMC:收到

    // ---------- 服务端身份声明 ----------
    private const ushort ServerIdent = 200;   // 服务端 → 对方:我是谁(负载 "ZXMS" / "ZXMOS")

    /// <summary>
    /// 连接 id → 对端身份("ZXMS" / "ZXMC")。
    /// 连接建立时还不知道对方是谁,靠握手包(100 / 300)认出来,断开时才能打对标签。
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, string> PeerKinds = new();

    static void OpenLog()
    {
        if (!Logger.OpenDefaultLogFile("ZXMOS", out var logFile))
        {
            Console.Error.WriteLine("[Fatal]打开日志文件失败: " + logFile);
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// 配置检查:ini 里这个键没有"有效值"就在控制台问一次,问到合法值后写回 ini。
    /// 直接回车(空输入)或输入流已结束(管道/重定向)时用 fallback 兜底,不会死循环。
    /// </summary>
    static string AskIfMissing(ZFile file, string key, string prompt, Func<string, bool> valid, string fallback)
    {
        var value = file.MapGet(key).Trim();
        if (valid(value))
        {
            return value;
        }

        while (true)
        {
            Logger.Log(LogLevel.Info, prompt);

            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                input = fallback;
            }

            if (valid(input))
            {
                file.WriteFileMap(key, input);
                Logger.Log(LogLevel.Info, $"[CONFIG]{key} 已设置为 {input}");
                return input;
            }

            Logger.Log(LogLevel.Warning, $"[CONFIG]{key} 输入不合法:{input}");
        }
    }

    /// <summary>
    /// 回复握手确认(收到 100 回 101、收到 300 回 301)。
    /// 成功/失败都记日志 —— 否则服务端这边只有"上线",看不出确认包有没有发出去。
    /// </summary>
    static async Task ReplyHandshakeAsync(ServerboundPacket packet, ushort ackType, string ackName, string peer, string text)
    {
        try
        {
            await packet.WriteAsync(new Packet(packet.Id, ackType, Encoding.UTF8.GetBytes(text)));
            Logger.Log(LogLevel.Info, $"[{peer}] 已回复握手确认({ackName}) → {packet.Connection.RemoteEndPoint},握手完成");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, $"[{peer}] 回复握手确认失败: {ex.Message}");
        }
    }

    static async Task Main(string[] args)
    {
        Console.WriteLine(" _____   _           _  __ __  ___                                   ____   _____\r\n/__  /  (_)___  ____| |/ //  |/  /___ _____  ____ _____ ____  _____ / __ \\ / ___/\r\n  / /  / / __ \\/ ___/   // /|_/ / __ `/ __ \\/ __ `/ __ `/ _ \\/ ___// / / / \\__ \\\r\n / /__/ / / / / /__/   |/ /  / / /_/ / / / / /_/ / /_/ /  __/ /   / /_/ / ___/ /\r\n/____/_/_/ /_/\\___/_/|_/_/  /_/\\__,_/_/ /_/\\__,_/\\__, /\\___/_/    \\____/ /____/");
        Console.WriteLine("欢迎使用ZincXManager官方服务端");
        Console.WriteLine("ZincXManager官网 zxm.zincms.top");
        Console.ForegroundColor = ConsoleColor.Green;
        OpenLog();

        // 设置文件
        var config = new ZFile();
        config.FileOperate("config.ini", "rwNFC");

        // 配置检查:没有 port 就问一次,问完写回 config.ini
        var port = AskIfMissing(config, "port",
            "[CONFIG]输入本服务端监听端口(1-65535)",
            value => ushort.TryParse(value, out var n) && n > 0,
            fallback: DefaultPort);

        // 起服务端:ZXMS / ZXMC 都连这个端口
        var server = new TcpServer(IPAddress.Any, ushort.Parse(port));

        // 有人接进来:还不知道对方是 ZXMS 还是 ZXMC,先中立记录,再主动报上"我是 ZXMOS"
        server.Connected += async conn =>
        {
            Logger.Log(LogLevel.Info, $"[网络] 连接接入 {conn.RemoteEndPoint}(等待握手识别身份)");

            try
            {
                await conn.WriteAsync(new Packet(0, ServerIdent, Encoding.UTF8.GetBytes("ZXMOS")));
                Logger.Log(LogLevel.Info, "[ZXMOS] 已发送身份包(200):ZXMOS");
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[ZXMOS] 发送身份包失败: " + ex.Message);
            }
        };

        server.Disconnected += conn =>
        {
            // 断开时用握手时记下的身份;没握过手的就是"未识别"
            var kind = PeerKinds.TryRemove(conn.Id, out var k) ? k : "未识别";
            Logger.Log(LogLevel.Info, $"[{kind}] 断开 {conn.RemoteEndPoint}");
        };

        server.AddHandler(async packet =>
        {
            switch (packet.Type)
            {
                // ---- ZXMS(子服务端)上线 ----
                case SOnline:
                    PeerKinds[packet.Connection.Id] = "ZXMS";
                    Logger.Log(LogLevel.Info, "[ZXMS] 上线(握手): " + Encoding.UTF8.GetString(packet.Data));
                    await ReplyHandshakeAsync(packet, SBack, "101", "ZXMS", "ZXMOS ok");
                    break;

                // ---- ZXMC(客户端)上线 ----
                case Conline:
                    PeerKinds[packet.Connection.Id] = "ZXMC";
                    Logger.Log(LogLevel.Info, "[ZXMC] 上线(握手): " + Encoding.UTF8.GetString(packet.Data));
                    await ReplyHandshakeAsync(packet, CBack, "301", "ZXMC", "ZXMOS ok");
                    break;

                // 其它类型先记日志,等协议定下来再补
                default:
                    Logger.Log(LogLevel.Warning, $"[网络] 未处理的包 type={packet.Type} len={packet.Data.Length}");
                    break;
            }
        });

        try
        {
            server.Open();
            Logger.Log(LogLevel.Info, $"[ZXMOS] 已开始监听 0.0.0.0:{port}");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Fatal, "[ZXMOS] 监听失败: " + ex.Message);
            Environment.Exit(1);
        }

        var cmd = new CommandHandler();//命令线程
        cmd.Start();
        Logger.Log(LogLevel.Info, "启动成功");
        await Task.Delay(Timeout.Infinite);
    }
}
