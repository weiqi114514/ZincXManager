using System;  // 基础类型 / DateTime 与格式化
using Avalonia.Media;  // 行颜色(画刷)

namespace ZincXManagerClient.Models;

/// <summary>RCON / ZXMM 桥接控制台里的一行:发出的命令、回执、错误或过渡输出。</summary>
public sealed class TerminalLine
{
    /// <summary>命令行的颜色:浅青,和输出分开。</summary>
    private static readonly IBrush CommandBrush = Brush.Parse("#7FD4E8");

    /// <summary>成功回执的颜色:灰白。</summary>
    private static readonly IBrush OkBrush = Brush.Parse("#D7E2EA");

    /// <summary>错误回执的颜色:红。</summary>
    private static readonly IBrush ErrorBrush = Brush.Parse("#F08A8A");

    /// <summary>持续输出的颜色:暗灰,不抢眼。</summary>
    private static readonly IBrush OutputBrush = Brush.Parse("#7C93A4");

    /// <summary>行的种类:<c>cmd</c> 命令、<c>ok</c> 成功、<c>err</c> 失败、<c>out</c> 持续输出。</summary>
    public string Kind { get; init; } = "out";

    /// <summary>这行是什么时候产生的(本地时间)。</summary>
    public DateTime Time { get; init; } = DateTime.Now;

    /// <summary>行正文,不含时间和提示符。</summary>
    public string Text { get; init; } = "";

    /// <summary>上屏文本:命令带 <c>&gt;</c> 前缀,回执带状态词,持续输出只留正文。</summary>
    public string Display => Kind switch
    {
        "cmd" => $"[{Time:HH:mm:ss}] > {Text}",
        "ok" => $"{Time:HH:mm:ss} ok | {Text}",
        "err" => $"{Time:HH:mm:ss} err | {Text}",
        _ => $"{Time:HH:mm:ss}      {Text}",
    };

    /// <summary>这一行在终端里的颜色,按种类走。</summary>
    public IBrush Foreground => Kind switch
    {
        "cmd" => CommandBrush,
        "ok" => OkBrush,
        "err" => ErrorBrush,
        _ => OutputBrush,
    };
}
