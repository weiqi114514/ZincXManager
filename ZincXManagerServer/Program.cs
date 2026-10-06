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

    // 初始化函数
    static void InitializeLog()
    {
        // 日志文件名统一带端类型前缀(与 ZXMC / ZXMOS 一致)
        if (!Logger.OpenDefaultLogFile("ZXMS", out var logFile))
        {
            Console.Error.WriteLine("打开日志文件失败: " + logFile);
        }
    }

    static void Main(string[] args)
    {
        Console.WriteLine(" _____   _           _  __ __  ___                                     _____    \r\n/__  /  (_)___  ____| |/ //  |/  /___ _____  ____ _____ ____  _____   / ___/    \r\n  / /  / / __ \\/ ___/   // /|_/ / __ `/ __ \\/ __ `/ __ `/ _ \\/ ___/   \\__ \\     \r\n / /__/ / / / / /__/   |/ /  / / /_/ / / / / /_/ / /_/ /  __/ /      ___/ /     \r\n/____/_/_/ /_/\\___/_/|_/_/  /_/\\__,_/_/ /_/\\__,_/\\__, /\\___/_/      /____/      \r\n                                                /____/                          ");
        Console.WriteLine("欢迎使用ZincXManager子服务端");
        SetConsoleColor();      // 设置控制台输出颜色
        InitializeLog();        // 初始化日志
        // 启动命令线程（所有命令逻辑都在 CommandHandler 里）
        var cmd = new CommandHandler();
        cmd.Start();

        // 主线程保持运行
        Thread.Sleep(Timeout.Infinite);
    }
}