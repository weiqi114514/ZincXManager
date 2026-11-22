using System;  // Exception
using System.Collections.Generic;  // List
using System.IO;  // Path.Combine
using System.Linq;  // Where / Select / Distinct
using System.Reflection;  // FieldInfo / BindingFlags
using System.Threading.Tasks;  // Task
using ZincXManagerShared.Login;  // LoginStore
using ZincXManagerShared.Network;  // Protocol

namespace ZincXManagerTests;

/// <summary>
/// 套件 26:多服数据分离的共享层。
///
/// <para>多服数据分离要跨三端改,这里只测测试项目够得着的部分(测试项目只引用 Shared,
/// 服务端的 <c>Zxms/*.cs</c> 是 internal,另由套件 27 起真进程测):</para>
/// <list type="bullet">
///   <item>四个新协议号的值、以及和既有协议号不撞号(反射枚举所有 ushort 常量);</item>
///   <item><see cref="LoginStore.BindFileOf"/> 的分区路径拼法 —— 空 = 共享区原路径,非空 = <c>Data/&lt;scope&gt;/BindUser.txt</c>;</item>
///   <item><see cref="LoginStore.CurrentScope"/> 的 AsyncLocal 语义:设了能读到、清空回共享区、
///         子任务继承外层值但不能反过来污染外层(别的服的事件就会串数据)。</item>
/// </list>
/// </summary>
public static class DataSeparationSuite
{
    /// <summary>跑一遍,结果追加到 results。</summary>
    /// <param name="results">总表。</param>
    public static async Task RunAsync(List<SuiteResult> results)
    {
        var r = new SuiteResult { Name = "26 多服数据分离(共享层)" };
        results.Add(r);

        try
        {
            ProtocolNumbers(r);
            BindFilePath(r);
            await ScopeIsolationAsync(r).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            r.Fatal = ex.Message;
        }
    }

    /// <summary>四个新协议号的值,以及全表不撞号。</summary>
    private static void ProtocolNumbers(SuiteResult r)
    {
        r.AddTrue("ServerListReq = 6070", Protocol.ServerListReq == 6070, $"实际 {Protocol.ServerListReq}");
        r.AddTrue("ServerList = 6071", Protocol.ServerList == 6071, $"实际 {Protocol.ServerList}");
        r.AddTrue("ServerSwitchReq = 6072", Protocol.ServerSwitchReq == 6072, $"实际 {Protocol.ServerSwitchReq}");
        r.AddTrue("ServerSwitchAck = 6073", Protocol.ServerSwitchAck == 6073, $"实际 {Protocol.ServerSwitchAck}");

        // 协议号撞了不会报错,只会让两边的 case 抢同一条包 —— 只能靠反射把整张表翻一遍
        var fields = typeof(Protocol)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(ushort))
            .Where(f => !f.Name.EndsWith("From", StringComparison.Ordinal))   //BusinessFrom 这类号段标记描述范围,和段内第一个包同号是设计如此
            .ToList();

        var values = fields.Select(f => (ushort)f.GetRawConstantValue()!).ToList();
        var duplicated = values.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key.ToString()).ToList();

        r.AddTrue("Protocol 的 ushort 常量没有重复值", duplicated.Count == 0,
            duplicated.Count == 0 ? $"{fields.Count} 个常量" : "重复:" + string.Join(",", duplicated));
    }

    /// <summary>分区路径:空 = 共享区,<c>SS-2</c> = <c>Data/SS-2/BindUser.txt</c>。</summary>
    private static void BindFilePath(SuiteResult r)
    {
        r.AddTrue("共享区路径就是 BindFile 常量", LoginStore.BindFileOf("") == LoginStore.BindFile,
            LoginStore.BindFileOf(""));

        var expected = Path.Combine("Data", "SS-2", "BindUser.txt");   //分区文件
        r.AddTrue("分区路径拼成 Data/SS-2/BindUser.txt", LoginStore.BindFileOf("SS-2") == expected,
            LoginStore.BindFileOf("SS-2"));

        // 两台服必须是两个不同路径,否则"分离"等于没做
        r.AddTrue("不同分区路径不同", LoginStore.BindFileOf("SS-1") != LoginStore.BindFileOf("SS-2"));
    }

    /// <summary>CurrentScope 的 AsyncLocal 行为:继承给子任务,但不从子任务漏回外层。</summary>
    private static async Task ScopeIsolationAsync(SuiteResult r)
    {
        var depth = LoginStore.CurrentScope.Value;   //进来时的值,测完要还原
        try
        {
            LoginStore.CurrentScope.Value = "SS-7";
            r.AddTrue("设了分区能立刻读到", LoginStore.CurrentScope.Value == "SS-7");

            string? seenInChild = null;   //子任务里看到的分区
            await Task.Run(() => seenInChild = LoginStore.CurrentScope.Value).ConfigureAwait(false);
            r.AddTrue("子任务继承外层分区", seenInChild == "SS-7", $"子任务看到 {seenInChild ?? "null"}");

            await Task.Run(() => LoginStore.CurrentScope.Value = "SS-8").ConfigureAwait(false);
            r.AddTrue("子任务改分区不影响外层", LoginStore.CurrentScope.Value == "SS-7",
                $"外层还是 {LoginStore.CurrentScope.Value ?? "null"}");

            LoginStore.CurrentScope.Value = "";
            r.AddTrue("清空后回到共享区", string.IsNullOrEmpty(LoginStore.CurrentScope.Value));
        }
        finally
        {
            LoginStore.CurrentScope.Value = depth;   //后面的套件不该带着 SS-7 跑
        }
    }
}
