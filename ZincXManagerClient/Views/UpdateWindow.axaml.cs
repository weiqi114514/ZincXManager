using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZincXManagerClient.Services;
using ZincXManagerShared.Logging;

namespace ZincXManagerClient.Views;

/// <summary>更新提示:+ 下载 + 装完提示,一个窗口切三种状态</summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _info;
    private CancellationTokenSource? _cts;

    /// <summary>设计器用</summary>
    public UpdateWindow() : this(new UpdateInfo("", "", "", "", "", "", "", false))
    {
    }

    /// <summary>按检查结果开窗</summary>
    public UpdateWindow(UpdateInfo info)
    {
        _info = info;
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);

        Headline.Text = "获取到新的更新";
        VersionLine.Text = $"{info.CurrentVersion}  ->  {info.LatestVersion}";
        LinkText.Text = "查看更新日志";
    }

    /// <summary>弹出并等它关掉</summary>
    public static Task ShowAsync(Window owner, UpdateInfo info) => new UpdateWindow(info).ShowDialog(owner);

    /// <summary>点「立刻更新」:开始下载并自动安装</summary>
    private async void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        UpdateButton.IsVisible = false;
        CancelButton.Content = "取消下载";
        ProgressRow.IsVisible = true;
        _cts = new CancellationTokenSource();

        var progress = new Progress<int>(p =>
        {
            Progress.Value = p;
            ProgressText.Text = p >= 100 ? "下载完成,正在安装…" : $"正在下载… {p}%";
        });

        Logger.Log(LogLevel.Info, $"[更新] 开始更新 {_info.CurrentVersion} -> {_info.LatestVersion}");
        var ok = await UpdateService.DownloadAndApplyAsync(_info, progress, _cts.Token);
        if (!ok)
        {
            ShowFailed();
            return;
        }

        ShowDone(UpdateService.IsInstalled);
    }

    /// <summary>失败提示</summary>
    private void ShowFailed()
    {
        ProgressRow.IsVisible = false;
        Headline.Text = "更新失败";
        Headline.Foreground = Avalonia.Media.Brushes.DarkRed;
        VersionLine.Text = "下载或安装没成功,可以稍后再试,或到Release页面手动下载";
        LinkRow.IsVisible = true;
        DoneText.IsVisible = false;
        UpdateButton.IsVisible = false;
        CancelButton.IsVisible = false;
        OkButton.IsVisible = true;
    }

    /// <summary>下载完成提示(要用户自己关程序)</summary>
    private void ShowDone(bool installed)
    {
        ProgressRow.IsVisible = false;
        LinkRow.IsVisible = false;
        Headline.Text = "更新已下载,正在自动安装";
        VersionLine.Text = installed
            ? "请关闭本程序,安装会在关闭后继续;装完重新打开就是新版本"
            : "请关闭本程序,文件会在关闭后替换;完成后重新打开就是新版本";
        DoneText.IsVisible = false;
        UpdateButton.IsVisible = false;
        CancelButton.IsVisible = false;
        OkButton.IsVisible = true;
    }

    /// <summary>打开更新日志</summary>
    private void OnChangelogClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = UpdateService.ChangelogUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[更新] 打开更新日志失败: " + ex.Message);
        }
    }

    /// <summary>取消 / 关闭</summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
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
