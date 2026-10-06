using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ZincXManagerServer.Logging;

namespace ZincXManagerServer.FileIO;

public class ZFile : IDisposable
{
    // 全局对象（对应 C++ 的 zFile::logF）
    public static readonly ZFile logF = new ZFile();

    private FileStream? _fs;                // 文件流
    private string _currentFile = "";       // 当前打开的文件名

    // 当前文件是否已打开
    public bool IsOpen => _fs != null;

    
    public bool FileOperate(string fileName, string mode)// 文件操作：如果文件存在则打开，不存在则创建并打开（对应 C++ 的 fileOperate）
    {
        // 独写，如果文件存在则打开，不存在则创建并打开
        if (mode == "rwNFC")
        {
            CloseStream();
            try
            {
                // 文件不存在，先创建
                if (!File.Exists(fileName))
                    using (File.Create(fileName)) { }

                // 以读写方式打开
                _fs = new FileStream(fileName,
                                      FileMode.Open,
                                      FileAccess.ReadWrite,
                                      FileShare.Read);
                _currentFile = fileName;

                Logger.Log(LogLevel.Info, "[zFile]开启或创建文件" + fileName);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error, "[zFile]打开失败: " + ex.Message);
                return false;
            }
        }

        // 删除文件操作
        else if (mode == "del")
        {
            CloseStream();
            try
            {
                File.Delete(fileName);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error,
                    "[zFile]del:删除文件失败，错误信息：" + ex.Message);
                return false;
            }
        }

        // 替换开启文件操作
        else if (mode == "replaceFile")
        {
            CloseStream();
            try
            {
                if (!File.Exists(fileName))
                    using (File.Create(fileName)) { }

                _fs = new FileStream(fileName,
                                      FileMode.Open,
                                      FileAccess.ReadWrite,
                                      FileShare.Read);
                _currentFile = fileName;

                Logger.Log(LogLevel.Info, "[zFile]开启文件" + fileName);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error,
                    "[zFile]replaceFile 失败: " + ex.Message);
                return false;
            }
        }

        // 未知操作模式
        else
        {
            Logger.Log(LogLevel.Warning, "[zFile]未知操作模式：" + mode);
            return false;
        }
    }

    
    public bool CloseFile()// 关闭文件操作（对应 C++ 的 closeFile）
    {
        if (_fs == null)
        {
            Logger.Log(LogLevel.Warning,
                "[zFile]close:关闭文件为无效操作，文件已经关闭");
            return false;
        }

        _fs.Dispose();
        _fs = null;
        _currentFile = "";

        Logger.Log(LogLevel.Info, "[zFile]文件关闭");
        return true;
    }

    
    private void CloseStream()// 内部关闭流（不写日志）
    {
        _fs?.Dispose();
        _fs = null;
    }

    public List<string> ReadFileTxt()// 读取文本文件，按行拆好返回（对应 C++ 的 readFileTxt）
    {
        var lines = new List<string>();

        if (_fs == null)
        {
            Logger.Log(LogLevel.Error,
                "[zFile]readFileTxt:读取TXT文件为无效操作，文件未开启");
            return lines;   // 空列表
        }

        _fs.Position = 0;
        using var reader = new StreamReader(_fs, Encoding.UTF8, leaveOpen: true);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // C# 的 ReadLine 已经自动去掉了 \r 和 \n
            lines.Add(line);
        }

        return lines;
    }


    public Dictionary<string, string> ReadFileMap() // 读取键值对文件，以 ini 为读取目标（对应 C++ 的 readFileMap）
    {
        var result = new Dictionary<string, string>();

        if (_fs == null)
        {
            Logger.Log(LogLevel.Error, "[zFile]readFileMap:文件未打开");
            return result;
        }

        _fs.Position = 0;
        using var reader = new StreamReader(_fs, Encoding.UTF8, leaveOpen: true);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // 跳过空行和注释（# 或 ; 开头）
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line[0] == '#' || line[0] == ';') continue;

            // 找第一个 '='
            var pos = line.IndexOf('=');
            if (pos < 0) continue;   // 没有等号，跳过

            // 切 key / value 并去首尾空格
            var key = line.Substring(0, pos).Trim();
            var value = line.Substring(pos + 1).Trim();

            if (!string.IsNullOrEmpty(key))
                result[key] = value;
        }

        return result;
    }

    
    public string ReadFileBin()// 读取二进制文件（对应 C++ 的 readFileBin）
    {
        if (string.IsNullOrEmpty(_currentFile))
        {
            Logger.Log(LogLevel.Error,
                "[zFile]readFileBin：当前没有打开的文件");
            return "";
        }

        try
        {
            // 用 Latin1 编码把字节 1:1 映射成 char，兼容 C++ 的 std::string 语义
            var bytes = File.ReadAllBytes(_currentFile);
            return Encoding.Latin1.GetString(bytes);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error,
                "[zFile]readFileBin：打开失败 " + ex.Message);
            return "";
        }
    }

    
    public string ReadDir(string dirPath)// 读取目录（对应 C++ 的 readDir，改成带参数版）
    {
        if (!Directory.Exists(dirPath))
        {
            Logger.Log(LogLevel.Warning,
                "[zFile]readDir:目录不存在 " + dirPath);
            return "";
        }

        var sb = new StringBuilder();
        foreach (var entry in Directory.EnumerateFileSystemEntries(dirPath))
        {
            sb.Append(Path.GetFileName(entry));
            if (Directory.Exists(entry)) sb.Append('/');   // 目录加个斜杠区分
            sb.Append('\n');
        }
        return sb.ToString();
    }

    
    public bool WriteFileTxt(string txt, bool endl)// 追加写入，endl 决定换行（对应 C++ 的 writeFileTxt(in, endl)）
    {
        if (_fs == null)
        {
            // 不在这里 log，避免和 Logger 形成递归
            return false;
        }

        _fs.Seek(0, SeekOrigin.End);   // 定位到末尾 = 追加
        var text = endl ? txt + "\n" : txt;
        var bytes = Encoding.UTF8.GetBytes(text);
        _fs.Write(bytes, 0, bytes.Length);
        _fs.Flush();                    // 立刻落盘
        return true;
    }

    
    public bool WriteFileTxt(string txt, bool newline, int endline)// 写入文件，从输入行开始（对应 C++ 的 writeFileTxt(in, endl, line)）
    {
        if (_fs == null)
        {
            return false;
        }
        if (endline < 1)
        {
            Logger.Log(LogLevel.Warning,
                "[zFile]writeFileTxt:行号必须 >= 1");
            return false;
        }

        // 1. 读出整个文件
        _fs.Position = 0;
        string all;
        using (var reader = new StreamReader(_fs, Encoding.UTF8, leaveOpen: true))
            all = reader.ReadToEnd();

        // 2. 按 \n 切成行
        var lines = all.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1] == "")
            lines.RemoveAt(lines.Count - 1);

        // 3. 保留前 line-1 行
        var keep = Math.Min(line - 1, lines.Count);
        var outLines = new List<string>();
        for (int i = 0; i < keep; i++)
            outLines.Add(lines[i]);

        // 4. 拼上新内容
        outLines.Add(newline ? content + "\n" : content);

        // 5. 覆盖写回
        var outStr = string.Join("\n", outLines) + "\n";
        _fs.SetLength(0);
        _fs.Position = 0;
        var bytes = Encoding.UTF8.GetBytes(outStr);
        _fs.Write(bytes, 0, bytes.Length);
        _fs.Flush();
        return true;
    }

    
    public bool WriteFileMap(string key, string value)// 键值追加/修改（对应 C++ 的 writeFileMap / mapEdit）
    {
        if (_fs == null)
        {
            Logger.Log(LogLevel.Error, "[zFile]writeFileMap:文件未打开");
            return false;
        }
        if (string.IsNullOrEmpty(key))
        {
            Logger.Log(LogLevel.Warning, "[zFile]writeFileMap:key 不能为空");
            return false;
        }

        // 1. 读出全部内容
        _fs.Position = 0;
        string all;
        using (var reader = new StreamReader(_fs, Encoding.UTF8, leaveOpen: true))
            all = reader.ReadToEnd();

        var lines = all.Split('\n')
                       .Where(l => !string.IsNullOrWhiteSpace(l))
                       .ToList();

        // 2. 找 "key=" 开头的行，替换；找不到就新增
        var target = key + "=";
        bool found = false;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(target, StringComparison.Ordinal))
            {
                lines[i] = $"{key}={value}";
                found = true;
                break;
            }
        }
        if (!found)
            lines.Add($"{key}={value}");

        // 3. 拼回一个字符串
        var outStr = string.Join("\n", lines) + "\n";

        // 4. 覆盖写回（先截断，避免残留旧内容）
        _fs.SetLength(0);
        _fs.Position = 0;
        var bytes = Encoding.UTF8.GetBytes(outStr);
        _fs.Write(bytes, 0, bytes.Length);
        _fs.Flush();
        return true;
    }

   
    public string MapGet(string key, string def = "")// 读取一个键值（对应 C++ 的 mapGet）
    {
        var m = ReadFileMap();
        return m.TryGetValue(key, out var v) ? v : def;
    }

    public string TxtGet(int n)// TXT读取第 n 行（n 从 1 开始）
    {
        if (n < 1) return "";

        var lines = ReadFileTxt();       // 复用全读
        if (n > lines.Count) return "";
        return lines[n - 1];
    }

    public void Dispose() => CloseFile();// 析构：自动关闭文件（对应 C++ 的 ~ZFile）
}