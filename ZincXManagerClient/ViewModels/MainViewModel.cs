using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZincXManagerShared.Logging;

namespace ZincXManagerClient.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string Greeting { get; set; } = "Welcome to Avalonia!";

    // 方式二:MVVM Command(源码生成器会生成 ClickCommand,XAML 里绑定它)
    [RelayCommand]
    private void Click()
    {
        Logger.Log(LogLevel.Info, "[ZXMC] 按钮(Command 方式)被点击");
    }
}
