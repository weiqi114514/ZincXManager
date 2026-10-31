using Avalonia;  // Point
using Avalonia.Controls;  // Window / WindowEdge
using Avalonia.Input;  // PointerPressedEventArgs / Cursor

namespace ZincXManagerClient;

/// <summary>无边框窗口按边缘拖动改大小:边缘上换成对应的缩放光标,按下就开始拖。</summary>
public static class WindowResizeHelper
{
    private static readonly Cursor NorthSouth = new(StandardCursorType.SizeNorthSouth);  //上下
    private static readonly Cursor WestEast = new(StandardCursorType.SizeWestEast);  //左右
    private static readonly Cursor SouthWestCursor = new(StandardCursorType.BottomLeftCorner);  //左下斜
    private static readonly Cursor SouthEastCursor = new(StandardCursorType.BottomRightCorner);  //右下斜

    /// <summary>给窗口挂上边缘拖拽;horizontal=false 时只允许调高度(宽度锁死)。</summary>
    public static void Attach(Window window, bool horizontal = true, double grip = 6)
    {
        window.PointerMoved += (_, e) =>
        {
            if (e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
            {
                return;  //拖动过程中不要抢光标
            }

            var hit = Hit(window, e.GetPosition(window), horizontal, grip);
            window.Cursor = hit?.Cursor ?? Cursor.Default;
        };

        window.PointerExited += (_, _) => window.Cursor = Cursor.Default;

        window.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
            {
                return;
            }

            var hit = Hit(window, e.GetPosition(window), horizontal, grip);
            if (hit is null)
            {
                return;
            }

            window.BeginResizeDrag(hit.Value.Edge, e);
            e.Handled = true;
        };
    }

    /// <summary>点到窗口哪条边;不在边缘返回 null。</summary>
    private static (WindowEdge Edge, Cursor Cursor)? Hit(Window window, Point p, bool horizontal, double grip)
    {
        var width = window.Bounds.Width;
        var height = window.Bounds.Height;

        var left = horizontal && p.X <= grip;
        var right = horizontal && p.X >= width - grip;
        var bottom = p.Y >= height - grip;

        return (left, right, bottom) switch
        {
            (true, _, true) => (WindowEdge.SouthWest, SouthWestCursor),
            (_, true, true) => (WindowEdge.SouthEast, SouthEastCursor),
            (true, _, _) => (WindowEdge.West, WestEast),
            (_, true, _) => (WindowEdge.East, WestEast),
            (_, _, true) => (WindowEdge.South, NorthSouth),
            _ => null,
        };
    }
}
