using System;  // Array / StringComparison
using System.Collections.Generic;  // Dictionary / HashSet / List
using System.IO;  // Path / File
using System.Text;  // Encoding
using System.Threading.Tasks;  // Task / Delay
using ZincXManagerShared.Modules;  // ModuleModes(半加载名单的解析与拼装)
using ZincXManagerShared.Network;  // Protocol

namespace ZincXManagerTests;

/// <summary>
/// 套件 30:模块系统(启动时装配、关闭即不加载)。
///
/// <para>每个套件都真起一个 ZXMS(不同端口、不同临时目录),验的是同一件事:
/// 配置里写进 <c>modules</c> 的功能,启动时到底装不装、装了之后行为对不对。</para>
/// <list type="bullet">
///   <item>默认启动:14 个有实现的服务端模块都在清单里,forceupdate / datasep 默认关,其余开;</item>
///   <item>开关:<c>module off nav.friends</c> 只写配置并提示要重启,关完清单显示「开(下次关)」;</item>
///   <item>带 <c>modules=nav.friends,wd.bind,msg</c> 重启:650 回 <c>err|…</c>、<c>bind</c> 被门禁拦下、
///         <c>module on chat</c> 报父模块关着、<c>help</c> 里看不到这几个模块的命令;</item>
///   <item><c>modules=none</c>:14 个全装,650 走真处理器;</item>
///   <item>写不认识的编号:启动日志「已忽略」,服务照常起来;</item>
///   <item>半加载(<c>wd.bind:half</c>):202 的 modules 与 half 各归各家,客户端包回 err 但控制台命令照常;</item>
///   <item>半加载只挡玩家:同一个 626,玩家连接回 <c>err|该功能已关闭</c>,LAdmin 连接交给真处理器;</item>
///   <item>半加载与完全关闭的区别:同样是 5005 回 err,<c>msg notice</c> 在完全关闭时被门禁拒、半加载时能跑;</item>
///   <item><see cref="ModuleModes.Parse"/> 的纯函数行为(空串 / none / 后缀 / 混分隔符 / 只有后缀)。</item>
/// </list>
/// </summary>
public static class ModuleSuite
{
    private const int PortDefault = 21520;    //默认启动
    private const int PortDisabled = 21521;   //关掉几个模块后重启
    private const int PortNone = 21522;       //modules=none
    private const int PortUnknown = 21524;    //modules 里写不认识的编号
    private const int PortHalfBind = 21530;   //wd.bind:half(半加载:命令在、包不回)
    private const int PortHalfReg = 21531;    //az.register:half
    private const int PortOffVsHalf = 21532;  //msg 完全关闭 + 两个半加载混写
    private const int PortHalfAdmin = 21533;  //wd.bind:half + 管理员登录(半加载只挡玩家)

    /// <summary>服务端有实现的 14 个模块编号。</summary>
    private static readonly string[] Implemented =
    [
        "nav.friends", "wd.bind", "af.ban", "as.whitelist", "az.account", "mg.server",
        "msg", "console", "serverlist", "loginconfirm", "chat", "forceupdate", "datasep",
        "az.register",
    ];

    /// <summary>默认关着的那两个。</summary>
    private static readonly string[] DefaultOff = ["forceupdate", "datasep"];

    /// <summary>跑一遍,结果追加到 <paramref name="results"/>。</summary>
    /// <param name="results">总表。</param>
    /// <param name="ws">测试产物目录(不污染仓库)。</param>
    /// <param name="debugMs">服务端编译输出目录。</param>
    public static async Task RunAsync(List<SuiteResult> results, string ws, string debugMs)
    {
        var r = new SuiteResult { Name = "30 模块系统(装配 / 开关 / 关闭即不加载)" };
        results.Add(r);

        ParseUnitTests(r);
        await DefaultStartupAsync(r, ws, debugMs);
        await DisabledRestartAsync(r, ws, debugMs);
        await NoneRestartAsync(r, ws, debugMs);
        await UnknownIdAsync(r, ws, debugMs);
        await HalfBindAsync(r, ws, debugMs);
        await HalfRegisterAsync(r, ws, debugMs);
        await HalfAdminAsync(r, ws, debugMs);
        await OffVsHalfAsync(r, ws, debugMs);
    }

    // ---------- ① 默认启动:清单 13 项 + 开关写盘 ----------

    /// <summary>默认起一个(不带 modules):清单、默认开关、module off 的写盘与提示。</summary>
    private static async Task DefaultStartupAsync(SuiteResult r, string ws, string debugMs)
    {
        string dir;
        Svc ms;
        try
        {
            dir = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "modules"));
            ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, Ini(PortDefault, "CSharp-模块服"));
        }
        catch (Exception ex)
        {
            r.Add("准备默认启动环境", false, ex.Message);
            return;
        }

        using var _ = ms;
        var up = ms.WaitPort(PortDefault);
        r.Add("默认启动:ZXMS 监听 " + PortDefault, up, up ? "" : "20 秒没监听");
        if (!up)
        {
            r.Note(ms.Out);
            return;
        }

        await Task.Delay(400);   //装配日志与控制台都还在往外走

        ms.SendCmd("module list");
        var listed = WaitFor(() => ms.Out.Contains("本次启动装配", StringComparison.Ordinal), 8000);
        r.Add("module list 打出了清单与汇总行", listed, listed ? "" : "8 秒没看到「本次启动装配」");
        if (!listed)
        {
            r.Note(ms.Out);
            return;
        }

        var text = ms.Out;
        var rows = CountModuleRows(text);
        r.Add("清单里是完整的 64 项", rows == 64, "实际 " + rows + " 行");

        var missing = new List<string>();
        var wrongState = new List<string>();
        foreach (var id in Implemented)
        {
            var line = LastLineOf(text, id);
            if (line.Length == 0) { missing.Add(id); continue; }

            var shouldBeOff = Array.IndexOf(DefaultOff, id) >= 0;
            var looksOn = line.Contains('开') && !line.Contains('关');   //状态列是「开」而不是「关 / 开(下次关)」
            if (shouldBeOff ? looksOn : !looksOn) { wrongState.Add(id + " → " + line.Trim()); }
        }

        r.Add("14 个有实现的模块都在清单里", missing.Count == 0,
            missing.Count == 0 ? "" : "缺:" + string.Join("、", missing));
        r.Add("默认开关对(forceupdate / datasep 关,其余开)", wrongState.Count == 0,
            wrongState.Count == 0 ? "" : string.Join(" / ", wrongState));

        var loaded12 = text.Contains("装配完成:加载 12 个", StringComparison.Ordinal);
        r.Add("默认启动装了 12 个(14 减掉默认关的两个)", loaded12,
            loaded12 ? "" : "没看到「装配完成:加载 12 个」");

        // 锁死模块与不认识的编号都要被拒,而不是悄悄写进配置
        ms.SendCmd("module off nav.settings");
        var locked = WaitFor(() => ms.Out.Contains("锁死的,不能关闭", StringComparison.Ordinal), 5000);
        r.Add("module off nav.settings 被拒(设置是锁死模块)", locked, locked ? "" : "没看到「锁死的,不能关闭」");
        r.Add("被拒后配置里没写进 nav.settings", !ReadIni(Path.Combine(dir, "config.ini"), "modules").Contains("nav.settings", StringComparison.Ordinal),
            "modules=" + ReadIni(Path.Combine(dir, "config.ini"), "modules"));

        ms.SendCmd("module off no.such");
        var unknown = WaitFor(() => ms.Out.Contains("没有这个模块:no.such", StringComparison.Ordinal), 5000);
        r.Add("module off 不认识的编号被拒", unknown, unknown ? "" : "没看到「没有这个模块」");

        // 关好友:写配置 + 提示重启 + 点名子模块
        var ini = Path.Combine(dir, "config.ini");
        ms.SendCmd("module off nav.friends");
        var wrote = WaitFor(() => ReadIni(ini, "modules").Contains("nav.friends", StringComparison.Ordinal), 6000);
        r.Add("module off nav.friends 把 nav.friends 写进 config 的 modules", wrote, "modules=" + ReadIni(ini, "modules"));
        r.Add("开关输出提示要重启才生效",
            WaitFor(() => ms.Out.Contains("重启 ZXMS 后生效", StringComparison.Ordinal), 5000),
            "没看到「重启 ZXMS 后生效」");
        r.Add("关父模块会点名子模块跟着关",
            WaitFor(() => ms.Out.Contains("子模块「好友聊天后端」", StringComparison.Ordinal), 5000),
            "没看到子模块提示");

        // 关完再列:nav.friends 要显示成「开(下次关)」,别让人以为没生效
        var before = ms.Out.Length;
        ms.SendCmd("module list");
        var relisted = WaitFor(() => ms.Out.Contains("开(下次关)", StringComparison.Ordinal), 6000);
        var second = relisted ? ms.Out[before..] : "";
        var friendsRow = LastLineOf(second, "nav.friends");
        r.Add("关完后清单显示「开(下次关)」", relisted && friendsRow.Contains("开(下次关)", StringComparison.Ordinal),
            friendsRow.Length > 0 ? friendsRow.Trim() : "没看到 nav.friends 那行");

        r.Note("短名不是别名:要写编号(module off nav.friends,不是 module off friends)");
    }

    // ---------- ② 带禁用名单重启:包 / 命令 / 父模块 / help ----------

    /// <summary>modules=nav.friends,wd.bind,msg 重启:包回 err、命令被拦、父模块校验、help 过滤。</summary>
    private static async Task DisabledRestartAsync(SuiteResult r, string ws, string debugMs)
    {
        string dir;
        Svc ms;
        try
        {
            dir = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "modules-off"));
            var ini = Ini(PortDisabled, "CSharp-关模块服");
            ini["modules"] = "nav.friends,wd.bind,msg";
            ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, ini);
        }
        catch (Exception ex)
        {
            r.Add("准备关模块重启环境", false, ex.Message);
            return;
        }

        using var _ = ms;
        var up = ms.WaitPort(PortDisabled);
        r.Add("关闭三个模块后服务端照常起来", up, up ? "" : "20 秒没监听 " + PortDisabled);
        if (!up)
        {
            r.Note(ms.Out);
            return;
        }

        await Task.Delay(500);

        var loaded9 = ms.Out.Contains("装配完成:加载 11 个", StringComparison.Ordinal)     //14 - 3
                      || ms.Out.Contains("装配完成:加载 10 个", StringComparison.Ordinal);  //减掉默认关的那两个再减 3
        r.Add("装配数按禁用名单减少", loaded9, FirstLine(ms.Out, "装配完成:"));

        // ③ 650 好友列表请求 → 651 且是 err|
        try
        {
            using var probe = await Probe.ConnectAsync("127.0.0.1", PortDisabled, "OS");
            r.Add("3080/3081 加密协商", true);

            await probe.SendAsync(1, Protocol.FriendListQuery, Array.Empty<byte>());
            var reply = await probe.TakeAsync(Protocol.FriendListReply, 8000);
            r.Add("好友关着时 6050 有回应(6051)", reply is not null, reply ?? "8 秒没回");
            r.Add("651 以 err| 开头", reply is not null && reply.StartsWith("err|", StringComparison.Ordinal), reply ?? "");
            r.Add("err 里说明是好友功能关了",
                reply is not null && reply.Contains("该功能已关闭", StringComparison.Ordinal) && reply.Contains("好友", StringComparison.Ordinal),
                reply ?? "");
        }
        catch (Exception ex)
        {
            r.Add("关模块时的报文往返", false, ex.Message);
        }

        // ④ 控制台的 bind 要被门禁拦,而不是当成未知命令
        ms.SendCmd("bind");
        var blocked = WaitFor(() => ms.Out.Contains("该功能已关闭:绑定", StringComparison.Ordinal), 5000);
        r.Add("绑定模块关着时 bind 被门禁拦下", blocked, blocked ? "" : FirstLine(ms.Out, "bind"));
        r.Add("拦下的理由是「该功能已关闭」而不是「未知命令」", blocked && !ms.Out.Contains("未知命令", StringComparison.Ordinal),
            "输出里出现了「未知命令」");

        // ⑧ 父模块关着,子模块不许单独打开
        ms.SendCmd("module on chat");
        var parent = WaitFor(() => ms.Out.Contains("父模块「好友」已关闭", StringComparison.Ordinal), 5000);
        r.Add("父模块关着时 module on chat 被拒", parent, parent ? "" : "没看到「父模块「好友」已关闭」");

        // ⑨ help 不列已关闭模块的命令(对照:没关的还在)
        // 注意:启动时 CommandHandler 自己会打一行「输入 help 查看可用命令」,
        // 所以这里等的是清单末尾那句「输入 help <命令> 查看具体用法」,保证整块已经到齐
        var before = ms.Out.Length;
        ms.SendCmd("help");
        var helped = WaitFor(() => ms.Out.Length > before && ms.Out[before..].Contains("输入 help <命令> 查看具体用法", StringComparison.Ordinal), 8000);
        var help = helped ? ms.Out[before..] : "";
        r.Add("help 打出了命令清单", helped, helped ? "" : "8 秒没看到清单末尾");
        r.Add("help 里没有消息模块的命令(msg notice / msgdays / srvmsgdays)",
            helped && !help.Contains("msg notice", StringComparison.Ordinal)
                  && !help.Contains("msgdays", StringComparison.Ordinal)
                  && !help.Contains("srvmsgdays", StringComparison.Ordinal),
            FirstLine(help, "msg"));
        r.Add("help 里没有绑定模块的命令(bind / unbind)",
            helped && !help.Contains("unbind", StringComparison.Ordinal) && !help.Contains("bind list", StringComparison.Ordinal),
            FirstLine(help, "bind"));
        r.Add("help 里还有没关模块的命令(ban 一组作对照)", helped && help.Contains("ban", StringComparison.Ordinal),
            helped ? FirstLine(help, "ban") : "help 没出来");

        // 打开父模块之后,子模块就能单独开关(说明前面拦的是父模块而不是 chat 自己)
        ms.SendCmd("module on nav.friends");
        var parentOn = WaitFor(() => ms.Out.Contains("已写入:好友(nav.friends) 开启", StringComparison.Ordinal), 5000);
        r.Add("先开父模块能写进配置", parentOn, parentOn ? "" : "没看到 nav.friends 被开启");
    }

    // ---------- ③ modules=none:全装 ----------

    /// <summary>modules=none:13 个全部装配,650 走真处理器(不是 err)。</summary>
    private static async Task NoneRestartAsync(SuiteResult r, string ws, string debugMs)
    {
        string dir;
        Svc ms;
        try
        {
            dir = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "modules-none"));
            var ini = Ini(PortNone, "CSharp-全开服");
            ini["modules"] = "none";
            ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, ini);
        }
        catch (Exception ex)
        {
            r.Add("准备 modules=none 环境", false, ex.Message);
            return;
        }

        using var _ = ms;
        var up = ms.WaitPort(PortNone);
        r.Add("modules=none 时服务端起来了", up, up ? "" : "20 秒没监听 " + PortNone);
        if (!up)
        {
            r.Note(ms.Out);
            return;
        }

        await Task.Delay(500);
        var all = WaitFor(() => ms.Out.Contains("装配完成:加载 14 个", StringComparison.Ordinal), 8000);
        r.Add("modules=none 装配了全部 14 个", all, all ? "" : FirstLine(ms.Out, "装配完成:"));

        try
        {
            using var probe = await Probe.ConnectAsync("127.0.0.1", PortNone, "OS");
            await probe.SendAsync(1, Protocol.FriendListQuery, Array.Empty<byte>());
            var reply = await probe.TakeAsync(Protocol.FriendListReply, 8000);
            r.Add("好友装着时 6050 有回应(6051)", reply is not null, reply ?? "8 秒没回");
            r.Add("回复走的是真处理器,不是「该功能已关闭」",
                reply is not null && !reply.StartsWith("err|该功能已关闭", StringComparison.Ordinal),
                reply ?? "");
        }
        catch (Exception ex)
        {
            r.Add("全装时的报文往返", false, ex.Message);
        }
    }

    // ---------- ④ 不认识的编号 ----------

    /// <summary>modules 里写一个不存在的编号:日志「已忽略」,服务照常起来。</summary>
    private static async Task UnknownIdAsync(SuiteResult r, string ws, string debugMs)
    {
        string dir;
        Svc ms;
        try
        {
            dir = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "modules-unknown"));
            var ini = Ini(PortUnknown, "CSharp-怪编号服");
            ini["modules"] = "no.such.module";
            ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, ini);
        }
        catch (Exception ex)
        {
            r.Add("准备未知编号环境", false, ex.Message);
            return;
        }

        using var _ = ms;
        var up = ms.WaitPort(PortUnknown);
        r.Add("modules 里有怪编号也能正常起来", up, up ? "" : "20 秒没监听 " + PortUnknown);
        if (!up)
        {
            r.Note(ms.Out);
            return;
        }

        var ignored = WaitFor(() => ms.Out.Contains("已忽略", StringComparison.Ordinal), 6000);
        r.Add("启动日志说明这个编号被忽略", ignored, FirstLine(ms.Out, "不认识的模块"));
        var all = WaitFor(() => ms.Out.Contains("装配完成:加载 14 个", StringComparison.Ordinal), 6000);
        r.Add("怪编号不挡住其它模块(14 个照样装)", all, FirstLine(ms.Out, "装配完成:"));
    }

    // ---------- ⑤ 半加载:命令在、包不回 ----------

    /// <summary>
    /// <c>modules=wd.bind:half</c>:2002 的 modules=none、half=wd.bind;6026 包回 err,
    /// 但控制台的 <c>bind list</c> 照常能跑 —— 这是半加载与完全关闭的唯一区别。
    /// </summary>
    private static async Task HalfBindAsync(SuiteResult r, string ws, string debugMs)
    {
        string dir;
        Svc ms;
        try
        {
            dir = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "modules-half-bind"));
            var ini = Ini(PortHalfBind, "CSharp-半加载绑定服");
            ini["modules"] = "wd.bind:half";
            ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, ini);
        }
        catch (Exception ex)
        {
            r.Add("准备半加载绑定环境", false, ex.Message);
            return;
        }

        using var _ = ms;
        var up = ms.WaitPort(PortHalfBind);
        r.Add("wd.bind:half 时服务端照常起来", up, up ? "" : "20 秒没监听 " + PortHalfBind);
        if (!up)
        {
            r.Note(ms.Out);
            return;
        }

        var half = WaitFor(() => ms.Out.Contains("已半加载 绑定 / 解绑(wd.bind)", StringComparison.Ordinal), 8000);
        r.Add("启动日志说绑定是半加载", half, FirstLine(ms.Out, "已半加载"));

        try
        {
            using var probe = await Probe.ConnectAsync("127.0.0.1", PortHalfBind, "OS");

            // 2002 的负载:modules 只放完全关闭的,half 单独一行
            var config = await probe.WaitAsync(Protocol.ServerConfig, 4000);
            var modulesLine = KeyLine(config, "modules");
            var halfLine = KeyLine(config, "half");
            r.Add("半加载时 202 的 modules 是 none", modulesLine == "none", "modules=" + modulesLine);
            r.Add("半加载时 202 的 half 是 wd.bind", halfLine == "wd.bind", "half=" + halfLine);

            // 6026 绑定申请:半加载 → 包一律回 err
            await probe.SendAsync(2, Protocol.BindApply, Encoding.UTF8.GetBytes("testuser|Player"));
            var ack = await probe.TakeAsync(Protocol.BindAck, 8000);
            var packetText = ack ?? "";
            r.Add("半加载的绑定模块收到 6026 回 6027", ack is not null, packetText.Length > 0 ? packetText : "8 秒没回");
            r.Add("627 是 err| 且点名绑定功能关闭",
                packetText.StartsWith("err|该功能已关闭:绑定 / 解绑", StringComparison.Ordinal), packetText);

            // 同一实例里命令照常(半加载放行控制台)
            var before = ms.Out.Length;
            ms.SendCmd("bind list");
            var cmdOk = WaitFor(() => ms.Out.Length > before && ms.Out[before..].Contains("[绑定] 无绑定信息", StringComparison.Ordinal), 6000);
            var cmdSlice = cmdOk ? ms.Out[before..] : ms.Out;
            var cmdText = FirstLine(cmdSlice, "[绑定] 无绑定信息");
            r.Add("半加载时 bind list 命令照常执行", cmdOk, cmdText);
            r.Add("[对照] 命令没有被模块门禁拦下(半加载放行命令)",
                !cmdSlice.Contains("[模块] 该功能已关闭:绑定", StringComparison.Ordinal),
                FirstLine(cmdSlice, "[模块] 该功能已关闭"));
            r.Note("半加载下 626 的应答:" + packetText + ";命令应答:" + cmdText);
        }
        catch (Exception ex)
        {
            r.Add("半加载绑定的报文往返", false, ex.Message);
        }
    }

    // ---------- ⑥ 半加载只挡玩家、放行管理账号 ----------

    /// <summary>
    /// <c>modules=wd.bind:half</c> 下同样发 6026:玩家连接回 <c>err|该功能已关闭</c>,
    /// LAdmin 连接交给真处理器(回「绑定码无效」)。这条证明半加载不是「谁都别用」,
    /// 而是「玩家看不到,管理员照样能做」。
    /// </summary>
    private static async Task HalfAdminAsync(SuiteResult r, string ws, string debugMs)
    {
        string dir;
        Svc ms;
        try
        {
            dir = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "modules-half-admin"));
            var ini = Ini(PortHalfAdmin, "CSharp-半加载管理员服");
            ini["modules"] = "wd.bind:half";
            ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, ini);
        }
        catch (Exception ex)
        {
            r.Add("准备半加载管理员环境", false, ex.Message);
            return;
        }

        using var _ = ms;
        var up = ms.WaitPort(PortHalfAdmin);
        r.Add("半加载管理员:服务端照常起来", up, up ? "" : "20 秒没监听 " + PortHalfAdmin);
        if (!up)
        {
            r.Note(ms.Out);
            return;
        }

        // 管理员账号只能在服务端建:半加载下 user reg 照常可用(这就是半加载存在的理由)
        ms.SendCmd("user reg halfadmin pw123456 LAdmin");
        var created = WaitFor(() => ms.Out.Contains("[账号] 已创建 halfadmin(LAdmin)", StringComparison.Ordinal), 8000);
        r.Add("半加载时 user reg 建出 LAdmin 账号", created, FirstLine(ms.Out, "[账号] 已创建"));

        try
        {
            // ① 玩家连接(不登录):半加载的包一律拒
            using var player = await Probe.ConnectAsync("127.0.0.1", PortHalfAdmin, "OS");
            await player.SendAsync(2, Protocol.BindApply, Encoding.UTF8.GetBytes("ABCD1234"));
            var playerAck = await player.TakeAsync(Protocol.BindAck, 8000);
            var playerText = playerAck ?? "";
            r.Add("玩家发 6026 被挡(回 6027)", playerAck is not null, playerText.Length > 0 ? playerText : "8 秒没回");
            r.Add("玩家的 627 是 err|该功能已关闭",
                playerText.StartsWith("err|该功能已关闭:绑定 / 解绑", StringComparison.Ordinal), playerText);

            // ② 管理员连接:同一个包交给真处理器,答复变成真实的业务判断
            using var admin = await Probe.ConnectAsync("127.0.0.1", PortHalfAdmin, "OS");
            var adminLogin = await admin.LoginAsync("halfadmin", "pw123456", "MACH-ADMIN", "LAdmin", "zxms");
            r.Add("LAdmin 登录成功", adminLogin, admin.LastAck ?? "");
            if (adminLogin)
            {
                await admin.SendAsync(2, Protocol.BindApply, Encoding.UTF8.GetBytes("ABCD1234"));
                var adminAck = await admin.TakeAsync(Protocol.BindAck, 8000);
                var adminText = adminAck ?? "";
                r.Add("管理账号的 626 没被模块门禁拦(回了 627)", adminAck is not null,
                    adminText.Length > 0 ? adminText : "8 秒没回");
                r.Add("管理账号拿到的是业务答复,不是「该功能已关闭」",
                    adminText.Length > 0 && !adminText.StartsWith("err|该功能已关闭", StringComparison.Ordinal), adminText);
                r.Note("半加载管理员 —— 玩家:" + playerText + " / LAdmin:" + adminText);
            }
            else
            {
                r.Note("半加载管理员 —— 玩家:" + playerText + " / LAdmin 登录失败:" + (admin.LastAck ?? ""));
            }
        }
        catch (Exception ex)
        {
            r.Add("半加载管理员的报文往返", false, ex.Message);
        }
    }

    // ---------- ⑦ 半加载:注册 ----------

    /// <summary><c>modules=az.register:half</c>:602 回 err,但 <c>user reg</c> 命令能跑。</summary>
    private static async Task HalfRegisterAsync(SuiteResult r, string ws, string debugMs)
    {
        string dir;
        Svc ms;
        try
        {
            dir = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "modules-half-reg"));
            var ini = Ini(PortHalfReg, "CSharp-半加载注册服");
            ini["modules"] = "az.register:half";
            ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, ini);
        }
        catch (Exception ex)
        {
            r.Add("准备半加载注册环境", false, ex.Message);
            return;
        }

        using var _ = ms;
        var up = ms.WaitPort(PortHalfReg);
        r.Add("az.register:half 时服务端照常起来", up, up ? "" : "20 秒没监听 " + PortHalfReg);
        if (!up)
        {
            r.Note(ms.Out);
            return;
        }

        var half = WaitFor(() => ms.Out.Contains("已半加载 账号注册(az.register)", StringComparison.Ordinal), 8000);
        r.Add("启动日志说注册是半加载", half, FirstLine(ms.Out, "已半加载"));

        try
        {
            using var probe = await Probe.ConnectAsync("127.0.0.1", PortHalfReg, "OS");
            var config = await probe.WaitAsync(Protocol.ServerConfig, 4000);
            var halfLine = KeyLine(config, "half");
            r.Add("半加载注册时 202 的 half 是 az.register", halfLine == "az.register", "half=" + halfLine);

            await probe.SendAsync(2, Protocol.AccountRegister, Encoding.UTF8.GetBytes("halfuser|hash|machine"));
            var ack = await probe.TakeAsync(Protocol.AccountRegisterAck, 8000);
            var packetText = ack ?? "";
            r.Add("半加载的注册模块收到 6002 回 6003", ack is not null, packetText.Length > 0 ? packetText : "8 秒没回");
            r.Add("603 是 err| 且点名账号注册关闭",
                packetText.StartsWith("err|该功能已关闭:账号注册", StringComparison.Ordinal), packetText);

            // 命令能跑:缺参数时给用法提示即算跑到了(不会真的建账号)
            ms.SendCmd("user reg");
            var cmdOk = WaitFor(() => ms.Out.Contains("[账号] 用法:user reg", StringComparison.Ordinal), 6000);
            var cmdText = FirstLine(ms.Out, "[账号] 用法:user reg");
            r.Add("半加载时 user reg 命令照常执行", cmdOk, cmdText);
            r.Add("[对照] user reg 没被模块门禁拦下(半加载放行命令)",
                !ms.Out.Contains("[模块] 该功能已关闭:账号注册", StringComparison.Ordinal),
                FirstLine(ms.Out, "[模块] 该功能已关闭"));
            r.Note("半加载注册下 602 的应答:" + packetText + ";命令应答:" + cmdText);
        }
        catch (Exception ex)
        {
            r.Add("半加载注册的报文往返", false, ex.Message);
        }
    }

    // ---------- ⑧ 完全关闭 vs 半加载:同样回 err,命令一个被拒一个能跑 ----------

    /// <summary>
    /// <c>modules=msg,wd.bind:half,as.whitelist:half</c>:混写能解析成 off={msg} / half={wd.bind,as.whitelist};
    /// 同样的 505 都回 err,但 <c>msg notice</c> 被门禁拒、<c>bind list</c> 能跑,证明三态真的不同。
    /// </summary>
    private static async Task OffVsHalfAsync(SuiteResult r, string ws, string debugMs)
    {
        string dir;
        Svc ms;
        try
        {
            dir = Svc.PrepareBin(debugMs, Path.Combine(ws, "_zxt", "modules-off-vs-half"));
            var cfg = Ini(PortOffVsHalf, "CSharp-三态对照服");
            cfg["modules"] = "wd.bind:half,msg,as.whitelist:half";
            ms = Svc.Start(Path.Combine(dir, "ZincXManagerServer.exe"), dir, cfg);
        }
        catch (Exception ex)
        {
            r.Add("准备三态对照环境", false, ex.Message);
            return;
        }

        using var _ = ms;
        var up = ms.WaitPort(PortOffVsHalf);
        r.Add("混写三态时服务端照常起来", up, up ? "" : "20 秒没监听 " + PortOffVsHalf);
        if (!up)
        {
            r.Note(ms.Out);
            return;
        }

        await Task.Delay(500);

        // 混写要能解析成 off={msg}、half={wd.bind,as.whitelist},并且 Encode 回读一致
        var ini = Path.Combine(dir, "config.ini");
        var modules = WaitFor(() => ReadIni(ini, "modules").Length > 0, 6000) ? ReadIni(ini, "modules") : "";
        r.Add("混写名单按全集顺序写回(off 在前、half 带后缀)",
            modules.Contains("wd.bind:half", StringComparison.Ordinal)
            && modules.Contains("as.whitelist:half", StringComparison.Ordinal)
            && modules.Contains("msg", StringComparison.Ordinal)
            && !modules.Contains("msg:half", StringComparison.Ordinal),
            "modules=" + modules);

        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var halfSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ModuleModes.Parse(modules, off, halfSet);
        r.Add("Parse 得到 off={msg}", off.Count == 1 && off.Contains("msg"), "off=" + string.Join(",", off));
        r.Add("Parse 得到 half={wd.bind,as.whitelist}",
            halfSet.Count == 2 && halfSet.Contains("wd.bind") && halfSet.Contains("as.whitelist"),
            "half=" + string.Join(",", halfSet));

        // 盘上留的是我们写进去的原始顺序,Encode 出来的是全集顺序 —— 比的是内容不是顺序
        var encoded = ModuleModes.Encode(off, halfSet);
        var offBack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var halfBack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ModuleModes.Parse(encoded, offBack, halfBack);
        r.Add("Parse→Encode→Parse 内容一致(顺序按全集重排)",
            encoded == "wd.bind:half,as.whitelist:half,msg"
            && offBack.SetEquals(off) && halfBack.SetEquals(halfSet),
            "Encode=" + encoded + " / 盘上=" + modules);

        try
        {
            using var probe = await Probe.ConnectAsync("127.0.0.1", PortOffVsHalf, "OS");
            var config = await probe.WaitAsync(Protocol.ServerConfig, 4000);
            r.Add("202 的 modules 只有完全关闭的 msg", KeyLine(config, "modules") == "msg", "modules=" + KeyLine(config, "modules"));
            r.Add("202 的 half 是两个半加载模块",
                KeyLine(config, "half") == "wd.bind,as.whitelist", "half=" + KeyLine(config, "half"));

            // 5005(消息历史请求)在两种状态下都回 err —— 但下面命令层的表现不同
            await probe.SendAsync(2, Protocol.MessageHistoryReq, Encoding.UTF8.GetBytes("0|20"));
            var reply = await probe.TakeAsync(Protocol.MessageHistoryDone, 8000);
            var packetText = reply ?? "";
            r.Add("完全关闭的 msg 收到 5005 回 5006", reply is not null, packetText.Length > 0 ? packetText : "8 秒没回");
            r.Add("506 是 err| 且点名消息与公告关闭",
                packetText.StartsWith("err|该功能已关闭:消息与公告", StringComparison.Ordinal), packetText);
        }
        catch (Exception ex)
        {
            r.Add("三态对照的报文往返", false, ex.Message);
        }

        // 命令层:完全关闭的被门禁拒,半加载的照常跑
        var before = ms.Out.Length;
        ms.SendCmd("msg notice text 三态对照 这条应该被门禁拒");
        var msgBlocked = WaitFor(() => ms.Out.Length > before && ms.Out[before..].Contains("该功能已关闭:消息与公告", StringComparison.Ordinal), 6000);
        var msgSlice = msgBlocked ? ms.Out[before..] : ms.Out;
        var msgCmdText = FirstLine(msgSlice, "该功能已关闭:消息与公告");
        r.Add("[对照] 完全关闭的 msg notice 命令被门禁拒", msgBlocked, msgCmdText);
        r.Add("[对照] 被拒的理由是模块关闭,不是用法错误",
            msgBlocked && !msgSlice.Contains("[消息] 用法", StringComparison.Ordinal), FirstLine(msgSlice, "[消息]"));

        var beforeBind = ms.Out.Length;
        ms.SendCmd("bind list");
        var bindOk = WaitFor(() => ms.Out.Length > beforeBind && ms.Out[beforeBind..].Contains("[绑定] 无绑定信息", StringComparison.Ordinal), 6000);
        var bindSlice = bindOk ? ms.Out[beforeBind..] : ms.Out;
        var bindCmdText = FirstLine(bindSlice, "[绑定] 无绑定信息");
        r.Add("[对照] 半加载的 bind list 命令照常执行", bindOk, bindCmdText);

        r.Note("三态对照 —— msg notice 命令:" + msgCmdText + " / bind list 命令:" + bindCmdText);
    }

    // ---------- ⑨ ModuleModes.Parse 纯函数 ----------

    /// <summary>纯函数单测:不起进程,直接喂字符串看解析结果。</summary>
    private static void ParseUnitTests(SuiteResult r)
    {
        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var half = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 空串:两边都空
        ModuleModes.Parse("", off, half);
        r.Add("Parse 空串 → 两边都空", off.Count == 0 && half.Count == 0,
            "off=" + off.Count + " half=" + half.Count);

        // none:同样两边都空
        ModuleModes.Parse("none", off, half);
        r.Add("Parse none → 两边都空", off.Count == 0 && half.Count == 0,
            "off=" + off.Count + " half=" + half.Count);

        // 单独一个带后缀的
        ModuleModes.Parse("a:half", off, half);
        r.Add("Parse a:half → half={a}", half.Count == 1 && half.Contains("a") && off.Count == 0,
            "off=" + string.Join(",", off) + " half=" + string.Join(",", half));

        // 同一个编号写了两次:后者(不带后缀)赢
        ModuleModes.Parse("a:half,a", off, half);
        r.Add("Parse a:half,a → off 赢(off={a},half 空)",
            off.Count == 1 && off.Contains("a") && half.Count == 0,
            "off=" + string.Join(",", off) + " half=" + string.Join(",", half));

        // 反序也一样:先 a 再 a:half,不能把已关闭的降级成半加载
        ModuleModes.Parse("a,a:half", off, half);
        r.Add("Parse a,a:half → 仍是 off={a}(关了不会再降级)",
            off.Count == 1 && off.Contains("a") && half.Count == 0,
            "off=" + string.Join(",", off) + " half=" + string.Join(",", half));

        // 混分隔符
        ModuleModes.Parse("a;b|c", off, half);
        r.Add("Parse a;b|c → off={a,b,c}", off.Count == 3 && off.Contains("a") && off.Contains("b") && off.Contains("c"),
            "off=" + string.Join(",", off));

        // 只有后缀没有编号:丢弃
        ModuleModes.Parse(" :half", off, half);
        r.Add("Parse 「 :half」 → 丢弃(没编号)", off.Count == 0 && half.Count == 0,
            "off=" + off.Count + " half=" + half.Count);

        // Encode:空进空出
        r.Add("Encode 两空 → none", ModuleModes.Encode(new HashSet<string>(), new HashSet<string>()) == "none",
            ModuleModes.Encode(new HashSet<string>(), new HashSet<string>()));

        // Parsed 之后 Encode 回读一致(按全集顺序)
        ModuleModes.Parse("msg,wd.bind:half", off, half);
        var encoded = ModuleModes.Encode(off, half);
        var off2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var half2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ModuleModes.Parse(encoded, off2, half2);
        r.Add("Parse→Encode→Parse 幂等", encoded == "wd.bind:half,msg" && off2.SetEquals(off) && half2.SetEquals(half),
            "encoded=" + encoded);
    }

    // ---------- 小工具 ----------

    /// <summary>从 202 这种「一行一个 key=value」的负载里取值(没有就返回空串)。</summary>
    private static string KeyLine(string? payload, string key)
    {
        if (payload is null) { return ""; }
        foreach (var line in payload.Split('\n'))
        {
            var t = line.Trim();
            var i = t.IndexOf('=');
            if (i <= 0) { continue; }
            if (t[..i].Trim() == key) { return t[(i + 1)..].Trim(); }
        }
        return "";
    }

    /// <summary>一份最小可启动的 config.ini(connectOS/linkname 关掉,连外网那些不走)。</summary>
    private static Dictionary<string, string> Ini(int port, string name) => new()
    {
        ["configver"] = "2",
        ["connectOS"] = "0",
        ["linkname"] = "0",
        ["name"] = name,
        ["czport"] = port.ToString(),
    };

    /// <summary>清单里“服务端有实现”的行数(带接管信息的那几条也算)。</summary>
    private static int CountModuleRows(string text)
    {
        var n = 0;
        foreach (var line in text.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (!t.StartsWith("  ", StringComparison.Ordinal) || t.StartsWith("      ", StringComparison.Ordinal)) { continue; }
            if (t.Contains("服务端有实现", StringComparison.Ordinal) || t.Contains("接管 ", StringComparison.Ordinal)
                || t.Contains("仅客户端入口", StringComparison.Ordinal) || t.Contains("锁死,不能关闭", StringComparison.Ordinal))
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>找编号对应的那一行(同名行有多条时取最后一条,便于看关完之后的清单)。</summary>
    private static string LastLineOf(string text, string id)
    {
        var hit = "";
        foreach (var line in text.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.StartsWith("  " + id, StringComparison.Ordinal)) { hit = t; }
        }
        return hit;
    }

    /// <summary>输出里第一条含这段字的那一行(失败详情用)。</summary>
    private static string FirstLine(string text, string contains)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.Contains(contains, StringComparison.Ordinal)) { return t.Trim(); }
        }
        return "没找到含「" + contains + "」的行";
    }

    /// <summary>轮询等一个条件成立(控制台输出与写盘都不是即时的)。</summary>
    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (condition()) { return true; }
            System.Threading.Thread.Sleep(120);
        }
        return condition();
    }

    /// <summary>读 config.ini 里一个键的原值(没有就返回空串)。</summary>
    /// <remarks>服务端随时可能写这个文件,必须 FileShare.ReadWrite 打开,否则会撞锁。</remarks>
    private static string ReadIni(string file, string key)
    {
        if (!File.Exists(file)) { return ""; }
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                var i = line.IndexOf('=');
                if (i <= 0) { continue; }
                if (line[..i].Trim() == key) { return line[(i + 1)..].Trim(); }
            }
        }
        catch (IOException)
        {
            //服务端正写在中途,让调用方下一轮再读
        }
        return "";
    }
}
