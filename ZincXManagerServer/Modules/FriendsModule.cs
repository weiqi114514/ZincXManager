using System.Collections.Generic;
using System.Threading.Tasks;
using ZincXManagerShared.Modules;  // ModuleCatalog / ModuleIds
using ZincXManagerShared.Network;  // Protocol / ServerboundPacket

namespace ZincXManagerServer.Modules;

/// <summary>
/// 好友功能(nav.friends):消息 / 历史 / 已读 / 头像 / 列表 / 申请 / 同意 / 删除 / 搜索 / 备注。
/// <para>处理体还在 Friends\*.cs 里(只把可见性从 private 改成 internal,逻辑一行没动),
/// 这个类只负责"哪种包交给哪个方法",以及模块关掉之后这些包连解析都不做。</para>
/// </summary>
internal sealed class FriendsModule : ServerModule
{
    public FriendsModule() : base(ModuleCatalog.Find(ModuleIds.NavFriends)!) { }

    /// <summary>好友模块认领的包(客户端发来的那 11 种请求)。</summary>
    public override IReadOnlyList<ushort> Packets { get; } =
    [
        Protocol.FriendMsg,             // 400 发好友消息
        Protocol.FriendMsgHistory,      // 402 拉好友聊天记录
        Protocol.FriendReadMark,        // 4005 已读标记
        Protocol.FriendAvatarReq,       // 607 要好友头像
        Protocol.FriendListQuery,       // 650 好友列表
        Protocol.FriendRequest,         // 654 加好友
        Protocol.FriendRequestsQuery,   // 656 待处理申请列表
        Protocol.FriendAnswer,          // 658 同意 / 拒绝申请
        Protocol.FriendRemove,          // 660 删好友
        Protocol.FriendSearch,          // 663 搜人
        Protocol.FriendRemark,          // 665 改备注
    ];

    /// <summary>把这个包交给 Friends 目录下对应的处理方法。</summary>
    /// <param name="packet">客户端发来的包。</param>
    /// <param name="context">装配时给的上下文(这里只用它的配置句柄)。</param>
    public override Task OnPacketAsync(ServerboundPacket packet, ModuleContext context) => packet.Type switch
    {
        Protocol.FriendMsg => Program.HandleFriendMessageAsync(packet, context.Config),
        Protocol.FriendMsgHistory => Program.HandleFriendHistoryQueryAsync(packet, context.Config),
        Protocol.FriendReadMark => Program.HandleFriendReadMarkAsync(packet, context.Config),
        Protocol.FriendAvatarReq => Program.HandleFriendAvatarAsync(packet, context.Config),
        Protocol.FriendListQuery => Program.HandleFriendListQueryAsync(packet, context.Config),
        Protocol.FriendRequest => Program.HandleFriendRequestAsync(packet, context.Config),
        Protocol.FriendRequestsQuery => Program.HandleFriendRequestsQueryAsync(packet, context.Config),
        Protocol.FriendAnswer => Program.HandleFriendAnswerAsync(packet, context.Config),
        Protocol.FriendRemove => Program.HandleFriendRemoveAsync(packet, context.Config),
        Protocol.FriendSearch => Program.HandleFriendSearchAsync(packet, context.Config),
        Protocol.FriendRemark => Program.HandleFriendRemarkAsync(packet, context.Config),
        _ => Task.CompletedTask,
    };
}
