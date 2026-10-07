using ZincXManagerShared.FileIO;
using ZincXManagerShared.Logging;

namespace ZincXManagerClient;

/// <summary>
/// 客户端自己的配置(config.ini),键名与两个服务端统一:
/// czip/czport = 子服务端,ozip/ozport = 官方服务端,mode = 连接方式,
/// role = 权限等级,wallpaper = 背景壁纸路径。
///
/// 启动时打开一次,全局共用这一份;缺键自动补缺省值。
/// </summary>
internal static class ClientConfig
{
    private static readonly ZFile File = Open();

    private static ZFile Open()
    {
        var file = new ZFile();
        if (!file.FileOperate("config.ini", "rwNFC"))
        {
            Logger.Log(LogLevel.Warning, "[ZXMC] 打开 config.ini 失败,使用缺省配置");
        }

        return file;
    }

    /// <summary>取值;没有这个键(或值为空)就写入缺省值并返回它。</summary>
    public static string Get(string key, string defaultValue)
    {
        var value = File.MapGet(key).Trim();
        if (value.Length > 0)
        {
            return value;
        }

        File.WriteFileMap(key, defaultValue);
        Logger.Log(LogLevel.Info, $"[CONFIG]缺少 {key},写入缺省值 {defaultValue}");
        return defaultValue;
    }

    /// <summary>取值;没有就返回空串(不写文件,给"可以没有"的配置用)。</summary>
    public static string GetOrEmpty(string key) => File.MapGet(key).Trim();

    /// <summary>存值(立即落盘)。</summary>
    public static void Set(string key, string value) => File.WriteFileMap(key, value);
}
