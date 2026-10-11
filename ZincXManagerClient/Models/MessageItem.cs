using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ZincXManagerClient.Models;

/// <summary>
/// 消息卡片类型:纯文本 / 更新 / 双按钮确认 / 填空 / 下拉选择。
/// 与服务端消息卡片的"种类"一一对应(见共享库 <c>MessageCard</c>)。
/// </summary>
public enum MessageKind
{
    /// <summary>纯文本卡片:只有标题 + 正文,不显示按钮。</summary>
    Text,
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

    /// <summary>收到时那张卡片的原文(7 段)。回动作给服务端时原样带上,服务端凭它认是哪张卡片。</summary>
    public string RawCard { get; init; } = "";

    /// <summary>本地编号(删除与存档用)。</summary>
    public int Id { get; init; }

    /// <summary>
    /// 服务端存档分配的<b>消息号</b>(卡片第 8 段;本地消息 / 老格式卡片是 0)。
    /// 补发去重靠「<see cref="SourceTag"/> + 消息号」这一对,所以服务端补发时按它认"这条我收过没有"。
    /// </summary>
    public int ServerId { get; init; }

    /// <summary>去重键:来源 + 消息号;不参与去重(消息号 0)时是空串。</summary>
    public string DedupeKey => ZincXManagerShared.Network.MessageCard.DedupeKey(SourceTag, ServerId);

    /// <summary>收到这条消息的时间(本地时间)。</summary>
    public DateTime ReceivedAt { get; init; } = DateTime.Now;

    /// <summary>来源标签的原文(ZXMOS-… / ZXMS-… / ZXMC),保留期按它算。</summary>
    public string SourceTag { get; init; } = "";

    /// <summary>这条消息的保留天数(按来源算:官方 20 / 子服按设置 / 本地按高级设置)。</summary>
    public int KeepDays { get; init; } = 60;

    /// <summary>卡片底部显示的时间文案。</summary>
    public string TimeText => ReceivedAt.ToString("MM-dd HH:mm");

    [ObservableProperty]
    public partial string InputText { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedOption { get; set; } = "";

    public bool IsText => Kind == MessageKind.Text;

    public bool IsUpdate => Kind == MessageKind.Update;

    public bool IsChoice => Kind == MessageKind.Choice;

    public bool IsInput => Kind == MessageKind.Input;

    public bool IsSelect => Kind == MessageKind.Select;
}
