using System;  // StringComparer
using System.Collections.Generic;  // Dictionary / IReadOnlyList
using ZincXManagerShared.Network;  // Protocol:协议号

namespace ZincXManagerShared.Modules;

/// <summary>
/// 模块归属表：命令属于谁、包属于谁、请求包该回哪个包。
/// 只做归属查询，不做开关判断——开关状态由服务端的模块注册表回答。
/// 服务端靠它把「属于已关闭模块的命令/包」挑出来，回一句明确的 err。
/// </summary>
public static class ModuleRules
{
    /// <summary>命令名 → 模块编号；基础命令不在表里（永远可用）。</summary>
    private static readonly Dictionary<string, string> _commandOwner
        = new(StringComparer.OrdinalIgnoreCase)
        {
            // 好友与绑定
            ["bind"] = ModuleIds.WidgetBind,
            ["bind list"] = ModuleIds.WidgetBind,
            ["unbind"] = ModuleIds.WidgetBind,

            // 封禁
            ["ban add"] = ModuleIds.FuncBan,
            ["ban del"] = ModuleIds.FuncBan,
            ["ban list"] = ModuleIds.FuncBan,
            ["ban purge"] = ModuleIds.FuncBan,
            ["ban user"] = ModuleIds.FuncBan,

            // 白名单
            ["wl"] = ModuleIds.AdminWhitelist,
            ["wl add"] = ModuleIds.AdminWhitelist,
            ["wl rm"] = ModuleIds.AdminWhitelist,
            ["wl on"] = ModuleIds.AdminWhitelist,
            ["wl off"] = ModuleIds.AdminWhitelist,

            // 账号
            ["user list"] = ModuleIds.AdminZxmAccount,
            ["user reg"] = ModuleIds.AdminZxmRegister,
            ["user del"] = ModuleIds.AdminZxmAccount,
            ["user rep"] = ModuleIds.AdminZxmAccount,
            ["user rel"] = ModuleIds.AdminZxmAccount,

            // 服务器信息与控制台
            ["srv show"] = ModuleIds.ManageServer,
            ["net status"] = ModuleIds.Console,
            ["net clients"] = ModuleIds.Console,
            ["net reconnect"] = ModuleIds.Console,
            ["net close"] = ModuleIds.Console,
            ["zxmm config"] = ModuleIds.Console,
            ["zxmm connect"] = ModuleIds.Console,
            ["zxmm status"] = ModuleIds.Console,

            // 消息与公告
            ["msg notice"] = ModuleIds.Message,
            ["msg push"] = ModuleIds.Message,
            ["msg backget"] = ModuleIds.Message,
            ["msgdays"] = ModuleIds.Message,
            ["srvmsgdays"] = ModuleIds.Message,
        };

    /// <summary>包类型 → 模块编号；核心包（握手/会话/账号身份）不在表里。</summary>
    private static readonly Dictionary<ushort, string> _packetOwner = new()
    {
        // 好友
        [Protocol.FriendMsg] = ModuleIds.NavFriends,
        [Protocol.FriendMsgAck] = ModuleIds.NavFriends,
        [Protocol.FriendMsgHistory] = ModuleIds.NavFriends,
        [Protocol.FriendMsgHistoryReply] = ModuleIds.NavFriends,
        [Protocol.FriendMsgReceipt] = ModuleIds.NavFriends,
        [Protocol.FriendReadMark] = ModuleIds.NavFriends,
        [Protocol.FriendAvatarReq] = ModuleIds.NavFriends,
        [Protocol.FriendAvatarAck] = ModuleIds.NavFriends,
        [Protocol.FriendRelay] = ModuleIds.NavFriends,
        [Protocol.FriendPresence] = ModuleIds.NavFriends,
        [Protocol.AvatarRelay] = ModuleIds.NavFriends,
        [Protocol.AvatarRelayAck] = ModuleIds.NavFriends,
        [Protocol.FriendListQuery] = ModuleIds.NavFriends,
        [Protocol.FriendListReply] = ModuleIds.NavFriends,
        [Protocol.FriendRequest] = ModuleIds.NavFriends,
        [Protocol.FriendRequestAck] = ModuleIds.NavFriends,
        [Protocol.FriendRequestsQuery] = ModuleIds.NavFriends,
        [Protocol.FriendRequestsReply] = ModuleIds.NavFriends,
        [Protocol.FriendAnswer] = ModuleIds.NavFriends,
        [Protocol.FriendAnswerAck] = ModuleIds.NavFriends,
        [Protocol.FriendRemove] = ModuleIds.NavFriends,
        [Protocol.FriendRemoveAck] = ModuleIds.NavFriends,
        [Protocol.FriendEvent] = ModuleIds.NavFriends,
        [Protocol.FriendSearch] = ModuleIds.NavFriends,
        [Protocol.FriendSearchReply] = ModuleIds.NavFriends,
        [Protocol.FriendRemark] = ModuleIds.NavFriends,
        [Protocol.FriendRemarkAck] = ModuleIds.NavFriends,

        // 账号注册(602 客户端自助注册)
        [Protocol.AccountRegister] = ModuleIds.AdminZxmRegister,
        [Protocol.AccountRegisterAck] = ModuleIds.AdminZxmRegister,

        // 绑定
        [Protocol.BindNotice] = ModuleIds.WidgetBind,
        [Protocol.BindApply] = ModuleIds.WidgetBind,
        [Protocol.BindAck] = ModuleIds.WidgetBind,
        [Protocol.Unbind] = ModuleIds.WidgetBind,
        [Protocol.BindQuery] = ModuleIds.WidgetBind,
        [Protocol.BindList] = ModuleIds.WidgetBind,
        [Protocol.BindConfirm] = ModuleIds.WidgetBind,

        // 进服确认
        [Protocol.LoginRequest] = ModuleIds.LoginConfirm,
        [Protocol.LoginAsk] = ModuleIds.LoginConfirm,
        [Protocol.LoginAnswer] = ModuleIds.LoginConfirm,
        [Protocol.LoginResult] = ModuleIds.LoginConfirm,
        [Protocol.LoginTimeout] = ModuleIds.LoginConfirm,

        // 消息与公告
        [Protocol.Notice] = ModuleIds.Message,
        [Protocol.Push] = ModuleIds.Message,
        [Protocol.CardAction] = ModuleIds.Message,
        [Protocol.CardActionAck] = ModuleIds.Message,
        [Protocol.MessageHistory] = ModuleIds.Message,
        [Protocol.MessageHistoryReq] = ModuleIds.Message,
        [Protocol.MessageHistoryDone] = ModuleIds.Message,
        [Protocol.MessageAck] = ModuleIds.Message,
        [Protocol.MegBackReq] = ModuleIds.Message,
        [Protocol.MegBackAck] = ModuleIds.Message,

        // 聊天宿主门牌
        [Protocol.ChatHello] = ModuleIds.Chat,

        // 强制更新
        [Protocol.ClientTooOld] = ModuleIds.ForceUpdate,

        // 服务器列表与切换
        [Protocol.ServerListReq] = ModuleIds.ServerList,
        [Protocol.ServerList] = ModuleIds.ServerList,
        [Protocol.ServerSwitchReq] = ModuleIds.ServerList,
        [Protocol.ServerSwitchAck] = ModuleIds.ServerList,

        // 控制台
        [Protocol.ConsoleCommand] = ModuleIds.Console,
        [Protocol.ConsoleResult] = ModuleIds.Console,
        [Protocol.ConsoleOutput] = ModuleIds.Console,
        [Protocol.ConsoleTap] = ModuleIds.Console,
        [Protocol.ConsoleServersReq] = ModuleIds.Console,
        [Protocol.ConsoleServers] = ModuleIds.Console,
    };

    /// <summary>
    /// 请求包 → 结果包；用来在模块关闭时回一句 err。
    /// 没有配对结果包的（单向包，如已读标记、登录回答、消息确认）返回 null。
    /// </summary>
    private static readonly Dictionary<ushort, ushort> _replyOf = new()
    {
        // 好友
        [Protocol.FriendMsg] = Protocol.FriendMsgAck,
        [Protocol.FriendMsgHistory] = Protocol.FriendMsgHistoryReply,
        [Protocol.FriendAvatarReq] = Protocol.FriendAvatarAck,
        [Protocol.FriendListQuery] = Protocol.FriendListReply,
        [Protocol.FriendRequest] = Protocol.FriendRequestAck,
        [Protocol.FriendRequestsQuery] = Protocol.FriendRequestsReply,
        [Protocol.FriendAnswer] = Protocol.FriendAnswerAck,
        [Protocol.FriendRemove] = Protocol.FriendRemoveAck,
        [Protocol.FriendSearch] = Protocol.FriendSearchReply,
        [Protocol.FriendRemark] = Protocol.FriendRemarkAck,

        // 账号注册
        [Protocol.AccountRegister] = Protocol.AccountRegisterAck,

        // 绑定(三个请求共用一个回执包)
        [Protocol.BindApply] = Protocol.BindAck,
        [Protocol.Unbind] = Protocol.BindAck,
        [Protocol.BindConfirm] = Protocol.BindAck,
        [Protocol.BindQuery] = Protocol.BindList,

        // 消息与公告
        [Protocol.CardAction] = Protocol.CardActionAck,
        [Protocol.MessageHistoryReq] = Protocol.MessageHistoryDone,

        // 服务器列表与切换
        [Protocol.ServerListReq] = Protocol.ServerList,
        [Protocol.ServerSwitchReq] = Protocol.ServerSwitchAck,

        // 控制台
        [Protocol.ConsoleCommand] = Protocol.ConsoleResult,
        [Protocol.ConsoleTap] = Protocol.ConsoleResult,
        [Protocol.ConsoleServersReq] = Protocol.ConsoleServers,
    };

    /// <summary>查这条命令属于哪个模块；基础命令返回空串。</summary>
    public static string CommandOf(string command)
        => _commandOwner.TryGetValue(command.Trim(), out var id) ? id : "";

    /// <summary>查这个包属于哪个模块；核心包返回空串。</summary>
    public static string PacketOf(ushort type)
        => _packetOwner.TryGetValue(type, out var id) ? id : "";

    /// <summary>查这个请求包该回哪个结果包；单向包返回 false。</summary>
    public static bool TryGetReply(ushort request, out ushort reply)
        => _replyOf.TryGetValue(request, out reply);

    /// <summary>模块关闭时回给客户端的统一措辞。</summary>
    public static string DisabledPayload(string title) => $"err|该功能已关闭:{title}";
}
