using System;   // DateTime / Environment
using System.Diagnostics;   // Process / ProcessStartInfo
using System.IO;   // Path / File
using System.Runtime.InteropServices;   // DllImport / StructLayout:把子进程关进作业对象
using System.Text;   // Encoding
using System.Threading;   // Thread.Sleep:等宿主把端口打开
using ZincXManagerShared.Logging;   // Logger / LogLevel

namespace ZincXManagerServer.Chat;   // 子服侧的聊天宿主管理:拉起、健康检查、收尾

/// <summary>
/// 聊天宿主(独立进程)的启动与收尾。
/// 为什么单独一个进程:聊天后端基于 ASP.NET Core + SQLite,而本工程是 NativeAOT 发布,
/// 两者不能编进同一个可执行文件;放独立进程既不破坏 AOT,也不会拖慢游戏主线程。
/// <para>子进程挂在一个「句柄一关就杀光成员」的作业对象上:子服被强杀(任务管理器、Stop-Process -Force)
/// 时没人会给它收尾,只能靠内核把作业里的进程一起带走,否则端口会被孤儿进程一直占着。</para>
/// </summary>
internal sealed class ChatHostRunner : IDisposable
{
    /// <summary>作业对象句柄;没建成功(非 Windows 或调用失败)时为 0,那时只靠 Dispose 收尾。</summary>
    private IntPtr _job;   // 关掉它 = 杀掉作业里的所有进程

    /// <summary>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE:作业最后一个句柄关闭时杀掉成员。</summary>
    private const uint KillOnJobClose = 0x2000;

    /// <summary>等宿主把端口打开的上限(毫秒):第一次启动要建库,给足 8 秒。</summary>
    private const int StartupWaitMs = 8000;

    /// <summary>JobObjectExtendedLimitInformation。</summary>
    private const int ExtendedLimitInformation = 9;

    /// <summary>子进程对象;未启动时为 null。</summary>
    private Process? _process;   // 退出时整棵杀掉,避免残留端口

    /// <summary>实际监听端口;未启动为 0。</summary>
    internal int Port { get; private set; }

    /// <summary>与宿主共享的签名密钥;未启动为空。</summary>
    internal string Secret { get; private set; } = "";

    /// <summary>数据目录(chat.db 与图片)。</summary>
    private string _dataDirectory = "";   // 相对子服运行目录

    /// <summary>宿主当前是否在跑。</summary>
    internal bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// 启动聊天宿主。
    /// </summary>
    /// <param name="executablePath">宿主可执行文件或 dll 的路径。</param>
    /// <param name="port">监听端口。</param>
    /// <param name="dataDirectory">数据目录。</param>
    /// <param name="secret">签名密钥(至少 16 字符)。</param>
    /// <returns>启动成功返回 true。</returns>
    internal bool Start(string executablePath, int port, string dataDirectory, string secret)
    {
        if (IsRunning || executablePath.Length == 0 || secret.Length < 16)
        {
            return false;
        }

        Port = port;
        Secret = secret;
        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);

        var launcher = ResolveLauncher(executablePath, out var argumentsBefore);
        if (launcher.Length == 0)
        {
            return false;
        }

        var info = new ProcessStartInfo(launcher)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            // 宿主起不来时原因只打在 stderr 上,不接住就等于丢弃;重定向到文件而不是管道,免得缓冲区塞满把子进程卡住
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in argumentsBefore)
        {
            info.ArgumentList.Add(argument);
        }
        info.ArgumentList.Add("--port");
        info.ArgumentList.Add(port.ToString());
        info.ArgumentList.Add("--data");
        info.ArgumentList.Add(dataDirectory);

        // 密钥走环境变量而不是命令行:命令行会被任何能看进程列表的人看到(任务管理器、wmic、ps)
        info.Environment["ZXM_CHAT_SECRET"] = secret;

        try
        {
            _process = Process.Start(info);
            if (_process is null)
            {
                return false;
            }

            _job = CreateKillOnCloseJob();
            if (_job != IntPtr.Zero && !AssignProcessToJobObject(_job, _process.Handle))
            {
                // 关不进作业也不是致命事:正常关服仍走 Dispose,只是顶不住强杀
                Logger.Log(LogLevel.Warning, "[聊天] 没能把聊天宿主关进作业对象,ZXMS 被强杀时它可能变孤儿");
                CloseHandle(_job);
                _job = IntPtr.Zero;
            }

            // Process.Start 成功只说明进程起来了:端口被占或聊天库打不开时它会立刻退出,
            // 那必须在这里就发现,不能等客户端发不出消息才暴露
            if (!WaitUntilListening(port))
            {
                var reason = ReadErrorTail();
                Logger.Log(LogLevel.Error,
                    $"[聊天] 聊天宿主起来了但端口 {port} 没在听(多半是端口被占或聊天库打不开),本次拉起算失败" +
                    (reason.Length > 0 ? ",宿主报错:" + reason : ""));
                Dispose();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[聊天] 拉起聊天宿主出错: " + ex.Message);
            _process = null;
            return false;
        }
    }

    /// <summary>把宿主 stderr 的末尾几行读出来(启动失败时用来解释原因);读不到就返回空串。</summary>
    private string ReadErrorTail()
    {
        try
        {
            if (_process is null || !_process.StartInfo.RedirectStandardError)
            {
                return "";
            }

            var text = _process.StandardError.ReadToEnd();
            if (text.Length == 0)
            {
                return "";
            }

            var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var tail = lines.Length > 3 ? string.Join(" | ", lines[^3..]) : string.Join(" | ", lines);
            return tail.Length <= 400 ? tail : tail[^400..];
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>最多等 <see cref="StartupWaitMs"/> 让宿主的端口开始监听;进程提前退出也算失败。</summary>
    /// <param name="port">宿主该听的端口。</param>
    private bool WaitUntilListening(int port)
    {
        var deadline = Environment.TickCount64 + StartupWaitMs;
        while (Environment.TickCount64 < deadline)
        {
            if (_process is null || _process.HasExited)
            {
                return false;
            }

            if (ProbeHost(port))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    /// <summary>
    /// 探一次宿主是否真的在服务。只「端口能连上」不够:端口被别的程序占着时
    /// TCP 一样能连,会把「聊天后端根本没起来」误判成成功。所以必须问到它自己的接口。
    /// </summary>
    /// <param name="port">宿主该听的端口。</param>
    private static bool ProbeHost(int port)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(400) };
            using var response = client.GetAsync($"http://127.0.0.1:{port}/chat/api/capabilities").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>建一个「最后一个句柄关闭时杀光成员」的作业对象;失败返回 0。</summary>
    private static IntPtr CreateKillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return IntPtr.Zero;
        }

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = KillOnJobClose }
        };
        if (SetInformationJobObject(job, ExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            return job;
        }

        CloseHandle(job);
        return IntPtr.Zero;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>作业的基本限制(只用得到 LimitFlags)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    /// <summary>作业的 IO 计数(结构体占位,必须原样摆着)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectIoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    /// <summary>JobObjectExtendedLimitInformation 的完整布局。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public JobObjectIoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    /// <summary>
    /// 找出可执行文件:发布目录有同名 .exe 就直接跑,否则当 dll 交给 dotnet 跑(开发调试走这条)。
    /// </summary>
    private static string ResolveLauncher(string executablePath, out string[] argumentsBefore)
    {
        var full = Path.GetFullPath(executablePath);
        var exePath = Path.ChangeExtension(full, ".exe");
        if (File.Exists(exePath))
        {
            argumentsBefore = Array.Empty<string>();
            return exePath;
        }
        if (File.Exists(full) && Path.GetExtension(full).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            argumentsBefore = new[] { full };
            return "dotnet";
        }
        argumentsBefore = Array.Empty<string>();
        return "";
    }

    /// <summary>给某个账号签一张聊天令牌。</summary>
    internal string SignFor(string userId, string displayName, long lifetimeSeconds)
        => Secret.Length < 16 ? "" : ZincXManagerShared.Chat.ChatToken.Sign(Secret, userId, displayName, lifetimeSeconds);

    /// <summary>停掉宿主进程。</summary>
    public void Dispose()
    {
        var process = _process;
        _process = null;
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception)
        {
            // 收尾失败不该影响关服;下面关作业句柄还会兜一次
        }
        finally
        {
            if (_job != IntPtr.Zero)
            {
                CloseHandle(_job);
                _job = IntPtr.Zero;
            }

            process?.Dispose();
        }
    }
}
