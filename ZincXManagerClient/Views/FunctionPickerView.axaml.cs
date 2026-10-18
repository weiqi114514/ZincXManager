using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZincXManagerClient.Models;

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

    private void OnCardPressed(object? sender, PointerPressedEventArgs e) => Open(sender);

    private void OnEnterClick(object? sender, RoutedEventArgs e) => Open(sender);

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
