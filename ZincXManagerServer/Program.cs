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

// 与 BCL 的 TcpClient 同名,起个别名
using TcpClient = ZincXManagerShared.Network.TcpClient;

namespace ZincXManagerServer;

internal static class Program
{
    // 配置键名:ozip/ozport = 官方服务端地址端口,czport = 客户端接入端口
    // 缺省值:正式环境一般不用,只在配置缺失且拿不到输入时兜底
    private const string KeyConnectOs = "connectOS";
    private const string KeyOzip = "ozip";
    private const string KeyOzport = "ozport";
    private const string KeyCzport = "czport";
    private const string DefaultOsPort = "25565";
    private const string DefaultCzPort = "25566";

    // ---------- 与 ZXMOS(官方服务端)约定的消息类型 ----------
    private const ushort SOnline = 100;   // ZXMS → ZXMOS:子服务端上线(握手)
    private const ushort SBack = 101;     // ZXMOS → ZXMS:收到

    // ---------- 与 ZXMC(客户端)约定的消息类型 ----------
    private const ushort Conline = 300;   // ZXMC → ZXMS:客户端上线(握手)
    private const ushort CBack = 301;     // ZXMS → ZXMC:收到

    // ---------- 服务端身份声明 ----------
    private const ushort ServerIdent = 200;   // 服务端 → 对方:我是谁(负载 "ZXMS" / "ZXMOS")

    /// <summary>连向 ZXMOS 的客户端;存字段,不然会被回收(中转要用它)。</summary>
    private static TcpClient? _os;

    /// <summary>监听 ZXMC / ZXMM 的服务端;存字段,中转时要靠它把消息推给客户端。</summary>
    private static TcpServer? _server;

    /// <summary>
    /// 连接 id → 对端身份("ZXMC" 等)。接入时还不知道对方是谁,
    /// 靠握手包(300)认出来,断开时才能打对标签。
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, string> PeerKinds = new();

    static void OpenLog()
    {
        if (!Logger.OpenDefaultLogFile("ZXMS", out var logFile))
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
    /// 回复握手确认(收到 300 回 301)。
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
        Console.WriteLine(" _____   _           _  __ __  ___                                     _____    \r\n/__  /  (_)___  ____| |/ //  |/  /___ _____  ____ _____ ____  _____   / ___/    \r\n  / /  / / __ \\/ ___/   // /|_/ / __ `/ __ \\/ __ `/ __ `/ _ \\/ ___/   \\__ \\     \r\n / /__/ / / / / /__/   |/ /  / / /_/ / / / / /_/ / /_/ /  __/ /      ___/ /     \r\n/____/_/_/ /_/\\___/_/|_/_/  /_/\\__,_/_/ /_/\\__,_/\\__, /\\___/_/      /____/      \r\n                                                /____/                          ");
        Console.WriteLine("欢迎使用ZincXManager子服务端");
        Console.WriteLine("ZincXManager官网 zxm.zincms.top");
        Console.ForegroundColor = ConsoleColor.Green;
        OpenLog();

        // 设置文件
        var config = new ZFile();
        config.FileOperate("config.ini", "rwNFC");

        // ================= 配置检查:缺哪项就问哪项,问完写回 config.ini =================

        // 要不要连官方服务端
        var connectOS = AskIfMissing(config, KeyConnectOs,
            "[CONFIG]输入0启用默认连接OS，输入1关闭默认连接OS",
            value => value is "0" or "1",
            fallback: "0");

        // 客户端接入端口:ZXMC / ZXMM 连这里
        var czport = AskIfMissing(config, KeyCzport,
            "[CONFIG]输入客户端接入端口czport(ZXMC/ZXMM 连这里,1-65535)",
            value => ushort.TryParse(value, out var n) && n > 0,
            fallback: DefaultCzPort);

        // ================= 启动监听:ZXMC / ZXMM 都连这个端口 =================
        _server = new TcpServer(IPAddress.Any, ushort.Parse(czport));

        // 有人接进来:先中立记录身份未知,再主动报上"我是 ZXMS",让客户端按模式校验
        _server.Connected += async conn =>
        {
            Logger.Log(LogLevel.Info, $"[网络] 连接接入 {conn.RemoteEndPoint}(等待握手识别身份)");

            try
            {
                await conn.WriteAsync(new Packet(0, ServerIdent, Encoding.UTF8.GetBytes("ZXMS")));
                Logger.Log(LogLevel.Info, "[ZXMS] 已发送身份包(200):ZXMS");
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[ZXMS] 发送身份包失败: " + ex.Message);
            }
        };

        _server.Disconnected += conn =>
        {
            var kind = PeerKinds.TryRemove(conn.Id, out var k) ? k : "未识别";
            Logger.Log(LogLevel.Info, $"[{kind}] 断开 {conn.RemoteEndPoint}");
        };

        _server.AddHandler(async packet =>
        {
            switch (packet.Type)
            {
                // ---- ZXMC(客户端)上线 ----
                case Conline:
                    PeerKinds[packet.Connection.Id] = "ZXMC";
                    Logger.Log(LogLevel.Info, "[ZXMC] 上线(握手): " + Encoding.UTF8.GetString(packet.Data));
                    await ReplyHandshakeAsync(packet, CBack, "301", "ZXMC", "ZXMS ok");
                    break;

                // 其它类型先记日志(ZXMM 模组端的类型还没定,定了在这里加 case)
                default:
                    Logger.Log(LogLevel.Warning, $"[网络] 未处理的包 type={packet.Type} len={packet.Data.Length}");
                    break;
            }
        });

        try
        {
            _server.Open();
            Logger.Log(LogLevel.Info, $"[ZXMS] 已开始监听 0.0.0.0:{czport}(czport,ZXMC/ZXMM 从这里接入)");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Fatal, "[ZXMS] 监听失败: " + ex.Message);
            Environment.Exit(1);
        }

        // ================= 按配置连接官方服务端 =================
        if (connectOS == "1")
        {
            // 官方服务端(ZXMOS)的地址 / 端口:没有或不合法就问
            var ozip = AskIfMissing(config, KeyOzip,
                "[CONFIG]输入官方服务端地址ozip(例如 127.0.0.1)",
                value => IPAddress.TryParse(value, out _),
                fallback: "127.0.0.1");

            var ozport = AskIfMissing(config, KeyOzport,
                "[CONFIG]输入官方服务端端口ozport(1-65535)",
                value => ushort.TryParse(value, out var n) && n > 0,
                fallback: DefaultOsPort);

            var osIp = IPAddress.Parse(ozip);
            var osPort = ushort.Parse(ozport);

            var client = new TcpClient(osIp, osPort);

            client.AddHandler(packet =>
            {
                switch (packet.Type)
                {
                    // ZXMOS 自报身份:必须真的是官方服务端
                    case ServerIdent:
                    {
                        var role = Encoding.UTF8.GetString(packet.Data).Trim();
                        if (role == "ZXMOS")
                        {
                            Logger.Log(LogLevel.Info, "[ZXMOS] 身份校验通过:ZXMOS");
                        }
                        else
                        {
                            Logger.Log(LogLevel.Error, $"[ZXMOS] 身份校验不通过:对方自报 {role},不是官方服务端");
                        }

                        break;
                    }

                    // ZXMOS 确认握手
                    case SBack:
                        Logger.Log(LogLevel.Info, "[ZXMOS] 握手成功: " + Encoding.UTF8.GetString(packet.Data));
                        break;

                    // TODO 中转:ZXMOS 下发的业务消息,应该在这里转发给对应的 ZXMC 连接
                    //       (转发入口:_server.Connections 找到目标连接 → conn.WriteAsync(packet))
                    default:
                        Logger.Log(LogLevel.Info, $"[ZXMOS] 收到 type={packet.Type}: {Encoding.UTF8.GetString(packet.Data)}");
                        break;
                }

                return Task.CompletedTask;
            });

            try
            {
                await client.ConnectAsync();
                _os = client;
                await client.WriteAsync(new Packet(1, SOnline, Encoding.UTF8.GetBytes("ZXMS online")));
                Logger.Log(LogLevel.Info, $"[ZXMOS] 已连接 {osIp}:{osPort}");
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error, "[ZXMOS] 连接失败: " + ex.Message);
                await client.CloseAsync();
            }
        }

        var cmd = new CommandHandler();//命令线程
        cmd.Start();
        Logger.Log(LogLevel.Info, "启动成功");
        await Task.Delay(Timeout.Infinite);
    }
}
