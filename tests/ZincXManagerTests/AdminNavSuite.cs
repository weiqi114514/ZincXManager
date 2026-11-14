using System;  // Exception
using System.Collections.Generic;  // List<SuiteResult>
using System.Threading.Tasks;  // Task
using ZincXManagerShared.Network;  // Protocol.IsManagementLevel

namespace ZincXManagerTests;

/// <summary>
/// 套件 25:管理员界面的等级判定。
///
/// <para>ZXMC 登录成功后把账号等级写进 config 的 level,主界面构造时用它决定
/// 「左侧导航整栏换成管理端 4 个大类」还是「玩家那几页」。这条判断是纯函数
/// <see cref="Protocol.IsManagementLevel"/>,不起进程就能测。</para>
/// </summary>
public static class AdminNavSuite
{
    /// <summary>跑一遍,结果追加到 results。</summary>
    /// <param name="results">总表。</param>
    public static Task RunAsync(List<SuiteResult> results)
    {
        var r = new SuiteResult { Name = "25 管理员界面(等级判定)" };
        results.Add(r);

        try
        {
            SubServerLevels(r);
            OfficialLevels(r);
            NotAdmin(r);
        }
        catch (Exception ex)
        {
            r.Fatal = ex.Message;
        }

        return Task.CompletedTask;
    }

    /// <summary>子服等级(LAdmin / HAdmin / System)都算管理账号。</summary>
    private static void SubServerLevels(SuiteResult r)
    {
        r.AddTrue("LAdmin 算管理账号", Protocol.IsManagementLevel("LAdmin"));
        r.AddTrue("HAdmin 算管理账号", Protocol.IsManagementLevel("HAdmin"));
        r.AddTrue("System 算管理账号", Protocol.IsManagementLevel("System"));
    }

    /// <summary>官方端登录的等级(Admin / System)同样算 —— 管理员也可能从 ZXMOS 那侧进来。</summary>
    private static void OfficialLevels(SuiteResult r)
    {
        r.AddTrue("Admin 算管理账号(官方端等级)", Protocol.IsManagementLevel("Admin"));

        // 两套等级表第 0 个都是 Player,实现靠下标 > 0 判非玩家,顺序写反就会把 Player 当成管理员
        r.AddTrue("等级表第 0 项是 Player", Protocol.Levels[0] == "Player");
        r.AddTrue("官方等级表第 0 项是 Player", Protocol.OSLevels[0] == "Player");
    }

    /// <summary>Player / 空 / 占位符 / 拼错的等级都不该放行。</summary>
    private static void NotAdmin(SuiteResult r)
    {
        r.AddTrue("Player 不算管理账号", !Protocol.IsManagementLevel("Player"));
        r.AddTrue("空串不算管理账号", !Protocol.IsManagementLevel(""));
        r.AddTrue("服务器账号占位符 \"-\" 不算管理账号", !Protocol.IsManagementLevel(Protocol.ServerAccountLevel));
        r.AddTrue("只有空格不算管理账号", !Protocol.IsManagementLevel(" "));
        r.AddTrue("小写的 player 不当管理员(等级区分大小写)", !Protocol.IsManagementLevel("player"));
        r.AddTrue("没见过的等级不放行", !Protocol.IsManagementLevel("SuperAdmin"));
    }
}
