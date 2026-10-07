using Avalonia.Threading;
using ZincXManagerClient.ViewModels;
using ZincXManagerShared.Logging;

namespace ZincXManagerClient.Logging;

/// <summary>
/// 日志输出目标:把共享库 <see cref="Logger"/> 的日志实时丢进连接界面的日志面板。
///
/// <para>作用:共享库(网络、文件、命令)里所有 Logger.Log 都会经过这里,
/// 所以界面能实时看到"收到包 / 连接失败 / 配置读取"这些日志,不用另写一套。</para>
///
/// <para>注意:Logger 可能在网络线程上回调,而 Avalonia 只能在 UI 线程改界面,
/// 所以这里统一用 <see cref="Dispatcher.UIThread"/> 切回去。</para>
/// </summary>
public sealed class UiLogSink : ILogger
{
    private readonly ConnectViewModel _viewModel;

    public UiLogSink(ConnectViewModel viewModel)
    {
        _viewModel = viewModel;
    }

    /// <summary>Logger 每次输出都会调用这里(可能不在 UI 线程)。</summary>
    public void Log(LogLevel level, string message)
    {
        Dispatcher.UIThread.Post(() => _viewModel.AppendLog(level, message));
    }
}
