namespace ZincXManagerClient.Models;

/// <summary>小功能组里的一个功能条:图标 + 名称 + 一句灰色介绍。</summary>
public sealed class WidgetEntry
{
    /// <summary>唯一标识(点开后按它分发)。</summary>
    public string Key { get; init; } = "";

    /// <summary>功能名称。</summary>
    public string Title { get; init; } = "";

    /// <summary>名称右边那句灰色小字介绍。</summary>
    public string Description { get; init; } = "";

    /// <summary>图标矢量路径(SVG path 数据)。</summary>
    public string IconPath { get; init; } = "";

    /// <summary>图标是描边(Stroke)还是实心(Fill);链环这类是描边。</summary>
    public bool Stroke { get; init; }
}
