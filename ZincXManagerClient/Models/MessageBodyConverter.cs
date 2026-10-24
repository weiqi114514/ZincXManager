using System;  // 基础类型 / 常用静态方法
using System.Globalization;  // 区域与格式化
using Avalonia.Data.Converters;  // 界面框架
using ZincXManagerShared.Logging;  // 共享库

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

    /// <summary>绑定回写时的反向转换(界面 → 数据)。</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
