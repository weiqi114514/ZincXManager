using System.Text;
using System.Text.Json;
using ZincXManagerShared.Logging;
using ZincXManagerShared.Network;

namespace ZincXManagerShared.Account;

/// <summary>一个账号。</summary>
public sealed class Account
{
    /// <summary>账号名(唯一,不区分大小写)。</summary>
    public string Name { get; init; } = "";

    /// <summary>密码密文(SHA-256,由客户端算好发来;文件里没有明文)。</summary>
    public string Password { get; set; } = "";

    /// <summary>权限等级:ZXMS 是 Player / LAdmin / HAdmin / System;ZXMOS 是 Player / Admin / System。</summary>
    public string Level { get; set; } = Protocol.RegisterLevel;

    /// <summary>数字 ID(展示用,自增)。</summary>
    public int Id { get; init; }

    /// <summary>
    /// 注册时对方的 IP。ZXMOS 上的"ZXMS 端账号"要记这个(哪个子服务端在用);
    /// 玩家账号留空。
    /// </summary>
    public string Ip { get; set; } = "";

    /// <summary>最后一次登录的 IP(封禁用户时方便连它的 IP 一起封)。</summary>
    public string LastIp { get; set; } = "";

    /// <summary>最后一次登录的机器码(同上)。</summary>
    public string Machine { get; set; } = "";

    /// <summary>这个用户账号是经哪个 ZXMS 注册的(黑名单移交账号时要用;直连官方注册的为空)。</summary>
    public string Owner { get; set; } = "";
}

/// <summary>
/// 账号表:存成文本文件(ZXMS 用 users.ini,ZXMOS 用 osusers.ini),一行一个
/// <c>名称=密码密文,等级,ID,IP</c>。
///
/// <para>本类只跟密文打交道:<see cref="Add"/> / <see cref="TryRegister"/> 收的就是密文,
/// <see cref="TryLogin"/> 比对的也是密文 —— 明文永远不进服务端。</para>
///
/// <para>等级表可以自定义:ZXMS 传 <see cref="Protocol.Levels"/>,ZXMOS 传 <see cref="Protocol.OSLevels"/>,
/// 所以两边的账号权限体系互不影响。</para>
///
/// <para>用普通文本读写而不是 ZFile 的 map:map 没有"删键"的接口,而账号要能删。</para>
/// </summary>
public sealed class AccountStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Account> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _levels;
    private readonly string _registerLevel;

    /// <summary>别的账号表里是否已经有这个名字(服务端账号 / 用户账号不能重名)。</summary>
    private readonly Func<string, bool>? _alsoTaken;

    /// <summary>账号文件路径。</summary>
    public string Path { get; }

    /// <param name="path">账号文件。</param>
    /// <param name="levels">本端的等级表;不传就用 ZXMS 的。</param>
    /// <param name="registerLevel">自助注册能拿到的等级;不传就用等级表第一项(Player)。</param>
    /// <param name="alsoTaken">别的账号表里是否已有这个名字;用于"任何类型任何账号都不能重名"。</param>
    public AccountStore(string path = "users.ini", string[]? levels = null, string? registerLevel = null,
        Func<string, bool>? alsoTaken = null)
    {
        Path = path;
        _alsoTaken = alsoTaken;
        _levels = levels is { Length: > 0 } ? levels : Protocol.Levels;
        _registerLevel = registerLevel is { Length: > 0 } && IsLevel(registerLevel) ? registerLevel : _levels[0];
        Load();
    }

    /// <summary>本端的等级表。</summary>
    public string[] Levels => _levels;

    /// <summary>自助注册能拿到的等级。</summary>
    public string RegisterLevel => _registerLevel;

    /// <summary>等级文案(报错提示用)。</summary>
    public string LevelList => string.Join(" / ", _levels);

    /// <summary>这个等级合法吗。</summary>
    public bool IsLevel(string level) => Array.IndexOf(_levels, level) >= 0;

    /// <summary>全部账号(按 ID 排序)。</summary>
    public IReadOnlyCollection<Account> All
    {
        get
        {
            lock (_sync)
            {
                return _map.Values.OrderBy(a => a.Id).ToArray();
            }
        }
    }

    /// <summary>按名字找账号;没有就返回 null。</summary>
    public Account? Find(string name)
    {
        lock (_sync)
        {
            return _map.TryGetValue(name.Trim(), out var account) ? account : null;
        }
    }

    /// <summary>按"名称或 ID"找账号(控制台命令用);没有就返回 null。</summary>
    public Account? FindByKey(string key)
    {
        key = key.Trim();

        if (int.TryParse(key, out var id))
        {
            lock (_sync)
            {
                foreach (var account in _map.Values)
                {
                    if (account.Id == id)
                    {
                        return account;
                    }
                }
            }
        }

        return Find(key);
    }

    /// <summary>
    /// 新建账号(等级任意,给控制台用)。<paramref name="passwordHash"/> 必须是密文。
    /// 已存在或参数不合法时返回 false 并给出原因。
    /// </summary>
    public bool Add(string name, string passwordHash, string level, out Account? account, out string error)
        => Add(name, passwordHash, level, "", out account, out error);

    /// <summary>新建账号,并记下注册方的 IP(ZXMOS 上的 ZXMS 端账号用)。</summary>
    public bool Add(string name, string passwordHash, string level, string ip, out Account? account, out string error)
    {
        account = null;
        name = name.Trim();

        if (name.Length < 2)
        {
            error = "账号名至少 2 个字符";
            return false;
        }

        if (!PasswordHash.IsHash(passwordHash))
        {
            error = "密码密文不合法(应为 SHA-256 十六进制)";
            return false;
        }

        if (!IsLevel(level))
        {
            error = $"权限等级只能是 {LevelList}";
            return false;
        }

        lock (_sync)
        {
            if (_map.ContainsKey(name))
            {
                error = "账号已存在";
                return false;
            }

            // 任何类型、任何账号都不能重名:别的账号表里有也不行
            if (_alsoTaken is not null && _alsoTaken(name))
            {
                error = "账号名已被占用(不能和其它类型的账号重名)";
                return false;
            }

            var created = new Account
            {
                Name = name,
                Password = passwordHash,
                Level = level,
                Id = _map.Count == 0 ? 1 : _map.Values.Max(a => a.Id) + 1,
                Ip = ip,
            };

            _map[name] = created;
            Save();
            account = created;
            error = "";
            Logger.Log(LogLevel.Info, $"[账号] 新建 {created.Name}({created.Level}) ID={created.Id}{(created.Ip.Length > 0 ? " IP=" + created.Ip : "")}");
            return true;
        }
    }

    /// <summary>自助注册:等级固定为本端的注册等级;密码参数是密文。</summary>
    public bool TryRegister(string name, string passwordHash, out Account? account, out string error)
        => Add(name, passwordHash, _registerLevel, "", out account, out error);

    /// <summary>自助注册(带 IP)。</summary>
    public bool TryRegister(string name, string passwordHash, string ip, out Account? account, out string error)
        => Add(name, passwordHash, _registerLevel, ip, out account, out error);

    /// <summary>用指定等级注册(ZXMOS 上 ZXMS 端账号走这条)。</summary>
    public bool TryRegisterAs(string name, string passwordHash, string level, string ip, out Account? account, out string error)
        => Add(name, passwordHash, level, ip, out account, out error);

    /// <summary>登录校验:账号存在 + 密文一致 + 所选等级与该账号一致。</summary>
    public bool TryLogin(string name, string passwordHash, string level, out Account? account, out string error)
    {
        account = Find(name);

        if (account is null)
        {
            error = "账号不存在";
            return false;
        }

        if (!string.Equals(account.Password, passwordHash, StringComparison.OrdinalIgnoreCase))
        {
            error = "密码错误";
            return false;
        }

        if (account.Level != level)
        {
            error = $"该账号的权限等级是 {account.Level}";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>改密码(控制台 user rep);<paramref name="newHash"/> 是密文。</summary>
    public bool SetPassword(string key, string newHash, out string error)
    {
        var account = FindByKey(key);
        if (account is null)
        {
            error = "账号不存在";
            return false;
        }

        if (!PasswordHash.IsHash(newHash))
        {
            error = "密码密文不合法(应为 SHA-256 十六进制)";
            return false;
        }

        lock (_sync)
        {
            account.Password = newHash;
            Save();
        }

        Logger.Log(LogLevel.Info, $"[账号] {account.Name} 已改密码");
        error = "";
        return true;
    }

    /// <summary>改权限等级(控制台 user rel)。</summary>
    public bool SetLevel(string key, string newLevel, out string error)
    {
        var account = FindByKey(key);
        if (account is null)
        {
            error = "账号不存在";
            return false;
        }

        if (!IsLevel(newLevel))
        {
            error = $"权限等级只能是 {LevelList}";
            return false;
        }

        lock (_sync)
        {
            account.Level = newLevel;
            Save();
        }

        Logger.Log(LogLevel.Info, $"[账号] {account.Name} 权限等级改为 {newLevel}");
        error = "";
        return true;
    }

    /// <summary>登录成功后记一下 IP / 机器码(封禁用户时能连坐它的 IP 与机器码)。</summary>
    public void Touch(string key, string ip, string machine)
    {
        var account = FindByKey(key);
        if (account is null)
        {
            return;
        }

        lock (_sync)
        {
            account.LastIp = ip;
            account.Machine = machine;
            Save();
        }
    }

    /// <summary>把账号标成"属于某个 ZXMS"(ZXMOS 用;账号移交给 ZXMS 时按这个筛)。</summary>
    public void SetOwner(string name, string owner)
    {
        var account = Find(name);
        if (account is null)
        {
            return;
        }

        lock (_sync)
        {
            account.Owner = owner;
            Save();
        }
    }

    /// <summary>删除账号(名称或 ID)。</summary>
    public bool Remove(string key)
    {
        var account = FindByKey(key);
        if (account is null)
        {
            return false;
        }

        lock (_sync)
        {
            if (!_map.Remove(account.Name))
            {
                return false;
            }

            Save();
        }

        Logger.Log(LogLevel.Info, "[账号] 已删除 " + account.Name);
        return true;
    }

    /// <summary>
    /// 读 JSON;顺手处理两件事:① 老版本的 users.ini 自动迁移成 json;
    /// ② 老版本留下的明文密码升级成密文。
    /// </summary>
    private void Load()
    {
        var migrated = false;

        try
        {
            // 没 json 但老的 ini 在 → 先按 ini 读,读完存成 json(账号不丢)
            var legacy = System.IO.Path.ChangeExtension(Path, ".ini");
            if (!File.Exists(Path) && File.Exists(legacy))
            {
                Logger.Log(LogLevel.Info, $"[账号] 发现老文件 {legacy},正在迁移成 {Path}");
                LoadLegacy(legacy, ref migrated);
                if (migrated)
                {
                    Logger.Log(LogLevel.Info, "[账号] 已把明文密码升级为密文");
                }

                Save();
                return;
            }

            if (!File.Exists(Path))
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(Path, Encoding.UTF8));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Length == 0)
                {
                    continue;
                }

                var secret = item.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";

                if (secret.Length > 0 && !PasswordHash.IsHash(secret))
                {
                    secret = PasswordHash.Hash(name, secret);
                    migrated = true;
                }

                var level = item.TryGetProperty("level", out var l) ? l.GetString() ?? "" : "";
                var account = new Account
                {
                    Name = name,
                    Password = secret,
                    Level = IsLevel(level) ? level : _registerLevel,
                    Id = item.TryGetProperty("id", out var i) && i.TryGetInt32(out var id) ? id : 0,
                    Ip = item.TryGetProperty("ip", out var ip) ? ip.GetString() ?? "" : "",
                    LastIp = item.TryGetProperty("lastIp", out var lip) ? lip.GetString() ?? "" : "",
                    Machine = item.TryGetProperty("machine", out var m) ? m.GetString() ?? "" : "",
                    Owner = item.TryGetProperty("owner", out var o) ? o.GetString() ?? "" : "",
                };

                _map[account.Name] = account;
            }
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[账号] 迁移老账号文件失败: " + ex.Message);
        }
    }

    /// <summary>读老格式(名称=密文,等级,ID,IP,最后IP,机器码,归属),一行一个。</summary>
    private void LoadLegacy(string legacyPath, ref bool migrated)
    {
        try
        {
            foreach (var line in File.ReadAllLines(legacyPath, Encoding.UTF8))
            {
                var text = line.Trim();
                if (text.Length == 0 || text.StartsWith('#'))
                {
                    continue;
                }

                var eq = text.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var fields = text[(eq + 1)..].Split(',');
                var name = text[..eq].Trim();
                var secret = fields.Length > 0 ? fields[0] : "";

                // 旧格式存的是明文 → 这里换成密文,下次保存就写回文件
                if (secret.Length > 0 && !PasswordHash.IsHash(secret))
                {
                    secret = PasswordHash.Hash(name, secret);
                    migrated = true;
                }

                var account = new Account
                {
                    Name = name,
                    Password = secret,
                    Level = fields.Length > 1 && IsLevel(fields[1]) ? fields[1] : _registerLevel,
                    Id = fields.Length > 2 && int.TryParse(fields[2], out var id) ? id : 0,
                    Ip = fields.Length > 3 ? fields[3].Trim() : "",
                    LastIp = fields.Length > 4 ? fields[4].Trim() : "",
                    Machine = fields.Length > 5 ? fields[5].Trim() : "",
                    Owner = fields.Length > 6 ? fields[6].Trim() : "",
                };

                _map[account.Name] = account;
            }
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[账号] 迁移老账号文件失败: " + ex.Message);
        }
    }

    /// <summary>JSON 字符串转义。</summary>
    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

    /// <summary>写文件(写的就是密文)。</summary>
    private void Save()
    {
        try
        {
            // 手写 JSON(不依赖反射,APO/AOT 下也安全)
            var json = new StringBuilder("[\n");
            var list = _map.Values.OrderBy(a => a.Id).ToArray();

            for (var index = 0; index < list.Length; index++)
            {
                var a = list[index];
                json.Append("  {\"name\":\"").Append(Escape(a.Name))
                    .Append("\",\"password\":\"").Append(Escape(a.Password))
                    .Append("\",\"level\":\"").Append(Escape(a.Level))
                    .Append("\",\"id\":").Append(a.Id)
                    .Append(",\"ip\":\"").Append(Escape(a.Ip))
                    .Append("\",\"lastIp\":\"").Append(Escape(a.LastIp))
                    .Append("\",\"machine\":\"").Append(Escape(a.Machine))
                    .Append("\",\"owner\":\"").Append(Escape(a.Owner))
                    .Append("\"}").Append(index == list.Length - 1 ? "\n" : ",\n");
            }

            json.Append(']');
            File.WriteAllText(Path, json.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[账号] 保存账号文件失败: " + ex.Message);
        }
    }
}
