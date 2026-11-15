using System.Buffers.Binary;        // 小端读写(假 RCON 的长度 / 编号 / 类型)
using System.Diagnostics;            // Stopwatch
using System.Net;                    // IPAddress
using System.Net.Sockets;            // TcpListener / NetworkStream
using System.Reflection;             // Assembly / BindingFlags / MethodInfo
using System.Text;                   // Encoding
using ZincXManagerShared.Logging;    // CommandLog
using ZincXManagerShared.Network;    // RconClient / RconAuthException

// BCL 的 TcpClient 与 ZincXManagerShared.Network.TcpClient 同名,本文件只用 BCL 的,统一走别名
using SocketClient = System.Net.Sockets.TcpClient;

namespace ZincXManagerTests;

/// <summary>
/// 套件 29:控制台。
/// 连接表 <c>ZxmmRegistry</c> 的解析 / 查询、RCON 客户端 <c>RconClient</c> 的真 TCP 往返、
/// 控制台选路 <c>ConsoleRouter</c> 的三条兜底、命令日志 <c>CommandLog</c> 的落盘 / 截断 / 脱敏。
///
/// <para>测试项目只引用了 Shared,服务端程序集既没被引用、<c>ZxmmRegistry</c> 还是 internal,
/// 所以连接表与选路两段用反射加载服务端编译输出里的 ZincXManagerServer.dll 直接调真实代码,
/// 不加 ProjectReference、也不动服务端源码。</para>
/// </summary>
public static class ConsoleSuite
{
    private static Assembly? _serverAssembly;   //已加载的服务端程序集(只加载一次)
    private static object? _entryForAttach;     //Attach 交给 ConsoleRouter 的那台服(null = 查不到)

    /// <summary>跑整套断言。</summary>
    public static async Task RunAsync(List<SuiteResult> results)
    {
        var r = new SuiteResult { Name = "29 控制台:连接表 / RCON 客户端 / 选路 / 命令日志" };
        results.Add(r);

        var root = Path.Combine(Path.GetTempPath(), "zxm-console-suite", Guid.NewGuid().ToString("N"));   //本套件独占的临时区
        var origin = Directory.GetCurrentDirectory();   //跑的过程中要切工作目录,收尾必须切回来

        try
        {
            Directory.CreateDirectory(root);

            // 命令日志先指到临时目录:后面无论谁写日志都不许落进仓库
            CommandLog.Initialize(Path.Combine(root, "cmdlog"));

            try { RegistrySection(r, root); }
            catch (Exception ex) { r.Add("连接表段跑完(没有中途抛异常)", false, ex.GetType().Name + ": " + ex.Message); }

            try { await RconSectionAsync(r); }
            catch (Exception ex) { r.Add("RCON 段跑完(没有中途抛异常)", false, ex.GetType().Name + ": " + ex.Message); }

            try { await RouterSectionAsync(r, root); }
            catch (Exception ex) { r.Add("选路段跑完(没有中途抛异常)", false, ex.GetType().Name + ": " + ex.Message); }

            try { LogSection(r); }
            catch (Exception ex) { r.Add("命令日志段跑完(没有中途抛异常)", false, ex.GetType().Name + ": " + ex.Message); }
        }
        catch (Exception ex)
        {
            r.Fatal = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            Directory.SetCurrentDirectory(origin);
            TryDelete(root);
        }
    }

    // ================= A. 连接表 ZxmmRegistry =================

    /// <summary>连接表:9 段行 / 5 段行默认值 / 旧表迁移 / 7 种坏行 / Find / RconAddress。</summary>
    private static void RegistrySection(SuiteResult r, string root)
    {
        var (assembly, reason) = LoadServerAssembly();
        r.Add("能加载服务端程序集 ZincXManagerServer.dll", assembly is not null, reason);
        if (assembly is null)
        {
            return;
        }

        var entryType = assembly.GetType("ZincXManagerServer.Zxms.ZxmmEntry");       //一台服的连接项
        var registryType = assembly.GetType("ZincXManagerServer.Zxms.ZxmmRegistry"); //连接表(internal static)
        r.Add("服务端程序集里有 ZxmmEntry / ZxmmRegistry", entryType is not null && registryType is not null);
        if (entryType is null || registryType is null)
        {
            return;
        }

        var load = registryType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!;   //读 Data/ZXMMC.dat(相对工作目录)
        var find = registryType.GetMethod("Find", BindingFlags.Public | BindingFlags.Static)!;   //按编号或名称查

        // ---------- ① 9 段行:全字段都要对上 ----------
        var full = LoadTable(registryType, load, root, "full",
            "1|一服主城|10.0.0.5|21200|tok-abc|1|10.0.0.9|25575|rconpass");
        r.Add("9 段行解析出 1 台", full.Count == 1, "实际 " + full.Count + " 台");
        var one = full.Count > 0 ? full[0] : null;
        r.Add("9 段行:编号 1 / 名称 一服主城 / 主机 10.0.0.5 / 端口 21200",
            Int(one, "Id") == 1 && Str(one, "Name") == "一服主城" && Str(one, "Host") == "10.0.0.5" && Int(one, "Port") == 21200,
            Describe(one));
        r.Add("9 段行:令牌 tok-abc / 模式 1 / RCON 主机 10.0.0.9 / RCON 端口 25575",
            Str(one, "Token") == "tok-abc" && Int(one, "ConsoleMode") == 1 && Str(one, "RconHost") == "10.0.0.9" && Int(one, "RconPort") == 25575,
            Describe(one));
        r.Add("9 段行:RCON 密码按第九段", Str(one, "RconPassword") == "rconpass",
            Str(one, "RconPassword") == "rconpass" ? "" : "密码段没解出来");

        // ---------- ② 只写 5 段:后 4 段取默认值 ----------
        var five = LoadTable(registryType, load, root, "five", "2|二服|10.0.0.6|21201|tok2");
        r.Add("5 段行解析出 1 台", five.Count == 1, "实际 " + five.Count + " 台");
        var two = five.Count > 0 ? five[0] : null;
        r.Add("5 段行:主机 / 端口 / 令牌按前三段与第五段",
            Str(two, "Host") == "10.0.0.6" && Int(two, "Port") == 21201 && Str(two, "Token") == "tok2", Describe(two));
        r.Add("5 段行:控制台模式默认 0(ZXMM 桥接)", Int(two, "ConsoleMode") == 0, Describe(two));
        r.Add("5 段行:RCON 主机默认空串(与 Host 同机)", Str(two, "RconHost") == "", Describe(two));
        r.Add("5 段行:RCON 端口默认 25575 / RCON 密码默认空串",
            Int(two, "RconPort") == 25575 && Str(two, "RconPassword") == "", Describe(two));

        // ---------- ②b 旧表 Servers.txt 迁移:主机由 Load 的默认值补 127.0.0.1 ----------
        var legacyDir = Path.Combine(root, "legacy");
        Directory.CreateDirectory(Path.Combine(legacyDir, "Data"));
        File.WriteAllLines(Path.Combine(legacyDir, "Data", "Servers.txt"),
            new[] { "3|三服|21202|tok3" }, new UTF8Encoding(false));
        var legacy = LoadIn(legacyDir, registryType, load);
        r.Add("旧表迁移后解析出 1 台", legacy.Count == 1, "实际 " + legacy.Count + " 台");
        var three = legacy.Count > 0 ? legacy[0] : null;
        r.Add("旧表迁移:主机补 127.0.0.1", Str(three, "Host") == "127.0.0.1", Describe(three));
        r.Add("旧表迁移:端口 / 令牌按旧表 4 段", Int(three, "Port") == 21202 && Str(three, "Token") == "tok3", Describe(three));
        r.Add("旧表迁移:控制台模式 0、RCON 端口 25575",
            Int(three, "ConsoleMode") == 0 && Int(three, "RconPort") == 25575, Describe(three));
        r.Add("旧表迁移:生成 ZXMMC.dat,旧表改名 .bak",
            File.Exists(Path.Combine(legacyDir, "Data", "ZXMMC.dat")) &&
            File.Exists(Path.Combine(legacyDir, "Data", "Servers.txt.bak")));

        // ---------- ③ 7 种坏行全跳过,好行照常 ----------
        var mixed = LoadTable(registryType, load, root, "mixed",
            "# 注释行不算坏行",
            "这不是一行连接",
            "0|零服|127.0.0.1|21200|t",
            "3||127.0.0.1|21200|t",
            "4|四服|127.0.0.1|70000|t",
            "5|五服|127.0.0.1|21200|t|2",
            "6|六服|127.0.0.1|21200|t|0||0",
            "7|七服甲|127.0.0.1|21207|t",
            "7|七服乙|127.0.0.1|21208|t",
            "9|好服|127.0.0.1|21209|tok9|1|127.0.0.2|25580|pw9",
            "10|MainSrv|127.0.0.1|21210|t");
        r.Add("坏行全跳过:只剩 3 台(七服甲 / 好服 / MainSrv)", mixed.Count == 3,
            "实际 " + mixed.Count + " 台:" + string.Join(" / ", mixed.Select(m => Str(m, "Name"))));
        r.Add("编号重复只留第一条(七服甲,端口 21207),重复行没覆盖",
            mixed.Count > 0 && Int(mixed[0], "Id") == 7 && Str(mixed[0], "Name") == "七服甲" && Int(mixed[0], "Port") == 21207,
            Describe(mixed.Count > 0 ? mixed[0] : null));
        r.Add("坏行之后的好行(编号 9)全字段照常解析",
            mixed.Count > 1 && Int(mixed[1], "Id") == 9 && strEqual(mixed[1], "Name", "好服") &&
            Int(mixed[1], "ConsoleMode") == 1 && Str(mixed[1], "RconHost") == "127.0.0.2" && Int(mixed[1], "RconPort") == 25580,
            Describe(mixed.Count > 1 ? mixed[1] : null));

        // ---------- ④ Find:编号 / 名称 / 大小写不敏感 / 找不到 ----------
        var byId = find.Invoke(null, new object?[] { "9" });
        r.Add("Find 按编号查(9 → 好服)", byId is not null && Int(byId, "Id") == 9, Describe(byId));
        var byName = find.Invoke(null, new object?[] { "好服" });
        r.Add("Find 按名称查(好服)", byName is not null && Int(byName, "Id") == 9, Describe(byName));
        var byCase = find.Invoke(null, new object?[] { "mainsrv" });
        r.Add("Find 名称大小写不敏感(mainsrv → MainSrv)", byCase is not null && Int(byCase, "Id") == 10, Describe(byCase));
        var noId = find.Invoke(null, new object?[] { "999" });
        r.Add("Find 编号查不到返回 null", noId is null, noId is null ? "" : Describe(noId));
        var noName = find.Invoke(null, new object?[] { "没这台" });
        r.Add("Find 名称查不到返回 null", noName is null, noName is null ? "" : Describe(noName));
        var blank = find.Invoke(null, new object?[] { "   " });
        r.Add("Find 空关键字返回 null", blank is null, blank is null ? "" : Describe(blank));

        // ---------- ⑤ RconAddress:RCON 主机留空时回落 Host ----------
        r.Add("RconAddress:RCON 主机填了就用自己(10.0.0.9:25575)",
            Str(one, "RconAddress") == "10.0.0.9:25575", "实际 " + Str(one, "RconAddress"));
        r.Add("RconAddress:RCON 主机空串时回落 Host(10.0.0.6:25575)",
            Str(two, "RconAddress") == "10.0.0.6:25575", "实际 " + Str(two, "RconAddress"));
    }

    // ================= B. RCON 客户端 RconClient(真 TCP) =================

    /// <summary>RCON 客户端:认证成功 / 认证失败 / 执行命令 / 不回包时取消 / 连不上。</summary>
    private static async Task RconSectionAsync(SuiteResult r)
    {
        // ---------- ⑥ 密码对:连上 + 执行命令 ----------
        using (var fake = new FakeRconServer(21501, "secret"))
        {
            var client = new RconClient("127.0.0.1", 21501, "secret");
            var failure = "";
            try { await client.ConnectAsync(); }
            catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }

            r.Add("密码正确:ConnectAsync 不抛", failure.Length == 0, failure);
            r.Add("密码正确:Connected == true", client.Connected);

            var reply = "";
            failure = "";
            try { reply = await client.SendAsync("say hi"); }
            catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }

            r.Add("SendAsync(\"say hi\") 拿到服务端回包原文", reply == "已执行:say hi",
                failure.Length > 0 ? failure : "回了「" + reply + "」");

            await client.DisposeAsync();
            r.Add("DisposeAsync 之后 Connected == false", !client.Connected);
            r.Note("假 RCON 接进来 " + fake.Accepted + " 条连接");
        }

        // ---------- ⑦ 密码不对:服务端回 -1 ----------
        using (var fake = new FakeRconServer(21502, "secret", rejectAuth: true))
        {
            var client = new RconClient("127.0.0.1", 21502, "wrong");
            Exception? caught = null;
            try { await client.ConnectAsync(); }
            catch (Exception ex) { caught = ex; }

            r.Add("密码不对抛 RconAuthException", caught is RconAuthException, caught is null ? "没抛" : caught.GetType().Name);
            r.Add("异常消息里点明「密码」", caught?.Message.Contains("密码", StringComparison.Ordinal) == true, caught?.Message ?? "");
            r.Add("认证失败后 Connected == false", !client.Connected);
            await client.DisposeAsync();
        }

        // ---------- ⑧ 认证过了但服务端不回命令:调用方取消,不能等满 5 秒 ----------
        using (var fake = new FakeRconServer(21503, "secret", answerCommand: false))
        {
            var client = new RconClient("127.0.0.1", 21503, "secret");
            await client.ConnectAsync();

            using var cts = new CancellationTokenSource(300);   // 300ms 就撤,别真等 RconClient 的 5 秒
            var watch = Stopwatch.StartNew();
            Exception? caught = null;
            try { await client.SendAsync("say hi", cts.Token); }
            catch (Exception ex) { caught = ex; }
            watch.Stop();

            r.Add("服务端不回包:调用方 300ms 取消抛 OperationCanceledException", caught is OperationCanceledException,
                caught is null ? "没抛(真等了 5 秒?)" : caught.GetType().Name);
            r.Add("取消路径没有等满 5 秒", watch.ElapsedMilliseconds < 3000, watch.ElapsedMilliseconds + " ms");
            await client.DisposeAsync();
        }

        // ---------- ⑨ 连没有监听的端口:立刻失败,不能静默卡住 ----------
        var dead = new RconClient("127.0.0.1", 21519, "x");
        var watchDead = Stopwatch.StartNew();
        Exception? refused = null;
        try { await dead.ConnectAsync(); }
        catch (Exception ex) { refused = ex; }
        watchDead.Stop();

        r.Add("连没监听的端口抛异常,不是静默卡住", refused is System.Net.Sockets.SocketException,
            refused is null ? "没抛" : refused.GetType().Name + ": " + refused.Message);
        r.Add("连接失败在 3 秒内返回", watchDead.ElapsedMilliseconds < 3000, watchDead.ElapsedMilliseconds + " ms");
        r.Add("连接失败后 Connected == false", !dead.Connected);
        await dead.DisposeAsync();
    }

    // ================= C. 控制台选路 ConsoleRouter =================

    /// <summary>选路:没接线 / 查不到这台服 / 模式 1 但 RCON 连不上,三条都得回 false 且不抛。</summary>
    private static async Task RouterSectionAsync(SuiteResult r, string root)
    {
        var (assembly, reason) = LoadServerAssembly();
        if (assembly is null)
        {
            r.Add("选路段:能加载服务端程序集", false, reason);
            return;
        }

        var entryType = assembly.GetType("ZincXManagerServer.Zxms.ZxmmEntry")!;
        var routerType = assembly.GetType("ZincXManagerServer.Zxms.ConsoleRouter")!;
        var send = routerType.GetMethod("SendAsync", BindingFlags.Public | BindingFlags.Static)!;
        var attach = routerType.GetMethod("Attach", BindingFlags.Public | BindingFlags.Static)!;

        // 兜底:就算命令日志落到工作目录,也落在临时区
        var origin = Directory.GetCurrentDirectory();
        var routerDir = Path.Combine(root, "router");
        Directory.CreateDirectory(routerDir);
        Directory.SetCurrentDirectory(routerDir);
        try
        {
            // ---------- ⑩ 一次都没 Attach ----------
            var (ok0, msg0) = await CallSendAsync(send, 501, "say hi", "tester", 0);
            r.Add("没 Attach 时返回 (false, 没有这个服务器)", !ok0 && msg0 == "没有这个服务器", $"ok={ok0} 原因={msg0}");

            // ---------- ⑪ Attach 了但查不到这台服 ----------
            Attach(attach, entryType, null);
            var (ok1, msg1) = await CallSendAsync(send, 502, "say hi", "tester", 0);
            r.Add("Attach 了但查不到这台服,同样 (false, 没有这个服务器)", !ok1 && msg1 == "没有这个服务器",
                $"ok={ok1} 原因={msg1}");

            // ---------- ⑫ 模式 1 直连 RCON,但那个端口没人监听 ----------
            var entry = Activator.CreateInstance(entryType)!;
            Set(entry, "Id", 61);
            Set(entry, "Name", "直连服");
            Set(entry, "Host", "127.0.0.1");
            Set(entry, "Port", 21200);
            Set(entry, "ConsoleMode", 1);          // RCON 直连
            Set(entry, "RconHost", "127.0.0.1");
            Set(entry, "RconPort", 21519);          // 本套件里没人监听这个端口
            Set(entry, "RconPassword", "x");
            Attach(attach, entryType, entry);

            var (ok2, msg2) = await CallSendAsync(send, 61, "say hi", "tester", 0);
            r.Add("模式 1 且 RCON 没开:返回 false 且原因提到 RCON 连不上",
                !ok2 && msg2.Contains("RCON 连不上", StringComparison.Ordinal) && msg2.Contains("127.0.0.1:21519", StringComparison.Ordinal),
                $"ok={ok2} 原因={msg2}");
        }
        finally
        {
            Directory.SetCurrentDirectory(origin);
        }
    }

    // ================= D. 命令日志 CommandLog =================

    /// <summary>命令日志:落盘 / ok 与 err / 超长截断 / 换行转义 / 参数里没有密码。</summary>
    private static void LogSection(SuiteResult r)
    {
        var path = CommandLog.Path;

        // ---------- ⑬ 一条成功的命令 ----------
        CommandLog.Write("张三", 42, 7, "主城服", "say hi", true, "已广播");
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        r.Add("Write 之后生成 cmd-*.log", File.Exists(path), path);
        r.Add("日志文件名是 cmd-*.log",
            Path.GetFileName(path).StartsWith("cmd-", StringComparison.Ordinal) && path.EndsWith(".log", StringComparison.Ordinal),
            Path.GetFileName(path));
        r.Add("日志含操作人、账号编号、服编号与名称(操作人:张三#42 服:7(主城服))",
            text.Contains("操作人:张三#42 服:7(主城服)", StringComparison.Ordinal));
        r.Add("日志含命令原文(命令:say hi)", text.Contains("命令:say hi", StringComparison.Ordinal));
        r.Add("成功记 ok 并带上结果原文", text.Contains("→ ok", StringComparison.Ordinal) && text.Contains("结果:已广播", StringComparison.Ordinal));

        // ---------- ⑭ 失败也要记 err;没有账号编号时不拼 #0 ----------
        CommandLog.Write("李四", 0, 8, "", "list", false, "服务端没开");
        text = File.ReadAllText(path);
        r.Add("失败记 err,结果原文照记",
            text.Contains("→ err", StringComparison.Ordinal) && text.Contains("结果:服务端没开", StringComparison.Ordinal));
        r.Add("账号编号 0 不拼 #0,服务器名为空只写编号(操作人:李四 服:8)",
            text.Contains("操作人:李四 服:8 ", StringComparison.Ordinal));

        // ---------- ⑮ 结果里的换行换成 ⏎ ----------
        CommandLog.Write("王五", 5, 9, "副服", "list", true, "第一行\n第二行");
        text = File.ReadAllText(path);
        r.Add("结果里的换行换成 ⏎(第一行 ⏎ 第二行)", text.Contains("第一行 ⏎ 第二行", StringComparison.Ordinal));

        // ---------- ⑯ 超长结果截断并标出总字数 / 略过字数 ----------
        CommandLog.Write("赵六", 6, 10, "长服", "help", true, new string('a', 3000));
        text = File.ReadAllText(path);
        r.Add("超长结果标出「共 3000 字」「略过 1200」",
            text.Contains("共 3000 字", StringComparison.Ordinal) && text.Contains("略过 1200", StringComparison.Ordinal));
        r.Add("超长结果只留前 1500 字 + 尾 300 字",
            text.Contains(new string('a', 1500), StringComparison.Ordinal) &&
            !text.Contains(new string('a', 1501), StringComparison.Ordinal));

        // ---------- ⑰ 参数里根本没有密码字段(代码层面) ----------
        var parameters = typeof(CommandLog).GetMethod("Write", BindingFlags.Public | BindingFlags.Static)!.GetParameters();
        r.Add("CommandLog.Write 的参数里没有密码字段",
            !parameters.Any(p => p.Name!.Contains("pass", StringComparison.OrdinalIgnoreCase)
                                 || p.Name!.Contains("pwd", StringComparison.OrdinalIgnoreCase)
                                 || p.Name!.Contains("密码", StringComparison.Ordinal)),
            "参数:" + string.Join(",", parameters.Select(p => p.Name)));
    }

    // ================= 反射:加载服务端程序集 =================

    /// <summary>从仓库里服务端的编译输出加载程序集;测试项目没引用服务端,只能这样拿到真实代码。</summary>
    private static (Assembly? Assembly, string Reason) LoadServerAssembly()
    {
        if (_serverAssembly is not null)
        {
            return (_serverAssembly, "");
        }

        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));   //测试 bin/Debug/net10.0 往上五层 = 仓库根
        var candidates = new[]
        {
            Path.Combine(root, "ZincXManagerServer", "bin", "Debug", "net10.0", "ZincXManagerServer.dll"),
            Path.Combine(root, "ZincXManagerServer", "bin", "Release", "net10.0", "ZincXManagerServer.dll"),
        };

        var errors = new List<string>();
        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                errors.Add(path + " 不存在");
                continue;
            }

            try
            {
                _serverAssembly = Assembly.LoadFrom(path);
                return (_serverAssembly, "");
            }
            catch (FileLoadException ex) { errors.Add(path + " → 加载失败:" + ex.Message); }
            catch (BadImageFormatException ex) { errors.Add(path + " → 不是有效程序集:" + ex.Message); }
            catch (IOException ex) { errors.Add(path + " → 读取失败:" + ex.Message); }
        }

        return (null, string.Join("; ", errors));
    }

    // ================= 反射:连接表读取 =================

    /// <summary>在 root/子目录 下写一张连接表并载入,返回解析出的全部条目。</summary>
    private static List<object> LoadTable(Type registryType, MethodInfo load, string root, string subDir, params string[] lines)
    {
        var dir = Path.Combine(root, subDir);
        Directory.CreateDirectory(Path.Combine(dir, "Data"));
        File.WriteAllLines(Path.Combine(dir, "Data", "ZXMMC.dat"), lines, new UTF8Encoding(false));
        return LoadIn(dir, registryType, load);
    }

    /// <summary>把工作目录切到 <paramref name="dir"/> 载入连接表(Load 用的是相对路径),读完切回来。</summary>
    private static List<object> LoadIn(string dir, Type registryType, MethodInfo load)
    {
        var origin = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(dir);
        try
        {
            load.Invoke(null, new object?[] { "127.0.0.1" });
        }
        finally
        {
            Directory.SetCurrentDirectory(origin);
        }

        var all = registryType.GetProperty("All", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        return ((System.Collections.IEnumerable)all).Cast<object>().ToList();
    }

    /// <summary>读条目上的一个属性(反射)。</summary>
    private static object? Value(object? entry, string name)
        => entry is null ? null : entry.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry);

    private static int Int(object? entry, string name) => Value(entry, name) is int value ? value : -1;

    private static string Str(object? entry, string name) => Value(entry, name) as string ?? "";

    private static bool strEqual(object? entry, string name, string expected) => Str(entry, name) == expected;

    private static void Set(object entry, string name, object value)
        => entry.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.SetValue(entry, value);

    /// <summary>条目各字段的可读文本(密码只报「对 / 不对」,不打明文)。</summary>
    private static string Describe(object? entry)
    {
        if (entry is null)
        {
            return "没有条目";
        }

        return $"编号={Value(entry, "Id")} 名称={Value(entry, "Name")} 主机={Value(entry, "Host")} 端口={Value(entry, "Port")} " +
               $"令牌={Value(entry, "Token")} 模式={Value(entry, "ConsoleMode")} RCON主机={Value(entry, "RconHost")} " +
               $"RCON端口={Value(entry, "RconPort")} RCON密码={(Str(entry, "RconPassword").Length > 0 ? "已填" : "空")} " +
               $"地址={Value(entry, "RconAddress")}";
    }

    // ================= 反射:ConsoleRouter =================

    /// <summary>接线:把"按编号取连接项"换成测试自己的靶子(查不到就返回 null)。</summary>
    private static void Attach(MethodInfo attach, Type entryType, object? entry)
    {
        _entryForAttach = entry;

        var delegateType = typeof(Func<,>).MakeGenericType(typeof(int), entryType);   // Func<int, ZxmmEntry?>
        var target = typeof(ConsoleSuite)
            .GetMethod(nameof(EntryOf), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(entryType);
        var entryOf = Delegate.CreateDelegate(delegateType, target);

        // zxmmOf 传 null:本套件只验"查不到"和"RCON 连不上"两条兜底,不验桥接模式
        attach.Invoke(null, new object?[] { entryOf, null });
    }

    /// <summary>Attach 的靶子:返回当前用例想给的那台服(可能是 null)。</summary>
    private static T? EntryOf<T>(int serverId) where T : class => _entryForAttach as T;

    /// <summary>调 ConsoleRouter.SendAsync 并取出 (Ok, Result);MetaTuple 用反射读,不引用服务端程序集。</summary>
    private static async Task<(bool Ok, string Result)> CallSendAsync(
        MethodInfo send, int serverId, string command, string operatorName, int accountId)
    {
        var task = (Task)send.Invoke(null, new object?[] { serverId, command, operatorName, accountId, CancellationToken.None })!;
        await task.ConfigureAwait(false);

        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var ok = (bool)result.GetType().GetField("Item1")!.GetValue(result)!;
        var text = (string)result.GetType().GetField("Item2")!.GetValue(result)!;
        return (ok, text);
    }

    // ================= 假 RCON 服务端 =================

    /// <summary>
    /// 最小的假 RCON 服务端:只认认证(3)与命令(2)两种包,
    /// 回包按小端 <c>长度(4)|编号(4)|类型(4)|负载|0x00 0x00</c> 拼。
    /// </summary>
    private sealed class FakeRconServer : IDisposable
    {
        private readonly TcpListener _listener;   //只绑 127.0.0.1
        private readonly string _password;        //认证时期望的密码
        private readonly bool _rejectAuth;        //true = 一律回 -1(密码不对)
        private readonly bool _answerCommand;     //false = 认证过但不回命令(测取消路径)
        private readonly Thread _accept;          //接受循环线程
        private int _accepted;                    //接进来过几条连接

        /// <summary>在 127.0.0.1:<paramref name="port"/> 起一个假 RCON。</summary>
        public FakeRconServer(int port, string password, bool rejectAuth = false, bool answerCommand = true)
        {
            _password = password;
            _rejectAuth = rejectAuth;
            _answerCommand = answerCommand;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _accept = new Thread(AcceptLoop) { IsBackground = true };
            _accept.Start();
        }

        /// <summary>接进来过几条连接。</summary>
        public int Accepted => Volatile.Read(ref _accepted);

        private void AcceptLoop()
        {
            while (true)
            {
                SocketClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (System.Net.Sockets.SocketException) { return; }   // listener 已停
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }

                Interlocked.Increment(ref _accepted);
                new Thread(() => Serve(client)) { IsBackground = true }.Start();
            }
        }

        private void Serve(SocketClient client)
        {
            try
            {
                using var owned = client;
                var stream = owned.GetStream();
                while (ReadFrame(stream, out var id, out var type, out var body))
                {
                    if (type == 3)   // 认证
                    {
                        if (_rejectAuth || body != _password)
                        {
                            WriteFrame(stream, -1, 2, "");   // 服务端约定的「密码不对」
                            continue;
                        }

                        WriteFrame(stream, id, 2, "");
                        continue;
                    }

                    if (type == 2 && _answerCommand)   // 命令
                    {
                        WriteFrame(stream, id, 0, "已执行:" + body);
                    }
                }
            }
            catch (IOException) { /* 客户端断开:正常收尾 */ }
            catch (System.Net.Sockets.SocketException) { /* 被打断:正常收尾 */ }
            catch (ObjectDisposedException) { /* 被打断:正常收尾 */ }
        }

        private static bool ReadFrame(NetworkStream stream, out int id, out int type, out string body)
        {
            id = 0;
            type = 0;
            body = "";

            var head = new byte[4];   //长度
            if (!ReadExactly(stream, head))
            {
                return false;
            }

            var size = BinaryPrimitives.ReadInt32LittleEndian(head);
            if (size < 10 || size > 1024 * 1024)
            {
                return false;
            }

            var rest = new byte[size];   //编号 + 类型 + 负载 + 两个空字节
            if (!ReadExactly(stream, rest))
            {
                return false;
            }

            id = BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(0, 4));
            type = BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(4, 4));
            var bodyLength = size - 10;
            body = bodyLength <= 0 ? "" : Encoding.UTF8.GetString(rest, 8, bodyLength);
            return true;
        }

        private static bool ReadExactly(NetworkStream stream, byte[] buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var got = stream.Read(buffer, read, buffer.Length - read);
                if (got <= 0)
                {
                    return false;   // 对端关了
                }

                read += got;
            }

            return true;
        }

        private static void WriteFrame(NetworkStream stream, int id, int type, string body)
        {
            var payload = Encoding.UTF8.GetBytes(body);
            var frame = new byte[14 + payload.Length];   // 4 长度 + 4 编号 + 4 类型 + 负载 + 2 结尾
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), 10 + payload.Length);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), id);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8, 4), type);
            payload.CopyTo(frame, 12);
            stream.Write(frame);
            stream.Flush();
        }

        public void Dispose()
        {
            _listener.Stop();   // 卡在 AcceptTcpClient 的线程会抛 SocketException 退出
            _accept.Join(2000);
        }
    }

    // ================= 收尾 =================

    /// <summary>删临时目录;删不掉也不影响结论(系统临时目录会自己清)。</summary>
    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (IOException) { /* 还有文件被占着 */ }
        catch (UnauthorizedAccessException) { /* 权限不够 */ }
    }
}
