using Avalonia.Controls;
using Avalonia.Interactivity;
using ZincXManagerShared.Logging;

namespace ZincXManagerClient.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // 方式一:直接在 XAML 里绑定事件(Click="OnMyButtonClick")
    private void OnMyButtonClick(object? sender, RoutedEventArgs e)
    {
        Logger.Log(LogLevel.Info, "[ZXMC] 按钮(事件方式)被点击");
    }
}
