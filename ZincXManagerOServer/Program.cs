using System;
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

    // 与 ZXMS/ZXMC 约定的消息类型(先占位,后面按协议文档改)
    private const ushort TypeHandshake = 100;      // 对方 → ZXMOS:我上线了
    private const ushort TypeHandshakeAck = 101;   // ZXMOS → 对方:收到

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
    /// <param name="file">已打开的配置文件</param>
    /// <param name="key">配置键名</param>
    /// <param name="prompt">提示文案</param>
    /// <param name="valid">判断输入是否合法</param>
    /// <param name="fallback">拿不到合法输入时的缺省值</param>
    static string AskIfMissing(ZFile file, string key, string prompt, Func<string, bool> valid, string fallback)
    {
        // ① 已经有合法值:直接用,不打扰用户
        var value = file.MapGet(key).Trim();
        if (valid(value))
        {
            return value;
        }

        // ② 没有/不合法:循环问,直到拿到合法值(或用兜底值)
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
                file.WriteFileMap(key, input);   // ③ 写回 config.ini,下次启动就不再问
                Logger.Log(LogLevel.Info, $"[CONFIG]{key} 已设置为 {input}");
                return input;
            }

            Logger.Log(LogLevel.Warning, $"[CONFIG]{key} 输入不合法:{input}");
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

        // ================= 配置检查:没有 port 就问一次,问完写回 config.ini =================
        var port = AskIfMissing(config, "port",
            "[CONFIG]输入本服务端监听端口(1-65535)",
            value => ushort.TryParse(value, out var n) && n > 0,
            fallback: DefaultPort);

        // ================= 起服务端:ZXMS / ZXMC 都连这个端口 =================
        var server = new TcpServer(IPAddress.Any, ushort.Parse(port));

        server.Connected += conn => Logger.Log(LogLevel.Info, $"[ZXMS] 接入 {conn.RemoteEndPoint}");
        server.Disconnected += conn => Logger.Log(LogLevel.Info, $"[ZXMS] 断开 {conn.RemoteEndPoint}");

        server.AddHandler(async packet =>
        {
            switch (packet.Type)
            {
                // 收到上线握手 → 回一个确认包(沿用请求的 Id,方便对方配对)
                case TypeHandshake:
                    Logger.Log(LogLevel.Info, "[ZXMS] 握手: " + Encoding.UTF8.GetString(packet.Data));
                    await packet.WriteAsync(new Packet(packet.Id, TypeHandshakeAck, Encoding.UTF8.GetBytes("ZXMOS ok")));
                    break;

                // 其它类型先记日志,等协议定下来再补
                default:
                    Logger.Log(LogLevel.Warning, $"[ZXMS] 未处理的包 type={packet.Type} len={packet.Data.Length}");
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
