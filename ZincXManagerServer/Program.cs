using System;
using System.Threading.Tasks;
using ZincXManagerServer.FileIO;
using ZincXManagerServer.Logging;

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
        // 取当前时间，截断到秒
        var fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log";

        // 打开日志文件（和 C++ main 里 logF.fileOperate 一样）
        if (!ZFile.logF.FileOperate(fileName, "rwNFC"))
        {
            Console.Error.WriteLine("打开日志文件失败: " + fileName);
        }
    }

    static async Task Main(string[] args)
    {
        SetConsoleColor();      // 设置控制台输出颜色
        InitializeLog();        // 初始化

        // 以下为测试代码
        using var fileo = new ZFile();

        // 异步写日志（对应 C++ 的 std::async）
        var t = Task.Run(() =>
        {
            Logger.Log(LogLevel.Info, "测试2");
            Logger.Log(LogLevel.Warning, "测试3");
            Logger.Log(LogLevel.Fatal, "测试4");
        });

        Logger.Log(LogLevel.Fatal, "测试1");

        fileo.FileOperate("config.ini", "rwNFC");
        fileo.WriteFileMap("name", "weiqi");
        Console.WriteLine("测试读取MAP " + fileo.MapGet("name"));
        fileo.CloseFile();

        Logger.Log(LogLevel.Debug, "测试5");
        fileo.FileOperate("exmple.txt", "rwNFC");
        fileo.WriteFileTxt("测试1234ABCDacbd",true);
        fileo.WriteFileTxt("测试54321weiqi", true);
        var lines = fileo.ReadFileTxt();
        foreach (var line in lines)
            Console.WriteLine(line);
        Console.WriteLine(fileo.TxtGet(2));
        Console.ReadKey();
        fileo.FileOperate("exmple.txt", "del");

        await t;

        Console.WriteLine("按任意键退出...");
        Console.ReadKey();
    }
}