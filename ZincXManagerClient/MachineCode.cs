using System;  // 基础类型 / 常用静态方法
using System.Security.Cryptography;  // 加密与哈希
using System.Text;  // 文本编码与 StringBuilder

namespace ZincXManagerClient;

/// <summary>
/// 本机机器码:机器名 + 首块网卡 MAC + 系统卷序列号 一起做 SHA-256。
/// 同一台机器不变,换机器/换硬盘才变 —— 服务端"封机器码"靠它。
/// </summary>
internal static class MachineCode
{
    private static string? _cached;

    /// <summary>本机机器码(64 位小写十六进制)。</summary>
    public static string Value => _cached ??= Build();

    private static string Build()
    {
        var text = $"{Environment.MachineName}|{FirstMac()}|{VolumeSerial()}|{Environment.OSVersion.Version}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>取第一块有 MAC 的网卡(按 MAC 排序,避免多网卡顺序不定)。</summary>
    private static string FirstMac()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                var mac = nic.GetPhysicalAddress().ToString();
                if (mac.Length >= 12)
                {
                    return mac;
                }
            }
        }
        catch
        {
            // 取不到就用占位,不影响登录
        }

        return "nomac";
    }

    /// <summary>取系统盘卷序列号。</summary>
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
    /// <summary>P/Invoke:向系统要卷信息,拿卷序列号。</summary>
    private static extern bool GetVolumeInformation(string rootPathName, StringBuilder? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer, int fileSystemNameSize);
}