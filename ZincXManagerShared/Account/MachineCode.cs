using System;  // Convert / Buffer / BitConverter / OperatingSystem
using System.Diagnostics;  // Process / ProcessStartInfo:macOS 要跑 ioreg
using System.IO;  // File.ReadAllText:读 sysfs
using System.Runtime.InteropServices;  // DllImport
using System.Runtime.Versioning;  // SupportedOSPlatform
using System.Security.Cryptography;  // SHA-256
using System.Text;  // Encoding
using ZincXManagerShared.Logging;  // Logger / LogLevel

namespace ZincXManagerShared.Account;

/// <summary>
/// 本机机器码 = 主板序列号的 SHA-256。
/// 同一块主板永远算出同一个值,换主板才变 —— 服务端"封机器码"靠它。
///
/// <para>为什么不用 CPU 序列号:消费级主板的固件基本不写这一项,
/// 读出来多半是 "To Be Filled By O.E.M." 这类出厂占位串,
/// 参与哈希只会让整批机器共用一个机器码,封机器码就失效了,所以只取主板序列号。</para>
///
/// <para>取值来源:Windows 读 SMBIOS 原始表(GetSystemFirmwareTable 'RSMB')的
/// Type 2(Baseboard Information)偏移 0x07;Linux 读 sysfs 的 board_serial,读不到退到 product_serial;
/// macOS 读 ioreg 的 IOPlatformSerialNumber。</para>
///
/// <para>四个端共用这一份实现,不允许再有第二份副本:
/// 算法差一个字节,同一台机器算出来的值就不同,许可证会忽有效忽无效。</para>
/// </summary>
public static class MachineCode
{
    /// <summary>主板序列号取不到时的占位值。</summary>
    private const string BoardPlaceholder = "noboard";

    private static string? _cached;

    /// <summary>本机机器码(64 位小写十六进制)。</summary>
    public static string Value => _cached ??= Build();

    /// <summary>本机能不能取到真实主板序列号;取不到时机器码是固定占位值,不能用来区分机器。</summary>
    public static bool HasSerial => BoardSerial() != BoardPlaceholder;

    /// <summary>登录/注册要发给服务端的机器码;取不到真实序列号时返回空串,由服务端按配置决定放不放行。</summary>
    public static string ValueForLogin => HasSerial ? Value : "";

    /// <summary>把主板序列号做 SHA-256;取不到序列号时用占位值参与哈希。</summary>
    private static string Build()
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(BoardSerial()))).ToLowerInvariant();

    /// <summary>取主板序列号;取不到或拿到出厂占位串时返回 <c>noboard</c>。</summary>
    private static string BoardSerial()
    {
        var raw = ReadBoardSerial();
        if (IsPlaceholderSerial(raw))
        {
            LogMissing("主板");
            return BoardPlaceholder;
        }

        return raw!.Trim();
    }

    /// <summary>记一条「取不到序列号」的警告;不抛异常、不影响机器码生成。</summary>
    private static void LogMissing(string part)
        => Logger.Log(LogLevel.Warning, $"[MachineCode] 取不到{part}序列号,机器码按占位值生成");

    /// <summary>按当前系统取主板序列号原始值;不认识系统返回 null。</summary>
    private static string? ReadBoardSerial()
    {
        if (OperatingSystem.IsWindows()) return ReadBoardSerialWindows();
        if (OperatingSystem.IsLinux()) return ReadBoardSerialLinux();
        if (OperatingSystem.IsMacOS()) return ReadBoardSerialMac();
        return null;
    }

    // ── Windows:SMBIOS 原始表 ───────────────────────────────────────────

    /// <summary>Windows:主板序列号在 SMBIOS Type 2(Baseboard Information)偏移 0x07。</summary>
    [SupportedOSPlatform("windows")]
    private static string? ReadBoardSerialWindows()
        => FindSmbiosSerial(ReadSmbiosTable(), structType: 2, offset: 0x07);

    /// <summary>
    /// 调 kernel32!GetSystemFirmwareTable 取整张 SMBIOS 表,返回表体(已剥掉 8 字节的 RawSMBIOSData 头)。
    /// 取不到返回 null。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static byte[]? ReadSmbiosTable()
    {
        try
        {
            // 'RSMB' = SMBIOS 固件表提供者的签名,SMBIOS 表挂在它下面
            const uint providerSignature = 0x52534D42;

            // 先问长度(缓冲区传 null、长度 0),系统返回所需字节数
            var size = GetSystemFirmwareTable(providerSignature, 0, null, 0);
            if (size <= 8) return null;

            var buffer = new byte[size];
            var written = GetSystemFirmwareTable(providerSignature, 0, buffer, size);
            if (written <= 8) return null;

            // RawSMBIOSData:版本信息 4 字节 + Length(4 字节,表体长度)+ 表体
            var length = BitConverter.ToUInt32(buffer, 4);
            if (length == 0 || length > written - 8) return null;

            var table = new byte[length];
            Buffer.BlockCopy(buffer, 8, table, 0, (int)length);
            return table;
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[MachineCode] 读 SMBIOS 表失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 在 SMBIOS 表里找指定类型的结构,取出 <paramref name="offset"/> 处字符串序号对应的字符串。
    /// 序号 0、格式化区长度不够(该版 SMBIOS 没有这个字段)、结构越界,都返回 null。
    /// </summary>
    private static string? FindSmbiosSerial(byte[]? table, byte structType, int offset)
    {
        if (table is null) return null;

        var index = 0;   //当前结构的起始偏移
        while (index + 4 <= table.Length)
        {
            var type = table[index];
            var length = table[index + 1];   //格式化区长度(不含字符串集)

            if (type == 127) return null;   //Type 127 = End-of-Table,后面没有结构了
            if (length < 4 || index + length > table.Length) return null;

            if (type == structType)
            {
                // 偏移落在格式化区之外,说明这版 SMBIOS 没定义该字段
                if (offset >= length) return null;

                var stringIndex = table[index + offset];
                return ReadSmbiosString(table, index + length, stringIndex);
            }

            var next = SkipSmbiosStructure(table, index + length);
            if (next < 0) return null;
            index = next;
        }

        return null;
    }

    /// <summary>从字符串集起点跳过整段,返回下一个结构的偏移;越界返回 -1。</summary>
    private static int SkipSmbiosStructure(byte[] table, int stringStart)
    {
        if (stringStart >= table.Length) return -1;

        // 字符串集至少是双 0:有字符串时是「串尾 0 + 集尾 0」,没有字符串时就是两个 0
        if (table[stringStart] == 0)
        {
            return stringStart + 2 <= table.Length ? stringStart + 2 : -1;
        }

        for (var i = stringStart; i + 1 < table.Length; i++)
        {
            if (table[i] == 0 && table[i + 1] == 0) return i + 2;
        }

        return -1;   //没有双 0 收尾 = 表被截断
    }

    /// <summary>按 1 起算的序号在字符串集里取第 N 条;序号 0 或越界返回 null。</summary>
    private static string? ReadSmbiosString(byte[] table, int stringStart, int stringIndex)
    {
        if (stringIndex <= 0 || stringStart >= table.Length) return null;

        var current = 1;   //当前是字符串集里的第几条(1 起算)
        var index = stringStart;
        while (index < table.Length && current <= stringIndex)
        {
            var start = index;
            while (index < table.Length && table[index] != 0) index++;

            if (current == stringIndex)
            {
                // SMBIOS 规定 ASCII,但厂商会塞非 ASCII 字,按 UTF-8 解才不会乱码
                return Encoding.UTF8.GetString(table, start, index - start);
            }

            index++;   //跳过这条字符串的结束 0
            current++;
        }

        return null;
    }

    // ── Linux:sysfs ───────────────────────────────────────────────────

    /// <summary>Linux:主板序列号优先 board_serial,读不到退到整机 product_serial。</summary>
    private static string? ReadBoardSerialLinux()
        => ReadFirstLine("/sys/class/dmi/id/board_serial") ?? ReadFirstLine("/sys/class/dmi/id/product_serial");

    /// <summary>读一个文本文件的首行内容(已去首尾空白);不存在、读不到、内容为空都返回 null。</summary>
    private static string? ReadFirstLine(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex)
        {
            // board_serial 等文件在内核里可能是 0400,普通用户读会抛;这类失败按取不到处理
            Logger.Log(LogLevel.Warning, $"[MachineCode] 读 {path} 失败: {ex.Message}");
            return null;
        }
    }

    // ── macOS:ioreg ───────────────────────────────────────────────────

    /// <summary>macOS:ioreg 取 IOPlatformSerialNumber(整机序列号,与主板一一对应)。</summary>
    private static string? ReadBoardSerialMac()
    {
        var output = RunCommand("/usr/sbin/ioreg", "-rd1 -c IOPlatformExpertDevice");
        if (string.IsNullOrEmpty(output)) return null;

        // 输出形如:    "IOPlatformSerialNumber" = "C02XXXXXXXXX"
        const string key = "\"IOPlatformSerialNumber\"";
        var at = output.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;

        var equal = output.IndexOf('=', at + key.Length);
        if (equal < 0) return null;

        var openQuote = output.IndexOf('"', equal + 1);
        if (openQuote < 0) return null;

        var closeQuote = output.IndexOf('"', openQuote + 1);
        if (closeQuote < 0) return null;

        var value = output[(openQuote + 1)..closeQuote].Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>跑一个只读子进程取标准输出;5 秒没结束就当取不到(杀掉子进程)。</summary>
    private static string? RunCommand(string fileName, string arguments)
    {
        try
        {
            using var process = new Process();   //用 using:句柄要随方法结束释放
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (!process.Start()) return null;

            //两条流同时读:只读一条时另一条写满管道缓冲区会把子进程卡死
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1000);   //等读流的任务收尾,别把未观察的异常留在后台
                }
                catch
                {
                    //子进程可能刚好自己退出,杀不掉不影响结论
                }

                Logger.Log(LogLevel.Warning, $"[MachineCode] {fileName} 5 秒未返回,按取不到处理");
                return null;
            }

            var text = outputTask.GetAwaiter().GetResult();
            var errorText = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                Logger.Log(LogLevel.Warning, $"[MachineCode] {fileName} 退出码 {process.ExitCode}: {errorText.Trim()}");
                return null;
            }

            return text;
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"[MachineCode] 执行 {fileName} 失败: {ex.Message}");
            return null;
        }
    }

    // ── 清洗 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 判定取到的序列号是不是出厂占位值或无效值；无效值不参与机器码区分。
    /// 空白、BIOS 厂商没填时的固定占位串、全 0 串都算无效,放行会让整批机器共用一个机器码。
    /// </summary>
    public static bool IsPlaceholderSerial(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;

        var text = value.Trim();

        switch (text.ToLowerInvariant())
        {
            case "to be filled by o.e.m.":
            case "default string":
            case "none":
            case "n/a":
            case "unknown":
            case "system serial number":
            case "0123456789":
            case "00000000":
                return true;
        }

        return text.All(ch => ch == '0');   //定长全 0 串(不同厂商位数不一样)
    }

    /// <summary>P/Invoke:取固件表(kernel32)。缓冲区传 null 时返回所需字节数。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint firmwareTableProviderSignature, uint firmwareTableId,
        byte[]? firmwareTableBuffer, uint bufferSize);
}
