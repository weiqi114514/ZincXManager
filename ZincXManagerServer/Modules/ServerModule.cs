using System;  // Func / Action
using System.Collections.Generic;  // IReadOnlyList
using ZincXManagerShared.Command;  // CommandHandler:模块自己注册命令
using ZincXManagerShared.FileIO;  // ZFile:读配置
using ZincXManagerShared.Modules;  // IModule / ModuleDef
using ZincXManagerShared.Network;  // ServerboundPacket

namespace ZincXManagerServer.Modules;

/// <summary>
/// 模块加载上下文：注册表在加载模块时交给它的那几样东西。
/// 模块从这里拿配置、命令表和对外发报文的通道，不直接摸 Program 的私有字段。
/// </summary>
internal sealed class ModuleContext
{
    /// <summary>服务端配置(config.ini)。</summary>
    public required ZFile Config { get; init; }

    /// <summary>命令处理器；模块在自己的 OnLoad 里往这里注册命令。</summary>
    public required CommandHandler Commands { get; init; }

    /// <summary>按服务器编号取 ZXMM 通道；取不到返回 null。</summary>
    public required Func<int, Zxmm.ZxmmApi?> ZxmmApiOf { get; init; }

    /// <summary>给本端在线客户端发一个包；返回发出去了几条。</summary>
    public required Func<ushort, string, string?, Task<int>> SendToLocalClientsAsync { get; init; }

    /// <summary>生成 202 配置包负载(含 msgdays / squarename / modules)。</summary>
    public required Func<string> BuildServerConfigPayload { get; init; }
}

/// <summary>
/// 服务端功能模块基类：一个模块 = 一段可开关的功能。
/// 关闭后注册表不实例化这个类，OnLoad / OnPacketAsync 一次都不会被调用：
/// 命令不注册、包处理器不登记、后台线程不启动、聊天宿主不拉起、文件不读。
/// </summary>
internal abstract class ServerModule : IModule
{
    /// <summary>建一个模块实例；静态声明由注册表从模块全集里查出来给它。</summary>
    protected ServerModule(ModuleDef meta)
    {
        Meta = meta;
    }

    /// <summary>本模块的静态声明。</summary>
    internal ModuleDef Meta { get; }

    /// <inheritdoc />
    public string Id => Meta.Id;

    /// <inheritdoc />
    public string Title => Meta.Title;

    /// <inheritdoc />
    public string Parent => Meta.Parent;

    /// <inheritdoc />
    public bool DefaultOn => Meta.DefaultOn;

    /// <inheritdoc />
    public bool Locked => Meta.Locked;

    /// <inheritdoc />
    public string LegacyKey => Meta.LegacyKey;

    /// <inheritdoc />
    public virtual IReadOnlyList<string> Commands => Array.Empty<string>();

    /// <inheritdoc />
    public virtual IReadOnlyList<ushort> Packets => Array.Empty<ushort>();

    /// <summary>模块加载：注册命令、起后台任务、打开文件。只在开启时调用一次。</summary>
    public virtual void OnLoad(ModuleContext context)
    {
    }

    /// <summary>处理本模块的包；只有 Packets 里声明的类型会进来。</summary>
    public virtual Task OnPacketAsync(ServerboundPacket packet, ModuleContext context)
    {
        return Task.CompletedTask;
    }

    /// <summary>进程退出前的收尾；只在加载过时调用。</summary>
    public virtual void OnUnload()
    {
    }
}
