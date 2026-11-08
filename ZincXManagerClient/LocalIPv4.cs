using System;  // 基础类型 / 常用静态方法
using System.Collections.Generic;  // 列表
using System.Linq;  // LINQ 查询
using System.Net;  // IP 地址
using System.Net.NetworkInformation;  // 网卡枚举与地址表
using System.Net.Sockets;  // AddressFamily

namespace ZincXManagerClient;

/// <summary>
/// 本机局域网 IPv4(网卡地址,例如 <c>192.168.1.23</c>)。
///
/// <para><b>为什么放在客户端而不是共享库:</b>只有 ZXMC 需要报自己的局域网地址(632),
/// ZXMS / ZXMOS 从不读自己机器的网卡地址做这件事 —— 放进 ZincXManagerShared 只会让两个服务端
/// 项目多背一份没人调用的代码。</para>
///
/// <para><b>只取网卡地址,不查公网出口 IP:</b>这个功能是为了在登录弹窗里显示"玩家从哪个网段来",
/// 而且绝不发任何外网 HTTP 请求(见协议 632 的说明)。</para>
/// </summary>
internal static class LocalIPv4
{
    /// <summary>取到的地址缓存多久(秒):网卡拔插、换 Wi-Fi 之后旧地址不能一直用下去。</summary>
    private const int CacheSeconds = 120;

    /// <summary>空结果只缓存这么久(秒):开机时网卡可能还没就绪,不能把「没有地址」永久钉死。</summary>
    private const int EmptyCacheSeconds = 5;

    private static readonly object Gate = new();   //缓存的读写锁
    private static string _cached = "";
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>
    /// 本机局域网 IPv4;取不到返回空串(调用处负责记 Warn,别静默)。
    ///
    /// <para>结果缓存 <see cref="CacheSeconds"/> 秒:上报只在登录成功与收到 633 时发生,本来就不频繁,
    /// 但也不能一辈子只算一次 —— 笔记本换网络、网卡刚开机才启用都会让旧值失效。</para>
    /// </summary>
    public static string Value
    {
        get
        {
            lock (Gate)
            {
                var age = (DateTime.UtcNow - _cachedAt).TotalSeconds;   //缓存已经放了多久
                var limit = _cached.Length > 0 ? CacheSeconds : EmptyCacheSeconds;
                if (age < limit)
                {
                    return _cached;
                }

                _cached = Find();
                _cachedAt = DateTime.UtcNow;
                return _cached;
            }
        }
    }

    private static string Find()
    {
        try
        {
            // 先排掉明显不代表"这台机器在局域网里"的网卡:没插网线的、回环、隧道。
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Where(nic => nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(nic => new NicAddress(
                    nic.Name,
                    nic.GetPhysicalAddress().ToString(),
                    FirstUnicast(nic),
                    IsVirtual(nic)))
                .Where(c => c.Address.Length > 0)
                .ToList();

            // 第一轮只要真网卡;一个都没有(比如整机都在虚拟网卡上跑)就退回全部候选 ——
            // 报个虚拟网段也比什么都不报强,界面那一行本来就是给人核对用的
            var picked = Pick(candidates.Where(c => !c.Virtual).ToList()) is { Length: > 0 } real
                ? real
                : Pick(candidates);

            return picked;
        }
        catch
        {
            // 网卡枚举在少数环境下会抛(驱动异常 / 权限),不影响登录,交给调用处记 Warn
            return "";
        }
    }

    /// <summary>
    /// 按固定顺序挑一个地址。排序是必须的:<c>GetAllNetworkInterfaces</c> 的顺序在不同进程 / 不同时间
    /// 可能不一样(理由同 MachineCode 取首块 MAC 的注释),不排序会今天报有线、明天报无线。
    /// </summary>
    private static string Pick(List<NicAddress> list)
        => list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)   // 网卡名在同一台机器上稳定
            .ThenBy(c => c.Mac, StringComparer.OrdinalIgnoreCase)        // 同名的(以太网 1 / 以太网 2)再按 MAC 定序
            .Select(c => c.Address)
            .DefaultIfEmpty("")
            .First();

    /// <summary>从一块网卡的地址表里取第一个可用的 IPv4(排掉回环与 169.254.* 自动地址)。</summary>
    private static string FirstUnicast(NetworkInterface nic)
    {
        try
        {
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                var text = unicast.Address.ToString();
                if (IsUsable(text))
                {
                    return text;
                }
            }
        }
        catch
        {
            // 单块网卡读属性失败就跳过它,换下一块
        }

        return "";
    }

    /// <summary>这个 IPv4 是不是能给别人看的局域网地址。</summary>
    private static bool IsUsable(string text)
    {
        if (!IPAddress.TryParse(text, out var address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        // 169.254.* 是网卡拿不到 DHCP 时自己编的链路本地地址,局域网里没人能用它找到这台机器
        return !text.StartsWith("169.254.", StringComparison.Ordinal);
    }

    /// <summary>
    /// 这块网卡是不是虚拟的(虚拟机 / 容器 / VPN / 手机热点桥接)。
    /// 看的是 Windows 给的<b>描述</b>(<c>Description</c>),不是用户能改的显示名,免得玩家把网卡改名就失灵。
    /// </summary>
    private static bool IsVirtual(NetworkInterface nic)
    {
        var text = (nic.Description ?? "") + " " + (nic.Name ?? "");
        return VirtualMarks.Any(mark => text.Contains(mark, StringComparison.OrdinalIgnoreCase));
    }

    // 虚拟网卡的特征词:VMware / Hyper-V / VirtualBox 的虚拟交换机、WSL 与 Docker 的 vEthernet、
    // Radmin / Tailscale / ZeroTier 这类组网 VPN、TAP/TUN 隧道、Wi-Fi Direct 与蓝牙的承载网卡
    private static readonly string[] VirtualMarks =
    [
        "Virtual", "VMware", "Hyper-V", "VirtualBox", "vEthernet", "Docker", "WSL",
        "TAP", "TUN", "VPN", "Radmin", "Tailscale", "ZeroTier", "WireGuard", "Hamachi",
        "Wi-Fi Direct", "Bluetooth", "Loopback",
    ];

    /// <summary>一块网卡的候选地址(排完序要用的三样东西)。</summary>
    private sealed record NicAddress(string Name, string Mac, string Address, bool Virtual);
}
