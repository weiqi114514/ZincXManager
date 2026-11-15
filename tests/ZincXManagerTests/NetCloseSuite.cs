using System.Net;  // IPAddress
using System.Net.Sockets;  // TcpClient
using ZincXManagerShared.Network;  // TcpServer / TcpConnection

namespace ZincXManagerTests;

/// <summary>
/// 套件 28:net close 的查找与断开判定。
/// 不起 ZXMS,进程内直接开一个 TcpServer,验证 FindConnections 的三种匹配方式、
/// 在线时断开返回 true、已离线(命令说的「已经不在线了」)返回 false。
/// </summary>
public static class NetCloseSuite
{
    /// <summary>跑整套断言。</summary>
    public static async Task RunAsync(List<SuiteResult> results)
    {
        var r = new SuiteResult { Name = "28 net close:查找 / 断开 / 已离线判定" };
        results.Add(r);

        const int port = 21461;                       // 本套件独占端口
        var server = new TcpServer(IPAddress.Loopback, (ushort)port);
        server.Open();
        try
        {
            // ---------- 在线:查找 + 断开成功 ----------
            using var client1 = new System.Net.Sockets.TcpClient();
            await client1.ConnectAsync(IPAddress.Loopback, port);
            var conn1 = await WaitConnectionAsync(server, 1, 3000);   // 等接入登记完成
            r.Add("客户端连上后连接表有 1 条", conn1 is not null);
            if (conn1 is null)
            {
                return;
            }

            var shortId = conn1.Id.ToString()[..8];                   // 命令里显示的那 8 位
            r.Add("按连接 id 前 8 位能查到", server.FindConnections(shortId).Count == 1);
            r.Add("按 IP 能查到", server.FindConnections("127.0.0.1").Count == 1);
            r.Add("按 IP:端口 能查到",
                server.FindConnections(conn1.RemoteEndPoint?.ToString() ?? "").Count == 1);
            r.Add("查不到的关键字返回空", server.FindConnections("10.9.9.9").Count == 0);
            r.Add("空关键字返回空", server.FindConnections("  ").Count == 0);

            r.Add("在线时断开返回 true(命令打「已断开」)", await server.CloseConnectionAsync(conn1));
            r.Add("断开后连接表清空", await WaitCountAsync(server, 0, 3000));

            // ---------- 已关闭但还没摘表:同样算已离线 ----------
            using var client3 = new System.Net.Sockets.TcpClient();
            await client3.ConnectAsync(IPAddress.Loopback, port);
            var conn3 = await WaitConnectionAsync(server, 1, 3000);
            r.Add("第三条连接已登记", conn3 is not null);
            if (conn3 is null)
            {
                return;
            }

            await conn3.DisposeAsync();    // 直接关掉,模拟对端刚断、收尾还没走完的那一瞬
            r.Add("连接对象已标记关闭", conn3.IsClosed);
            r.Note(server.Connections.Any(c => c.Id == conn3.Id)
                ? "关闭后仍在在线表里,走 IsClosed 分支"
                : "关闭后已被摘表,走查表失败分支");
            r.Add("对已关闭的连接返回 false(命令打「已经不在线了」)",
                await server.CloseConnectionAsync(conn3) == false);
            await WaitCountAsync(server, 0, 3000);

            // ---------- 已离线:断开返回 false ----------
            using var client2 = new System.Net.Sockets.TcpClient();
            await client2.ConnectAsync(IPAddress.Loopback, port);
            var conn2 = await WaitConnectionAsync(server, 1, 3000);
            r.Add("第二条连接已登记", conn2 is not null);
            if (conn2 is null)
            {
                return;
            }

            client2.Close();                                          // 客户端自己断,服务端收尾走 finally
            r.Add("客户端自己断开后连接表清空", await WaitCountAsync(server, 0, 3000));
            r.Add("对已离线的连接返回 false(命令打「已经不在线了」)",
                await server.CloseConnectionAsync(conn2) == false);
        }
        finally
        {
            await server.CloseAsync();
        }
    }

    /// <summary>等连接表出现指定条数,超时返回 null。</summary>
    private static async Task<TcpConnection?> WaitConnectionAsync(TcpServer server, int expected, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var list = server.Connections;
            if (list.Count == expected)
            {
                return list.First();
            }
            await Task.Delay(30);
        }
        return null;
    }

    /// <summary>等连接表变成指定条数;到点还没变返回 false。</summary>
    private static async Task<bool> WaitCountAsync(TcpServer server, int expected, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (server.Connections.Count == expected)
            {
                return true;
            }
            await Task.Delay(30);
        }
        return server.Connections.Count == expected;
    }
}
