using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ZincXManagerClient.Models;
using ZincXManagerShared.Logging;

namespace ZincXManagerClient;

/// <summary>
/// 配色切换:把选中的配色写进应用级资源,界面里用 {DynamicResource ThemeXxx} 的地方会立刻变。
///
/// <para>键名:</para>
/// <code>
/// ThemePrimary / ThemePrimaryDark / ThemePrimaryHover / ThemeSecondary / ThemeLight / ThemeMuted  → 画刷
/// ThemePrimaryColor / ThemeSecondaryColor / ThemeMutedColor                                        → 颜色(给渐变色标用)
/// ThemePage / ThemePanelBorder                                                                     → 带透明度的画刷
/// </code>
/// </summary>
internal static class ThemeManager
{
    /// <summary>当前配色。</summary>
    public static ThemeOption Current { get; private set; } = Themes.Default;

    /// <summary>当前是否开启毛玻璃(亚克力)。</summary>
    public static bool GlassEnabled { get; private set; } = true;

    /// <summary>毛玻璃开关变化时通知界面(主窗口据此调壁纸模糊半径与窗口材质)。</summary>
    public static event Action<bool>? GlassChanged;

    /// <summary>Fluent 亚克力模糊半径(Windows 的亚克力就是 30)。</summary>
    public const double GlassBlurRadius = 30;

    /// <summary>
    /// 给一个窗口定材质:亚克力(开)= 向系统要 AcrylicBlur;半透明(关)= 只要 Transparent。
    ///
    /// <para>只在开窗时调一次 —— 运行中改 <see cref="Window.TransparencyLevelHint"/> 会让 Windows 重建窗口,
    /// 屏幕上会闪出"像多了一个窗口"的东西。开关切换只换表面不透明度(见 ApplyGlass)。</para>
    /// </summary>
    public static void ApplyWindowMaterial(Window window)
    {
        window.TransparencyLevelHint = GlassEnabled
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent]
            : [WindowTransparencyLevel.Transparent];
    }

    /// <summary>启动时按配置应用一次(配色 + 毛玻璃开关)。</summary>
    public static void ApplySaved()
    {
        Apply(Themes.Find(ClientConfig.GetOrEmpty("theme")), save: false);

        // 没配过就是开;config 里 glass=0 表示关
        ApplyGlass(ClientConfig.GetOrEmpty("glass") != "0", save: false);
    }

    /// <summary>
    /// 开关毛玻璃(亚克力)。
    /// 开 = 壁纸按 Fluent 亚克力模糊(半径 30)、表面着色 80%;
    /// 关 = 不模糊、表面降到 60% 的纯半透明。
    /// </summary>
    public static void ApplyGlass(bool enabled, bool save = true)
    {
        GlassEnabled = enabled;

        if (save)
        {
            ClientConfig.Set("glass", enabled ? "1" : "0");
        }

        ApplySurface();
        GlassChanged?.Invoke(enabled);
        Logger.Log(LogLevel.Info, $"[ZXMC] 毛玻璃(亚克力)已{(enabled ? "开启" : "关闭")}");
    }

    /// <summary>切换配色;<paramref name="save"/> 为真时写进 config.ini。</summary>
    public static void Apply(ThemeOption theme, bool save = true)
    {
        Current = theme;

        foreach (var item in Themes.All)
        {
            item.IsSelected = ReferenceEquals(item, theme);
        }

        if (save)
        {
            ClientConfig.Set("theme", theme.Name);
        }

        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        resources["ThemePrimary"] = Solid(theme.Primary);
        resources["ThemePrimaryDark"] = Solid(theme.PrimaryDark);
        resources["ThemePrimaryHover"] = Solid(theme.PrimaryHover);
        resources["ThemeSecondary"] = Solid(theme.Secondary);
        resources["ThemeLight"] = Solid(theme.Light);
        resources["ThemeMuted"] = Solid(theme.Muted);

        resources["ThemePrimaryColor"] = Color.Parse(theme.Primary);

        // 勾选框 / 单选框 / 滑块的强调色读的是这几个系统键,不改它们就不会跟主题走
        resources["SystemAccentColor"] = Color.Parse(theme.Primary);
        resources["SystemAccentColorLight1"] = Color.Parse(theme.PrimaryHover);
        resources["SystemAccentColorLight2"] = Color.Parse(theme.PrimaryHover);
        resources["SystemAccentColorLight3"] = Color.Parse(theme.PrimaryHover);
        resources["SystemAccentColorDark1"] = Color.Parse(theme.PrimaryDark);
        resources["SystemAccentColorDark2"] = Color.Parse(theme.PrimaryDark);
        resources["SystemAccentColorDark3"] = Color.Parse(theme.PrimaryDark);
        resources["ThemeSecondaryColor"] = Color.Parse(theme.Secondary);
        resources["ThemeMutedColor"] = Color.Parse(theme.Muted);

        // 页面底 / 面板描边:用主题色淡淡铺一层
        // 表面一律中性色(白/浅灰),主题色只用于强调 —— 这样任何配色看起来都"正常"
        resources["ThemeWindow"] = Solid("#F2F5F8");
        resources["ThemePanelBorder"] = Solid("#598C9AA5");             // 描边:中性灰 35%

        ApplySurface();                                                 // 表面(受毛玻璃开关影响)
        resources["ThemeOnRail"] = Solid("#FFFFFFFF");                 // 左栏前景:纯白
        resources["ThemeOnRailSoft"] = Solid("#A6FFFFFF");               // 左栏前景:65%
        resources["ThemeOnRailFaint"] = Solid("#D9FFFFFF");              // 左栏前景:85%

        // 滚动条:Fluent 的 ScrollBar 模板把轨道/滑块颜色写在资源键里(见 ScrollBar.xaml),
        // 所以改这些键就能连"槽"一起跟主题
        resources["ScrollBarPanningThumbBackground"] = Solid(theme.Muted);
        resources["ScrollBarThumbFillPointerOver"] = Solid(theme.Primary);
        resources["ScrollBarThumbFillPressed"] = Solid(theme.PrimaryDark);
        resources["ScrollBarThumbFillDisabled"] = Solid(Alpha(theme.Muted, 0x66));
        resources["ScrollBarThumbBackgroundColor"] = Solid(theme.Primary);
        resources["ScrollBarTrackFill"] = Solid(Alpha(theme.Muted, 0x40));
        resources["ScrollBarTrackStroke"] = Solid(Alpha(theme.Muted, 0x66));
        resources["ScrollBarTrackFillPointerOver"] = Solid(Alpha(theme.Muted, 0x59));
        resources["ScrollBarTrackStrokePointerOver"] = Solid(Alpha(theme.Muted, 0x80));
        resources["ScrollBarBackground"] = Solid("#00000000");
        resources["ScrollBarBackgroundPointerOver"] = Solid(Alpha(theme.Muted, 0x26));

        Logger.Log(LogLevel.Info, $"[ZXMC] 配色已切换为 {theme.Name}");
    }

    /// <summary>
    /// 刷"带透明度的表面"资源:透明度跟着毛玻璃开关走 ——
    /// 亚克力(开)= Fluent 的 80% 着色;半透明(关)= 60%。
    /// 换配色时也要再调一次,所以这里读 <see cref="Current"/>。
    /// </summary>
    private static void ApplySurface()
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        var theme = Current;
        var rail = GlassEnabled ? (byte)0xCC : (byte)0x8C;   // 侧栏:主题色 80% / 55%
        var panel = GlassEnabled ? "CC" : "99";              // 面板:白 80% / 60%
        var card = GlassEnabled ? "B3" : "66";               // 卡片 / 页底:白 70% / 40%

        resources["ThemeGlass"] = Solid(Alpha(theme.Primary, rail));
        resources["ThemeGlassLight"] = Solid("#" + card + "FFFFFF");
        resources["ThemePanel"] = Solid("#" + panel + "FFFFFF");
        resources["ThemePage"] = Solid("#" + card + "FFFFFF");
    }

    private static IBrush Solid(string argbHex) => new SolidColorBrush(Color.Parse(argbHex));

    /// <summary>给 6 位颜色加上透明度前缀(0x00-0xFF),得到 #AARRGGBB。</summary>
    private static string Alpha(string hex, byte alpha)
        => $"#{alpha:X2}{hex.TrimStart('#')}";
}
