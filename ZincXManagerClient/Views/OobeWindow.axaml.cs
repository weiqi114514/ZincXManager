using System;  // 基础类型 / 常用静态方法
using Avalonia.Animation;  // 界面框架
using Avalonia.Animation.Easings;  // 界面框架
using Avalonia.Controls;  // 窗口与控件
using Avalonia.Controls.Shapes;  // 图形(线 / 矩形 / 路径)
using Avalonia.Media;  // 画刷 / 颜色 / 几何
using Avalonia.Styling;  // 样式与主题
using Avalonia.Threading;  // UI 线程调度
using Avalonia.Input;  // 鼠标键盘输入
using Avalonia.Interactivity;  // 路由事件
using Avalonia.Platform.Storage;  // 界面框架
using ZincXManagerShared.Logging;  // 共享库
using ZincXManagerClient.ViewModels;  // 客户端自身

namespace ZincXManagerClient.Views;

public partial class OobeWindow : Window
{
    /// <summary>首次使用向导窗口的构造函数。</summary>
    public OobeWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);   // 半透明 / 亚克力:开窗时定一次

        // 欢迎页那个 5 秒换一次的词:窗口开起来再量宽度/起定时器
        Opened += (_, _) =>
        {
            // 摆正位置:窗口比屏幕大或算歪了会跑到屏幕外面,这里按当前屏幕再居中一次
            try
            {
                var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
                if (screen is not null)
                {
                    var area = screen.WorkingArea;
                    var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
                    var w = (int)(ClientSize.Width * scale);
                    var h = (int)(ClientSize.Height * scale);
                    var x = area.X + Math.Max(0, (area.Width - w) / 2);
                    var y = area.Y + Math.Max(0, (area.Height - h) / 2);
                    Position = new Avalonia.PixelPoint(x, y);
                }
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[ZXMC] 引导窗定位失败: " + ex.Message);
            }

            if (this.FindControl<Border>("XSlot") is { } slot)
            {
                slot.Width = MeasureWidth("X");
            }

            if (Vm is { } vm0)
            {
                MoveModeHighlight(vm0.ModeIndex, animate: false);
            }

            StartWordAnimation();
        };

        Closed += (_, _) =>
        {
            _wordTimer?.Stop();
        };

        // 出错(被封禁 / 连不上服务器)弹窗告诉用户
        DataContextChanged += (_, _) =>
        {
            if (DataContext is OobeViewModel vm)
            {
                vm.ErrorRaised += (title, text) => _ = MessageDialog.InfoAsync(this, title, text, hint: "");
            }
        };
    }

    private OobeViewModel? Vm => DataContext as OobeViewModel;

    /// <summary>标题栏拖动。</summary>
    // ---------- 欢迎页那个会变的词 ----------

    private static readonly string[] WordCycle = { "X", "Admin", "User", "Player", "System" };

    private int _wordIndex;
    private DispatcherTimer? _wordTimer;

    /// <summary>开始每 5 秒换一次 Zinc[X]Manager 里的词。</summary>
    private void StartWordAnimation()
    {
        _wordTimer?.Stop();
        _wordTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _wordTimer.Tick += (_, _) => CycleWord();
        _wordTimer.Start();
    }

    /// <summary>旧词向下滑出,新词从上面滑进;Manager 跟着词宽左右移动。</summary>
    private void CycleWord()
    {
        var slot = this.FindControl<Border>("XSlot");
        var cur = this.FindControl<TextBlock>("XCurrent");
        var inc = this.FindControl<TextBlock>("XIncoming");
        if (slot is null || cur is null || inc is null)
        {
            return;
        }

        _wordIndex = (_wordIndex + 1) % WordCycle.Length;
        var next = WordCycle[_wordIndex];

        var h = Math.Max(cur.Bounds.Height, 56);
        var oldWidth = slot.Bounds.Width > 0 ? slot.Bounds.Width : MeasureWidth(cur.Text ?? "X");
        var newWidth = MeasureWidth(next);

        inc.Text = next;
        inc.Opacity = 1;
        inc.IsVisible = true;

        var currentShift = new TranslateTransform(0, 0);
        var incomingShift = new TranslateTransform(0, -h);
        cur.RenderTransform = currentShift;
        inc.RenderTransform = incomingShift;

        // 手写补间:不依赖动画库(之前那套在 AOT 下没真正动起来)
        var watch = System.Diagnostics.Stopwatch.StartNew();
        const double duration = 420;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var raw01 = Math.Min(1, watch.Elapsed.TotalMilliseconds / duration);
            var e = EaseOutCubic(raw01);

            currentShift.Y = h * e;
            incomingShift.Y = -h * (1 - e);
            slot.Width = oldWidth + (newWidth - oldWidth) * e;

            if (raw01 < 1)
            {
                return;
            }

            timer.Stop();
            cur.Text = next;
            cur.RenderTransform = null;
            inc.IsVisible = false;
            inc.RenderTransform = null;
            slot.Width = newWidth;
        };
        timer.Start();
    }

    /// <summary>缓出曲线:开头快、结尾慢。</summary>
    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    private static Animation MakeAnim(Transform target, string property, double to, int ms)
    {
        var prop = TranslateTransform.YProperty;
        return new Animation
        {
            Duration = TimeSpan.FromMilliseconds(ms),
            Easing = new CubicEaseInOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter { Property = prop, Value = to } } },
            },
        };
    }

    /// <summary>量一下这段文字在当前字号下多宽(用来定 X 槽的宽度)。</summary>
    private static double MeasureWidth(string text)
    {
        try
        {
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI", FontStyle.Normal, FontWeight.SemiBold),
                46, Brushes.Black);
            return Math.Ceiling(ft.Width) + 2;
        }
        catch
        {
            return text.Length * 30;
        }
    }

    /// <summary>数据源那步:点「连接」。</summary>
    private async void OnConnectSourceClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        string? lastError = null;
        void OnConnectError(string title, string text) => lastError = text;

        try
        {
            Vm.Connect.ErrorRaised += OnConnectError;

            // 按当前模式设目标(OS/SAOS → czip,OOS → ozip),否则会用 config 里的旧地址
            Vm.ApplyConnectTarget();

            // 已经连在同一目标上:不重复连,弹窗提醒
            if (Vm.Connect.IsConnectedToSameTarget)
            {
                await MessageDialog.InfoAsync(this, "已经连接",
                    $"当前已经连在 {Vm.Connect.ConnectedHost}:{Vm.Connect.ConnectedPort},不用重复连接。",
                    hint: "换目标会先断开再连");
                return;
            }

            await Vm.Connect.ConnectAsync();
            var ok = await Vm.Connect.WaitConnectedAsync();
            Vm.IsSourceConnected = ok;
            Vm.Message = ok ? "数据源已连接" : "连接失败,检查地址和端口";

            if (ok)
            {
                ClientConfig.Set("czip", Vm.CzIp.Trim());
                ClientConfig.Set("czport", Vm.CzPort.Trim());
                ClientConfig.Set("mode", Vm.Mode);
                return;
            }

            // 连不上要明确弹窗,别只改一行状态文字
            var target = Vm.Mode == "OOS" ? "ZXMOS" : "ZXMS";
            var detail = string.IsNullOrWhiteSpace(lastError) ? Vm.Message : lastError!;
            _ = MessageDialog.InfoAsync(this, $"连接 {target} 失败",
                $"{Vm.CzIp.Trim()}:{Vm.CzPort.Trim()}\n{detail}",
                hint: "检查数据源地址、端口,确认服务端已启动后重试");
        }
        catch (Exception ex)
        {
            Vm.IsSourceConnected = false;
            Vm.Message = "连接出错:" + ex.Message;

            var target = Vm.Mode == "OOS" ? "ZXMOS" : "ZXMS";
            _ = MessageDialog.InfoAsync(this, $"连接 {target} 失败",
                $"{Vm.CzIp.Trim()}:{Vm.CzPort.Trim()}\n{ex.Message}",
                hint: "检查数据源地址、端口,确认服务端已启动后重试");
        }
        finally
        {
            Vm.Connect.ErrorRaised -= OnConnectError;
        }
    }

    /// <summary>按住标题栏拖动窗口。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
    }

    /// <summary>点最小化按钮:窗口最小化。</summary>
    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>点右上角关闭按钮:关掉这个窗口。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>选择背景壁纸。</summary>
    private async void OnPickWallpaperClick(object? sender, RoutedEventArgs e)
    {
        // 只有"个性化"这一步、且按钮确实可见时才处理
        if (Vm is null || !Vm.IsPersonalize) return;
        if (sender is Button button && !button.IsEffectivelyVisible) return;

        Logger.Log(LogLevel.Info, "[ZXMC] 打开文件选择框:OOBE 背景壁纸\n" + Environment.StackTrace);

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择背景壁纸",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] }],
        });

        if (files.Count > 0)
        {
            Vm?.SelectWallpaper(files[0].Path.LocalPath);
        }
    }

    /// <summary>清除壁纸。</summary>
    private void OnClearWallpaperClick(object? sender, RoutedEventArgs e) => Vm?.ClearWallpaper();

    /// <summary>选透明样式:毛玻璃(亚克力)/ 半透明</summary>
    private void OnGlassPick(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is null || sender is not Control c || c.Tag is not string tag)
        {
            return;
        }

        Vm.GlassEnabled = tag == "glass";
        e.Handled = true;
    }

    /// <summary>选配色。</summary>
    private void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && Vm is not null)
        {
            Vm.SelectTheme(name);
        }
    }

    /// <summary>把联排按钮的高亮块移到第 index 个(每个 92 宽)</summary>
    private void MoveModeHighlight(int index, bool animate = true)
    {
        if (this.FindControl<Border>("ModeHighlight")?.RenderTransform is not TranslateTransform t)
        {
            return;
        }

        const double step = 92;
        var to = index * step;

        if (!animate)
        {
            t.X = to;
            return;
        }

        var from = t.X;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var raw01 = Math.Min(1, watch.Elapsed.TotalMilliseconds / 180);
            var e = EaseOutCubic(raw01);
            t.X = from + (to - from) * e;
            if (raw01 >= 1)
            {
                timer.Stop();
                t.X = to;
            }
        };
        timer.Start();
    }

    /// <summary>切换连接方式</summary>
    private void OnModeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode })
        {
            Vm?.SelectMode(mode);

            if (Vm is { } vm)
            {
                MoveModeHighlight(vm.ModeIndex);
            }
        }
    }

    /// <summary>提交登录。</summary>
    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) await Vm.LoginAsync();
    }

    /// <summary>注册账号(等级固定 Player)。</summary>
    private async void OnRegisterClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) await Vm.RegisterAsync();
    }

    /// <summary>上一步。</summary>
    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && Vm.Step > 0)
        {
            Vm.Step--;
        }
    }

    /// <summary>下一步 / 完成。</summary>
    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        if (Vm.Step < Vm.LastStep)
        {
            Vm.Step++;
            return;
        }

        // 完成:把这次选的壁纸 / 配色 / 透明样式 / 消息分类写进 config,
        // 然后收掉引导窗 —— 账号登录交给登录窗口(引导里不再要求登录)。
        Vm.Finish();
        ClientSession.Connect = Vm.Connect;

        if (ClientConfig.GetOrEmpty("czip").Length > 0 && ClientConfig.GetOrEmpty("czport").Length > 0)
        {
            AppWindows.ShowMain(this);
        }
        else
        {
            var connect = new ConnectViewModel();
            ClientSession.Connect = connect;
            var w = new ConnectWindow { DataContext = connect };
            WindowPlacement.CenterOnPrimaryScreen(w);
            w.Show();
            Close();
        }
    }
}
