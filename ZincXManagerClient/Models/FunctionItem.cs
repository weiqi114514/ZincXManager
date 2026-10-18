using CommunityToolkit.Mvvm.ComponentModel;

namespace ZincXManagerClient.Models;

/// <summary>子功能卡片:一个页面里的一个功能入口(占位阶段只有标题/说明/图标)。</summary>
public partial class FunctionItem : ObservableObject
{
    /// <summary>唯一标识(后面接真实页面用)。</summary>
    public string Key { get; init; } = "";

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    public string Description { get; init; } = "";

    /// <summary>SVG 图标资源路径(avares://...);不为空时直接用它,否则退回矢量 Icon。</summary>
    public string IconSvg { get; init; } = "";

    /// <summary>有 PNG 图标时只显示图片。</summary>
    public bool HasIconPng => false;

    /// <summary>没有 PNG 时按描边图标显示。</summary>
    public bool ShowStrokeIcon => IconStroke;

    /// <summary>没有 PNG 且不是描边时按填充图标显示。</summary>
    public bool ShowVectorIcon => !IconStroke;

    /// <summary>图标是描边图标(Stroke)还是实心图标(Fill)。</summary>
    public bool IconStroke { get; init; }

    /// <summary>图标路径(占位阶段先用 Icons 里现成的)。</summary>
    public string Icon { get; init; } = "";

    /// <summary>当前是否显示(按模式 / 是否连官方动态控制)。</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;
}
