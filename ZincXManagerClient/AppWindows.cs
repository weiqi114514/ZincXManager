using System;
using Avalonia;
using Avalonia.Controls;
using ZincXManagerClient.Views;

namespace ZincXManagerClient;

/// <summary>
/// 主界面窗口的管家:全局只有一个主界面。
///
/// <para>「进入主界面」时:已经有了就把它置顶(最小化过就还原),没有才新建一个,
/// 新建时摆到来源窗口(连接流程 / 登录 / OOBE)的位置上,然后关掉来源窗口。</para>
/// </summary>
internal static class AppWindows
{
    private static MainWindow? _main;

    /// <summary>当前主界面;没开就是 null。</summary>
    public static MainWindow? Main => _main;

    /// <summary>登记一个主界面(Avalonia 启动时创建的那个也要登记)。关掉后自动清空。</summary>
    public static void Register(MainWindow window)
    {
        _main = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_main, window))
            {
                _main = null;
            }
        };
    }

    /// <summary>
    /// 显示主界面:已有 → 置顶;没有 → 新建在 <paramref name="from"/> 的位置上。
    /// 两种情况最后都会关掉 <paramref name="from"/>。
    /// </summary>
    public static void ShowMain(Window? from)
    {
        if (_main is not null)
        {
            if (_main.WindowState == WindowState.Minimized)
            {
                _main.WindowState = WindowState.Normal;
            }

            _main.Activate();
            from?.Close();
            return;
        }

        var window = new MainWindow { DataContext = new ViewModels.MainViewModel() };
        PlaceAtTopLeft(window, from);
        Register(window);
        window.Show();
        from?.Close();
    }

    /// <summary>摆在来源窗口的左上角(顶部对齐),并夹在屏幕工作区内,免得跑到屏幕外。</summary>
    private static void PlaceAtTopLeft(Window next, Window? from)
    {
        if (from is null)
        {
            WindowPlacement.CenterOnPrimaryScreen(next);
            return;
        }

        var screen = from.Screens.Primary ?? next.Screens.Primary;
        var x = from.Position.X;
        var y = from.Position.Y;

        if (screen is not null)
        {
            var work = screen.WorkingArea;
            var width = (int)next.Width;
            var height = (int)next.Height;

            x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - width));
            y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
        }

        next.Position = new PixelPoint(x, y);
    }
}
