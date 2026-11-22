using Avalonia;  // AvaloniaPropertyChangedEventArgs
using Avalonia.Controls;  // 用户控件 / 文本变更事件
using Avalonia.Input;  // 键盘输入
using Avalonia.Interactivity;  // 路由事件
using Avalonia.Threading;  // UI 线程调度
using ZincXManagerClient.ViewModels;  // TerminalViewModel

namespace ZincXManagerClient.Views;

/// <summary>RCON / ZXMM 桥接控制台页:只读终端 + 目标服下拉 + 持续输出开关。</summary>
public partial class TerminalView : UserControl
{
    private TerminalViewModel? _viewModel;  // 当前绑定的视图模型,换 DataContext 时跟着换

    /// <summary>构造:挂好回车发送、开关回包、输出跟随滚动。</summary>
    public TerminalView()
    {
        InitializeComponent();

        // 开关只认用户点击:VM 里因切服自动关掉时不该再发包
        TapSwitch.PropertyChanged += OnTapSwitchPropertyChanged;

        DataContextChanged += (_, _) => _viewModel = DataContext as TerminalViewModel;
        AttachedToVisualTree += (_, _) => _viewModel = DataContext as TerminalViewModel;
    }

    /// <summary>输出区文本换了一批就滚到最后一行,新行别被挤在下面看不见。</summary>
    private void OnTerminalTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        // 文本刚换掉时行数还没重算,等这一轮布局跑完再滚
        Dispatcher.UIThread.Post(() =>
        {
            var last = TerminalBox.GetLineCount() - 1;
            if (last >= 0)
            {
                TerminalBox.ScrollToLine(last);
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>开关被拨动就发 6083;等 VM 回填的同值不重复发。</summary>
    private void OnTapSwitchPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ToggleSwitch.IsCheckedProperty || _viewModel is null)
        {
            return;
        }

        var on = TapSwitch.IsChecked == true;
        if (on == _viewModel.TapEnabled)
        {
            return;
        }

        _ = _viewModel.SetTapAsync(on);
    }

    /// <summary>输入框里按回车等于点发送。</summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        Submit();
    }

    /// <summary>把输入框里的命令交给视图模型发出去。</summary>
    private void Submit()
    {
        if (_viewModel is null)
        {
            return;
        }

        var text = _viewModel.InputText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _ = _viewModel.SendAsync(text);
    }

    /// <summary>点「发送」。</summary>
    private void OnSendClick(object? sender, RoutedEventArgs e) => Submit();

    /// <summary>搜索框内容变了就本地重算一遍结果。</summary>
    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.SearchText = SearchBox.Text ?? "";
        _viewModel.RefreshSearch();
    }

    /// <summary>展开 / 收起搜索结果区。</summary>
    private void OnSearchClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.SearchActive = !_viewModel.SearchActive;
        if (_viewModel.SearchActive)
        {
            _viewModel.RefreshSearch();
            SearchBox.Focus();
        }
    }

    /// <summary>清空终端(只清屏幕)。</summary>
    private void OnClearClick(object? sender, RoutedEventArgs e) => _viewModel?.Clear();
}
