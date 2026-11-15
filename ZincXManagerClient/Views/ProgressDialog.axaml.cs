using System;                    // EventArgs
using Avalonia.Controls;         // Window / WindowCloseReason
using Avalonia.Threading;        // Dispatcher

namespace ZincXManagerClient.Views;

/// <summary>
/// 不可关的进度框:一行说明 + 一条 indeterminate 进度条。
///
/// <para>标题栏没有关闭按钮,用户手动关(Alt+F4、任务栏关闭)也被拦下,只有 <see cref="Finish"/> 放行;
/// 宿主窗口关掉时一并收(Avalonia 默认级联关子窗口,这里再挂一层兜底),不会残留。</para>
/// </summary>
public sealed partial class ProgressDialog : Window
{
    private Window? _owner;      // 宿主窗口(Opened 时拿到)
    private bool _finishing;     // 已放行关闭,防止 Closing 把 Finish 自己的 Close() 也拦下
    private bool _opened;        // 已经弹出来过(Opened 到过)
    private bool _closeOnOpen;   // 没弹出来就被 Finish 了:等弹出那一刻立刻关,别留个关不掉的框

    /// <summary>建一个「正在处理」的进度框,显示文案随后用 <see cref="SetText"/> 换。</summary>
    public ProgressDialog()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);   // 半透明 / 亚克力:开窗时定一次

        // 宿主关了就跟着收:框架的级联在部分场景(用 Show 而不是 ShowDialog 弹)不生效,自己挂一层
        Opened += (_, _) =>
        {
            _opened = true;

            if (_closeOnOpen)
            {
                Close();   // 弹出前就 Finish 过了:一露面就收,不然会卡住一个关不掉的窗
                return;
            }

            if (Owner is Window owner)   // Owner 的声明类型是 WindowBase,这里只跟真正的窗口走
            {
                _owner = owner;
                owner.Closed += OnOwnerClosed;
            }
        };

        Closed += (_, _) =>
        {
            if (_owner is { } owner)
            {
                owner.Closed -= OnOwnerClosed;
            }

            _owner = null;
        };

        // 只拦用户手动关;程序收尾、系统关机、宿主关闭引起的关闭一律放行
        Closing += (_, e) =>
        {
            if (_finishing || e.IsProgrammatic)
            {
                return;
            }

            if (e.CloseReason is WindowCloseReason.OwnerWindowClosing
                or WindowCloseReason.ApplicationShutdown
                or WindowCloseReason.OSShutdown)
            {
                return;
            }

            e.Cancel = true;
        };
    }

    /// <summary>换一句当前进度说明;从后台线程调也行(自动切回 UI 线程)。</summary>
    public void SetText(string text)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            MessageText.Text = text;
            return;
        }

        Dispatcher.UIThread.Post(() => MessageText.Text = text);
    }

    /// <summary>收掉进度框;从后台线程调也行,重复调无副作用。</summary>
    public void Finish()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            CloseNow();
            return;
        }

        Dispatcher.UIThread.Post(CloseNow);
    }

    /// <summary>在 UI 线程上真正关窗(先置放行标记再 Close,不然会被自己的 Closing 拦下)。</summary>
    private void CloseNow()
    {
        if (_finishing)
        {
            return;
        }

        _finishing = true;

        if (!_opened)
        {
            _closeOnOpen = true;   // 还没弹出来:交给 Opened 那一刻关
            return;
        }

        if (IsVisible)
        {
            Close();
        }
    }

    /// <summary>宿主窗口关掉了:进度框没意义了,一并关掉。</summary>
    private void OnOwnerClosed(object? sender, EventArgs e) => CloseNow();
}
