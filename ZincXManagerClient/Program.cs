// Avalonia:提供 AppBuilder / StartWithClassicDesktopLifetime 这套桌面启动入口
using Avalonia;  // 界面框架入口
// System:STAThread 特性、异常处理都要它
using System;
using System.Threading;  // 线程名(日志标签用)  // 基础类型 / 常用静态方法
// ZincXManagerShared.Logging:统一日志(落文件、带端类型前缀)
using ZincXManagerShared.Logging;  // 共享库
// ZincXManagerShared.Network:协议常量与网络层(客户端启动时会用到,保留引用方便排查)
using ZincXManagerShared.Network;  // 共享库

namespace ZincXManagerClient;

/// <summary>
/// ZXMC 的进程入口。
///
/// <para>客户端的启动只做三件事:把日志挂起来 → 起 Avalonia → 退出时补一条日志。
/// 拆成几个小静态函数,是为了让"哪一步失败了"在日志里能一眼看出来 ——
/// 客户端没有控制台,日志是唯一的线索。</para>
/// </summary>
sealed class Program
{
    /// <summary>
    /// 进程主入口。
    /// <paramref name="args"/> 是命令行参数(目前客户端不用,只透传给 Avalonia)。
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        // 主线程起名 Main:日志里才会显示 [Program/Main] 这种标签
        Thread.CurrentThread.Name ??= "Main";
        // 日志文件路径:打开成功时返回 true,路径写进 logFile(失败时也写,方便排查)
        var logReady = OpenLog();

        try
        {
            // Avalonia 的经典桌面生命周期:阻塞到主窗口关闭才返回
            RunDesktop(args);
        }
        catch (Exception ex)
        {
            // 启动阶段炸了:先把异常完整写进日志,再往外抛(别让界面无声退出)
            Logger.Log(LogLevel.Error, "[ZXMC] 启动失败:" + ex);
            throw;
        }
        finally
        {
            // 不管正常退出还是异常退出,都留一条"客户端退出",方便对齐日志时间线
            Logger.Log(LogLevel.Info, "[ZXMC] 客户端退出");
        }
    }

    /// <summary>打开默认日志文件(文件名自动带 ZXMC 前缀和启动时间)。 返回 false 表示文件没打开 —— 但这不会阻止界面启动,只是那之后没有落盘日志。</summary>
    private static bool OpenLog()
    {
        // 客户端是 GUI,没有控制台可以看,所以别往 stdout 回显
        Logger.EchoToConsole = false;

        // logFile:实际用的日志文件路径(失败时是尝试过的路径)
        var ok = Logger.OpenDefaultLogFile("ZXMC", out var logFile);

        if (!ok)
        {
            // 日志还没开成功,这条只能打到调试器/事件日志里,尽力而为
            Logger.Log(LogLevel.Error, "[ZXMC] 打开日志文件失败: " + logFile);
        }

        Logger.Log(LogLevel.Info, "[ZXMC] 客户端启动");
        return ok;
    }

    /// <summary>起 Avalonia 桌面应用,直到主窗口关掉才返回。</summary>
    /// <param name="args">命令行参数,交给 Avalonia 处理。</param>
    private static void RunDesktop(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// Avalonia 的启动配置。
    /// <para>设计器也会调这个方法,别删。</para>
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            // 只有 Debug 构建带开发者工具(F12 打开),Release 不带,免得白送调试入口
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
