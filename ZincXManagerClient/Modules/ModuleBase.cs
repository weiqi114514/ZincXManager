using ZincXManagerShared.Modules;  // 模块编号 / 全集清单

namespace ZincXManagerClient.Modules;

/// <summary>
/// 客户端模块基类:只管「入口要不要出现」。
///
/// <para>服务端的模块类管命令注册与包接管,客户端的模块类管左侧导航项与功能卡片。
/// 两边都不在自己身上存开关状态 —— 每次现查 <see cref="ModuleState"/>,
/// 服务端改了开关、202 一到,所有入口立刻跟着变。</para>
/// </summary>
public abstract class ModuleBase
{
    /// <summary>定下模块编号(见 <see cref="ModuleIds"/>)。</summary>
    protected ModuleBase(string id) => Id = id;

    /// <summary>模块编号。</summary>
    public string Id { get; }

    /// <summary>中文名:与分享的模块清单里写的一致,提示与日志都用它。</summary>
    public string Title => ModuleCatalog.TitleOf(Id);

    /// <summary>父模块编号;顶层模块是空串。</summary>
    public string Parent => ModuleCatalog.ParentOf(Id);

    /// <summary>现在开着没有(父模块关掉时子模块一起算关)。</summary>
    public bool IsOn => ModuleState.IsOn(Id);
}
