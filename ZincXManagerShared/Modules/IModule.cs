using System.Collections.Generic;  // IReadOnlyList:只读列表

namespace ZincXManagerShared.Modules;

/// <summary>
/// 功能模块契约：一个模块 = 一段可开关的功能。
/// 模块自己声明编号、中文名、父模块、归属命令、归属协议包、默认开关；
/// 关闭后服务端不实例化它，命令不注册、包处理器不注册、后台线程不启动。
/// </summary>
public interface IModule
{
    /// <summary>模块编号，取自 ModuleIds。</summary>
    string Id { get; }

    /// <summary>配置表与控制台里显示的中文名。</summary>
    string Title { get; }

    /// <summary>父模块编号；父模块关闭时本模块不加载。顶层模块返回空串。</summary>
    string Parent { get; }

    /// <summary>默认是否开启。</summary>
    bool DefaultOn { get; }

    /// <summary>是否锁死不可关闭。</summary>
    bool Locked { get; }

    /// <summary>旧配置键别名；迁移旧配置时用它反查旧值。没有别名返回空串。</summary>
    string LegacyKey { get; }

    /// <summary>本模块注册的命令名；未加载时这些命令不存在。</summary>
    IReadOnlyList<string> Commands { get; }

    /// <summary>本模块接管的协议包类型；服务端据此建包分发表。</summary>
    IReadOnlyList<ushort> Packets { get; }
}
