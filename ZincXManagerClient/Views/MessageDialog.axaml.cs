using System;  // 基础类型 / 常用静态方法
using System.Threading.Tasks;  // async / await 异步
using Avalonia;  // 界面框架入口
using Avalonia.Controls;  // 窗口与控件
using Avalonia.Input;  // 鼠标键盘输入
using Avalonia.Interactivity;  // 路由事件
using Avalonia.Media;  // 画刷 / 颜色 / 几何

namespace ZincXManagerClient.Views;

/// <summary>弹窗参数(按设计稿:标题栏 + 大标题 + 说明 + 可选填空 / 选择 + 左下提示 + 右下按钮)。</summary>
public sealed class DialogOptions
{
    /// <summary>大标题(设计稿里的"请输入标题")。</summary>
    public string Title { get; init; } = "提示";

    /// <summary>警告 / 错误类弹窗:标题栏变红,标题前加感叹号。</summary>
    public bool Warning { get; init; }

    /// <summary>说明文本。</summary>
    public string Text { get; init; } = "";

    /// <summary>左下角淡色提示(可空)。</summary>
    public string Hint { get; init; } = "";

    /// <summary>填空弹窗:输入框占位文本;为 null 表示不显示输入框。</summary>
    public string? Input { get; init; }

    /// <summary>选择弹窗:下拉选项;为 null 表示不显示下拉。</summary>
    public string[]? Choices { get; init; }

    /// <summary>输入框初始值。</summary>
    public string InputValue { get; init; } = "";

    /// <summary>下拉初始选中项。</summary>
    public string Choice { get; init; } = "";

    /// <summary>右下按钮(文字, 是否主按钮, 回传的标记)。</summary>
    public (string Text, bool Primary, string Tag)[] Buttons { get; init; } = [("确认", true, "ok")];

    /// <summary>
    /// "必须读完才能点"的秒数(&gt;0 时:主按钮先禁用并显示倒计时,读够秒数才可点)。
    /// 用在"改官方服务端地址"这种后果很重的操作上。
    /// </summary>
    public int GateSeconds { get; init; }

    /// <summary>倒计时期间主按钮上的文字(会拼上剩余秒数)。</summary>
    public string GateText { get; init; } = "请阅读";
}

/// <summary>弹窗结果:点了哪个按钮 + 输入内容 + 选中的项。</summary>
public sealed record DialogResult(string Tag, string Input, string Choice);

/// <summary>
/// 通用弹窗:一套窗口覆盖设计稿里的四种(单按钮 / 双按钮 / 填空 / 选择)。
/// 用法见 <see cref="InfoAsync"/> / <see cref="ConfirmAsync"/> / <see cref="AskAsync"/> / <see cref="ChooseAsync"/>。
/// </summary>
public partial class MessageDialog : Window
{
    private DialogResult _result = new("", "", "");

    public MessageDialog()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);   // 半透明 / 亚克力:开窗时定一次

        // Esc = 不确认(和点右上角关闭同一个语义):一律按"取消"收尾,
        // 这样"读满 5 秒才能确认"的守门弹窗也能被随手关掉,不会卡住用户。
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; Finish("cancel"); }
        };
    }

    /// <summary>单按钮:提示 / 报错。</summary>
    public static async Task InfoAsync(Window owner, string title, string text, string ok = "确认", string hint = "")
        => await ShowAsync(owner, new DialogOptions
        {
            Title = title,
            Text = text,
            Hint = hint,
            Buttons = [(ok, true, "ok")],
        });

    /// <summary>警告弹窗:标题栏红色 + 感叹号,单个「确认」按钮。</summary>
    public static async Task WarnAsync(Window owner, string title, string text, string ok = "确认", string hint = "")
        => await ShowAsync(owner, new DialogOptions
        {
            Title = title,
            Text = text,
            Hint = hint,
            Warning = true,
            Buttons = [(ok, true, "ok")],
        });

    /// <summary>双按钮:是 / 否。</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string text,
        string yes = "是", string no = "否", string hint = "")
    {
        var result = await ShowAsync(owner, new DialogOptions
        {
            Title = title,
            Text = text,
            Hint = hint,
            Buttons = [(yes, true, "yes"), (no, false, "no")],
        });

        return result.Tag == "yes";
    }

    /// <summary>填空弹窗:返回输入内容(取消返回 null)。</summary>
    public static async Task<string?> AskAsync(Window owner, string title, string text,
        string placeholder = "", string ok = "确认", string hint = "", string value = "")
    {
        var result = await ShowAsync(owner, new DialogOptions
        {
            Title = title,
            Text = text,
            Hint = hint,
            Input = placeholder,
            InputValue = value,
            Buttons = [(ok, true, "ok"), ("取消", false, "cancel")],
        });

        return result.Tag == "ok" ? result.Input : null;
    }

    /// <summary>选择弹窗:返回选中项(取消返回 null)。</summary>
    public static async Task<string?> ChooseAsync(Window owner, string title, string text, string[] choices,
        string ok = "确认", string hint = "")
    {
        var result = await ShowAsync(owner, new DialogOptions
        {
            Title = title,
            Text = text,
            Hint = hint,
            Choices = choices,
            Choice = choices.Length > 0 ? choices[0] : "",
            Buttons = [(ok, true, "ok"), ("取消", false, "cancel")],
        });

        return result.Tag == "ok" ? result.Choice : null;
    }

    /// <summary>
    /// 警示 + <b>读满 5 秒才能确认</b> 的双按钮弹窗(用于"改官方服务端地址"这类危险操作)。
    /// 返回 true = 用户读完并确认;false = 取消 / 直接关掉窗口。
    /// </summary>
    public static async Task<bool> ReadConfirmAsync(Window owner, string title, string text,
        string confirm = "我已阅读,继续自定义", string cancel = "算了,保持官方", int seconds = 5, string hint = "")
    {
        var result = await ShowAsync(owner, new DialogOptions
        {
            Title = title,
            Text = text,
            Hint = hint,
            GateSeconds = seconds,
            GateText = "请阅读",
            Buttons = [(confirm, true, "ok"), (cancel, false, "cancel")],
        });

        return result.Tag == "ok";
    }

    /// <summary>弹一个自定义弹窗,等用户点按钮。</summary>
    public static async Task<DialogResult> ShowAsync(Window owner, DialogOptions options)
    {
        var dialog = new MessageDialog();
        dialog.Configure(options);

        // 显式按宿主窗口居中:CenterOwner 在某些环境下会退化成“屏幕居中”,
        // 这里用 owner 的屏幕位置 + 尺寸自己算一遍,保证弹窗压在宿主正中。
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        var ow = owner.Bounds.Width;
        var oh = owner.Bounds.Height;
        var dw = dialog.Width;
        var dh = 216;   // 弹窗高度(SizeToContent=Height,先按设计稿估值;显示后 Avalonia 会自己收)
        dialog.Position = new PixelPoint(
            (int)Math.Round(owner.Position.X + (ow - dw) / 2),
            (int)Math.Round(owner.Position.Y + (oh - dh) / 2));

        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    /// <summary>配置对话框内容。</summary>
    private void Configure(DialogOptions options)
    {
        TitleText.Text = options.Title;
        BodyText.Text = options.Text;
        HintText.Text = options.Hint;

        // 警告 / 错误类:标题栏换成红色,标题前加感叹号
        if (options.Warning)
        {
            TitleBar.Background = new SolidColorBrush(Color.Parse("#C62828"));
            BarTitle.Text = "⚠ " + options.Title;
        }
        else
        {
            BarTitle.Text = options.Title;
        }


        if (options.Input is { } placeholder)
        {
            InputBox.IsVisible = true;
            InputBox.PlaceholderText = placeholder;
            InputBox.Text = options.InputValue;
        }

        if (options.Choices is { Length: > 0 } choices)
        {
            ChoiceBox.IsVisible = true;
            ChoiceBox.ItemsSource = choices;
            ChoiceBox.SelectedItem = options.Choice.Length > 0 && Array.IndexOf(choices, options.Choice) >= 0
                ? options.Choice
                : choices[0];
        }

        Button? primaryButton = null;
        var primaryText = "";

        foreach (var (text, primary, tag) in options.Buttons)
        {
            var button = new Button
            {
                Content = text,
                Classes = { "dbtn", primary ? "primary" : "secondary" },
                IsDefault = primary,                    // 回车 = 主按钮
                IsCancel = tag == "cancel",             // Esc = 取消
            };

            button.Click += (_, _) => Finish(tag);
            ButtonsHost.Children.Add(button);

            if (primary)
            {
                primaryButton = button;
                primaryText = text;
            }
        }

        // 读满 N 秒才让点:主按钮先禁用 + 倒计时,读完恢复文字并启用
        if (options.GateSeconds > 0 && primaryButton is not null)
        {
            var left = options.GateSeconds;
            primaryButton.IsEnabled = false;
            primaryButton.IsDefault = false;                  // 倒计时里回车也不能"提前确认"
            primaryButton.Content = $"{options.GateText}… {left}";

            var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) =>
            {
                left--;

                if (left > 0)
                {
                    primaryButton.Content = $"{options.GateText}… {left}";
                    return;
                }

                timer.Stop();
                primaryButton.Content = primaryText;
                primaryButton.IsEnabled = true;
                primaryButton.IsDefault = true;
            };

            timer.Start();
        }

        // 有输入框就把焦点给它并全选预填内容(直接输入即替换),回车等于点第一个按钮
        if (InputBox.IsVisible)
        {
            Opened += (_, _) =>
            {
                InputBox.Focus();
                InputBox.SelectAll();
            };
        }
    }

    /// <summary>收结果并关窗。</summary>
    private void Finish(string tag)
    {
        _result = new DialogResult(tag, InputBox.Text ?? "", ChoiceBox.SelectedItem as string ?? "");
        Close();
    }

    /// <summary>回车确认 / Esc 取消。</summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Finish("ok");
        }
    }

    /// <summary>按住标题栏拖动窗口。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>点右上角关闭按钮:关掉这个窗口。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Finish("cancel");
}
