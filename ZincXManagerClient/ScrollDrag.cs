using System;  // 基础类型 / 常用静态方法
using Avalonia;  // 界面框架入口
using Avalonia.Controls;  // 窗口与控件
using Avalonia.Input;  // 鼠标键盘输入
using Avalonia.Interactivity;  // 路由事件
using Avalonia.VisualTree;  // 界面框架

namespace ZincXManagerClient;

/// <summary>
/// 给 ScrollViewer 加"按住拖动滚动"(滚动条隐藏时用)。
/// 用隧道路由:连按钮之类的子控件也能当拖动介质;拖动超过阈值时吞掉事件,不会误触发点击。
/// </summary>
public static class ScrollDrag
{
    private const double Threshold = 6;

    /// <summary>把拖动滚动挂到某个 ScrollViewer 上(重复挂不会重复注册)。</summary>
    public static void Attach(ScrollViewer sv)
    {
        if (sv.Tag as string == "scroll-drag")
        {
            return;
        }

        sv.Tag = "scroll-drag";

        Point start = default;
        Vector offset = default;
        var pressed = false;
        var dragging = false;

        sv.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(sv).Properties.IsLeftButtonPressed)
            {
                return;
            }

            start = e.GetPosition(sv);
            offset = sv.Offset;
            pressed = true;
            dragging = false;
        }, RoutingStrategies.Tunnel);

        sv.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (!pressed)
            {
                return;
            }

            var p = e.GetPosition(sv);
            var dx = p.X - start.X;
            var dy = p.Y - start.Y;

            if (!dragging && Math.Abs(dx) + Math.Abs(dy) < Threshold)
            {
                return;
            }

            dragging = true;

            var maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
            var maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            sv.Offset = new Vector(
                Math.Clamp(offset.X - dx, 0, maxX),
                Math.Clamp(offset.Y - dy, 0, maxY));

            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        sv.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (pressed && dragging)
            {
                e.Handled = true;
            }

            pressed = false;
            dragging = false;
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>把窗口里所有 ScrollViewer 都挂上(滚动条记得在 XAML 里设 Hidden/Disabled)。</summary>
    public static void AttachAll(Visual root)
    {
        if (root is ScrollViewer self)
        {
            Attach(self);
        }

        foreach (var child in root.GetVisualChildren())
        {
            AttachAll(child);
        }
    }
}
