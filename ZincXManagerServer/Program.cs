using System;
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
    // 连向 ZXMOS 的客户端;存字段,不然会被回收(后面中转也要用它)
    private static TcpClient? _os;

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
        var connectOS = AskIfMissing(config, "connectOS",
            "[CONFIG]输入1启用默认连接OS，输入0关闭默认连接OS",
            value => value is "0" or "1",
            fallback: "0");

        // 要连接时才问 IP 和端口,不连就不打扰
        if (connectOS == "1")
        {
            // ZXMOS 的 IP
            var zip = AskIfMissing(config, "zip",
                "[CONFIG]输入ZXMOS的IP地址(例如 127.0.0.1)",
                value => IPAddress.TryParse(value, out _),
                fallback: "127.0.0.1");

            // ZXMOS 的端口
            var zport = AskIfMissing(config, "zport",
                "[CONFIG]输入ZXMOS的端口(1-65535)",
                value => ushort.TryParse(value, out var n) && n > 0,
                fallback: "25565");

            var ip = IPAddress.Parse(zip);
            var port = ushort.Parse(zport);

            // ================= 连接官方服务端 =================
            var client = new TcpClient(ip, port);

            // 收到 ZXMOS 的包:这里先打日志,以后按协议分派
            client.AddHandler(packet =>
            {
                Logger.Log(LogLevel.Info, $"[ZXMOS] 收到 type={packet.Type}: {Encoding.UTF8.GetString(packet.Data)}");
                return Task.CompletedTask;
            });

            try
            {
                await client.ConnectAsync();
                _os = client;
                await client.WriteAsync(new Packet(1, 100, Encoding.UTF8.GetBytes("ZXMS online")));
                Logger.Log(LogLevel.Info, $"[ZXMOS] 已连接 {ip}:{port}");
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
