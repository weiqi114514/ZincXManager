using System;  // Math / MathClamp / ProcessStartInfo
using System.Diagnostics;  // Process:用系统看图器打开图片
using System.IO;  // MemoryStream:头像字节转图片
using System.Linq;  // FirstOrDefault:从拖进来的文件里挑第一个图片
using System.Threading.Tasks;  // Task:右键复制的异步等待
using Avalonia;  // Thickness / RelativePoint
using Avalonia.Controls;  // 窗口与控件
using Avalonia.Input;  // 鼠标键盘输入
using Avalonia.Interactivity;  // RoutedEventArgs
using Avalonia.Layout;  // Orientation / Layout 对齐
using Avalonia.Media;  // 画刷与缩放变换
using Avalonia.Media.Imaging;  // Bitmap:头像与聊天图片
using Avalonia.Platform.Storage;  // IStorageFile:拖入与粘贴的文件
using Avalonia.Threading;  // Dispatcher:异步回到界面线程

using ZincXManagerShared.Account;  // AvatarStore:读账号头像缓存

namespace ZincXManagerClient.Views;

/// <summary>聊天窗体:从好友条里选中好友后单独打开。图片显示/预览/粘贴/拖入都是本地行为,不接网络。</summary>
public partial class ChatWindow : Window
{
    private bool _settingsOpen;  //侧栏现在是靠位移显示的,记一下展开状态

    private double _scale = 1;  //预览缩放倍率(1 = 适应窗口)
    private string _previewPath = "";  //当前预览的图片路径,双击时交给系统看图器

    private readonly ScaleTransform _zoom = new(1, 1);  //预览缩放
    private readonly TranslateTransform _pan = new();  //预览平移:图放大后可以拖着看
    private bool _dragging;  //是否正在拖图
    private Point _dragFrom;  //按下时鼠标所在位置
    private Vector _panFrom;  //按下时的平移量

    private const double MinScale = 0.25;  //最小缩放 25%
    private const double MaxScale = 4;  //最大缩放 400%
    private const double ZoomStep = 0.1;  //滚轮与加减按钮的步进

    public ChatWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);
        WindowResizeHelper.Attach(this);  //无边框窗口自己处理边缘拖拽,宽高都能调

        DragDrop.SetAllowDrop(this, true);  //把图片拖进聊天窗就能发
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);
        AddHandler(DragDrop.DropEvent, OnFileDrop);

        //Ctrl+V 走隧道:输入框先拿到的话会被当成文本粘贴,图片就进不来了
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        //预览图:缩放 + 平移两个变换叠着用;拖图事件挂在舞台上,图本身不抢事件
        PreviewImage.RenderTransformOrigin = RelativePoint.Center;
        PreviewImage.RenderTransform = new TransformGroup { Children = { _zoom, _pan } };
        PreviewStage.PointerPressed += OnPreviewPressed;
        PreviewStage.PointerMoved += OnPreviewMoved;
        PreviewStage.PointerReleased += OnPreviewReleased;
    }

    private const string DemoAccountId = "5";  //界面阶段的示例账号 ID,接上协议后换成对方的真实 ID

    public ChatWindow(string friendName) : this()
    {
        FriendNameText.Text = friendName;
        BindSource();
        BuildSamples(friendName);

        //界面预览用:config 里写 demoimg=<图片路径> 就直接发一条图片气泡,省得每次手动拖
        if (ClientConfig.GetOrEmpty("demoimg") is { Length: > 0 } demo)
        {
            SendImageFile(demo);
        }
    }

    /// <summary>对象 ID 按「数据源名称-ID」显示;官方服务端固定用 ZXMOS 打头。来源服务端不动,仍是 XAML 里写的那样。</summary>
    private void BindSource()
    {
        var officialOnly = ClientConfig.GetOrEmpty("mode") == "OOS";  //只连官方端时,来源就是 ZXMOS
        var source = officialOnly ? "ZXMOS" : ClientSession.Connect?.ServerName;

        if (string.IsNullOrWhiteSpace(source))
        {
            source = ClientConfig.GetOrEmpty("czname");
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            source = "ZXMOS";  //拿不到名称时按官方端打头,至少比空着好认
        }

        ObjectIdText.Text = $"{source}-{DemoAccountId}";
    }

    /// <summary>按住标题栏拖动窗口。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>点最小化按钮:窗口最小化。</summary>
    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>点最大化按钮:在最大化与还原之间切换。</summary>
    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>点右上角关闭按钮:关掉这个窗口。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>点三个点:右侧会话设置栏从右边滑出 / 滑回去(改外层宽度,带过渡)。</summary>
    private void OnToggleSettings(object? sender, RoutedEventArgs e)
    {
        _settingsOpen = !_settingsOpen;
        DrawerHost.Width = _settingsOpen ? 286 : 0;
    }

    /// <summary>点发送:把输入框里的文字当成自己的消息贴到列表末尾。</summary>
    private void OnSendClick(object? sender, RoutedEventArgs e) => SendCurrentInput();

    /// <summary>回车发送;Shift+回车留给以后换行。</summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        SendCurrentInput();
    }

    /// <summary>发红包 / 发送文件:界面先占位,点了只提示还没接上。</summary>
    private void OnRedPacketClick(object? sender, RoutedEventArgs e)
        => AppendSystemLine("发红包:界面占位,还没接服务端");

    private void OnSendFileClick(object? sender, RoutedEventArgs e)
        => AppendSystemLine("发送文件:界面占位,还没接服务端");

    /// <summary>界面阶段先摆几条示例消息,等接上服务端再换成真实记录。</summary>
    private void BuildSamples(string friendName)
    {
        var initial = friendName.Length > 0 ? friendName[..1] : "友";
        MessageList.Children.Add(BuildIncoming("晚上七点开服,记得提前十分钟上线", initial));
        MessageList.Children.Add(BuildOutgoing("收到,我先把手头的日志看完"));
        MessageList.Children.Add(BuildIncoming("对了,新版本 b0.2.0c 的包我放桌面了", initial));
    }

    /// <summary>取输入框内容 → 追加气泡 → 清空输入并滚到底。</summary>
    private void SendCurrentInput()
    {
        var text = (InputBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            return;
        }

        MessageList.Children.Add(BuildOutgoing(text));
        InputBox.Text = "";
        MessageScroll.ScrollToEnd();
    }

    /// <summary>对方发的一条:头像在左、气泡在右。</summary>
    private StackPanel BuildIncoming(string text, string initial)
    {
        var bubble = new Border { Padding = new Thickness(12, 9), MaxWidth = 420 };
        bubble.Classes.Add("bubbleIn");
        bubble.ContextFlyout = BuildTextMenu(text);
        bubble.Child = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#1F2A28")),  //深墨:白底气泡上要够暗
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        row.Children.Add(BuildAvatar(initial, null));
        row.Children.Add(bubble);
        return row;
    }

    /// <summary>自己发的一条:气泡在左、头像在右,整体靠右。</summary>
    private StackPanel BuildOutgoing(string text)
    {
        var bubble = new Border { Padding = new Thickness(12, 9), MaxWidth = 420 };
        bubble.Classes.Add("bubbleOut");
        bubble.ContextFlyout = BuildTextMenu(text);
        bubble.Child = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White,  //拉白:主题蓝底上白字
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        row.Children.Add(bubble);
        row.Children.Add(BuildAvatar("我", ClientSession.User));
        return row;
    }

    /// <summary>文本气泡的右键菜单:只给「复制」。</summary>
    private MenuFlyout BuildTextMenu(string text)
    {
        var copy = new MenuItem { Header = "复制" };
        copy.Click += async (_, _) => await CopyTextAsync(text);

        var flyout = new MenuFlyout();
        flyout.Items.Add(copy);
        return flyout;
    }

    /// <summary>图片气泡的右键菜单:打开所在位置 / 复制图片。</summary>
    private MenuFlyout BuildImageMenu(string path)
    {
        var open = new MenuItem { Header = "打开图片所在位置" };
        open.Click += (_, _) => OpenInExplorer(path);

        var copy = new MenuItem { Header = "复制" };
        copy.Click += async (_, _) => await CopyImageAsync(path);

        var flyout = new MenuFlyout();
        flyout.Items.Add(open);
        flyout.Items.Add(copy);
        return flyout;
    }

    /// <summary>把一条消息的文字放进剪贴板。</summary>
    private async Task CopyTextAsync(string text)
    {
        try
        {
            if (Clipboard is { } board)
            {
                var transfer = new DataTransfer();
                transfer.Add(DataTransferItem.CreateText(text));
                await board.SetDataAsync(transfer);
            }
        }
        catch
        {
            AppendSystemLine("复制失败");
        }
    }

    /// <summary>把一张图片放进剪贴板(可以直接粘到别的程序里)。</summary>
    private async Task CopyImageAsync(string path)
    {
        try
        {
            var bitmap = new Bitmap(path);  //别用 using:交给剪贴板后还要活一会儿
            if (Clipboard is { } board)
            {
                var transfer = new DataTransfer();
                transfer.Add(DataTransferItem.Create(DataFormat.Bitmap, bitmap));
                await board.SetDataAsync(transfer);
            }
        }
        catch
        {
            AppendSystemLine("复制图片失败");
        }
    }

    /// <summary>在资源管理器里选中这个文件。</summary>
    private static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // 系统没有资源管理器就什么都不做,不值得打断聊天
        }
    }

    /// <summary>一行居中的系统提示(占位功能用)。</summary>
    private void AppendSystemLine(string text)
        => MessageList.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Brushes.Gray,  //淡一点,和聊天内容区分开
        });

    /// <summary>消息旁边的小头像:方形,有账号头像就显示图片,没有就用首字占位。</summary>
    private static Border BuildAvatar(string caption, string? user)
    {
        var border = new Border
        {
            Width = 30,
            Height = 30,
            VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(1),
        };
        border.Classes.Add("avatarMini");

        var image = TryLoadAvatar(user);
        if (image is not null)
        {
            border.Child = new Image { Source = image, Stretch = Stretch.UniformToFill };
            return border;
        }

        border.Child = new TextBlock
        {
            Text = caption,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return border;
    }

    /// <summary>读账号头像缓存;没有或解不开就返回 null,交给首字占位。</summary>
    private static IImage? TryLoadAvatar(string? user)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            return null;
        }

        try
        {
            var bytes = AvatarStore.Load(user);
            return bytes.Length == 0 ? null : new Bitmap(new MemoryStream(bytes));
        }
        catch
        {
            return null;  //头像坏了不影响聊天界面
        }
    }

    // ───────── 图片消息 ─────────

    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];  //能显示/发送的图片后缀

    /// <summary>后缀是不是图片。</summary>
    private static bool IsImage(string path)
        => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>发一张本机图片:现在只贴本地气泡,接上协议后改成引用服务端文件名。</summary>
    private void SendImageFile(string path)
    {
        if (!File.Exists(path) || !IsImage(path))
        {
            AppendSystemLine("这个文件不是图片,没发出去");
            return;
        }

        var row = BuildImageRow(path, true);
        if (row is null)
        {
            AppendSystemLine("这张图片打不开,没发出去");
            return;
        }

        MessageList.Children.Add(row);
        MessageScroll.ScrollToEnd();
    }

    /// <summary>图片气泡:上面是图,下面一行文件名+大小;单击预览,双击交给系统看图器。</summary>
    private StackPanel? BuildImageRow(string path, bool mine)
    {
        Bitmap bitmap;
        try
        {
            bitmap = new Bitmap(path);
        }
        catch
        {
            return null;  //坏图或格式不支持:安静失败,由调用方给提示
        }

        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            MaxWidth = 220,
            MaxHeight = 220,
        };

        var box = new Border { Child = image, Tag = path };
        box.Classes.Add("bubbleImg");
        box.PointerPressed += (_, e) => OnImagePressed(box, path, e);
        box.ContextFlyout = BuildImageMenu(path);

        var caption = new TextBlock
        {
            Text = $"{Path.GetFileName(path)}  {DescribeSize(new FileInfo(path).Length)}",
            FontSize = 10.5,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = Brushes.Gray,  //和气泡里的正文区分开
        };

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(box);
        stack.Children.Add(caption);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };

        if (mine)
        {
            row.Children.Add(stack);
            row.Children.Add(BuildAvatar("我", ClientSession.User));
        }
        else
        {
            row.Children.Add(BuildAvatar("友", null));
            row.Children.Add(stack);
        }

        return row;
    }

    /// <summary>左键打开预览;双击(ClickCount=2)交给系统看图器。</summary>
    private void OnImagePressed(Border box, string path, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            e.Handled = true;  //双击不再触发预览层的单击逻辑
            OpenWithSystemViewer(path);
            return;
        }

        OpenPreview(path);
    }

    /// <summary>把字节数说成人话。</summary>
    private static string DescribeSize(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0:0.#} MB"
            : $"{Math.Max(1, bytes / 1024)} KB";

    // ───────── 预览层 ─────────

    /// <summary>打开预览层:加载图片并回到 100%。</summary>
    private void OpenPreview(string path)
    {
        try
        {
            PreviewImage.Source = new Bitmap(path);
        }
        catch
        {
            AppendSystemLine("这张图片打不开");
            return;
        }

        _previewPath = path;
        _pan.X = 0;  //每次打开都从「没拖过」的状态开始
        _pan.Y = 0;
        SetScale(1);
        PreviewLayer.IsVisible = true;
    }

    /// <summary>关掉预览层,顺手把图释放掉。</summary>
    private void ClosePreview()
    {
        PreviewLayer.IsVisible = false;
        PreviewImage.Source = null;
        _previewPath = "";
    }

    /// <summary>设置缩放倍率:限制在 25%–400%,同步百分比与加减按钮可用状态。</summary>
    private void SetScale(double scale)
    {
        _scale = Math.Clamp(scale, MinScale, MaxScale);
        _zoom.ScaleX = _scale;
        _zoom.ScaleY = _scale;
        ZoomText.Text = $"{(int)Math.Round(_scale * 100)}%";
        ZoomOutButton.IsEnabled = _scale > MinScale + 0.001;
        ZoomInButton.IsEnabled = _scale < MaxScale - 0.001;
    }

    /// <summary>按下左键开始拖图。</summary>
    private void OnPreviewPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PreviewStage).Properties.IsLeftButtonPressed)
        {
            return;
        }

        //双击交给系统看图器(捕获在这里,子元素的 DoubleTapped 收不到,只能自己判)
        if (e.ClickCount >= 2)
        {
            _dragging = false;
            if (_previewPath.Length > 0)
            {
                OpenWithSystemViewer(_previewPath);
            }

            return;
        }

        _dragging = true;
        _dragFrom = e.GetPosition(PreviewStage);
        _panFrom = new Vector(_pan.X, _pan.Y);
        e.Pointer.Capture(PreviewStage);
        PreviewStage.Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    /// <summary>拖动时跟着鼠标平移图片。</summary>
    private void OnPreviewMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var now = e.GetPosition(PreviewStage);
        _pan.X = _panFrom.X + (now.X - _dragFrom.X);
        _pan.Y = _panFrom.Y + (now.Y - _dragFrom.Y);
    }

    /// <summary>松手结束拖动。</summary>
    private void OnPreviewReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        e.Pointer.Capture(null);
        PreviewStage.Cursor = Cursor.Default;
    }

    /// <summary>滚轮缩放:向上放大、向下缩小。</summary>
    private void OnPreviewWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        SetScale(_scale + (e.Delta.Y > 0 ? ZoomStep : -ZoomStep));
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => SetScale(_scale + ZoomStep);

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => SetScale(_scale - ZoomStep);

    /// <summary>双击百分比:回到 100%,顺手把拖动位移也归零。</summary>
    private void OnZoomReset(object? sender, TappedEventArgs e) => ResetZoom();

    /// <summary>点百分比重置:回到 100% 并去掉拖动偏移。</summary>
    private void OnZoomResetClick(object? sender, RoutedEventArgs e) => ResetZoom();

    private void ResetZoom()
    {
        _pan.X = 0;
        _pan.Y = 0;
        SetScale(1);
    }

    /// <summary>双击预览图:交给系统看图器。</summary>
    private void OnPreviewDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_previewPath.Length > 0)
        {
            OpenWithSystemViewer(_previewPath);
        }
    }

    /// <summary>点右上角叉:关预览。</summary>
    private void OnPreviewCloseClick(object? sender, RoutedEventArgs e) => ClosePreview();

    /// <summary>用系统默认看图器打开;打不开就退回预览层提示。</summary>
    private void OpenWithSystemViewer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            AppendSystemLine("系统里没有能打开这张图的程序");
        }
    }

    // ───────── 键盘 / 拖入 ─────────

    /// <summary>Esc 关预览;Ctrl+V 从剪贴板取图片发出去。</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && PreviewLayer.IsVisible)
        {
            e.Handled = true;
            ClosePreview();
            return;
        }

        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            _ = PasteImageAsync();
        }
    }

    /// <summary>剪贴板里有文件就取第一个图片,没有就取位图存成临时文件再发。</summary>
    private async System.Threading.Tasks.Task PasteImageAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        try
        {
            // Avalonia 12 的剪贴板是「先取一次数据快照,再从里面按格式取」,不是按格式逐个问
            var transfer = await clipboard.TryGetDataAsync();
            if (transfer is not null)
            {
                var files = await transfer.TryGetFilesAsync();
                var file = files?.OfType<IStorageFile>().FirstOrDefault(f => IsImage(f.Name));
                if (file?.TryGetLocalPath() is { Length: > 0 } local)
                {
                    SendImageFile(local);
                    return;
                }

                var bitmap = await transfer.TryGetBitmapAsync();
                if (bitmap is not null)
                {
                    var temp = Path.Combine(Path.GetTempPath(), $"zxm-paste-{DateTime.Now:yyyyMMddHHmmssfff}.png");  //临时文件:界面阶段先给本地气泡用
                    await using (var stream = File.Create(temp))
                    {
                        bitmap.Save(stream);
                    }

                    Dispatcher.UIThread.Post(() => SendImageFile(temp));
                    return;
                }
            }

            AppendSystemLine("剪贴板里没有图片");
        }
        catch
        {
            AppendSystemLine("读剪贴板失败,没发出去");
        }
    }

    /// <summary>拖着文件经过窗口:是图片才允许放下。</summary>
    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        var hasImage = files?.OfType<IStorageFile>().Any(f => IsImage(f.Name)) == true;
        e.DragEffects = hasImage ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>松手:取第一个图片文件发出去。</summary>
    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        var file = files?.OfType<IStorageFile>().FirstOrDefault(f => IsImage(f.Name));
        if (file?.TryGetLocalPath() is { Length: > 0 } path)
        {
            SendImageFile(path);
        }
    }
}
