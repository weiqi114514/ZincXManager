using CommunityToolkit.Mvvm.ComponentModel;

namespace ZincXManagerClient.Models;

/// <summary>下载镜像的一条记录</summary>
public partial class MirrorItem : ObservableObject
{
    /// <summary>镜像地址前缀(拼在 GitHub 下载地址前面)</summary>
    [ObservableProperty] public partial string Url { get; set; } = "";

    /// <summary>给这条镜像起的名字(可空)</summary>
    [ObservableProperty] public partial string Name { get; set; } = "";

    /// <summary>是否启用(不启用的下载时跳过)</summary>
    [ObservableProperty] public partial bool Enabled { get; set; } = true;
}
