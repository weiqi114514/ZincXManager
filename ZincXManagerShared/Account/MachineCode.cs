using System;
using System.Security.Cryptography;
using System.Text;

namespace ZincXManagerShared.Account;

/// <summary>
/// 本机机器码:机器名 + 首块网卡 MAC + 系统卷序列号 + 系统版本 一起做 SHA-256。
/// 同一台机器不变,换机器 / 换硬盘才变 —— 服务端"封机器码"靠它。
///
/// <para>算法必须与客户端的 <c>ZincXManagerClient.MachineCode</c> 完全一致,
/// 否则同一台机器在两端算出来的值不同。</para>
/// </summary>
public static class MachineCode
{
    private static string? _cached;

    /// <summary>本机机器码(64 位小写十六进制)。</summary>
    public static string Value => _cached ??= Build();

    private static string Build()
    {
        var text = $"{Environment.MachineName}|{FirstMac()}|{VolumeSerial()}|{Environment.OSVersion.Version}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string FirstMac()
    {
        try
        {
            // 网卡要按名字排序再取第一块 —— 多网卡(Docker/虚拟网卡/RDP)时
            // GetAllNetworkInterfaces 的顺序在不同进程间可能不一样,
            // 会导致同一台机器算出不同机器码(许可证忽有效忽无效)。
            var list = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Select(nic => new { nic.Name, Mac = nic.GetPhysicalAddress().ToString() })
                .Where(x => x.Mac.Length >= 12)
                .OrderBy(x => x.Mac, StringComparer.OrdinalIgnoreCase)   // 用 MAC 排序:网卡名可能重名(以太网1/2/Docker),MAC 才是唯一的
                .ToList();
            if (list.Count > 0)
            {
                return list[0].Mac;
            }
        }
        catch
        {
            // 取不到就用占位,不影响连接
        }

        return "nomac";
    }

    private static string VolumeSerial()
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            if (GetVolumeInformation(root, null, 0, out var serial, out _, out _, null, 0))
            {
                return serial.ToString("X8");
            }
        }
        catch
        {
            // 同上
        }

        return "novolume";
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string rootPathName, StringBuilder? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer, int fileSystemNameSize);
}
