using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ZincXManagerClient.Models;

/// <summary>左侧导航项。</summary>
public partial class NavItem : ObservableObject
{
    public NavItem(string label, string iconData, double iconWidth = 5, double iconHeight = 5)
    {
        Label = label;
        IconData = iconData;
        IconWidth = iconWidth;
        IconHeight = iconHeight;
        Icon = Geometry.Parse(iconData);
    }

    public string Label { get; }

    /// <summary>图标路径数据(SVG path data,坐标系见 IconWidth / IconHeight)。</summary>
    public string IconData { get; }

    /// <summary>图标原始宽度(设计稿单位)。</summary>
    public double IconWidth { get; }

    /// <summary>图标原始高度(设计稿单位)。</summary>
    public double IconHeight { get; }

    /// <summary>供 XAML 直接绑定的几何图形。</summary>
    public Geometry Icon { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
