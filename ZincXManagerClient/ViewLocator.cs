using Avalonia.Controls;  // 窗口与控件
using Avalonia.Controls.Templates;  // 界面框架
using ZincXManagerClient.ViewModels;  // 客户端自身
using ZincXManagerClient.Views;  // 客户端自身

namespace ZincXManagerClient;

/// <summary>
/// 视图模型 → 视图的对应关系(Avalonia 的全局 DataTemplate)。
///
/// <para><b>这里故意不用反射:</b>Avalonia 模板里默认的 ViewLocator 用 <c>Type.GetType</c> + <c>Activator</c>
/// 按名字找视图,NativeAOT 裁剪掉视图类型之后就失效了(发布时还会报 IL2026)。
/// 本项目的对应关系只有下面这几个且固定,写死既省事又对 AOT 友好。</para>
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        SettingsViewModel => new SettingsView(),
        MainViewModel => new MainWindow(),
        ConnectViewModel => new ConnectWindow(),
        LoginViewModel => new LoginWindow(),
        OobeViewModel => new OobeWindow(),
        null => null,
        _ => new TextBlock { Text = "Not Found: " + param.GetType().Name },
    };

    /// <summary>视图定位:按 ViewModel 类型找对应窗口/控件。</summary>
    public bool Match(object? data) => data is ViewModelBase;
}
