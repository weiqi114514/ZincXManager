using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace ZincXManagerClient.Models;

/// <summary>找出机器上装了哪些浏览器,给"用浏览器打开"用(火狐 / Chrome / Edge / 360 之类)。</summary>
public static class BrowserLauncher
{
    /// <summary>浏览器显示名 → 可执行文件路径。</summary>
    public static List<(string Name, string Path)> List()
    {
        var found = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            if (seen.Add(path))
            {
                found.Add((name, path));
            }
        }

        // 1) 注册表:StartMenuInternet(系统登记的默认浏览器都在这儿)
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
                if (key is null)
                {
                    continue;
                }

                foreach (var name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name);
                    var display = sub?.GetValue(null) as string ?? name;
                    using var cmd = sub?.OpenSubKey(@"shell\open\command");
                    var command = cmd?.GetValue(null) as string;
                    if (command is null)
                    {
                        continue;
                    }

                    var path = command.Trim();
                    if (path.StartsWith("\""))
                    {
                        var end = path.IndexOf('"', 1);
                        path = end > 1 ? path[1..end] : path;
                    }
                    else
                    {
                        var space = path.IndexOf(' ');
                        if (space > 0 && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == false)
                        {
                            path = path[..space];
                        }
                    }

                    Add(display, path);
                }
            }
            catch
            {
                // 注册表读不到就跳过
            }
        }

        // 2) 常见安装位置兜底(注册表不一定有)
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Add("Edge", Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"));
        Add("Edge", Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"));
        Add("Chrome", Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"));
        Add("Chrome", Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"));
        Add("Chrome", Path.Combine(local, @"Google\Chrome\Application\chrome.exe"));
        Add("Firefox", Path.Combine(pf, @"Mozilla Firefox\firefox.exe"));
        Add("Firefox", Path.Combine(pf86, @"Mozilla Firefox\firefox.exe"));
        Add("360 浏览器", Path.Combine(local, @"360Chrome\Chrome\Application\360chrome.exe"));
        Add("360 极速浏览器", Path.Combine(pf86, @"360\360Chrome\Chrome\Application\360chrome.exe"));

        return found;
    }
}
