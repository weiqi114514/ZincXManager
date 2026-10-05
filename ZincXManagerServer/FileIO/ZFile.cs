using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ZincXManagerServer.Logging;

namespace ZincXManagerServer.FileIO;

public class ZFile : IDisposable
{
    private FileStream? _fs;
    private string _currentFile = "";

    public bool IsOpen => _fs != null && _fs.CanRead;

    public bool FileOperate(string fileName, string mode)
    {
        switch (mode)
        {
            case "rwNFC":
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
                    Logger.Log(LogLevel.Info, $"[zFile]开启或创建文件 {fileName}");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Error, $"[zFile]打开失败: {ex.Message}");
                    return false;
                }

            case "del":
                CloseStream();
                try
                {
                    File.Delete(fileName);
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Error,
                        $"[zFile]del:删除文件失败，错误信息：{ex.Message}");
                    return false;
                }

            case "replaceFile":
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
                    Logger.Log(LogLevel.Info, $"[zFile]开启文件 {fileName}");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Error, $"[zFile]replaceFile 失败: {ex.Message}");
                    return false;
                }

            default:
                Logger.Log(LogLevel.Warning, $"[zFile]未知操作模式：{mode}");
                return false;
        }
    }

    public bool CloseFile()
    {
        if (_fs == null)
        {
            Logger.Log(LogLevel.Warning, "[zFile]close:关闭文件为无效操作，文件已经关闭");
            return false;
        }
        _fs.Dispose();
        _fs = null;
        _currentFile = "";
        Logger.Log(LogLevel.Info, "[zFile]文件关闭");
        return true;
    }

    private void CloseStream()
    {
        _fs?.Dispose();
        _fs = null;
    }

    public string ReadFileTxt()
    {
        if (_fs == null)
        {
            Logger.Log(LogLevel.Error, "[zFile]readFileTxt:读取TXT文件为无效操作，文件未开启");
            return "";
        }
        _fs.Position = 0;
        using var reader = new StreamReader(_fs, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }

    public Dictionary<string, string> ReadFileMap()
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
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line[0] == '#' || line[0] == ';') continue;

            var pos = line.IndexOf('=');
            if (pos < 0) continue;

            var key = line.Substring(0, pos).Trim();
            var value = line.Substring(pos + 1).Trim();

            if (!string.IsNullOrEmpty(key))
                result[key] = value;
        }
        return result;
    }

    public string ReadFileBin()
    {
        if (string.IsNullOrEmpty(_currentFile))
        {
            Logger.Log(LogLevel.Error, "[zFile]readFileBin：当前没有打开的文件");
            return "";
        }
        try
        {
            var bytes = File.ReadAllBytes(_currentFile);
            return Encoding.Latin1.GetString(bytes);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, $"[zFile]readFileBin：打开失败 {ex.Message}");
            return "";
        }
    }

    public string ReadDir(string dirPath)
    {
        if (!Directory.Exists(dirPath))
        {
            Logger.Log(LogLevel.Warning, $"[zFile]readDir:目录不存在 {dirPath}");
            return "";
        }
        var sb = new StringBuilder();
        foreach (var entry in Directory.EnumerateFileSystemEntries(dirPath))
        {
            sb.Append(Path.GetFileName(entry));
            if (Directory.Exists(entry)) sb.Append('/');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public bool WriteFileTxt(string content, bool newline)
    {
        if (_fs == null) return false;
        _fs.Seek(0, SeekOrigin.End);
        var text = newline ? content + "\n" : content;
        var bytes = Encoding.UTF8.GetBytes(text);
        _fs.Write(bytes, 0, bytes.Length);
        _fs.Flush();
        return true;
    }

    public bool WriteFileTxt(string content, bool newline, int line)
    {
        if (_fs == null) return false;
        if (line < 1) return false;

        _fs.Position = 0;
        string all;
        using (var reader = new StreamReader(_fs, Encoding.UTF8, leaveOpen: true))
            all = reader.ReadToEnd();

        var lines = all.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);

        var keep = Math.Min(line - 1, lines.Count);
        var outLines = new List<string>();
        for (int i = 0; i < keep; i++) outLines.Add(lines[i]);
        outLines.Add(newline ? content + "\n" : content);

        var outStr = string.Join("\n", outLines) + "\n";
        _fs.SetLength(0);
        _fs.Position = 0;
        var bytes = Encoding.UTF8.GetBytes(outStr);
        _fs.Write(bytes, 0, bytes.Length);
        _fs.Flush();
        return true;
    }

    public bool WriteFileMap(string key, string value)
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

        _fs.Position = 0;
        string all;
        using (var reader = new StreamReader(_fs, Encoding.UTF8, leaveOpen: true))
            all = reader.ReadToEnd();

        var lines = all.Split('\n')
                       .Where(l => !string.IsNullOrWhiteSpace(l))
                       .ToList();

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
        if (!found) lines.Add($"{key}={value}");

        var outStr = string.Join("\n", lines) + "\n";
        _fs.SetLength(0);
        _fs.Position = 0;
        var bytes = Encoding.UTF8.GetBytes(outStr);
        _fs.Write(bytes, 0, bytes.Length);
        _fs.Flush();
        return true;
    }

    public string MapGet(string key, string def = "")
    {
        var m = ReadFileMap();
        return m.TryGetValue(key, out var v) ? v : def;
    }

    public void Dispose() => CloseFile();
}