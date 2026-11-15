using Avalonia.Media;  // 画刷 / 颜色 / 几何
using CommunityToolkit.Mvvm.ComponentModel;  // MVVM 可观察对象

namespace ZincXManagerClient.Models;

/// <summary>左侧导航项。</summary>
public partial class NavItem : ObservableObject
{
    public NavItem(string label, string iconData, double iconWidth = 5, double iconHeight = 5, bool iconStroke = false)
    {
        Label = label;
        IconData = iconData;
        IconWidth = iconWidth;
        IconHeight = iconHeight;
        IconStroke = iconStroke;
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

    /// <summary>图标是描边图形(24x24 线条,靠 Stroke 画)还是填充图形(旧的 5x5 轮廓)。</summary>
    public bool IconStroke { get; }

    /// <summary>描边图标:模板里那条 Stroke 用的 Path 显示。</summary>
    public bool ShowStrokeIcon => IconStroke;

    /// <summary>填充图标:模板里那条 Fill 用的 Path 显示。</summary>
    public bool ShowVectorIcon => !IconStroke;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>右上角红点里的条数(0 = 不显示红点)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBadge))]
    [NotifyPropertyChangedFor(nameof(BadgeText))]
    public partial int BadgeCount { get; set; }

    /// <summary>有条数才把红点摆出来。</summary>
    public bool ShowBadge => BadgeCount > 0;

    /// <summary>红点里写的数字:超过 99 写 99+。</summary>
    public string BadgeText => BadgeCount > 99 ? "99+" : BadgeCount.ToString();
}
