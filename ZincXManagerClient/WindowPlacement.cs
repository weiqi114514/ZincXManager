using System;  // 基础类型 / 常用静态方法
using Avalonia;  // 界面框架入口
using Avalonia.Platform;  // 平台相关能力
using Avalonia.Controls;  // 窗口与控件

namespace ZincXManagerClient;

/// <summary>
/// 新窗口的位置:出现在"上一个窗口现在所在的位置"上(居中于它),
/// 而不是每次都回到系统默认点或上一个窗口最初被创建的位置。
/// </summary>
internal static class WindowPlacement
{
    /// <summary>把 <paramref name="next"/> 摆到 <paramref name="previous"/> 现在的位置上(居中);没有上一个就不动。</summary>
    public static void PlaceAt(Window next, Window? previous)
    {
        if (previous is null || previous.WindowState == WindowState.Minimized)
        {
            return;
        }

        var origin = previous.Position;
        var size = previous.ClientSize;

        var offsetX = (int)((size.Width - next.Width) / 2);
        var offsetY = (int)((size.Height - next.Height) / 2);

        next.Position = new PixelPoint(origin.X + offsetX, origin.Y + offsetY);
    }
    /// <summary>
    /// 把窗口摆到"用户主屏的正中心"(第一个窗口用)。
    /// 用 Screens.Primary 的工作区算,比 WindowStartupLocation=CenterScreen 更明确;
    /// 分辨率缩放也一起算进去,否则高 DPI 下会偏。
    /// </summary>
    public static void CenterOnPrimaryScreen(Window window)
    {
        var screen = window.Screens.Primary;
        if (screen is null)
        {
            return;   // 取不到就交给系统默认
        }

        var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var work = screen.WorkingArea;

        // 自适应:窗口比屏幕工作区还大就先缩到放得下(留 40px 边距),
        // 否则高 DPI 小屏(比如 1366x768@125%)上右侧面板会跑到屏幕外面去。
        var maxW = Math.Max(640, (int)(work.Width / scaling) - 40);
        var maxH = Math.Max(480, (int)(work.Height / scaling) - 40);
        if (window.Width > maxW) { window.Width = maxW; }
        if (window.Height > maxH) { window.Height = maxH; }

        var width = (int)(window.Width * scaling);
        var height = (int)(window.Height * scaling);

        window.Position = new PixelPoint(
            work.X + Math.Max(0, (work.Width - width) / 2),
            work.Y + Math.Max(0, (work.Height - height) / 2));
    }
}