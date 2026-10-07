using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ZincXManagerClient.Models;

/// <summary>
/// 一套配色。色块按钮上的分配:
///   左半边       = <see cref="Primary"/>(主要色)
///   右半边 上 2/3 = <see cref="Secondary"/>
///   右半边 下 1/3 = <see cref="Light"/>
///   第四色 <see cref="Muted"/> 当描边和次要文字
/// </summary>
public partial class ThemeOption : ObservableObject
{
    /// <summary>配色名称(存进 config.ini 的就是它)。</summary>
    public string Name { get; init; } = "";

    /// <summary>主要色:标题栏、主按钮、强调条。</summary>
    public string Primary { get; init; } = "#2662A7";

    /// <summary>主要色深一档:按下 / 深描边。</summary>
    public string PrimaryDark { get; init; } = "#1F5292";

    /// <summary>主要色亮一档:悬停。</summary>
    public string PrimaryHover { get; init; } = "#2F76C4";

    /// <summary>次要色:选中块、次强调。</summary>
    public string Secondary { get; init; } = "#B9DEFF";

    /// <summary>最浅色:浅底块。</summary>
    public string Light { get; init; } = "#E9F5FF";

    /// <summary>灰调:描边、次要文字。</summary>
    public string Muted { get; init; } = "#8FCBDF";

    /// <summary>是否当前选中的配色。</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    // 给界面直接绑定用(绑字符串到 Fill 需要转换器,所以这里给出画刷)
    public IBrush PrimaryBrush => new SolidColorBrush(Color.Parse(Primary));
    public IBrush SecondaryBrush => new SolidColorBrush(Color.Parse(Secondary));
    public IBrush LightBrush => new SolidColorBrush(Color.Parse(Light));
    public IBrush MutedBrush => new SolidColorBrush(Color.Parse(Muted));
}

/// <summary>内置配色表。加新配色只要往这里加一项。</summary>
public static class Themes
{
    /// <summary>全部配色(第一项是默认)。</summary>
    public static ThemeOption[] All { get; } =
    [
        new()
        {
            Name = "默认",
            Primary = "#2662A7",
            PrimaryDark = "#1F5292",
            PrimaryHover = "#2F76C4",
            Secondary = "#B9DEFF",
            Light = "#E9F5FF",
            Muted = "#8FCBDF",
        },
        new()
        {
            // 锌源绿:285050 / 4e9c9c / cee9d4 / a0b5a5
            Name = "锌源绿",
            Primary = "#285050",
            PrimaryDark = "#1B3A3A",
            PrimaryHover = "#356A6A",
            Secondary = "#4E9C9C",
            Light = "#CEE9D4",
            Muted = "#A0B5A5",
        },
        new()
        {
            Name = "薄荷绿",
            Primary = "#1F7A5E",
            PrimaryDark = "#155C46",
            PrimaryHover = "#2A9473",
            Secondary = "#4FB69A",
            Light = "#DFF6EC",
            Muted = "#9FC7B8",
        },
        new()
        {
            Name = "高级橙",
            Primary = "#8A4B12",
            PrimaryDark = "#6B390D",
            PrimaryHover = "#A85D18",
            Secondary = "#E08A3C",
            Light = "#FBEBD9",
            Muted = "#C9A88A",
        },
        new()
        {
            // 巨硬蓝(微软蓝 / Fluent 蓝):0078D4 005A9E 2899F5 A9D3F2 DEECF9
            Name = "巨硬蓝",
            Primary = "#0078D4",
            PrimaryDark = "#005A9E",
            PrimaryHover = "#2899F5",
            Secondary = "#A9D3F2",
            Light = "#DEECF9",
            Muted = "#7FA8C9",
        },
    ];

    /// <summary>默认配色。</summary>
    public static ThemeOption Default => All[0];

    /// <summary>按名字找;找不到就给默认。</summary>
    public static ThemeOption Find(string name)
    {
        foreach (var theme in All)
        {
            if (theme.Name == name)
            {
                return theme;
            }
        }

        return Default;
    }
}
