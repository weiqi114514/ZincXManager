using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ZincXManagerClient.Models;

/// <summary>
/// 消息正文的轻量渲染:支持 Markdown 和简单 HTML,解析成 Avalonia 控件。
/// 只做"看得懂"的级别 —— 标题 / 段落 / 粗体 / 列表 / 代码,不追求完整规范。
/// </summary>
public static class MessageFormat
{
    private static readonly Regex HtmlTag = new("<[a-zA-Z/!][^>]*>", RegexOptions.Compiled);

    /// <summary>是不是 HTML(有标签就算)。</summary>
    public static bool IsHtml(string text) => HtmlTag.IsMatch(text);

    /// <summary>是不是 Markdown(常见记号)。</summary>
    public static bool IsMarkdown(string text)
        => !IsHtml(text) && Regex.IsMatch(text, @"(^|\n)\s*(#{1,6}\s|[-*+]\s|\d+\.\s|\|)", RegexOptions.Compiled)
           || text.Contains("**") || text.Contains("```");

    /// <summary>把正文渲染成控件(自动识别 Markdown / HTML / 纯文本)。</summary>
    public static Control Render(string text, double fontSize = 13, IBrush? brush = null)
    {
        var blocks = IsHtml(text) ? ParseHtml(text) : ParseMarkdown(text);
        var panel = new StackPanel { Spacing = 6 };

        foreach (var (kind, level, content) in blocks)
        {
            if (kind == "li")
            {
                var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(new TextBlock { Text = "·", FontSize = fontSize, Foreground = brush, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top });
                row.Children.Add(InlineText(content, fontSize, brush));
                panel.Children.Add(row);
                continue;
            }

            var size = kind switch
            {
                "h1" => fontSize + 8,
                "h2" => fontSize + 5,
                "h3" => fontSize + 3,
                _ => fontSize,
            };

            var block = InlineText(content, size, brush);
            if (kind is "h1" or "h2" or "h3")
            {
                block.FontWeight = FontWeight.SemiBold;
            }

            if (kind == "code")
            {
                panel.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#F2F6F8")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#D5E4EC")),
                    BorderThickness = new Avalonia.Thickness(1),
                    Padding = new Avalonia.Thickness(10, 8),
                    Child = new SelectableTextBlock
                    {
                        Text = content,
                        FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                        FontSize = fontSize - 1,
                        Foreground = brush,
                        TextWrapping = TextWrapping.Wrap,
                    },
                });
                continue;
            }

            panel.Children.Add(block);
        }

        return panel;
    }

    /// <summary>一行文字,处理 **粗体** 与 `代码`。</summary>
    private static SelectableTextBlock InlineText(string text, double size, IBrush? brush)
    {
        var tb = new SelectableTextBlock
        {
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
        };

        if (brush is not null)
        {
            tb.Foreground = brush;
        }

        var parts = Regex.Split(text, @"(\*\*[^*]+\*\*|`[^`]+`)");
        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                continue;
            }

            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
            {
                tb.Inlines?.Add(new Run(part[2..^2]) { FontWeight = FontWeight.SemiBold });
            }
            else if (part.StartsWith("`") && part.EndsWith("`") && part.Length > 2)
            {
                tb.Inlines?.Add(new Run(part[1..^1]) { FontFamily = new FontFamily("Consolas, Microsoft YaHei UI") });
            }
            else
            {
                tb.Inlines?.Add(new Run(part));
            }
        }

        if (tb.Inlines is null || tb.Inlines.Count == 0)
        {
            tb.Text = text;
        }

        return tb;
    }

    private static List<(string kind, int level, string content)> ParseMarkdown(string text)
    {
        var list = new List<(string, int, string)>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var inCode = false;
        var code = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.TrimStart().StartsWith("```"))
            {
                if (inCode)
                {
                    list.Add(("code", 0, string.Join("\n", code)));
                    code.Clear();
                }
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                code.Add(line);
                continue;
            }

            if (line.Trim().Length == 0)
            {
                continue;
            }

            var t = line.TrimStart();
            if (t.StartsWith("### ")) { list.Add(("h3", 3, t[4..])); continue; }
            if (t.StartsWith("## ")) { list.Add(("h2", 2, t[3..])); continue; }
            if (t.StartsWith("# ")) { list.Add(("h1", 1, t[2..])); continue; }
            if (t.StartsWith("- ") || t.StartsWith("* ") || t.StartsWith("+ ")) { list.Add(("li", 0, t[2..])); continue; }

            var m = Regex.Match(t, @"^(\d+)\.\s+(.*)$");
            if (m.Success) { list.Add(("li", 0, m.Groups[2].Value)); continue; }

            list.Add(("p", 0, t));
        }

        if (inCode && code.Count > 0)
        {
            list.Add(("code", 0, string.Join("\n", code)));
        }

        return list;
    }

    private static List<(string kind, int level, string content)> ParseHtml(string text)
    {
        var list = new List<(string, int, string)>();
        var s = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li[^>]*>", "\n<li>", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<(/p|/div|/h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase);

        foreach (var raw in s.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var kind = "p";
            var level = 0;
            if (Regex.IsMatch(line, @"^<h1", RegexOptions.IgnoreCase)) { kind = "h1"; level = 1; }
            else if (Regex.IsMatch(line, @"^<h2", RegexOptions.IgnoreCase)) { kind = "h2"; level = 2; }
            else if (Regex.IsMatch(line, @"^<h3", RegexOptions.IgnoreCase)) { kind = "h3"; level = 3; }
            else if (Regex.IsMatch(line, @"^<li", RegexOptions.IgnoreCase)) { kind = "li"; }

            var plain = HtmlTag.Replace(line, "");
            plain = System.Net.WebUtility.HtmlDecode(plain).Trim();
            if (plain.Length > 0)
            {
                list.Add((kind, level, plain));
            }
        }

        return list;
    }
}
