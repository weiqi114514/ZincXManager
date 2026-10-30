using System.Text;  // 文本编码

namespace ZincXManagerShared.Account;

/// <summary>
/// 白名单表(ZXMS 侧):存「通联标」——MC 玩家名 / ZXM 账号 ID / ZXM 账号名,一行一个。
/// 只做存储与 O(1) 比对,不放行判断由 ZXMS 做;开关单独一个文件(<see cref="EnabledPath"/>,内容 1 / 0)。
/// </summary>
public sealed class WhitelistStore
{
    private readonly object _sync = new();

    /// <summary>条目的哈希集合(忽略大小写):Contains 是 O(1),不做穷举。</summary>
    private readonly HashSet<string> _set = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>白名单条目文件。</summary>
    public string Path { get; }

    /// <summary>开关文件(1 = 开,0 = 关)。</summary>
    public string EnabledPath { get; }

    /// <summary>白名单当前是否开启(默认关)。</summary>
    public bool Enabled { get; private set; }

    /// <summary>建一个白名单表并读盘。</summary>
    public WhitelistStore(string path = "Data/Whitelist.txt")
    {
        Path = path;
        EnabledPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(path) ?? ".", "Whitelist.enabled");
        Load();
    }

    /// <summary>当前全部通联标(快照,给控制台显示用)。</summary>
    public string[] All
    {
        get
        {
            lock (_sync)
            {
                return _set.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    /// <summary>这个通联标在不在白名单里(O(1) 哈希比对)。</summary>
    public bool Contains(string? key)
    {
        var value = (key ?? "").Trim();
        if (value.Length == 0)
        {
            return false;
        }

        lock (_sync)
        {
            return _set.Contains(value);
        }
    }

    /// <summary>加一个通联标;已存在返回 false。</summary>
    public bool Add(string key, out string error)
    {
        var value = (key ?? "").Trim();
        error = "";
        if (value.Length == 0)
        {
            error = "通联标不能为空";
            return false;
        }

        lock (_sync)
        {
            if (!_set.Add(value))
            {
                error = "已经在白名单里了";
                return false;
            }

            Save();
        }

        return true;
    }

    /// <summary>删一个通联标;不存在返回 false。</summary>
    public bool Remove(string key)
    {
        var value = (key ?? "").Trim();
        if (value.Length == 0)
        {
            return false;
        }

        lock (_sync)
        {
            if (!_set.Remove(value))
            {
                return false;
            }

            Save();
        }

        return true;
    }

    /// <summary>开 / 关白名单,写开关文件。</summary>
    public void SetEnabled(bool on)
    {
        lock (_sync)
        {
            Enabled = on;
            System.IO.File.WriteAllText(EnabledPath, on ? "1" : "0", Encoding.UTF8);
        }
    }

    /// <summary>从磁盘读条目 + 开关。</summary>
    public void Load()
    {
        lock (_sync)
        {
            _set.Clear();

            if (System.IO.File.Exists(Path))
            {
                foreach (var line in System.IO.File.ReadAllLines(Path, Encoding.UTF8))
                {
                    var value = line.Trim();
                    if (value.Length > 0 && !value.StartsWith('#'))
                    {
                        _set.Add(value);
                    }
                }
            }

            Enabled = System.IO.File.Exists(EnabledPath)
                      && System.IO.File.ReadAllText(EnabledPath, Encoding.UTF8).Trim() == "1";
        }
    }

    /// <summary>把条目写回磁盘(一行一个)。</summary>
    private void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
        {
            System.IO.Directory.CreateDirectory(dir);
        }

        System.IO.File.WriteAllLines(Path, _set.OrderBy(v => v, StringComparer.OrdinalIgnoreCase), Encoding.UTF8);
    }
}
