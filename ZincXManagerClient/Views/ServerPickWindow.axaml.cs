using System;                      // Action
using System.Collections.Generic;  // Dictionary / IReadOnlyList
using Avalonia;                    // Thickness
using Avalonia.Controls;           // Window / Border / Grid / TextBlock / RadioButton / StackPanel
using Avalonia.Input;              // PointerPressedEventArgs / Key
using Avalonia.Interactivity;      // RoutedEventArgs
using Avalonia.Layout;             // HorizontalAlignment / VerticalAlignment / Orientation
using Avalonia.Media;              // SolidColorBrush / Color / TextTrimming

namespace ZincXManagerClient.Views;

/// <summary>
/// 选择 MC 服务器弹窗:一行一台服务端,点行选中,双击行或点「应用」提交。
///
/// <para>这里只管选哪台、不连服务端;选中的编号通过 <see cref="Applied"/> 交回调用方。</para>
/// </summary>
public sealed partial class ServerPickWindow : Window
{
    private readonly Dictionary<int, RadioButton> _radios = new();   // 编号 → 该行的单选钮
    private readonly Dictionary<int, Border> _rows = new();          // 编号 → 整行(选中高亮用)
    private int _selectedId = -1;                                    // 当前选中的编号;-1 = 还没选

    /// <summary>点「应用」或双击某一行时回传选中的服务器编号。</summary>
    public event Action<int>? Applied;

    /// <summary>建一个空的选择窗;服务器列表随后由 <see cref="SetServers"/> 填。</summary>
    public ServerPickWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);   // 半透明 / 亚克力:开窗时定一次

        // Esc = 取消(和点右上角叉同一个语义)
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    /// <summary>
    /// 填服务器列表。currentId 在列表里就预选中它;不在(含 0 = 还没选)就不选,等用户自己点,
    /// 免得第一次打开时「应用」直接把用户切到列表第一台。
    /// </summary>
    public void SetServers(IReadOnlyList<(int Id, string Name)> servers, int currentId)
    {
        _radios.Clear();
        _rows.Clear();
        RowsHost.Children.Clear();
        _selectedId = -1;
        ApplyButton.IsEnabled = false;

        foreach (var (id, name) in servers)
        {
            RowsHost.Children.Add(BuildRow(id, name));
        }

        EmptyText.IsVisible = servers.Count == 0;

        if (_radios.ContainsKey(currentId))
        {
            Select(currentId);
        }
    }

    /// <summary>画一行:左边「名称 + 灰色 #编号」,右边单选钮;整行都是点击区。</summary>
    private Border BuildRow(int id, string name)
    {
        var nameText = new TextBlock
        {
            Text = name.Length > 0 ? name : $"服务器 {id}",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#1F3B3A")),
            VerticalAlignment = VerticalAlignment.Center,
            // 窗口固定 520 宽:名字最长给 340,超了省略号截断,免得挤到右边的单选钮上
            MaxWidth = 340,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var idText = new TextBlock
        {
            Text = "#" + id,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#8A8F99")),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var label = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.Children.Add(nameText);
        label.Children.Add(idText);

        // 单选钮不接鼠标:整行都是点击区,免得"点名字没反应、只有小圆点管用"
        var radio = new RadioButton
        {
            GroupName = "McServer",
            IsHitTestVisible = false,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(label);
        Grid.SetColumn(radio, 1);
        grid.Children.Add(radio);

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(10, 8, 10, 8),
            Tag = id,
        };
        row.Classes.Add("pickRow");
        row.PointerPressed += OnRowPointerPressed;

        _radios[id] = radio;
        _rows[id] = row;
        return row;
    }

    /// <summary>选中某一行:点亮单选钮、整行浅蓝底、放开「应用」。</summary>
    private void Select(int id)
    {
        if (!_radios.ContainsKey(id))
        {
            return;
        }

        _selectedId = id;

        foreach (var (key, radio) in _radios)
        {
            radio.IsChecked = key == id;
        }

        foreach (var (key, row) in _rows)
        {
            if (key == id)
            {
                if (!row.Classes.Contains("selected"))
                {
                    row.Classes.Add("selected");
                }
            }
            else
            {
                row.Classes.Remove("selected");
            }
        }

        ApplyButton.IsEnabled = true;
    }

    /// <summary>点行 = 选中该行;双击 = 直接应用。</summary>
    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: int id })
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Select(id);

        if (e.ClickCount >= 2)
        {
            ApplyCurrent();
        }
    }

    /// <summary>把选中的编号交回去再关窗;没选就什么都不做。</summary>
    private void ApplyCurrent()
    {
        if (_selectedId < 0)
        {
            return;
        }

        var id = _selectedId;
        _selectedId = -1;            // 防连点:同一个编号只回传一次
        ApplyButton.IsEnabled = false;
        Applied?.Invoke(id);
        Close();
    }

    /// <summary>点「应用」:提交当前选中项。</summary>
    private void OnApplyClick(object? sender, RoutedEventArgs e) => ApplyCurrent();

    /// <summary>点「取消」/右上角叉:直接关,不回传。</summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>按住标题栏拖动窗口。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
