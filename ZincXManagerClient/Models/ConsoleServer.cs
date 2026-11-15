using System;  // 基础类型

namespace ZincXManagerClient.Models;

/// <summary>控制台能连的一台 MC 服务器,对应 685 清单里的一行。</summary>
public sealed class ConsoleServer
{
    /// <summary>服务器编号(和选服那套 670/671 的编号一致)。</summary>
    public int Id { get; init; }

    /// <summary>服务器名称。</summary>
    public string Name { get; init; } = "";

    /// <summary>控制台模式:0 = ZXMM 桥接,1 = RCON。</summary>
    public int ConsoleMode { get; init; }

    /// <summary>ZXMM 桥接通道是否在线。</summary>
    public bool BridgeOnline { get; init; }

    /// <summary>RCON 通道是否在线。</summary>
    public bool RconOnline { get; init; }

    /// <summary>控制台模式文本。</summary>
    public string ModeText => ConsoleMode == 1 ? "RCON" : "桥接";

    /// <summary>下拉里跟在名字后面的模式后缀。</summary>
    public string ModeSuffix => ConsoleMode == 1 ? "（RCON）" : "（桥接）";

    /// <summary>当前模式对应的那条通道在不在线。</summary>
    public bool Online => ConsoleMode == 1 ? RconOnline : BridgeOnline;

    /// <summary>在线状态文本。</summary>
    public string OnlineText => Online ? "在线" : "离线";
}
