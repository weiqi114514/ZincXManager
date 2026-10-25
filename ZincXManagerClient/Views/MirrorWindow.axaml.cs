using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZincXManagerClient.Models;
using ZincXManagerClient.Services;
using ZincXManagerShared.Logging;

namespace ZincXManagerClient.Views;

/// <summary>下载镜像设置:表格式编辑(地址 / 名称 / 优先级 / 启用)</summary>
public partial class MirrorWindow : Window
{
    /// <summary>表格数据</summary>
    public ObservableCollection<MirrorItem> Items { get; }

    /// <summary>名称下拉建议</summary>
    public string[] MirrorNames { get; } =
    [
        "gh-proxy",
        "ghfast",
        "ghproxy",
        "自建",
    ];

    /// <summary>点保存后的新配置(取消时为 null)</summary>
    public string? Result { get; private set; }

    /// <summary>设计器用</summary>
    public MirrorWindow() : this(MirrorSettingsStore.Load())
    {
    }

    /// <summary>用现有配置打开</summary>
    public MirrorWindow(List<MirrorItem> current)
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);
        Items = new ObservableCollection<MirrorItem>(current);
        MirrorList.ItemsSource = Items;
        DataContext = this;
    }

    /// <summary>弹出编辑,返回新配置(null = 取消)</summary>
    public static async Task<List<MirrorItem>?> EditAsync(Window owner)
    {
        var win = new MirrorWindow(MirrorSettingsStore.Load());
        await win.ShowDialog(owner);
        return win.Result is null ? null : MirrorSettingsStore.Parse(win.Result);
    }

    /// <summary>添加一条空行</summary>
    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        Items.Add(new MirrorItem { Url = "", Name = "", Enabled = true });
    }

    /// <summary>上移(提高优先级)</summary>
    private void OnMoveUpClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MirrorItem item })
        {
            var index = Items.IndexOf(item);
            if (index > 0)
            {
                Items.Move(index, index - 1);
            }
        }
    }

    /// <summary>下移</summary>
    private void OnMoveDownClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MirrorItem item })
        {
            var index = Items.IndexOf(item);
            if (index >= 0 && index < Items.Count - 1)
            {
                Items.Move(index, index + 1);
            }
        }
    }

    /// <summary>保存</summary>
    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        Result = MirrorSettingsStore.Serialize(Items);
        Close();
    }

    /// <summary>取消 / 关窗</summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    /// <summary>拖标题栏</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
