using System.Text;  // 文本编码与 StringBuilder

namespace ZincXManagerShared.Logging;

/// <summary>
/// 客户端审计日志(clients.log):追加写,每行一条。
///
/// 记两类内容:
/// ① 上下线事件(谁、什么身份、什么模式、什么地址、在线多久)
/// ② 有信息量的业务数据 —— 例如好友消息内容(<c>ClientLog.Data("ZXMC", "127.0.0.1:64545", "好友消息 → 114514")</c>)
///
/// 用普通 File.AppendAllText 而不是 ZFile:这里是"只追加、低频、多线程"的场景,
/// 加锁后直接追加最省事,也不用担心把 ZFile 的当前文件指针搞乱。
/// </summary>
public static class ClientLog
{
    private static readonly object Sync = new();

    /// <summary>日志文件路径,默认程序工作目录下的 clients.log。</summary>
    public static string Path { get; set; } = "clients.log";

    /// <summary>
    /// 改用指定目录 + 本次启动时间做文件名(logs/CClient/clients-2026-11-15_17-30-00.log)。
    /// 相对路径锚在可执行文件目录(与 <see cref="Logger.ResolveLogDirectory"/> 同一锚点),不跟着进程当前目录跑,
    /// 否则从别的目录启动时这份日志会和 MLog 分成两棵树。
    /// 父目录不存在会自动建;建不出来就维持工作目录的 clients.log,不抛。
    /// </summary>
    public static void Initialize(string directory)
    {
        try
        {
            var target = Logger.ResolveDirectory(directory);   //解析后的绝对目录
            Directory.CreateDirectory(target);
            Path = System.IO.Path.Combine(target, $"clients-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[ClientLog] 建目录失败,仍写工作目录: " + ex.Message);
        }
    }

    /// <summary>追加一行:时间 [标签] 内容。</summary>
    public static void Write(string tag, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{tag}] {message}{Environment.NewLine}";

        lock (Sync)
        {
            try
            {
                File.AppendAllText(Path, line, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "[ClientLog] 写 clients.log 失败: " + ex.Message);
            }
        }
    }

    /// <summary>记上线。</summary>
    public static void Online(string role, string mode, string address)
        => Write(role, $"上线 模式={mode} 地址={address}");

    /// <summary>记下线(带在线时长和收包数)。</summary>
    public static void Offline(string role, string mode, string address, TimeSpan online, int packets)
        => Write(role, $"下线 模式={mode} 地址={address} 在线={online:hh\\:mm\\:ss} 收包={packets}");

    /// <summary>记业务数据(例如好友消息内容)。</summary>
    public static void Data(string role, string address, string content)
        => Write(role, $"数据 地址={address} 内容={content}");
}
