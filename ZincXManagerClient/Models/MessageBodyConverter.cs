using System;
using System.Globalization;
using Avalonia.Data.Converters;
using ZincXManagerShared.Logging;

namespace ZincXManagerClient.Models;

/// <summary>消息正文 → 控件(Markdown / HTML / 纯文本自动识别)。</summary>
public class MessageBodyConverter : IValueConverter
{
    public static readonly MessageBodyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text)
        {
            return null;
        }

        try
        {
            return MessageFormat.Render(text);
        }
        catch (Exception ex)
        {
            // 渲染出错就退回纯文本,别让消息卡片空着
            return new Avalonia.Controls.TextBlock
            {
                Text = text,
                FontSize = 12.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Foreground = Avalonia.Media.Brushes.Black,
            };
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
