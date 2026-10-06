using System;
using System.Threading;
using ZincXManagerShared.Command;
using ZincXManagerShared.FileIO;
using ZincXManagerShared.Logging;

namespace ZincXManagerServer;

internal static class Program
{
    // 设置控制台输出颜色函数
    static void SetConsoleColor()
    {
        Console.ForegroundColor = ConsoleColor.Green;
    }

    static void OpenLog()
    {
        if (!Logger.OpenDefaultLogFile("ZXMOS", out var logFile))
        {
            Console.Error.WriteLine("打开日志文件失败: 可能是文件被占用或者被锁定 " + logFile);

        }
    }
    static void Main(string[] args)
    {
        
        Console.WriteLine(" _____   _           _  __ __  ___                                     _____    \r\n/__  /  (_)___  ____| |/ //  |/  /___ _____  ____ _____ ____  _____   / ___/    \r\n  / /  / / __ \\/ ___/   // /|_/ / __ `/ __ \\/ __ `/ __ `/ _ \\/ ___/   \\__ \\     \r\n / /__/ / / / / /__/   |/ /  / / /_/ / / / / /_/ / /_/ /  __/ /      ___/ /     \r\n/____/_/_/ /_/\\___/_/|_/_/  /_/\\__,_/_/ /_/\\__,_/\\__, /\\___/_/      /____/      \r\n                                                /____/                          ");
        Console.WriteLine("欢迎使用ZincXManager子服务端");
        Console.WriteLine("ZincXManager官网 zxm.zincms.top");
        SetConsoleColor();
        OpenLog();
        Logger.Log(LogLevel.Info, "正在启动ZXMS");
        ZFile Dzfile = new ZFile();
        if (!Dzfile.FileOperate("config.ini", "rwNFC"))
        {
            Console.Error.WriteLine("打开设置文件失败: config.ini");
        }
        //
        //此处预留设置相关代码
        //
        var cmd = new CommandHandler();//命令线程
        cmd.Start();
        Logger.Log(LogLevel.Info, "启动成功");
        Thread.Sleep(Timeout.Infinite);
    }
}