using ZincXManagerClient.Models;  // NavItem

namespace ZincXManagerClient.Modules;

/// <summary>
/// 导航分区模块:左侧导航上的一个分区就是一个模块。
/// 关掉的分区,导航项直接不出现(不是变灰)。
/// </summary>
public abstract class SectionModule : ModuleBase
{
    protected SectionModule(string id) : base(id) { }

    /// <summary>管理端专属分区(只有管理账号登录后才出现)。</summary>
    public virtual bool AdminOnly => false;

    /// <summary>玩家端分区(管理账号登录后左侧整栏换成管理端,这些不再出现)。</summary>
    public virtual bool PlayerOnly => false;

    /// <summary>造出这个分区的导航项;到底显不显示由 <see cref="ModuleState"/> 决定。</summary>
    public abstract NavItem BuildNav();
}
