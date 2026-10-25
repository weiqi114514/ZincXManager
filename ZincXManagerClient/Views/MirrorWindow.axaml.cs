using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ZincXManagerClient.Views;

/// <summary>下载镜像设置窗口:预设点选 + 手动输入,确定后回填</summary>
public partial class MirrorWindow : Window
{
    // 预设组合(列表里那两条组合项用)
    private static readonly string[] Presets =
    [
        "https://gh-proxy.com/",
        "https://ghfast.top/",
        "https://ghproxy.net/",
        "https://gh-proxy.com/;https://ghfast.top/",
        "https://gh-proxy.com/;https://ghfast.top/;https://ghproxy.net/",
    ];

    /// <summary>用户填的地址(取消时为 null)</summary>
    public string? Result { get; private set; }

    /// <summary>设计器用</summary>
    public MirrorWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);
    }

    /// <summary>带着当前值打开</summary>
    public MirrorWindow(string current) : this()
    {
        MirrorBox.Text = current ?? "";
    }

    /// <summary>弹出并返回新值(null = 取消)</summary>
    public static async Task<string?> EditAsync(Window owner, string current)
    {
        var win = new MirrorWindow(current);
        await win.ShowDialog(owner);
        return win.Result;
    }

    /// <summary>点预设:填进输入框</summary>
    private void OnPresetSelected(object? sender, SelectionChangedEventArgs e)
    {
        var index = PresetList.SelectedIndex;
        if (index >= 0 && index < Presets.Length)
        {
            MirrorBox.Text = Presets[index];
            MirrorBox.CaretIndex = MirrorBox.Text?.Length ?? 0;
        }
    }

    /// <summary>确定</summary>
    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Result = (MirrorBox.Text ?? "").Trim();
        Close();
    }

    /// <summary>取消 / 关窗</summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    /// <summary>拖标题栏</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
