using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ZincXManagerClient.Models;

/// <summary>消息卡片类型:更新 / 双按钮确认 / 填空 / 下拉选择。</summary>
public enum MessageKind
{
    Update,
    Choice,
    Input,
    Select
}

/// <summary>右侧消息面板中的一条消息。</summary>
public partial class MessageItem : ObservableObject
{
    public string Title { get; init; } = "";

    public string Body { get; init; } = "";

    /// <summary>来源标签,如 源:ZXMOS。</summary>
    public string Source { get; init; } = "";

    public MessageKind Kind { get; init; }

    /// <summary>主动作文案(更新 / 是 / 确认)。</summary>
    public string PrimaryAction { get; init; } = "";

    /// <summary>次动作文案(否),仅双按钮卡片使用。</summary>
    public string SecondaryAction { get; init; } = "";

    /// <summary>下拉选项,仅选择卡片使用。</summary>
    public IReadOnlyList<string> Options { get; init; } = new List<string>();

    [ObservableProperty]
    public partial string InputText { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedOption { get; set; } = "";

    public bool IsUpdate => Kind == MessageKind.Update;

    public bool IsChoice => Kind == MessageKind.Choice;

    public bool IsInput => Kind == MessageKind.Input;

    public bool IsSelect => Kind == MessageKind.Select;
}
