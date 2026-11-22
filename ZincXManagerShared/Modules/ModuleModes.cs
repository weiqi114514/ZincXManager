using System;  // StringComparer / StringSplitOptions
using System.Collections.Generic;  // ISet / ICollection / List

namespace ZincXManagerShared.Modules;

/// <summary>
/// 一个模块的装配强度。
/// </summary>
public enum ModuleMode
{
    /// <summary>正常装配:命令、包、后台线程全在。</summary>
    On = 0,

    /// <summary>
    /// 半加载:客户端发来的包一律拒绝,客户端界面全隐藏,
    /// 但服务端控制台的管理命令照常可用 —— 留给白名单/注册/绑定这类要人工兜底的功能。
    /// </summary>
    Half = 1,

    /// <summary>完全关闭:命令与包都不接管,收到相关包回 err。</summary>
    Off = 2,
}

/// <summary>
/// modules 键的文本格式,服务端与客户端共用同一份解析与拼装。
/// <para>值是逗号分隔的模块编号:带 :half 后缀表示半加载,不带的表示完全关闭。</para>
/// <para>例:modules=wd.bind:half,as.whitelist:half,msg</para>
/// </summary>
public static class ModuleModes
{
    /// <summary>配置里存模块名单的键名。</summary>
    public const string KeyModules = "modules";

    /// <summary>modules 写这个值时表示「一个都不关、也不半加载」。</summary>
    public const string NoneToken = "none";

    /// <summary>半加载后缀。</summary>
    public const string HalfSuffix = ":half";

    /// <summary>切分用的分隔符:两端都按这套切,避免一边认一边不认。</summary>
    private static readonly char[] _separators = [',', ';', '|', ' ', '\t', '\r', '\n'];

    /// <summary>
    /// 把 modules 的值拆成「完全关闭」与「半加载」两份名单。
    /// 同一个编号出现两次时以完全关闭为准(更严的那一头赢)。
    /// </summary>
    /// <param name="raw">配置里的原始值,可以带 <see cref="NoneToken"/> 或 :half 后缀。</param>
    /// <param name="off">收「完全关闭」的编号。</param>
    /// <param name="half">收「半加载」的编号。</param>
    public static void Parse(string raw, ISet<string> off, ISet<string> half)
    {
        off.Clear();
        half.Clear();

        if (raw.Length == 0)
        {
            return;
        }

        foreach (var token in raw.Split(_separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var item = token.Trim();   //单个编号,可能带 :half
            if (item.Length == 0 || string.Equals(item, NoneToken, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isHalf = item.EndsWith(HalfSuffix, StringComparison.OrdinalIgnoreCase);
            var id = isHalf ? item[..^HalfSuffix.Length].Trim() : item;
            if (id.Length == 0)
            {
                continue;
            }

            if (isHalf)
            {
                if (!off.Contains(id))
                {
                    half.Add(id);
                }
            }
            else
            {
                half.Remove(id);
                off.Add(id);
            }
        }
    }

    /// <summary>
    /// 把两份名单拼回配置文本:按模块全集的顺序输出,一个都没关就写 <see cref="NoneToken"/>。
    /// </summary>
    /// <param name="off">「完全关闭」的编号。</param>
    /// <param name="half">「半加载」的编号。</param>
    public static string Encode(ICollection<string> off, ICollection<string> half)
    {
        var parts = new List<string>();   //拼出来的编号
        foreach (var def in ModuleCatalog.All)
        {
            if (off.Contains(def.Id))
            {
                parts.Add(def.Id);
            }
            else if (half.Contains(def.Id))
            {
                parts.Add(def.Id + HalfSuffix);
            }
        }

        return parts.Count == 0 ? NoneToken : string.Join(",", parts);
    }

    /// <summary>按两份名单判断某个编号的装配强度。</summary>
    /// <param name="id">模块编号。</param>
    /// <param name="off">「完全关闭」的编号。</param>
    /// <param name="half">「半加载」的编号。</param>
    public static ModuleMode ModeOf(string id, ICollection<string> off, ICollection<string> half)
    {
        if (off.Contains(id))
        {
            return ModuleMode.Off;
        }

        return half.Contains(id) ? ModuleMode.Half : ModuleMode.On;
    }
}
