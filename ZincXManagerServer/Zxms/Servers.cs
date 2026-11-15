using System.Collections.Generic;            // IReadOnlyList
using ZincXManagerServer.Zxms;               // ZxmmRegistry / ZxmmEntry

namespace ZincXManager.Server.Zxms;

/// <summary>
/// 服务器登记表的兼容外壳。
///
/// <para>登记表已经和 ZXMM 连接表合并成一份 <c>Data/ZXMMC.dat</c>（见 <see cref="ZxmmRegistry"/>），
/// 这个类留着是因为数据分离那边一直按「编号 → 名称」查表，换成新类型要改一大片调用点，
/// 而这里转发一层就够了。新代码直接用 <see cref="ZxmmRegistry"/>，别再往这上面加东西。</para>
/// </summary>
internal static class Servers
{
    /// <summary>登记表路径（合并后就是 ZXMM 连接表）。</summary>
    public const string FileName = ZxmmRegistry.FileName;

    /// <summary>全部已登记的服务器，按文件里的顺序排列；尚未载入时为空。</summary>
    public static IReadOnlyList<ZxmmEntry> All => ZxmmRegistry.All;

    /// <summary>
    /// 按编号查一台服务器。
    /// </summary>
    /// <param name="id">服务器编号。</param>
    /// <param name="entry">查到时为对应登记项，查不到为 null。</param>
    /// <returns>查到返回 true。</returns>
    public static bool TryGet(int id, out ZxmmEntry entry) => ZxmmRegistry.TryGet(id, out entry);

    /// <summary>登记表里的第一台服务器；没有任何登记时为 null。单服部署下它就是唯一那台。</summary>
    public static ZxmmEntry? Default
    {
        get
        {
            var all = ZxmmRegistry.All;
            return all.Count > 0 ? all[0] : null;
        }
    }

    /// <summary>
    /// 表还没载入时补读一次；已经载入就什么都不做。
    /// </summary>
    /// <remarks>
    /// 原有调用点是数据分离的迁移器 —— 它可能在 Program 读完表之前被调到。
    /// 这里不带默认主机（旧表迁移要用，Program 那边会先读完），补读只发生在真的空表时。
    /// </remarks>
    public static void Load()
    {
        if (ZxmmRegistry.All.Count == 0)
        {
            ZxmmRegistry.Load("");
        }
    }
}
