using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;  // 导入 Registry 类(判断是不是 MSI 安装版)
using ZincXManagerShared.Logging;  // 导入 Logger / LogLevel

namespace ZincXManagerClient.Services;

/// <summary>一次检查更新的结果</summary>
public sealed record UpdateInfo(string CurrentVersion, string LatestVersion, string NotesUrl, string MsiUrl, string ZipUrl, string MsiSha, string ZipSha, bool HasUpdate);

/// <summary>客户端自更新:查 GitHub Releases → 下载 → 按安装形态自动装</summary>
public static class UpdateService
{
    // 仓库与接口
    private const string Repo = "weiqi114514/ZincXManager";
    private static readonly Uri LatestApi = new($"https://api.github.com/repos/{Repo}/releases/latest");

    // 缓存键(避免每次启动都打接口)
    private const string CacheTimeKey = "updatecheck.time";
    private const string CacheVerKey = "updatecheck.version";
    private static readonly TimeSpan CacheWindow = TimeSpan.FromHours(24);

    // 复用连接
    private static readonly HttpClient Http = CreateClient();

    /// <summary>当前版本(InformationalVersion,如 b0.1.0)</summary>
    public static string CurrentVersion
    {
        get
        {
            var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)System.Attribute.GetCustomAttribute(
                typeof(UpdateService).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            var info = attr?.InformationalVersion ?? "0.0.0";
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
    }

    /// <summary>更新日志页(官网 docs)</summary>
    public static string ChangelogUrl => "https://weiqi114514.github.io/ZincXManager/changelog.html";

    /// <summary>是不是 MSI 安装版(否则按便携版处理)</summary>
    public static bool IsInstalled
    {
        get
        {
            try
            {
                // Program Files 下 → 安装版
                var exe = Environment.ProcessPath ?? "";
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (exe.StartsWith(pf, StringComparison.OrdinalIgnoreCase) || exe.StartsWith(pf86, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // 注册表里有卸载项且装了本程序 → 安装版
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (key is not null)
                {
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using var app = key.OpenSubKey(sub);
                        var name = app?.GetValue("DisplayName") as string;
                        if (name is not null && name.Contains("ZincXManager", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("[更新] 判断安装形态失败: " + ex.Message);
            }

            return false;
        }
    }

    /// <summary>检查更新(useCache=true 时 24 小时内直接拿缓存)</summary>
    /// <param name="useCache">允许用缓存</param>
    /// <param name="ct">取消令牌</param>
    public static async Task<UpdateInfo?> CheckAsync(bool useCache = true, CancellationToken ct = default)
    {
        var current = CurrentVersion;

        // 缓存:24 小时内、且缓存版本不比当前新就复用
        if (useCache && TryReadCache(out var cachedVer, out var cachedAt) && DateTime.Now - cachedAt < CacheWindow)
        {
            if (CompareVersion(cachedVer, current) <= 0)
            {
                return new UpdateInfo(current, current, ChangelogUrl, "", "", "", "", false);
            }
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log($"[更新] 接口返回 {(int)resp.StatusCode}");
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var notes = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            if (tag.Length == 0)
            {
                return null;
            }

            // 产物地址 + 摘要(按文件名挑 msi / portable.zip)
            var msiUrl = "";
            var zipUrl = "";
            var msiSha = "";
            var zipSha = "";
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                    var sha = a.TryGetProperty("digest", out var d) ? (d.GetString() ?? "").Replace("sha256:", "") : "";

                    if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                    {
                        msiUrl = url; msiSha = sha;
                    }
                    else if (name.Contains("portable", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = url; zipSha = sha;
                    }
                }
            }

            // 写缓存
            WriteCache(tag, DateTime.Now);

            var hasUpdate = CompareVersion(tag, current) > 0;
            var level = ZincXManagerShared.Update.VersionRules.Level(current, tag);
            Log($"[更新] 当前 {current},远端 {tag},有更新={hasUpdate},级别={ZincXManagerShared.Update.VersionRules.LevelName(level)}");
            return new UpdateInfo(current, tag, notes.Length > 0 ? notes : ChangelogUrl, msiUrl, zipUrl, msiSha, zipSha, hasUpdate);
        }
        catch (Exception ex)
        {
            Log("[更新] 检查失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 下载并自动安装。onProgress 收 0-100 的进度。
    /// 返回是否成功启动安装。
    /// </summary>
    public static async Task<bool> DownloadAndApplyAsync(UpdateInfo info, IProgress<int>? onProgress, CancellationToken ct = default)
    {
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), "ZXM", "update");
            Directory.CreateDirectory(temp);

            var installed = IsInstalled;
            var url = installed ? info.MsiUrl : info.ZipUrl;
            var sha = installed ? info.MsiSha : info.ZipSha;

            if (url.Length == 0)
            {
                Log($"[更新] 没有可用的 {(installed ? "msi" : "zip")} 产物");
                return false;
            }

            var file = Path.Combine(temp, installed ? $"ZXM-{info.LatestVersion}.msi" : $"ZXM-{info.LatestVersion}.zip");
            if (!await DownloadAsync(url, file, onProgress, ct))
            {
                return false;
            }

            // 有 digest 就校验,没有就跳过(老 release 可能没有)
            if (sha.Length == 64 && !VerifySha256(file, sha))
            {
                Log("[更新] SHA256 不匹配,放弃");
                return false;
            }

            onProgress?.Report(100);

            if (installed)
            {
                // MSI:/qb 有进度条,会等程序退出后继续
                Process.Start(new ProcessStartInfo
                {
                    FileName = "msiexec.exe",
                    Arguments = $"/i \"{file}\" /qb /norestart",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                Log("[更新] 已启动 msiexec /qb");
            }
            else
            {
                // 便携版:解压到临时目录,写切换脚本并启动
                var stage = Path.Combine(temp, "stage");
                if (Directory.Exists(stage))
                {
                    Directory.Delete(stage, true);
                }

                ZipFile.ExtractToDirectory(file, stage);

                var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

                // 目标目录可能是 stage 的子目录(包里带一层文件夹)
                var source = Directory.GetFileSystemEntries(stage).Length == 1 && Directory.Exists(Directory.GetFileSystemEntries(stage)[0])
                    ? Directory.GetFileSystemEntries(stage)[0]
                    : stage;

                var script = Path.Combine(temp, "apply-update.cmd");
                var relaunch = ClientConfig.Get("update.relaunch", "1") == "1";
                WriteApplyScript(script, source, exeDir, relaunch);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{script}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                Log("[更新] 已启动替换脚本");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log("[更新] 下载/安装失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 候选下载地址:镜像可以配多个(分号/逗号/换行分隔),按顺序试;
    /// 最后一个一定是官方原地址,兜底用。
    /// </summary>
    public static string[] BuildDownloadUrls(string url)
    {
        // 不是 GitHub 的地址(比如自己搭的源)不套镜像
        if (!url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return [url];
        }

        // 新清单(带启用/顺序)优先;没有就用旧的 update.mirror 纯地址串
        var mirrors = MirrorSettingsStore.EnabledUrls();
        if (mirrors.Length == 0)
        {
            var raw = ClientConfig.GetOrEmpty("update.mirror");
            mirrors = raw.Split([';', ',', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        var list = new List<string>();
        foreach (var mirror in mirrors)
        {
            if (mirror.Length == 0)
            {
                continue;
            }

            var prefix = mirror.EndsWith('/') ? mirror : mirror + "/";
            var candidate = prefix + url;
            if (!list.Contains(candidate))
            {
                list.Add(candidate);
            }
        }

        // 官方源永远最后兜底
        if (!list.Contains(url))
        {
            list.Add(url);
        }

        return list.ToArray();
    }

    /// <summary>下载:按配置的镜像逐个试,全失败就走官方源</summary>
    private static async Task<bool> DownloadAsync(string url, string dest, IProgress<int>? onProgress, CancellationToken ct)
    {
        var candidates = BuildDownloadUrls(url);
        foreach (var candidate in candidates)
        {
            if (await TryDownloadAsync(candidate, dest, onProgress, ct))
            {
                // 换源成功也把进度补满,免得界面停在 99%
                onProgress?.Report(100);
                return true;
            }

            Log("[更新] 这个地址没下成,换下一个: " + candidate);
        }

        Log("[更新] 所有下载地址都失败");
        return false;
    }

    /// <summary>真正下这一次;失败返回 false,由调用方决定要不要换源</summary>
    private static async Task<bool> TryDownloadAsync(string url, string dest, IProgress<int>? onProgress, CancellationToken ct)
    {
        try
        {
            Log("[更新] 下载地址: " + url);
            return await DownloadCoreAsync(url, dest, onProgress, ct);
        }
        catch (Exception ex)
        {
            Log("[更新] 下载异常: " + ex.Message);
            return false;
        }
    }

    /// <summary>下载主体(带进度)</summary>
    private static async Task<bool> DownloadCoreAsync(string url, string dest, IProgress<int>? onProgress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            Log($"[更新] 下载返回 {(int)resp.StatusCode}");
            return false;
        }

        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0)
            {
                onProgress?.Report((int)(read * 100 / total));
            }
        }

        return true;
    }

    /// <summary>按 SHA256 摘要校验文件</summary>
    private static bool VerifySha256(string file, string expected)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(file);
        var hash = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        return string.Equals(hash, expected.ToLowerInvariant(), StringComparison.Ordinal);
    }

    /// <summary>写边等进程退出边替换的批处理</summary>
    private static void WriteApplyScript(string script, string source, string target, bool relaunch)
    {
        var lines = new List<string>
        {
            "@echo off",
            "chcp 65001 >nul",   // 目录里可能有中文,cmd 默认 GBK 会乱码
            "setlocal",
            ":wait",
            "tasklist /fi \"imagename eq ZincXManagerClient.exe\" | find /i \"ZincXManagerClient\" >nul",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto wait",
            ")",
            $"robocopy \"{source}\" \"{target}\" /E /IS /NFL /NDL /NJH /NJS >nul",
        };

        if (relaunch)
        {
            lines.Add($"start \"\" \"{Path.Combine(target, "ZincXManagerClient.exe")}\"");
        }

        lines.Add($"rmdir /s /q \"{source}\"");
        lines.Add("del \"%~f0\"");
        lines.Add("exit /b 0");

        // 写成 UTF-8 无 BOM(配合上面的 chcp 65001),免得分区/语言不同导致中文路径失效
        File.WriteAllText(script, string.Join("\r\n", lines), new System.Text.UTF8Encoding(false));
    }

    /// <summary>
    /// 版本比较:规则在 Shared 的 <see cref="ZincXManagerShared.Update.VersionRules"/>(测试直接测那份实现)。
    /// 字母后缀参与比较,否则 b0.1.2a 与 b0.1.2b 会被判成相等。
    /// </summary>
    public static int CompareVersion(string a, string b) => ZincXManagerShared.Update.VersionRules.Compare(a, b);

    /// <summary>读缓存(上次检查到的版本 + 时间)</summary>
    private static bool TryReadCache(out string version, out DateTime at)
    {
        version = ClientConfig.GetOrEmpty(CacheVerKey);
        at = DateTime.MinValue;
        var raw = ClientConfig.GetOrEmpty(CacheTimeKey);
        return version.Length > 0 && DateTime.TryParse(raw, out at);
    }

    /// <summary>写缓存</summary>
    private static void WriteCache(string version, DateTime at)
    {
        ClientConfig.Set(CacheVerKey, version);
        ClientConfig.Set(CacheTimeKey, at.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    /// <summary>建 HttpClient(带 UA,不然 GitHub 会 403)</summary>
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZincXManagerClient/" + CurrentVersion);
        return client;
    }

    /// <summary>写日志</summary>
    private static void Log(string text) => Logger.Log(LogLevel.Info, text);
}
