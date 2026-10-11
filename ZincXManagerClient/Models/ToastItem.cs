using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ZincXManagerClient.Models;

/// <summary>提示条的口气:普通消息 / 成功 / 提醒 / 失败(只有左边那条颜色不同)。</summary>
public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// 左上角滑出来的那条提示(toast)。一次说一件事,几秒后自己滑回去。
/// 样子跟着消息卡片走:左边一条强调色,右边一句话 + 一行小字(口气 · 时间)。
/// </summary>
public partial class ToastItem : ObservableObject
{
    /// <summary>提示内容(一句话,别写实现细节)。</summary>
    public string Text { get; init; } = "";

    /// <summary>口气(决定左边那条与图标的颜色)。</summary>
    public ToastKind Kind { get; init; } = ToastKind.Info;

    /// <summary>停留多久(毫秒),到点自己收回去。</summary>
    public int Milliseconds { get; init; } = 3600;

    /// <summary>弹出来的时间(右下角小字用)。</summary>
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public bool IsInfo => Kind == ToastKind.Info;

    public bool IsSuccess => Kind == ToastKind.Success;

    public bool IsWarning => Kind == ToastKind.Warning;

    public bool IsError => Kind == ToastKind.Error;

    /// <summary>小字那行:口气 · 时间(和卡片的"来源 时间"一个样式)。</summary>
    public string FootText => Kind switch
    {
        ToastKind.Success => "已完成",
        ToastKind.Warning => "注意",
        ToastKind.Error => "失败",
        _ => "提示",
    } + " · " + CreatedAt.ToString("HH:mm");
}
