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

    /// <summary>当前是否开启亚克力。</summary>
    public static bool GlassEnabled { get; private set; } = true;

    /// <summary>半透明档不透明度的默认值(%)—— 只在 config.ini 没写 alpha 时用一次,之后以 config 为准。</summary>
    public const int DefaultAlpha = 90;

    /// <summary>当前半透明档的表面不透明度(%):读 config.ini 的 alpha(默认 90),个性化滑杆可改。</summary>
    public static int SurfaceAlpha { get; private set; } = DefaultAlpha;

    /// <summary>亚克力开关变化时通知界面(主窗口据此调壁纸模糊半径与窗口材质)。</summary>
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

        // 记一行:出问题时能直接从日志看出这一档向系统要的是什么材质
        // (半透明档 = 省性能档:只申请 Transparent,不要任何模糊)
        Logger.Log(LogLevel.Info, GlassEnabled
            ? $"[ZXMC] 窗口材质:{window.GetType().Name} → AcrylicBlur(亚克力档)"
            : $"[ZXMC] 窗口材质:{window.GetType().Name} → Transparent(半透明档,不申请模糊)");

        // 诊断:开窗后把"系统实际批的材质"也记下来 —— 请求了 AcrylicBlur 不等于真拿到了,
        // 拿不到时窗口就是纯透明 = 看着像半透明(这正是"亚克力变半透明"的根因判定点)。
        window.Opened += (_, _) =>
        {
            var actual = window.ActualTransparencyLevel;
            Logger.Log(LogLevel.Info,
                $"[ZXMC] 材质实测:{window.GetType().Name} 请求={(GlassEnabled ? "AcrylicBlur" : "Transparent")} 实际={actual}");
        };
    }

    /// <summary>启动时按配置应用一次(配色 + 亚克力开关)。</summary>
    public static void ApplySaved()
    {
        Apply(Themes.Find(ClientConfig.GetOrEmpty("theme")), save: false);

        // 没配过就是开;config 里 glass=0 表示关
        ApplyGlass(ClientConfig.GetOrEmpty("glass") != "0", save: false);

        // 半透明档的不透明度:以 config.ini 的 alpha 为准;没配过/非法就写入默认值 90,之后一切读 config
        var raw = ClientConfig.GetOrEmpty("alpha").Trim();
        if (!int.TryParse(raw, out var alpha))
        {
            if (raw.Length > 0)
            {
                Logger.Log(LogLevel.Warning, $"[ZXMC] config.ini 的 alpha 不合法({raw}),已重置为 90");
            }

            ClientConfig.Set("alpha", DefaultAlpha.ToString());
            alpha = DefaultAlpha;
            Logger.Log(LogLevel.Info, $"[CONFIG]alpha 未配置,已写入默认值 {DefaultAlpha}(半透明样式不透明度)");
        }

        ApplyAlpha(alpha, save: false);
    }

    /// <summary>
    /// 开关亚克力。
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
        Logger.Log(LogLevel.Info, $"[ZXMC] 亚克力已{(enabled ? "开启" : "关闭")}");
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

        ApplySurface();                                                 // 表面(受亚克力开关影响)
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

    /// <summary>半透明档的表面不透明度(50-100,默认 90):个性化滑杆调它。</summary>
    public static void ApplyAlpha(int percent, bool save = true)
    {
        SurfaceAlpha = Math.Clamp(percent, 50, 100);

        if (save)
        {
            ClientConfig.Set("alpha", SurfaceAlpha.ToString());
        }

        ApplySurface();
        Logger.Log(LogLevel.Info, $"[ZXMC] 半透明档不透明度已设为 {SurfaceAlpha}%");
    }

    /// <summary>
    /// 刷"带透明度的表面"资源 —— 两种档位的区别就在这里:
    ///
    /// <para><b>亚克力开</b>:按设计稿,<b>内容区不铺白底</b>,让窗口的亚克力模糊透出来
    /// (壁纸 / 桌面是糊的);卡片留 80% 白保证字看得清;侧边栏仍是主题色 80%。</para>
    ///
    /// <para><b>半透明(关)</b>:<b>只有侧边栏半透明</b>(不透明度 = 个性化滑杆),其它表面一律不透明,
    /// 免得内容被背景干扰。</para>
    ///
    /// <para>换配色时也要再调一次,所以这里读 <see cref="Current"/>。</para>
    /// </summary>
    private static void ApplySurface()
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        var theme = Current;
        var glass = GlassEnabled;

        var percent = Math.Clamp(SurfaceAlpha, 50, 100);
        var railByte = (byte)(percent * 255 / 100);
        // 侧栏:亚克力档 80%(透出模糊);半透明档按滑杆的百分比(默认 90%)
        var rail = glass ? (byte)0xCC : railByte;


        resources["ThemeGlass"] = Solid(Alpha(theme.Primary, rail));     // 侧边栏:主题色 + 不透明度

        // 亚克力档:内容区留 36% 白,让窗口的模糊透出来;半透明档:内容/卡片不透明,只有侧栏按 alpha 透
        resources["ThemeGlassLight"] = Solid(glass ? "#E6FFFFFF" : "#FFFFFFFF");   // 消息卡片:亚克力 90%,半透明档纯白
        resources["ThemePanel"] = Solid(glass ? "#5CFFFFFF" : "#FFFFFFFF");        // 面板 / 内容区:亚克力 36%,半透明档不透明
        resources["ThemePage"] = Solid(glass ? "#5CFFFFFF" : "#FFFFFFFF");         // 设置页底:同上
        resources["ThemeContentVeil"] = Solid(glass ? "#66E9EFF3" : "#99E9EFF3");  // 内容区那层磨砂:再压一层白

        // 入口窗口(登录 / OOBE / 连接 / 弹窗)只有一层白幕:
        // 亚克力档 60% 白(让窗口的模糊透出来);半透明档不透明(工作区/窗口不要透)
        var veilByte = glass ? (byte)0x99 : (byte)0xFF;
        resources["ThemeWindowVeil"] = Solid("#" + veilByte.ToString("X2") + "FFFFFF");
    }

    private static IBrush Solid(string argbHex) => new SolidColorBrush(Color.Parse(argbHex));

    /// <summary>给 6 位颜色加上透明度前缀(0x00-0xFF),得到 #AARRGGBB。</summary>
    private static string Alpha(string hex, byte alpha)
        => $"#{alpha:X2}{hex.TrimStart('#')}";
}
