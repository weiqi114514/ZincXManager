using System;  // 基础类型 / 常用静态方法
using Avalonia;  // 界面框架入口
using Avalonia.Platform;  // 平台相关能力
using Avalonia.Controls;  // 窗口与控件

namespace ZincXManagerClient;

/// <summary>新窗口的位置:出现在"上一个窗口现在所在的位置"上(居中于它), 而不是每次都回到系统默认点或上一个窗口最初被创建的位置。</summary>
internal static class WindowPlacement
{
    /// <summary>
    /// 把 <paramref name="next"/> 摆到 <paramref name="previous"/> 现在所在的位置上(居中);没有上一个 / 它最小化了就不动。
    ///
    /// <para>Position 是物理像素,ClientSize 与 Width 是 DIP,高 DPI(125% / 150%)下必须先乘 RenderScaling,
    /// 否则算出来会往左上偏几十像素。算完再夹进「上一个窗口所在那块屏」的工作区,免得开在屏幕边上时一半露在外面。</para>
    /// </summary>
    public static void PlaceAt(Window next, Window? previous)
    {
        if (previous is null || previous.WindowState == WindowState.Minimized)
        {
            return;
        }

        var scale = previous.RenderScaling <= 0 ? 1.0 : previous.RenderScaling;

        // SizeToContent 的窗口 Width/Height 是 NaN,乘出来还是 NaN,后面 Clamp 会算成 int.MinValue
        var nextWidth = double.IsNaN(next.Width) ? next.ClientSize.Width : next.Width;
        var nextHeight = double.IsNaN(next.Height) ? next.ClientSize.Height : next.Height;

        var origin = previous.Position;
        var ownerWidth = (int)(previous.ClientSize.Width * scale);    // 上一个窗口的宽(物理像素)
        var ownerHeight = (int)(previous.ClientSize.Height * scale);  // 上一个窗口的高(物理像素)
        var width = (int)(nextWidth * scale);                         // 新窗口的宽(物理像素)
        var height = (int)(nextHeight * scale);                       // 新窗口的高(物理像素)

        var x = origin.X + (ownerWidth - width) / 2;
        var y = origin.Y + (ownerHeight - height) / 2;

        // 按「上一个窗口所在的那块屏」钳:多显示器下用主屏会把窗口拽到别的屏上
        var screen = previous.Screens.ScreenFromWindow(previous) ?? previous.Screens.Primary;
        if (screen is not null)
        {
            var work = screen.WorkingArea;
            x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - width));
            y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
        }

        next.Position = new PixelPoint(x, y);
    }

    /// <summary>摆到主屏正中(第一个窗口用),按工作区算,高 DPI 不偏</summary>
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