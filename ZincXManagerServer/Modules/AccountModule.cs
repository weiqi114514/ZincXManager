using System.Collections.Generic;
using ZincXManagerShared.Modules;  // ModuleCatalog / ModuleIds

namespace ZincXManagerServer.Modules;

/// <summary>
/// ZXM 账号管理(az.account):命令 user list / reg / del / rep / rel。
/// <para>关掉之后这几个命令一律拒绝;客户端登录 / 注册 / 头像这些核心账号包不受影响
/// (它们在核心分发表里,归 <see cref="Program"/> 自己管,不属于这个模块)。</para>
/// </summary>
internal sealed class AccountModule : ServerModule
{
    public AccountModule() : base(ModuleCatalog.Find(ModuleIds.AdminZxmAccount)!) { }

    /// <summary>归属本模块的命令。</summary>
    public override IReadOnlyList<string> Commands { get; } =
    [
        "user list",
        "user reg",
        "user del",
        "user rep",
        "user rel",
    ];
}
