using System.Text;
using ZincXManagerShared.Logging;

namespace ZincXManagerShared.FileIO;

/// <summary>
/// 文件读写工具(ZXMC / ZXMS / ZXMOS 共用,对应早期 C++ 版 zFile)。
///
/// 约定:
///   · 打开文件使用 <c>FileShare.ReadWrite</c>,同一文件可被多个实例/其它进程同时打开;
///   · 所有公开方法内部加锁,可跨线程调用;<b>日志一律在释放锁之后写出</b>,避免与 Logger 互锁;
///   · 写文件统一 UTF-8 无 BOM,并保留文件原有的换行风格(CRLF / LF);
///   · 出错不抛异常,统一返回 false / 空值并写日志,调用方可用 Try* 方法区分"空"与"失败"。
/// </summary>
public class ZFile : IDisposable
{
    /// <summary>全局日志文件对象(对应 C++ 的 zFile::logF),由宿主在启动时打开。</summary>
    public static readonly ZFile logF = new ZFile();

    private readonly object _sync = new();
    private FileStream? _fs;
    private string _currentFile = "";
    private string? _eol;         // 本次打开探测到的换行风格(CRLF / LF),写文件时复用

    /// <summary>当前打开的文件名;未打开时为空字符串。</summary>
    public string CurrentFile
    {
        get { lock (_sync) return _currentFile; }
    }

    /// <summary>当前是否已打开文件。</summary>
    public bool IsOpen
    {
        get { lock (_sync) return _fs != null; }
    }

    // ---------- 打开 / 创建 / 替换 / 删除 / 关闭 ----------

    /// <summary>
    /// 文件操作。
    /// <paramref name="mode"/>:
    ///   rwNFC       —— 不存在则创建,存在则打开(读写)
    ///   replaceFile —— 打开已有文件(不存在则创建),不截断内容
    ///   del         —— 删除文件
    /// </summary>
    public bool FileOperate(string fileName, string mode)
    {
        bool ok;
        LogLevel level;
        string message;

        lock (_sync)
        {
            switch (mode)
            {
                case "rwNFC":
                    ok = OpenCore(fileName, "[zFile]开启或创建文件", out level, out message);
                    break;
                case "replaceFile":
                    ok = OpenCore(fileName, "[zFile]开启文件", out level, out message);
                    break;
                case "del":
                    ok = DeleteCore(fileName, out level, out message);
                    break;
                default:
                    ok = false;
                    level = LogLevel.Warning;
                    message = "[zFile]未知操作模式：" + mode;
                    break;
            }
        }

        Logger.Log(level, message);
        return ok;
    }

    /// <summary>关闭当前文件。未打开时返回 false 并写 Warning 日志。</summary>
    public bool CloseFile()
    {
        bool ok;
        LogLevel level;
        string message;

        lock (_sync)
        {
            if (_fs == null)
            {
                ok = false;
                level = LogLevel.Warning;
                message = "[zFile]close:关闭文件为无效操作，文件已经关闭";
            }
            else
            {
                CloseStreamCore();
                ok = true;
                level = LogLevel.Info;
                message = "[zFile]文件关闭";
            }
        }

        Logger.Log(level, message);
        return ok;
    }

    /// <summary>
    /// 析构:静默关闭文件(不写日志)。
    /// 旧版 Dispose 会走 CloseFile,重复释放时会在退出阶段刷出一条无意义的 Warning。
    /// </summary>
    public void Dispose()
    {
        lock (_sync) CloseStreamCore();
    }

    // ---------- 读取 ----------

    /// <summary>按行读取当前文件,返回物理行(空文件返回空列表)。</summary>
    public List<string> ReadFileTxt()
    {
        var lines = new List<string>();
        LogLevel level = LogLevel.Info;
        string? message = null;

        lock (_sync)
        {
            if (_fs == null)
            {
                level = LogLevel.Error;
                message = "[zFile]readFileTxt:读取TXT文件为无效操作，文件未开启";
            }
            else
            {
                try
                {
                    lines.AddRange(SplitLines(ReadAllCore()));
                }
                catch (Exception ex)
                {
                    level = LogLevel.Error;
                    message = "[zFile]readFileTxt:读取失败 " + ex.Message;
                }
            }
        }

        if (message != null) Logger.Log(level, message);
        return lines;
    }

    /// <summary>读取当前文件为键值对(ini 格式:跳过空行与 # / ; 注释行)。</summary>
    public Dictionary<string, string> ReadFileMap()
    {
        var result = new Dictionary<string, string>();
        LogLevel level = LogLevel.Info;
        string? message = null;

        lock (_sync)
        {
            if (_fs == null)
            {
                level = LogLevel.Error;
                message = "[zFile]readFileMap:文件未打开";
            }
            else
            {
                try
                {
                    foreach (var line in SplitLines(ReadAllCore()))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (line[0] == '#' || line[0] == ';') continue;

                        var pos = line.IndexOf('=');
                        if (pos < 0) continue;

                        var key = line[..pos].Trim();
                        var value = line[(pos + 1)..].Trim();
                        if (key.Length > 0) result[key] = value;
                    }
                }
                catch (Exception ex)
                {
                    level = LogLevel.Error;
                    message = "[zFile]readFileMap:读取失败 " + ex.Message;
                }
            }
        }

        if (message != null) Logger.Log(level, message);
        return result;
    }

    /// <summary>
    /// 读取当前打开文件的全部字节。
    /// 返回 null 表示读取失败(未打开 / 出错),返回空数组表示文件确实为空。
    /// </summary>
    public byte[]? ReadFileBytes()
    {
        byte[]? data = null;
        string? message = null;

        lock (_sync)
        {
            if (_fs == null)
            {
                message = "[zFile]readFileBin：文件未打开";
            }
            else
            {
                try
                {
                    var length = _fs.Length;
                    if (length > int.MaxValue)
                    {
                        message = "[zFile]readFileBin：文件过大(超过 2GB)";
                    }
                    else
                    {
                        _fs.Position = 0;
                        var buffer = new byte[(int)length];
                        var read = 0;
                        while (read < buffer.Length)
                        {
                            var n = _fs.Read(buffer, read, buffer.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        data = read == buffer.Length ? buffer : buffer[..read];
                        _fs.Position = 0;
                    }
                }
                catch (Exception ex)
                {
                    data = null;
                    message = "[zFile]readFileBin：读取失败 " + ex.Message;
                }
            }
        }

        if (message != null) Logger.Log(LogLevel.Error, message);
        return data;
    }

    /// <summary>
    /// 读取当前打开文件的全部字节,并按 Latin1 把字节 1:1 映射成字符串。
    /// 失败时返回空字符串,要区分"空文件"与"读取失败"请用 <see cref="TryReadFileBin"/>。
    /// </summary>
    public string ReadFileBin()
    {
        var data = ReadFileBytes();
        return data == null ? "" : Encoding.Latin1.GetString(data);
    }

    /// <summary>读取当前打开文件的字节:成功(含空文件)返回 true,失败返回 false。</summary>
    public bool TryReadFileBin(out string data)
    {
        var bytes = ReadFileBytes();
        data = bytes == null ? "" : Encoding.Latin1.GetString(bytes);
        return bytes != null;
    }

    /// <summary>按路径读取文件字节(不需要先打开),失败返回空字符串并写 Error 日志。</summary>
    public string ReadFileBin(string fileName)
    {
        try
        {
            using var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[fs.Length];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = fs.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return Encoding.Latin1.GetString(buffer, 0, read);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[zFile]readFileBin：打开失败 " + ex.Message);
            return "";
        }
    }

    /// <summary>列出目录内容:普通文件为名字,子目录带 "/" 后缀,每项一行,按名称排序。</summary>
    public string ReadDir(string dirPath)
    {
        if (!Directory.Exists(dirPath))
        {
            Logger.Log(LogLevel.Warning, "[zFile]readDir:目录不存在 " + dirPath);
            return "";
        }

        try
        {
            var sb = new StringBuilder();
            foreach (var entry in Directory.EnumerateFileSystemEntries(dirPath).OrderBy(e => e, StringComparer.Ordinal))
            {
                sb.Append(Path.GetFileName(entry));
                if (Directory.Exists(entry)) sb.Append('/');
                sb.Append('\n');
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[zFile]readDir:读取失败 " + ex.Message);
            return "";
        }
    }

    // ---------- 写入 ----------

    /// <summary>追加写入(txt 直接写到文件末尾),endl 决定是否补一个换行(沿用文件原有换行风格)。</summary>
    public bool WriteFileTxt(string txt, bool endl)
    {
        lock (_sync)
        {
            if (_fs == null)
            {
                // 此处不写日志:该方法可能被 Logger 自身调用,写日志会递归
                return false;
            }

            try
            {
                _fs.Seek(0, SeekOrigin.End);
                var text = endl ? txt + DetectNewlineCached() : txt;
                var bytes = Encoding.UTF8.GetBytes(text);
                _fs.Write(bytes, 0, bytes.Length);
                _fs.Flush();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 从第 <paramref name="endline"/> 行开始覆盖写:保留前 (endline-1) 行,写入 txt,
    /// 其后的原内容被丢弃(即"从该行起覆盖")。
    /// <paramref name="newline"/> 表示新写入的这一行是否以换行结尾
    /// (false 时文件不再补末尾换行,与追加写重载的 endl 含义一致)。
    /// </summary>
    public bool WriteFileTxt(string txt, bool newline, int endline)
    {
        var ok = false;
        LogLevel level = LogLevel.Info;
        string? message = null;

        lock (_sync)
        {
            if (_fs == null) return false;

            if (endline < 1)
            {
                level = LogLevel.Warning;
                message = "[zFile]writeFileTxt:行号必须 >= 1";
            }
            else
            {
                try
                {
                    var all = ReadAllCore();
                    var eol = DetectNewline(all);
                    _eol = eol;
                    var lines = SplitLines(all);
                    var keep = Math.Min(endline - 1, lines.Count);

                    var outLines = new List<string>(lines.GetRange(0, keep)) { txt };
                    WriteAllCore(outLines, eol, ensureTrailingNewline: newline);
                    ok = true;
                }
                catch (Exception ex)
                {
                    level = LogLevel.Error;
                    message = "[zFile]writeFileTxt:写入失败 " + ex.Message;
                }
            }
        }

        if (message != null) Logger.Log(level, message);
        return ok;
    }

    /// <summary>
    /// 写一个键值:已存在同名键则原地替换(顺序不变),否则追加到文件末尾。
    /// 空行、注释行与其它行都会原样保留。
    /// </summary>
    public bool WriteFileMap(string key, string value)
    {
        var ok = false;
        LogLevel level = LogLevel.Info;
        string? message = null;

        lock (_sync)
        {
            if (_fs == null)
            {
                level = LogLevel.Error;
                message = "[zFile]writeFileMap:文件未打开";
            }
            else if (string.IsNullOrWhiteSpace(key))
            {
                level = LogLevel.Warning;
                message = "[zFile]writeFileMap:key 不能为空";
            }
            else
            {
                try
                {
                    var all = ReadAllCore();
                    var eol = DetectNewline(all);
                    _eol = eol;
                    var lines = SplitLines(all);

                    var found = false;
                    for (var i = 0; i < lines.Count; i++)
                    {
                        var line = lines[i];
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                        var pos = line.IndexOf('=');
                        if (pos < 0) continue;

                        // 与 ReadFileMap 对齐:键两侧的空格会被忽略
                        if (line[..pos].Trim() == key)
                        {
                            lines[i] = key + "=" + value;
                            found = true;
                            break;
                        }
                    }

                    if (!found) lines.Add(key + "=" + value);

                    WriteAllCore(lines, eol, ensureTrailingNewline: true);
                    ok = true;
                }
                catch (Exception ex)
                {
                    level = LogLevel.Error;
                    message = "[zFile]writeFileMap:写入失败 " + ex.Message;
                }
            }
        }

        if (message != null) Logger.Log(level, message);
        return ok;
    }

    // ---------- 取值 ----------

    /// <summary>读取一个键值,键不存在时返回 <paramref name="def"/>。</summary>
    public string MapGet(string key, string def = "")
    {
        var map = ReadFileMap();
        return map.TryGetValue(key, out var value) ? value : def;
    }

    /// <summary>读取第 n 行(n 从 1 开始),不存在返回空字符串。</summary>
    public string TxtGet(int n)
    {
        if (n < 1) return "";

        var lines = ReadFileTxt();
        if (n > lines.Count) return "";
        return lines[n - 1];
    }

    // ---------- 内部实现(调用方必须已经持有 _sync) ----------

    private bool OpenCore(string fileName, string actionLog, out LogLevel level, out string message)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            level = LogLevel.Warning;
            message = "[zFile]文件名不能为空";
            return false;
        }

        CloseStreamCore();

        try
        {
            if (!File.Exists(fileName))
                using (File.Create(fileName)) { }

            // FileShare.ReadWrite:允许同一文件被多个实例/其它进程同时打开
            _fs = new FileStream(fileName, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            _currentFile = fileName;

            level = LogLevel.Info;
            message = actionLog + fileName;
            return true;
        }
        catch (Exception ex)
        {
            _fs = null;
            _currentFile = "";

            level = LogLevel.Error;
            message = "[zFile]打开失败: " + ex.Message;
            return false;
        }
    }

    private bool DeleteCore(string fileName, out LogLevel level, out string message)
    {
        CloseStreamCore();

        if (string.IsNullOrWhiteSpace(fileName))
        {
            level = LogLevel.Warning;
            message = "[zFile]del:文件名不能为空";
            return false;
        }

        try
        {
            File.Delete(fileName);
            level = LogLevel.Info;
            message = "[zFile]删除文件" + fileName;
            return true;
        }
        catch (Exception ex)
        {
            level = LogLevel.Error;
            message = "[zFile]del:删除文件失败，错误信息：" + ex.Message;
            return false;
        }
    }

    /// <summary>关闭流并清空当前文件名(不写日志)。</summary>
    private void CloseStreamCore()
    {
        try
        {
            _fs?.Dispose();
        }
        catch
        {
            // 关闭失败不影响状态清理
        }

        _fs = null;
        _currentFile = "";
        _eol = null;
    }

    /// <summary>取本次打开期间缓存的新行风格;首次调用时探测一次(空文件按 LF)。</summary>
    private string DetectNewlineCached()
    {
        if (_eol != null) return _eol;

        try
        {
            _eol = _fs!.Length > 0 ? DetectNewline(ReadAllCore()) : "\n";
        }
        catch
        {
            _eol = "\n";
        }

        return _eol;
    }

    /// <summary>把整个文件读成字符串(定位到 0,UTF-8 并识别 BOM)。</summary>
    private string ReadAllCore()
    {
        _fs!.Position = 0;
        using var reader = new StreamReader(
            _fs,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        return reader.ReadToEnd();
    }

    /// <summary>整体覆盖写回,保留原有换行风格,UTF-8 无 BOM。</summary>
    private void WriteAllCore(List<string> lines, string eol, bool ensureTrailingNewline)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            sb.Append(lines[i]);
            if (i < lines.Count - 1) sb.Append(eol);
        }
        if (ensureTrailingNewline && lines.Count > 0) sb.Append(eol);

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        _fs!.SetLength(0);
        _fs.Position = 0;
        _fs.Write(bytes, 0, bytes.Length);
        _fs.Flush();
    }

    /// <summary>按 CRLF / LF / CR 拆成物理行,等价于逐行 ReadLine 的结果。</summary>
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') continue;   // CRLF 由 '\n' 收尾
                lines.Add(text[start..i]);
                start = i + 1;
            }
            else if (c == '\n')
            {
                var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                lines.Add(text[start..end]);
                start = i + 1;
            }
        }

        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    /// <summary>探测文件使用的换行风格(默认 LF)。</summary>
    private static string DetectNewline(string text)
        => text.Contains("\r\n") ? "\r\n" : "\n";
}
