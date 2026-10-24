using CommunityToolkit.Mvvm.ComponentModel;  // MVVM 可观察对象

namespace ZincXManagerClient.Models;

/// <summary>引导页右上角进度条上的一颗"步骤豆":名字 + 是否当前步。</summary>
public partial class StepChip : ObservableObject
{
    public StepChip(string name)
    {
        Name = name;
    }

    /// <summary>步骤名。</summary>
    public string Name { get; }

    /// <summary>是不是当前这一步(高亮)。</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }
}
