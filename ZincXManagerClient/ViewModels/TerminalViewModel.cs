using System;  // 基础类型 / Interlocked / StringComparison
using System.Collections.Generic;  // List / Dictionary
using System.Collections.ObjectModel;  // 可观察集合(界面绑定用)
using System.Collections.Specialized;  // 集合变更事件参数
using System.Linq;  // 过滤 / 查找
using System.Text;  // StringBuilder(输出区整段文本)
using System.Threading;  // Interlocked
using System.Threading.Tasks;  // async / await 异步
using Avalonia.Threading;  // UI 线程调度
using CommunityToolkit.Mvvm.ComponentModel;  // MVVM 可观察对象
using ZincXManagerClient.Models;  // TerminalLine / ConsoleServer
using ZincXManagerShared.Network;  // 共享库:协议常量

namespace ZincXManagerClient.ViewModels;

/// <summary>RCON / ZXMM 桥接控制台的视图模型:命令上下行、目标服清单、持续输出开关。</summary>
public partial class TerminalViewModel : ViewModelBase
{
    /// <summary>终端最多保留的行数,跑久了不能把内存吃满。</summary>
    private const int MaxLines = 2000;

    /// <summary>等回执的命令最多记这么多条;超了整批丢掉,服务端不回也不能让字典长着。</summary>
    private const int MaxPending = 256;

    /// <summary>序号 → 已经上屏的命令行,681 回来按它对号。</summary>
    private readonly Dictionary<long, TerminalLine> _pending = new();

    /// <summary>输出区整段文本的拼装缓冲,行一变就跟着更新。</summary>
    private readonly StringBuilder _terminalText = new();

    /// <summary>已经拼好的整段文本,只在拼完那一刻换一次,免得每次读都重新 ToString。</summary>
    private string _terminalTextValue = "";

    /// <summary>命令序号,本地自增(服务端只原样带回,不需要全局唯一)。</summary>
    private long _sequence;

    /// <summary>打开页面时配置里选中的服,清单到了优先选它。</summary>
    private int _preferredServerId;

    /// <summary>搜索结果,换一批就整体替换。</summary>
    private IReadOnlyList<TerminalLine> _searchHits = Array.Empty<TerminalLine>();

    /// <summary>构造函数注入用不到;这里只是让编译器知道基类有个可观察对象。</summary>
    public TerminalViewModel()
    {
        Servers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanPickServer));
        Lines.CollectionChanged += OnTerminalLinesChanged;
    }

    /// <summary>终端内容(命令、回执、持续输出都在这里)。</summary>
    public ObservableCollection<TerminalLine> Lines { get; } = new();

    /// <summary>685 回来的目标服清单。</summary>
    public ObservableCollection<ConsoleServer> Servers { get; } = new();

    /// <summary>当前目标服;它同时就是下拉的选中项,发送时按它的编号走。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTap), nameof(IsRconMode))]
    public partial ConsoleServer? Target { get; set; }

    /// <summary>输入框里的命令。</summary>
    [ObservableProperty]
    public partial string InputText { get; set; } = "";

    /// <summary>是否开着持续输出(682);只有桥接模式的服支持。</summary>
    [ObservableProperty]
    public partial bool TapEnabled { get; set; }

    /// <summary>搜索框里的关键字。</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>搜索结果区是否展开,展开时占终端上半部分。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchButtonText))]
    public partial bool SearchActive { get; set; }

    /// <summary>输出区整段文本:只读文本框拿它显示,于是全选与复制交给文本框自己。</summary>
    public string TerminalText
    {
        get => _terminalTextValue;
        private set
        {
            if (string.Equals(_terminalTextValue, value, StringComparison.Ordinal))
            {
                return;
            }

            _terminalTextValue = value;
            OnPropertyChanged();
        }
    }

    /// <summary>搜索结果,按 <see cref="SearchText"/> 在本地过滤。</summary>
    public IReadOnlyList<TerminalLine> SearchHits => _searchHits;

    /// <summary>下拉能不能点:只有一台服时留着但禁用,别让人以为功能没了。</summary>
    public bool CanPickServer => Servers.Count > 1;

    /// <summary>当前服是不是桥接模式(0);只有它能开持续输出。</summary>
    public bool CanTap => Target is { ConsoleMode: 0 };

    /// <summary>当前服是不是 RCON 模式;是就显示「不支持持续输出」的小字。</summary>
    public bool IsRconMode => Target is { ConsoleMode: 1 };

    /// <summary>搜索按钮上的字:开着显示「收起」。</summary>
    public string SearchButtonText => SearchActive ? "收起" : "搜索";

    /// <summary>搜索结果条数,摆在结果区顶上。</summary>
    public string SearchSummary => $"搜索结果 {_searchHits.Count} 条";

    /// <summary>搜索结果一条都没有(给结果区一个空态提示)。</summary>
    public bool SearchEmpty => _searchHits.Count == 0;

    /// <summary>终端还一行都没有(给输出区一个空态提示)。</summary>
    public bool IsTerminalEmpty => Lines.Count == 0;

    /// <summary>把配置里当前选中的 MC 服务器记成优先目标;清单还没到时先存着。</summary>
    public void PreferServer(int serverId)
    {
        _preferredServerId = serverId;
        if (Servers.FirstOrDefault(server => server.Id == serverId) is { } hit)
        {
            Target = hit;
        }
    }

    /// <summary>发 684 要服务器清单;没人回也不等,界面空着就行。</summary>
    public async Task RefreshServersAsync()
    {
        var connect = ClientSession.Connect;
        if (connect is null)
        {
            AppendLine("out", "还没连上服务端,拿不到服务器清单");
            return;
        }

        if (!await connect.SendAsync(Protocol.ConsoleServersReq, string.Empty))
        {
            AppendLine("err", "服务器清单请求没发出去");
        }
    }

    /// <summary>处理 685:每行 <c>ID|名称|控制台模式|桥接在线|RCON在线</c>,坏行跳过。</summary>
    public void ApplyServers(string payload)
    {
        var servers = new List<ConsoleServer>();  //解析出来的新清单
        foreach (var row in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = row.Trim().Split('|');
            if (parts.Length < 3 || !int.TryParse(parts[0].Trim(), out var id) || id <= 0)
            {
                continue;
            }

            servers.Add(new ConsoleServer
            {
                Id = id,
                Name = parts[1].Trim(),
                ConsoleMode = parts[2].Trim() == "1" ? 1 : 0,
                BridgeOnline = parts.Length > 3 && parts[3].Trim() == "1",
                RconOnline = parts.Length > 4 && parts[4].Trim() == "1",
            });
        }

        Dispatcher.UIThread.Post(() =>
        {
            Servers.Clear();
            foreach (var server in servers)
            {
                Servers.Add(server);
            }

            // 先保住原来选的那台,其次配置里那台,最后退回第一台
            var keepId = Target?.Id ?? _preferredServerId;
            Target = Servers.FirstOrDefault(server => server.Id == keepId) ?? Servers.FirstOrDefault();
            OnPropertyChanged(nameof(CanPickServer));
        });
    }

    /// <summary>发 680 一条命令:负载是 <c>选中编号|命令</c>,编号取下拉当前选中的那台。</summary>
    public async Task SendAsync(string command)
    {
        var text = command.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var target = Target;
        if (target is null)
        {
            AppendLine("err", "还没选目标服");
            return;
        }

        var connect = ClientSession.Connect;
        if (connect is null)
        {
            AppendLine("err", "还没连上服务端");
            return;
        }

        var sequence = Interlocked.Increment(ref _sequence);
        var line = new TerminalLine { Kind = "cmd", Text = text };

        // 序号和命令行都在 UI 线程登记,681 回来也是 UI 线程查,不用加锁
        Dispatcher.UIThread.Post(() =>
        {
            if (_pending.Count >= MaxPending)
            {
                _pending.Clear();
            }

            _pending[sequence] = line;
            AppendLineCore(line);
            InputText = "";
        });

        if (!await connect.SendAsync(Protocol.ConsoleCommand, $"{target.Id}|{text}"))
        {
            AppendLine("err", $"命令没发出去:{text}");
        }
    }

    /// <summary>处理 681:<c>序号|ok|文本</c> 或 <c>序号|err|原因</c>;对不上序号也照样上屏。</summary>
    public void ApplyResult(string payload)
    {
        var parts = payload.Split('|', 3);  //文本里可能还有竖线,只切两刀
        if (!long.TryParse(parts[0].Trim(), out var sequence))
        {
            AppendLine("out", payload);
            return;
        }

        var ok = parts.Length > 1 && parts[1].Trim() == "ok";
        var text = parts.Length > 2 ? parts[2] : "";

        Dispatcher.UIThread.Post(() =>
        {
            // 断线重连后序号可能换过,找不到也照样显示,不能把服务端的回答吞掉
            _pending.Remove(sequence);
            if (text.Length == 0)
            {
                text = ok ? "(无输出)" : "(没有原因)";
            }

            foreach (var row in text.Split('\n'))
            {
                AppendLineCore(new TerminalLine { Kind = ok ? "ok" : "err", Text = row.TrimEnd('\r') });
            }
        });
    }

    /// <summary>处理 682 的一行持续输出;只显示当前目标服的,别把别的服串进来。</summary>
    public void AppendOutput(int serverId, string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Target is { } target && target.Id != serverId)
            {
                return;
            }

            AppendLineCore(new TerminalLine { Kind = "out", Text = text.TrimEnd('\r') });
        });
    }

    /// <summary>发 683 开 / 关持续输出;编号取下拉当前选中的那台,发不出去就把开关拨回去。</summary>
    public async Task SetTapAsync(bool on)
    {
        var target = Target;
        if (target is not { ConsoleMode: 0 })
        {
            Dispatcher.UIThread.Post(() => TapEnabled = false);
            return;
        }

        var connect = ClientSession.Connect;
        if (connect is null)
        {
            AppendLine("err", "还没连上服务端");
            return;
        }

        TapEnabled = on;
        if (!await connect.SendAsync(Protocol.ConsoleTap, $"{target.Id}|{(on ? "1" : "0")}"))
        {
            Dispatcher.UIThread.Post(() => TapEnabled = !on);
            AppendLine("err", "持续输出开关没发出去");
        }
    }

    /// <summary>按当前开关和选中的服,把 683 重新发一次。</summary>
    /// <remarks>
    /// 开关是客户端变量(切走再回来还是开着),而 ZXMS 那边一处连接只认一台服 —— 切服、重连、
    /// 重新打开控制台都会让它指向旧服,所以每次目标确定下来都要重新登记一次,否则界面显示"开"
    /// 但一台服都没在推。
    /// </remarks>
    public async Task ReapplyTapAsync()
    {
        var target = Target;
        if (!TapEnabled || target is not { ConsoleMode: 0 })
        {
            return;
        }

        var connect = ClientSession.Connect;
        if (connect is null)
        {
            return;
        }

        await connect.SendAsync(Protocol.ConsoleTap, $"{target.Id}|1");
    }

    /// <summary>清空终端内容(只清屏幕,不动服务端任何东西)。</summary>
    public void Clear()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Lines.Clear();
            _pending.Clear();
            RefreshSearchCore();
        });
    }

    /// <summary>按当前关键字重算搜索结果(网络线程也能调,内部回 UI 线程)。</summary>
    public void RefreshSearch() => Dispatcher.UIThread.Post(RefreshSearchCore);

    /// <summary>在当前线程重算搜索结果。</summary>
    private void RefreshSearchCore()
    {
        var keyword = SearchText.Trim();
        _searchHits = keyword.Length == 0
            ? Lines.ToList()
            : Lines.Where(line => line.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        OnPropertyChanged(nameof(SearchHits));
        OnPropertyChanged(nameof(SearchSummary));
        OnPropertyChanged(nameof(SearchEmpty));
    }

    /// <summary>行集合变了就同步输出区整段文本,顺带刷新空态与搜索结果。</summary>
    private void OnTerminalLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (var line in e.NewItems?.Cast<TerminalLine>() ?? [])
                {
                    _terminalText.AppendLine(line.Display);
                }

                break;

            case NotifyCollectionChangedAction.Reset:
                _terminalText.Clear();
                break;

            default:
                // 超过上限要砍行,只能整段重拼
                _terminalText.Clear();
                foreach (var line in Lines)
                {
                    _terminalText.AppendLine(line.Display);
                }

                break;
        }

        TerminalText = _terminalText.ToString();
        OnPropertyChanged(nameof(IsTerminalEmpty));
        if (SearchActive)
        {
            RefreshSearchCore();
        }
    }

    /// <summary>往终端追加一行(内部用,调用方保证已经在 UI 线程)。</summary>
    private void AppendLineCore(TerminalLine line)
    {
        Lines.Add(line);
        while (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(0);
        }
    }

    /// <summary>追加一行提示(可能从网络线程来,先回 UI 线程)。</summary>
    private void AppendLine(string kind, string text) =>
        Dispatcher.UIThread.Post(() => AppendLineCore(new TerminalLine { Kind = kind, Text = text }));

    /// <summary>目标服换了就重新登记持续输出;切到 RCON 服时把开关收起来,那模式没这条通道。</summary>
    partial void OnTargetChanged(ConsoleServer? value)
    {
        if (value is not { ConsoleMode: 0 })
        {
            TapEnabled = false;
            return;
        }

        // 打开控制台拉完清单、切到另一台,都会走到这里:开关开着就重新发 683,让推送跟着目标走
        if (TapEnabled)
        {
            _ = ReapplyTapAsync();
        }
    }
}
