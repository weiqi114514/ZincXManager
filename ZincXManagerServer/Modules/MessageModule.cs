using System.Collections.Generic;
using System.Threading.Tasks;
using ZincXManagerShared.Logging;  // Logger
using ZincXManagerShared.Modules;  // ModuleCatalog / ModuleIds
using ZincXManagerShared.Network;  // Protocol / ServerboundPacket

namespace ZincXManagerServer.Modules;

/// <summary>
/// 消息系统(msg):公告 / 推送 / 卡片动作 / 补发 / 回执(客户端方向 5000-5009)。
/// <para>关掉之后:这些包全部按 <see cref="ZincXManagerShared.Modules.ModuleRules"/>
/// 回 err(补发请求回 5006、卡片动作回 5003),单向包只记一条调试日志。</para>
/// </summary>
internal sealed class MessageModule : ServerModule
{
    public MessageModule() : base(ModuleCatalog.Find(ModuleIds.Message)!) { }

    /// <summary>消息模块认领的包(5000-5009 整段:服务端下发 + 客户端回话都在这一段里)。</summary>
    public override IReadOnlyList<ushort> Packets { get; } =
    [
        Protocol.Notice,              // 5000 公告(服务端下发,客户端发来一律忽略)
        Protocol.Push,                // 501 定向推送(同上)
        Protocol.CardAction,          // 502 卡片按钮被点
        Protocol.CardActionAck,       // 5003 回执
        Protocol.MessageHistory,      // 5004 补发内容
        Protocol.MessageHistoryReq,   // 505 客户端要补发
        Protocol.MessageHistoryDone,  // 5006 补发收尾
        Protocol.MessageAck,          // 507 客户端已收
        Protocol.MegBackReq,          // 5008 补发查询请求
        Protocol.MegBackAck,          // 5009 补发查询结果
    ];

    /// <summary>归属本模块的命令。</summary>
    public override IReadOnlyList<string> Commands { get; } =
    [
        "msg notice",
        "msg push",
        "msg backget",
        "msgdays",
        "srvmsgdays",
    ];

    /// <summary>按包类型分派;只有"客户端真会发"的几种有处理,其余记调试日志就结束。</summary>
    /// <param name="packet">发来的包。</param>
    /// <param name="context">装配时给的上下文。</param>
    public override Task OnPacketAsync(ServerboundPacket packet, ModuleContext context) => packet.Type switch
    {
        Protocol.Notice or Protocol.Push => Program.HandleServerOnlyMessageAsync(packet),
        Protocol.CardAction => Program.HandleCardActionAsync(packet),
        Protocol.MessageHistoryReq => Program.HandleMessageHistoryReqAsync(packet),
        Protocol.MessageAck => Program.HandleMessageAckAsync(packet),
        Protocol.MegBackAck => Program.HandleMegBackAckAsync(packet),
        _ => Skip(packet),
    };

    /// <summary>服务端下发用的那几种(5003/5004/5006/5008)被客户端发回来:没有可做的,记一条调试日志。</summary>
    /// <param name="packet">发来的包。</param>
    private static Task Skip(ServerboundPacket packet)
    {
        Logger.Log(LogLevel.Debug, $"[模块] 收到只有服务端会发的消息包 type={packet.Type},已忽略");
        return Task.CompletedTask;
    }
}
