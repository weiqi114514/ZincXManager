using Avalonia.Controls;  // 窗口与控件

namespace ZincXManagerClient.Views;

/// <summary>启动时的极简等待窗:只显示"正在连接数据源…"和目标地址。 连上就换成登录窗口,连不上换成连接窗口 + 弹窗。</summary>
public partial class StartupWindow : Window
{
    public StartupWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);
    }

    public StartupWindow(string target) : this()
    {
        TargetText.Text = target;
    }
}
