using System;
using System.Threading.Tasks;
using ZincXManagerServer.FileIO;
using ZincXManagerServer.Logging;

namespace ZincXManagerServer;

internal static class Program
{
    static void SetConsoleColor()               // ✅ 换个名字
    {
        Console.ForegroundColor = ConsoleColor.Green;
    }

    static void InitializeLog()
    {
        var fileName = $"ZXMS{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log";
        Logger.Init(fileName);
    }

    static async Task Main(string[] args)
    {
        SetConsoleColor();
        InitializeLog();

        var t = Task.Run(() =>
        {
            Logger.Log(LogLevel.Info, "测试2");
            Logger.Log(LogLevel.Warning, "测试3");
            Logger.Log(LogLevel.Fatal, "测试4");
        });

        Logger.Log(LogLevel.Fatal, "测试1");

        using var fileo = new ZFile();
        fileo.FileOperate("config.ini", "rwNFC");
        fileo.WriteFileMap("name", "weiqi");
        Console.WriteLine("测试读取MAP " + fileo.MapGet("name"));
        fileo.CloseFile();

        Logger.Log(LogLevel.Debug, "测试5");

        await t;

        Console.WriteLine("按任意键退出...");
        Console.ReadKey();
    }
}