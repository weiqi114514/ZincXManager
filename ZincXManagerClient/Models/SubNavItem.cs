using CommunityToolkit.Mvvm.ComponentModel;

namespace ZincXManagerClient.Models;

/// <summary>设置页左侧的二级导航项(只有文字和选中态)。</summary>
public partial class SubNavItem : ObservableObject
{
    public SubNavItem(string label)
    {
        Label = label;
    }

    /// <summary>显示文字。</summary>
    public string Label { get; }

    /// <summary>是否选中(选中时左侧出现强调色小条)。</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
