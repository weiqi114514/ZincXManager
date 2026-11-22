using ZincXManagerClient.Models;  // FunctionItem

namespace ZincXManagerClient.Modules;

/// <summary>功能卡片模块:某个分区里的一张卡片(一个功能入口)。</summary>
public abstract class CardModule : ModuleBase
{
    private FunctionItem? _card;

    protected CardModule(string id) : base(id) { }

    /// <summary>属于哪个分区(分区模块的编号)。</summary>
    public abstract string Section { get; }

    /// <summary>卡片只造一次:界面显示、开关判断、按 key 查找共用同一份。</summary>
    public FunctionItem Card => _card ??= BuildCard();

    /// <summary>卡片的 key:与旧代码里的一致,点开功能时按它分发。</summary>
    public string Key => Card.Key;

    /// <summary>造这张卡片(标题 / 说明 / 图标);子类只填数据。</summary>
    public abstract FunctionItem BuildCard();
}
