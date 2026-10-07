using System;
using System.Threading.Tasks;
using ZincXManagerShared.Command;
using ZincXManagerShared.Logging;

// 与 BCL 的 TcpClient 同名,起个别名
using TcpClient = ZincXManagerShared.Network.TcpClient;

namespace ZincXManagerServer.Command;

/// <summary>
/// ZXMS 专属控制台命令 —— 共享类库里没有这些,只在本端启动时挂上去。
///
/// <para><b>挂载方式(必须在 <c>CommandHandler.Start()</c> 之前):</b></para>
/// <code>
/// var cmd = new CommandHandler();
///
/// cmd.RegisterZxmsCommands(
///     osProvider:          () => _os,                                        // 当前与 ZXMOS 的连接
///     osTargetProvider:    () => config.MapGet("zip") + ":" + config.MapGet("zport"),
///     clientCountProvider: () => _server?.Connections.Count ?? 0,            // 已接入的客户端数
///     osReconnect:         () => ConnectOServerAsync(config));               // 重连
///
/// cmd.Start();
/// </code>
///
/// <para><b>为什么要传这四个委托:</b>扩展方法是静态类,读不到 <c>Program</c> 里的私有字段
/// (<c>_os</c> / <c>_server</c> / <c>config</c>),所以由调用方把"取值"和"动作"传进来;
/// 命令多了嫌参数长,可以在端里放一个静态上下文类,直接读它。</para>
/// </summary>
public static class ZxmsCommands
{
    /// <summary>挂载 ZXMS 专属命令。</summary>
    /// <param name="cmd">命令处理器</param>
    /// <param name="osProvider">取当前与 ZXMOS 的连接;没连上返回 null</param>
    /// <param name="osTargetProvider">取配置里 ZXMOS 的目标地址(用于显示)</param>
    /// <param name="clientCountProvider">取当前接入的客户端数量</param>
    /// <param name="osReconnect">重连 ZXMOS 的动作</param>
    public static void RegisterZxmsCommands(
        this CommandHandler cmd,
        Func<TcpClient?> osProvider,
        Func<string> osTargetProvider,
        Func<int> clientCountProvider,
        Func<Task> osReconnect)
    {
        cmd.Register("net status", _ => CmdNetStatus(osProvider(), osTargetProvider(), clientCountProvider()),
            "查看 ZXMOS 连接与客户端接入情况",
            "net status");

        cmd.Register("net reconnect", args =>
            {
                // 故意不 await:重连可能要重试好几秒,不能把命令线程卡住
                // 注意:lambda 参数不能命名成 _ ,否则这里的 _ 会被当成参数而不是丢弃符
                _ = ReconnectAsync(osReconnect);
            },
            "断开并重新连接官方服务端",
            "net reconnect");
    }

    /// <summary>net status:打印当前网络状态(连接状态、目标地址、接入的客户端数)</summary>
    private static void CmdNetStatus(TcpClient? os, string target, int clientCount)
    {
        var osState = os is null
            ? "未连接"
            : os.IsConnected ? "已连接" : "已断开(对象还在,可 net reconnect)";

        Logger.Log(LogLevel.Info,
            $"[网络] ZXMOS  {osState}\n" +
            $"[网络] 目标    {target}\n" +
            $"[网络] 已接入客户端 {clientCount} 个");
    }

    /// <summary>后台执行重连,异常在这里兜住(否则 async void / 丢弃的任务会静默失败)</summary>
    private static async Task ReconnectAsync(Func<Task> osReconnect)
    {
        try
        {
            Logger.Log(LogLevel.Info, "[网络] 正在重连 ZXMOS…");
            await osReconnect();
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[网络] 重连出错: " + ex.Message);
        }
    }
}
