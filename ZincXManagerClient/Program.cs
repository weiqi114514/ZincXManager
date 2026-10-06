using Avalonia;
using System;
using ZincXManagerShared.Logging;

namespace ZincXManagerClient;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 客户端没有控制台:日志只落文件,文件名与两个服务端一样带端类型前缀
        Logger.EchoToConsole = false;

        if (!Logger.OpenDefaultLogFile("ZXMC", out var logFile))
        {
            // 日志文件打不开时不阻止界面启动
            Logger.Log(LogLevel.Error, "[ZXMC] 打开日志文件失败: " + logFile);
        }

        Logger.Log(LogLevel.Info, "[ZXMC] 客户端启动");

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Logger.Log(LogLevel.Info, "[ZXMC] 客户端退出");
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
