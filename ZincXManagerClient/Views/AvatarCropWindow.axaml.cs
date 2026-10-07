using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ZincXManagerShared.Logging;

namespace ZincXManagerClient.Views;

/// <summary>
/// 设置头像(QQ 式框选):拖动图片调整位置 + 滑块/滚轮缩放,方框里就是最终头像。
/// 点「确定」把框里那块裁成 256x256 的 PNG 字节,通过 <see cref="Window.Close(object?)"/> 返回。
/// </summary>
public partial class AvatarCropWindow : Window
{
    /// <summary>预览区边长(与 XAML 里的 400 一致)。</summary>
    private const double StageSize = 400;

    /// <summary>选框边长(居中,框外压暗)。</summary>
    private const double FrameSize = 260;

    /// <summary>输出头像边长。</summary>
    private const int OutputSize = 256;

    private readonly Bitmap? _source;

    /// <summary>图片"刚好装进预览区"时的缩放;没有图就是 0。</summary>
    private readonly double _fitScale;

    private double _zoom = 1;
    private double _offsetX;
    private double _offsetY;
    private bool _dragging;
    private Point _lastPoint;

    public AvatarCropWindow()
        : this("")
    {
    }

    public AvatarCropWindow(string path)
    {
        InitializeComponent();

        try
        {
            using var stream = File.OpenRead(path);
            _source = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[ZXMC] 打开图片失败: " + ex.Message);
        }

        if (_source is null)
        {
            return;
        }

        Photo.Source = _source;
        _fitScale = Math.Min(StageSize / _source.PixelSize.Width, StageSize / _source.PixelSize.Height);
        ApplyTransform();
    }

    /// <summary>把当前缩放/偏移画到图片上(TransformGroup 里的元素取不到 x:Name 字段,所以从 RenderTransform 上取)。</summary>
    private void ApplyTransform()
    {
        if (Photo.RenderTransform is not TransformGroup group || group.Children.Count < 2)
        {
            return;
        }

        if (group.Children[0] is ScaleTransform scale)
        {
            scale.ScaleX = _zoom;
            scale.ScaleY = _zoom;
        }

        if (group.Children[1] is TranslateTransform move)
        {
            move.X = _offsetX;
            move.Y = _offsetY;
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnStagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = true;
        _lastPoint = e.GetPosition(Stage);
        e.Pointer.Capture(Stage);
    }

    private void OnStagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;

        var now = e.GetPosition(Stage);
        _offsetX += now.X - _lastPoint.X;
        _offsetY += now.Y - _lastPoint.Y;
        _lastPoint = now;
        ApplyTransform();
    }

    private void OnStagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);
    }

    /// <summary>滚轮缩放。</summary>
    private void OnStageWheel(object? sender, PointerWheelEventArgs e)
    {
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value + e.Delta.Y * 0.05, ZoomSlider.Minimum, ZoomSlider.Maximum);
        e.Handled = true;
    }

    private void OnZoomChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _zoom = e.NewValue;
        ApplyTransform();
    }

    /// <summary>确定:把选框里的内容裁成 256x256 的 PNG 返回。</summary>
    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (_source is null)
        {
            Close(null);
            return;
        }

        var scale = _fitScale * _zoom;
        var frameLeft = (StageSize - FrameSize) / 2;

        // 图片内容(缩放后)在预览区里的左上角
        var contentLeft = StageSize / 2 + _offsetX - _source.PixelSize.Width * scale / 2;
        var contentTop = StageSize / 2 + _offsetY - _source.PixelSize.Height * scale / 2;

        var side = Math.Min(FrameSize / scale, Math.Min(_source.PixelSize.Width, _source.PixelSize.Height));
        var sx = Math.Clamp((frameLeft - contentLeft) / scale, 0, Math.Max(0, _source.PixelSize.Width - side));
        var sy = Math.Clamp((frameLeft - contentTop) / scale, 0, Math.Max(0, _source.PixelSize.Height - side));

        try
        {
            var target = new RenderTargetBitmap(new PixelSize(OutputSize, OutputSize), new Vector(96, 96));
            using (var context = target.CreateDrawingContext())
            {
                context.DrawImage(_source, new Rect(sx, sy, side, side), new Rect(0, 0, OutputSize, OutputSize));
            }

            using var stream = new MemoryStream();
            target.Save(stream);

            Logger.Log(LogLevel.Info, $"[ZXMC] 已裁剪头像 {OutputSize}x{OutputSize}(源区域 {sx:F0},{sy:F0},{side:F0})");
            Close(stream.ToArray());
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[ZXMC] 裁剪头像失败: " + ex.Message);
            Close(null);
        }
    }
}
