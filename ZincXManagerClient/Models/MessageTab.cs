using System;

namespace ZincXManagerClient.Models;

/// <summary>消息面板顶部的一个页签(消息 / 日志 / 更新日志 / 自定义网页)。</summary>
public sealed class MessageTab
{
    public MessageTab(string key, string label)
    {
        Key = key;
        Label = label;
    }

    /// <summary>内部标识:messages / log / changelog / web。</summary>
    public string Key { get; }

    /// <summary>显示文字。</summary>
    public string Label { get; }

    /// <summary>页签宽度:按文字长度估(15px 字号,中文约 15px/字),夹在 56–128 之间。</summary>
    public double Width => System.Math.Clamp(Label.Length * 15 + 20, 56, 128);
}
