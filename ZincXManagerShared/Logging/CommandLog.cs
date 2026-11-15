using System.Text;  // 文本编码

namespace ZincXManagerShared.Logging;

/// <summary>
/// 命令日志(logs/Cmd/):控制台每执行一条命令记一行,按启动时间分文件。
///
/// 和 <see cref="ClientLog"/> 一样走普通 <c>File.AppendAllText</c>:低频、只追加、加锁串行,
/// 不碰 ZFile 的当前文件指针。
///
/// 行格式:<c>[时间] 操作人:名#ID 服:编号(名称) 命令:… → ok|结果</c>。
/// 结果太长会把中间截掉并标总长度 —— 命令输出动辄几千字,整条塞进日志没意义。
/// </summary>
public static class CommandLog
{
    private static readonly object Sync = new();

    /// <summary>结果最多留多少字,超出截断。</summary>
    private const int MaxResultLength = 2000;

    /// <summary>日志文件路径;没初始化时落在工作目录的 cmd.log。</summary>
    public static string Path { get; set; } = "cmd.log";

    /// <summary>
    /// 指定目录并开一个新文件(文件名带启动时间)。父目录不存在会自动建。
    /// 相对路径锚在可执行文件目录(与 <see cref="Logger.ResolveLogDirectory"/> 同一锚点),不跟着进程当前目录跑。
    /// </summary>
    public static void Initialize(string directory)
    {
        try
        {
            var target = Logger.ResolveDirectory(directory);   //解析后的绝对目录
            Directory.CreateDirectory(target);
            Path = System.IO.Path.Combine(target, $"cmd-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[CommandLog] 建目录失败,仍写工作目录: " + ex.Message);
        }
    }

    /// <summary>记一条控制台命令(成功失败都记,权限拒绝也记)。</summary>
    public static void Write(string operatorName, int accountId, int serverId, string serverName, string command, bool ok, string result)
    {
        var who = accountId > 0 ? $"{operatorName}#{accountId}" : operatorName;
        var server = string.IsNullOrEmpty(serverName) ? $"{serverId}" : $"{serverId}({serverName})";
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 操作人:{who} 服:{server} 命令:{command} → " +
                   (ok ? "ok" : "err") + " | 结果:" + Shorten(result) + Environment.NewLine;

        lock (Sync)
        {
            try
            {
                File.AppendAllText(Path, line, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // 日志写不进去不能拖垮命令本身,只记一条警告
                Logger.Log(LogLevel.Warning, "[CommandLog] 写命令日志失败: " + ex.Message);
            }
        }
    }

    /// <summary>结果太长只留前 1500 字 + 尾巴 300 字,中间标出省略了多少。</summary>
    private static string Shorten(string result)
    {
        if (string.IsNullOrEmpty(result))
        {
            return "(空)";
        }

        var text = result.Replace("\r", "").Replace("\n", " ⏎ ");   //换行会破坏"一行一条"
        if (text.Length <= MaxResultLength)
        {
            return text;
        }

        const int head = 1500;
        const int tail = 300;
        return $"{text[..head]}…(共 {text.Length} 字,略过 {text.Length - head - tail})…{text[^tail..]}";
    }
}
