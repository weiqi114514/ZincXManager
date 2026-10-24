using System.Linq;  // LINQ 查询
using Avalonia.Controls;  // 窗口与控件
using Avalonia.Input;  // 鼠标键盘输入
using Avalonia.Interactivity;  // 路由事件
using ZincXManagerClient.Models;  // 客户端自身

namespace ZincXManagerClient.Views;

/// <summary>子功能选择页(所有页共用这一个模板):标题 + 子功能卡片列表。</summary>
public partial class FunctionPickerView : UserControl
{
    public FunctionPickerView()
    {
        InitializeComponent();
    }

    /// <summary>页面标题 + 子功能列表。</summary>
    public void SetContent(string title, System.Collections.Generic.IEnumerable<FunctionItem> items)
    {
        TitleText.Text = title;
        ItemsHost.ItemsSource = items;
    }

    /// <summary>点卡片。</summary>
    private void OnCardPressed(object? sender, PointerPressedEventArgs e) => Open(sender);

    /// <summary>点「进入」。</summary>
    private void OnEnterClick(object? sender, RoutedEventArgs e) => Open(sender);

    /// <summary>打开子功能(目前仅提示)。</summary>
    private void Open(object? sender)
    {
        if (sender is not Control { Tag: string key } || key.Length == 0)
        {
            return;
        }

        var title = (ItemsHost.ItemsSource as System.Collections.Generic.IEnumerable<FunctionItem>)?
            .FirstOrDefault(i => i.Key == key)?.Title ?? key;

        var (source, hint) = key switch
        {
            "srv-square" => ("数据来源:子服务端(ZXMS)", "和锌源广场功能一样,只是社区内容来自你连的子服务端"),
            "zinc-square" => ("数据来源:官方服务端(ZXMOS)", "和服务器广场功能一样,只是社区内容来自官方"),
            _ => ("", "先占个位置,做好之后从这里进"),
        };

        _ = MessageDialog.InfoAsync(TopLevel.GetTopLevel(this) as Window, title,
            key is "srv-square" or "zinc-square" ? title + "还没做 —— " + source : "这个子功能还没做",
            hint: hint);
    }
}
