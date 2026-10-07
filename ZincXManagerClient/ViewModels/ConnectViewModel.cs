using System;
using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ZincXManagerClient.Logging;
using ZincXManagerClient.Models;
using ZincXManagerShared.FileIO;
using ZincXManagerShared.Logging;
using ZincXManagerShared.Network;

// 与 BCL 的 TcpClient 同名,起个别名
using TcpClient = ZincXManagerShared.Network.TcpClient;

namespace ZincXManagerClient.ViewModels;

/// <summary>
/// 连接界面的视图模型:选连接模式 → 连接 → 收服务端身份包校验 → 实时日志。
///
/// <para><b>协议(与 ZXMS / ZXMOS 约定):</b></para>
/// <code>
/// 200 = ServerIdent  服务端 → 对方:我是谁(负载就是 "ZXMS" 或 "ZXMOS")
/// 300 = Conline      ZXMC  → 服务端:客户端上线
/// 301 = CBack        服务端 → ZXMC:收到
/// </code>
///
/// <para><b>校验方式(连上之后校验,不是连之前拦):</b>连上后服务端会主动发 200 报身份,
/// 客户端拿它跟当前模式比 —— OS/SAOS 必须是 ZXMS,OOS 必须是 ZXMOS;
/// 对不上就立刻断开并标红,对得上才继续(等 301 完成握手)。</para>
/// </summary>
public partial class ConnectViewModel : ViewModelBase, IDisposable
{
    /// <summary>服务端身份声明(负载为 "ZXMS" / "ZXMOS")。</summary>
    public const ushort ServerIdent = 200;

    /// <summary>ZXMC 上线(发给服务端)。</summary>
    public const ushort Conline = 300;

    /// <summary>服务端收到(服务端回给 ZXMC)。</summary>
    public const ushort CBack = 301;

    // ---------- 客户端自己的 config.ini 键名(与两个服务端同一套命名) ----------
    private const string KeyCzip = "czip";      // 子服务端(ZXMS)地址
    private const string KeyCzport = "czport";  // 子服务端端口
    private const string KeyOzip = "ozip";      // 官方服务端(ZXMOS)地址
    private const string KeyOzport = "ozport";  // 官方服务端端口

    // 从 config.ini 读到的两组目标(切模式时自动填进输入框,仍可手动改)
    private readonly string _czip;
    private readonly string _czport;
    private readonly string _ozip;
    private readonly string _ozport;

    private readonly UiLogSink _sink;
    private TcpClient? _client;

    /// <summary>身份校验没通过(已断开),用来忽略后续到达的 301。</summary>
    private bool _rejected;

    /// <summary>连接模式:OS(仅子服务端)/ OOS(仅官方服务端)/ SAOS(两者)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOs), nameof(IsOos), nameof(IsSaos), nameof(ModeHint))]
    public partial string Mode { get; set; } = "SAOS";

    /// <summary>服务端地址(可手动改,连接后由服务端身份包校验)。</summary>
    [ObservableProperty]
    public partial string Host { get; set; } = "127.0.0.1";

    /// <summary>服务端端口(可手动改)。</summary>
    [ObservableProperty]
    public partial string Port { get; set; } = "21000";

    /// <summary>状态文字。</summary>
    [ObservableProperty]
    public partial string State { get; set; } = "未连接";

    /// <summary>状态点颜色。</summary>
    [ObservableProperty]
    public partial IBrush StatusBrush { get; set; } = new SolidColorBrush(Color.Parse("#7C99A6"));

    /// <summary>是否正在连接。</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>是否已连上(身份校验通过 + 收到 301)。</summary>
    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    /// <summary>实时日志(界面直接绑定;最多保留最近 500 条)。</summary>
    public ObservableCollection<LogEntry> Logs { get; } = new();

    public bool IsOs => Mode == "OS";
    public bool IsOos => Mode == "OOS";
    public bool IsSaos => Mode == "SAOS";

    /// <summary>这个模式要求对方是什么端。</summary>
    private string ExpectRole => Mode == "OOS" ? "ZXMOS" : "ZXMS";

    /// <summary>界面上的模式说明:这个模式连的是谁(来源 config.ini)。</summary>
    public string ModeHint => Mode switch
    {
        "OS" => $"OS:子服务端 ZXMS(czip:czport = {_czip}:{_czport})",
        "OOS" => $"OOS:官方服务端 ZXMOS(ozip:ozport = {_ozip}:{_ozport})",
        _ => $"SAOS:先连子服务端 {_czip}:{_czport},由它转发官方服务端 {_ozip}:{_ozport}",
    };

    public ConnectViewModel()
    {
        // 把共享库日志接到界面:网络/文件/命令的日志都会实时显示
        _sink = new UiLogSink(this);
        Logger.AddSink(_sink);

        Logger.Log(LogLevel.Info, "[ZXMC] 客户端启动");

        // 读客户端自己的配置:子服务端 / 官方服务端各一组地址端口,缺了就写缺省值
        var config = new ZFile();
        if (!config.FileOperate("config.ini", "rwNFC"))
        {
            Logger.Log(LogLevel.Warning, "[ZXMC] 打开 config.ini 失败,使用缺省地址");
        }

        _czip = EnsureValue(config, KeyCzip, "127.0.0.1");
        _czport = EnsureValue(config, KeyCzport, "21000");
        _ozip = EnsureValue(config, KeyOzip, "127.0.0.1");
        _ozport = EnsureValue(config, KeyOzport, "20000");
        config.Dispose();

        // 按当前模式填好输入框(SAOS 默认先连子服务端)
        Host = _czip;
        Port = _czport;
    }

    /// <summary>配置项缺省补齐:没有这个键(或值为空)就写进缺省值并返回它。</summary>
    private static string EnsureValue(ZFile file, string key, string defaultValue)
    {
        var value = file.MapGet(key).Trim();
        if (value.Length > 0)
        {
            return value;
        }

        file.WriteFileMap(key, defaultValue);
        Logger.Log(LogLevel.Info, $"[CONFIG]缺少 {key},写入缺省值 {defaultValue}");
        return defaultValue;
    }

    /// <summary>
    /// 按当前模式把目标地址重填成配置里的值。
    /// 点模式按钮时调用 —— 重复点同一个模式也重新填一次,避免"模式没变、端口还是上一个"的坑。
    /// </summary>
    public void ApplyModeTarget()
    {
        if (Mode == "OOS")
        {
            Host = _ozip;
            Port = _ozport;
            Logger.Log(LogLevel.Info, $"[ZXMC] 模式 OOS(要求官方服务端 ZXMOS)→ 目标 {Host}:{Port}");
        }
        else if (Mode == "OS")
        {
            Host = _czip;
            Port = _czport;
            Logger.Log(LogLevel.Info, $"[ZXMC] 模式 OS(要求子服务端 ZXMS)→ 目标 {Host}:{Port}");
        }
        else
        {
            Host = _czip;
            Port = _czport;
            Logger.Log(LogLevel.Info, $"[ZXMC] 模式 SAOS → 先连子服务端 {Host}:{Port},由 ZXMS 转发官方服务端");
        }
    }

    /// <summary>切换模式时按模式填一次目标地址(仍可手动改)。</summary>
    partial void OnModeChanged(string value) => ApplyModeTarget();

    /// <summary>追加一行日志(由 <see cref="UiLogSink"/> 在 UI 线程调用)。</summary>
    public void AppendLog(LogLevel level, string message)
    {
        Logs.Add(new LogEntry(DateTime.Now, level.GetName(), message));
        while (Logs.Count > 500)
        {
            Logs.RemoveAt(0);
        }
    }

    /// <summary>
    /// 连接:建连接 → 双方互发身份/上线包 → 收到服务端身份包后按模式校验。
    /// 校验放在连上之后做,不匹配就立刻断开。
    /// </summary>
    public async Task ConnectAsync()
    {
        if (IsBusy) return;

        var host = Host.Trim();
        var portText = Port.Trim();

        if (!IPAddress.TryParse(host, out var ip))
        {
            Logger.Log(LogLevel.Warning, "[ZXMC] 地址不合法:" + host);
            return;
        }

        if (!ushort.TryParse(portText, out var port) || port == 0)
        {
            Logger.Log(LogLevel.Warning, "[ZXMC] 端口不合法:" + portText);
            return;
        }

        _rejected = false;
        IsBusy = true;
        SetState("连接中…", "#F0A020");
        Logger.Log(LogLevel.Info, $"[ZXMC] 模式 {Mode}(要求 {ExpectRole}),连接 {ip}:{port}");
        AddLogOnly($"本轮要求对方身份:{ExpectRole}");

        var client = new TcpClient(ip, port);

        client.AddHandler(packet =>
        {
            // 回调在网络线程,切回 UI 线程再改界面
            Dispatcher.UIThread.Post(() =>
            {
                var text = Encoding.UTF8.GetString(packet.Data);

                switch (packet.Type)
                {
                    // 服务端自报身份 → 按模式校验
                    case ServerIdent:
                        Logger.Log(LogLevel.Info, $"[ZXMC] 收到服务端身份包:{text}");
                        CheckServerIdent(client, text.Trim(), ip, port);
                        break;

                    // 服务端确认上线(校验通过才算连上)
                    case CBack:
                        Logger.Log(LogLevel.Info, $"[ZXMC] 收到上线确认:{text}");
                        if (!_rejected)
                        {
                            IsConnected = true;
                            SetState($"已连接 {ExpectRole} {ip}:{port}", "#22A05A");
                        }
                        break;

                    default:
                        Logger.Log(LogLevel.Info, $"[ZXMC] 收到 type={packet.Type} len={packet.Data.Length}: {text}");
                        break;
                }
            });
            return Task.CompletedTask;
        });

        try
        {
            await client.ConnectAsync();
            _client = client;

            // 报上自己是谁 + 上线
            await client.WriteAsync(new Packet(1, Conline, Encoding.UTF8.GetBytes("ZXMC online")));
            Logger.Log(LogLevel.Info, "[ZXMC] TCP 已连接,已发送上线包(300),等待服务端身份包(200)校验");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "[ZXMC] 连接失败: " + ex.Message);
            SetState("连接失败", "#D64545");
            await client.CloseAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 身份校验:服务端自报的端类型必须与模式要求一致,否则立刻断开。
    /// </summary>
    private void CheckServerIdent(TcpClient client, string role, IPAddress ip, ushort port)
    {
        if (role == ExpectRole)
        {
            Logger.Log(LogLevel.Info, $"[ZXMC] 身份校验通过:{role} 符合模式 {Mode}");
            SetState($"已连上 {role} {ip}:{port},等待确认", "#F0A020");
            return;
        }

        _rejected = true;
        IsConnected = false;
        Logger.Log(LogLevel.Error,
            $"[ZXMC] 身份校验不通过:模式 {Mode} 要求 {ExpectRole},但 {ip}:{port} 自报是 {role} —— 已断开");
        SetState("模式与目标不匹配,已断开", "#D64545");

        // 用闭包里的 client(连接可能还没赋给字段),直接断开
        _ = client.CloseAsync();
        _client = null;
    }

    /// <summary>断开连接。</summary>
    public async Task DisconnectAsync()
    {
        if (_client is null)
        {
            Logger.Log(LogLevel.Info, "[ZXMC] 当前没有连接");
            return;
        }

        await _client.CloseAsync();
        _client = null;
        IsConnected = false;
        _rejected = false;
        SetState("未连接", "#7C99A6");
        Logger.Log(LogLevel.Info, "[ZXMC] 已断开连接");
    }

    /// <summary>清空日志面板。</summary>
    public void ClearLogs() => Logs.Clear();

    private void SetState(string text, string color)
    {
        State = text;
        StatusBrush = new SolidColorBrush(Color.Parse(color));
    }

    private void AddLogOnly(string message) => Logger.Log(LogLevel.Info, "[ZXMC] " + message);

    public void Dispose()
    {
        Logger.RemoveSink(_sink);

        // 界面关掉时顺手断开连接(CloseAsync 是异步的,这里不等它)
        _ = _client?.CloseAsync();
        _client = null;
    }
}
