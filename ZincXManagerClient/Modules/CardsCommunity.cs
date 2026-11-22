using System;  // StringComparison
using ZincXManagerClient.Models;  // FunctionItem / Icons
using ZincXManagerShared.Modules;  // ModuleIds

namespace ZincXManagerClient.Modules;

/// <summary>
/// 社区 → 服务器广场:本服的帖子与资源。直连官方服(mode=OOS)时没有「本服」这回事,置基准不可见。
/// </summary>
public sealed class ServerSquareCard : CardModule
{
    public ServerSquareCard() : base(ModuleIds.ComSrvSquare) { }

    public override string Section => ModuleIds.NavCommunity;

    public override FunctionItem BuildCard() => new()
    {
        Key = "srv-square",
        Title = Title,
        Description = "本服的帖子与资源",
        IconSvg = "avares://ZincXManagerClient/Assets/icons/server-square.svg",
        Icon = Icons.ServerSquare,
        IconStroke = true,
        ModuleId = Id,
        BaseVisible = !IsOfficialOnly(),
    };

    /// <summary>连的是不是官方服(OOS):官方服没有子服广场。</summary>
    internal static bool IsOfficialOnly()
        => string.Equals(ClientConfig.GetOrEmpty("mode"), "OOS", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 社区 → 锌源广场:官方 ZXMOS 的共享广场。基准不可见,等主界面确认「已连上官方」再把基准打开
/// (<c>MainViewModel.RefreshOfficialVisibility</c>)。
/// </summary>
public sealed class ZincSquareCard : CardModule
{
    public ZincSquareCard() : base(ModuleIds.ComZincSquare) { }

    public override string Section => ModuleIds.NavCommunity;

    public override FunctionItem BuildCard() => new()
    {
        Key = "zinc-square",
        Title = Title,
        Description = "官方的共享交流广场 交流/资源",
        IconSvg = "avares://ZincXManagerClient/Assets/icons/official-square.svg",
        Icon = Icons.OfficialSquare,
        IconStroke = true,
        ModuleId = Id,
        BaseVisible = false,
    };
}
