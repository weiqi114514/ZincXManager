using System;  // 基础类型 / 常用静态方法
using System.Linq;  // LINQ 查询
using System.Text;  // 文本编码与 StringBuilder
using System.Threading.Tasks;  // async / await 异步
using Avalonia;  // 界面框架入口
using Avalonia.Animation;  // 界面框架
using Avalonia.Animation.Easings;  // 界面框架
using Avalonia.Controls;  // 窗口与控件
using Avalonia.Controls.Shapes;  // 图形(线 / 矩形 / 路径)
using Avalonia.Styling;  // 样式与主题
using Avalonia.Input;  // 鼠标键盘输入
using Avalonia.Interactivity;  // 路由事件
using Avalonia.Media;  // 画刷 / 颜色 / 几何
using Avalonia.Input.Platform;  // ClipboardExtensions:IClipboard 没有 SetTextAsync 实例方法,要靠扩展
using ZincXManagerClient.Chat;  // ChatBridge:收 510 时登记聊天后端地址
using ZincXManagerClient.Models;  // 客户端自身
using ZincXManagerClient.Services;  // 导入 UpdateService(启动查更新)
using ZincXManagerClient.ViewModels;  // 客户端自身
using ZincXManagerShared.Friend;  // FriendCodec / ChatLog:好友负载编解码与本地聊天记录
using ZincXManagerShared.Logging;  // 共享库
using ZincXManagerShared.Network;  // 共享库

namespace ZincXManagerClient.Views;

public partial class MainWindow : Window
{
    /// <summary>主窗口的构造函数:挂心跳、拉伸、拖拽,恢复上次状态。</summary>
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => ScrollDrag.AttachAll(this);

        // 左下角 ID 那行:右键「复制 ID」,把完整账号 ID 抄进剪贴板(别人拿它搜得到我)
        UserIdText.ContextMenu = BuildCopyIdMenu();

        // 启动后按设置查一次更新(不挡启动,失败只弹提示条)
        Opened += (_, _) => _ = CheckUpdateOnStartupAsync();

        // 小功能组:点条目 → 主窗口决定打开什么
        WidgetsPage.WidgetActivated += OnWidgetActivated;

        // 绑定码 6 格:填满一格自动跳下一格
        var codeBoxes = new[] { Code0, Code1, Code2, Code3, Code4, Code5 };
        for (var i = 0; i < codeBoxes.Length; i++)
        {
            var index = i;
            codeBoxes[i].PropertyChanged += (_, args) =>
            {
                if (args.Property != TextBox.TextProperty)
                {
                    return;
                }

                if (codeBoxes[index].Text is { Length: >= 1 } && index < codeBoxes.Length - 1)
                {
                    codeBoxes[index + 1].Focus();
                }
            };
        }

        // 绑定 / 解绑回执:关掉覆盖层并告诉用户结果
        BindAckArrived += (ok, detail) =>
        {
            BindOverlay.IsVisible = false;
            _ = ok ? MessageDialog.InfoAsync(this, "绑定成功", detail)
                   : MessageDialog.WarnAsync(this, "操作失败", detail);
        };
        // 右下角拖拽手柄:窗口没有系统边框,靠它拉大小
        ResizeGrip.PointerPressed += OnGripPressed;
        ResizeGrip.PointerMoved += OnGripMoved;
        ResizeGrip.PointerReleased += OnGripReleased;
        // 心跳:每 5 秒看一次连接还在不在;断了就用红色提示条告知并自动重连(最多 5 次)
        Opened += (_, _) => StartHeartbeat();
        Opened += (_, _) => RefreshFunctionPages();

        // 调试用:config 里写 testmsg=1 就塞两条消息(Markdown / HTML),看渲染效果
        Opened += (_, _) =>
        {
            if (ClientConfig.GetOrEmpty("testmsg") == "1")
            {
                Vm?.AddMessage(new Models.MessageItem
                {
                    Title = "Markdown 测试",
                    Body = "# 大标题\n## 小标题\n这是 **粗体** 和反引号代码的段落。\n- 列表项一\n- 列表项二",
                });
                Vm?.AddMessage(new Models.MessageItem
                {
                    Title = "HTML 测试",
                    Body = "<h2>HTML 标题</h2><p>带粗体和斜体的段落。</p><ul><li>第一项</li><li>第二项</li></ul>",
                });
            }
        };

        // 调试用:config 里写 testtoast=1 就弹一条长提示,看换行/高度对不对
        Opened += (_, _) =>
        {
            if (ClientConfig.GetOrEmpty("testtoast") == "1")
            {
                // 测试用:隔两秒弹一次,方便截图
                var left = 8;
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) =>
                {
                    Vm?.ShowToast("已确认对方是官方服务端:锌能源ZXMOS官方服务器(序列号 ZXM-2026-0002),欢迎回来", ToastKind.Success);
                    if (--left <= 0)
                    {
                        timer.Stop();
                    }
                };
                timer.Start();
            }
        };

        // 登录后才建立连接:前几秒每秒核对一次"官方社区"该不该显示(连上就显示,断开就收)
        Opened += (_, _) => StartOfficialWatch();

        // 窗口材质只在这里定一次:运行中改 TransparencyLevelHint 会让 Windows 重建窗口,
        // 屏幕上会闪出"像多了一个窗口"的东西,所以开关只换材料、不动材质
        ThemeManager.ApplyWindowMaterial(this);

        Opened += (_, _) => ApplyMaterial(ThemeManager.GlassEnabled);
        ThemeManager.GlassChanged += ApplyMaterial;
        Closed += (_, _) => ThemeManager.GlassChanged -= ApplyMaterial;

        // 服务端下发的消息(公告 500 / 定向推送 501)接到右侧消息面板
        Opened += (_, _) => AttachSession();
        Closed += (_, _) => DetachSession();

        // 窗口关掉就停心跳:否则关了主界面还在后台重连,连不上又弹一个新窗口出来
        Closed += (_, _) =>
        {
            _heartbeat?.Stop();
            _heartbeat = null;
        };

        // 调试 / 自动化(和 skipauth / startpage 一个路子):config 里 testaction=customaddress
        // 就直接走一遍"改用第三方官方服务端"的流程,用来测 5 秒阅读确认
        Opened += async (_, _) =>
        {
            if (ClientConfig.GetOrEmpty("testaction") != "customaddress")
            {
                return;
            }

            await Task.Delay(800);
            Logger.Log(LogLevel.Info, "[ZXMC] testaction=customaddress:准备打开第三方官方服务端风险说明");
            await SettingsPage.OpenCustomAddressFlowAsync();
        };
    }

    /// <summary>左下角 ID 的右键菜单:选中行就能复制,不用去别处翻。</summary>
    private ContextMenu BuildCopyIdMenu()
    {
        var copy = new MenuItem { Header = "复制 ID" };
        copy.Click += async (_, _) => await CopyUserIdAsync();
        return new ContextMenu { Items = { copy } };
    }

    /// <summary>把当前账号的 ID 抄进剪贴板:复制完整ID(别人拿它搜得到我),不能再是左下角那个短码。</summary>
    private async Task CopyUserIdAsync()
    {
        // 左下角只显示最后的编号,完整值存在 ViewModel 里;它没有时才退到会话缓存
        var text = (Vm?.UserIdFull ?? "").Trim();
        if (text.Length == 0)
        {
            text = ClientSession.FullId.Trim();
        }

        if (text.Length == 0)
        {
            text = ClientSession.UserId.Trim();
        }

        if (text.Length == 0)
        {
            Vm?.ShowToast("还没登录,没有 ID 可复制", ToastKind.Warning);
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            Vm?.ShowToast("复制失败:这台机器上拿不到剪贴板", ToastKind.Warning);
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            Vm?.ShowToast("已复制 ID:" + text, ToastKind.Success);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[ZXMC] 复制账号 ID 失败: " + ex.Message);
            Vm?.ShowToast("复制失败:" + ex.Message, ToastKind.Warning);
        }
    }

    /// <summary>挂上当前会话的收包事件(连接视图模型已经在 UI 线程回调,这里直接改界面)。</summary>
    private void AttachSession()
    {
        if (ClientSession.Connect is { } connect)
        {
            connect.PacketReceived += OnServerPacket;
            connect.OfficialChecked += OnOfficialChecked;
            connect.PropertyChanged += OnConnectChanged;

            // 官方社区卡片按实际连接状态显示(直连 ZXMOS / 经子服连官方都算)
            Vm?.RefreshOfficialVisibility();
        }

        // 窗口开起来再要一次历史消息(504 补发):这一刻收包事件才挂上,不然补发的包没人接。
        // 已经在登录成功时要过一次的,这里再要一次也不要紧 —— 客户端按「来源+消息号」去重,不会多出一条。
        if (Vm is { } vm)
        {
            _ = vm.RequestMessageHistoryAsync();
        }
    }

    // ---------- 消息面板页签 ----------

    /// <summary>点页签:切到对应分类。</summary>
    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not Button b || b.DataContext is not Models.MessageTab tab)
        {
            return;
        }

        var index = Vm.Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Logger.Log(LogLevel.Info, $"[ZXMC] 切到页签:{tab.Label}(index={index})");
        Vm.SelectTab(index);

        AnimateUnderline(Vm.UnderlineOffset);
    }

    /// <summary>蓝色小条滑到目标位置(180ms);动画失败就直接跳位,不影响切换。</summary>
    private void AnimateUnderline(double to)
    {
        if (this.FindControl<Rectangle>("TabUnderline")?.RenderTransform is not TranslateTransform t)
        {
            return;
        }

        // 先把逻辑位置定下来:即使动画库出问题,小条也不会停在旧位置
        t.X = to;

        try
        {
            var animation = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(180),
                Easing = new CubicEaseOut(),
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(1d),
                        Setters =
                        {
                            new Setter { Property = TranslateTransform.XProperty, Value = to },
                        },
                    },
                },
            };

            _ = animation.RunAsync(t);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[ZXMC] 页签小条动画失败(已直接跳位): " + ex.Message);
        }
    }

    /// <summary>刷新日志。</summary>
    private void OnRefreshLogClick(object? sender, RoutedEventArgs e) => Vm?.LoadLog();

    /// <summary>刷新更新日志。</summary>
    private void OnRefreshChangelogClick(object? sender, RoutedEventArgs e) => Vm?.LoadChangelog();

    /// <summary>刷新网页。</summary>
    private void OnLoadWebClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null)
        {
            _ = Vm.LoadWebAsync();
        }
    }

    /// <summary>用系统默认浏览器打开自定义网页。</summary>
    private async void OnOpenWebClick(object? sender, RoutedEventArgs e)
    {
        var url = Vm?.WebUrl ?? "";
        if (url.Length == 0)
        {
            return;
        }

        // 有多个浏览器时让用户选(火狐 / Chrome / Edge / 360…),只有一个就直接用
        var browsers = Models.BrowserLauncher.List();

        try
        {
            if (browsers.Count > 1)
            {
                var names = browsers.Select(b => b.Name).ToArray();
                var picked = await MessageDialog.ChooseAsync(this, "用浏览器打开",
                    "想在哪个浏览器里看这个页面?", names, "打开", "选一个就行");

                if (picked is null)
                {
                    return;
                }

                var target = browsers.FirstOrDefault(b => b.Name == picked);
                if (target.Path is null)
                {
                    return;
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target.Path, url)
                {
                    UseShellExecute = true,
                });
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[ZXMC] 打开网页失败: " + ex.Message);
        }
    }

    /// <summary>窗口关掉就退订,免得连接还活着却一直往已销毁的界面写。</summary>
    private void DetachSession()
    {
        if (ClientSession.Connect is { } connect)
        {
            connect.PacketReceived -= OnServerPacket;
            connect.OfficialChecked -= OnOfficialChecked;
        }

        // 好友窗是独立顶层窗,主窗关了它不会跟着关:留着它只会照常发 650/656 却再也收不到 651/662
        _friendsWindow?.Close();
        _friendsWindow = null;
    }

    /// <summary>官方验证第二层的结果:通过就轻描淡写提一句;不是官方服务端就明确警告(官方构建会直接断开)。</summary>
    private Avalonia.Threading.DispatcherTimer? _officialTimer;

    private void StartOfficialWatch()
    {
        _officialTimer?.Stop();
        var ticks = 0;
        _officialTimer = new Avalonia.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1) };
        _officialTimer.Tick += (_, _) =>
        {
            Vm?.RefreshOfficialVisibility();
            if (++ticks >= 10)
            {
                _officialTimer?.Stop();
            }
        };
        _officialTimer.Start();
    }

    /// <summary>连接状态变化后刷新。</summary>
    private void OnConnectChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectViewModel.IsConnected))
        {
            Vm?.RefreshOfficialVisibility();
        }
    }

    /// <summary>处理官方验证结果。</summary>
    private void OnOfficialChecked(bool ok, string detail)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        if (ok)
        {
            vm.ShowToast($"已确认对方是官方服务端:{detail}", ToastKind.Success);
            return;
        }

        vm.ShowToast("注意:对方不是官方服务端", ToastKind.Warning, 6000);
        Logger.Log(LogLevel.Warning, "[ZXMC] 官方验证提示:" + detail);
    }

    /// <summary>绑定/解绑回执:窗口在就订阅显示,没人订阅直接弹框</summary>
    public event Action<bool, string>? BindAckArrived;

    // 当前账号绑定的 MC 玩家名(空 = 没绑)
    private string _boundPlayer = "";

    /// <summary>问服务端:我这个账号绑了谁(629,回 630)。</summary>
    private async void QueryBindAsync()
    {
        var connect = ClientSession.Connect;
        if (connect is null)
        {
            return;
        }

        try
        {
            await connect.SendAsync(Protocol.BindQuery, string.Empty);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[绑定] 查询失败: " + ex.Message);
        }
    }

    /// <summary>绑定二次确认:用户点了「确定绑定」才把令牌发回服务端(631)。</summary>
    private async Task ConfirmBindAsync(string token, string player)
    {
        var connect = ClientSession.Connect;
        if (connect is null || token.Length == 0)
        {
            return;
        }

        var agree = await MessageDialog.ConfirmAsync(this, "绑定确认",
            $"确定把当前账号绑定到 MC 玩家「{player}」吗？\n绑定后这名玩家进服时，需要你在登录请求上点同意。",
            yes: "确定绑定", no: "取消", hint: "绑定码是一次性的，确认后才会真正写入服务端");

        if (!agree)
        {
            Logger.Log(LogLevel.Info, "[绑定] 用户取消了绑定确认");
            return;
        }

        try
        {
            await connect.SendAsync(Protocol.BindConfirm, token);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[绑定] 确认发送失败: " + ex.Message);
        }
    }

    /// <summary>收到登录请求(621):弹窗问玩家,回答发回服务端(622)。 绑定码窗口做好之前先用通用对话框顶着。</summary>
    private async Task HandleLoginAskAsync(string requestId, string player, string ip, string geo)
    {
        var connect = ClientSession.Connect;
        if (connect is null || requestId.Length == 0)
        {
            return;
        }

        // 专用弹窗(外观和 MessageDialog 一套):显示账号 / IP / 归属地,让玩家决定放不放行
        var dialog = new LoginRequestWindow(player, ip, geo);
        await dialog.ShowDialog(this);
        var agree = dialog.Agreed;

        try
        {
            await connect.SendAsync(Protocol.LoginAnswer, $"{requestId}|{(agree ? "yes" : "no")}");
            Logger.Log(LogLevel.Info, $"[登录] 已回服务端:请求 {requestId} → {(agree ? "同意" : "拒绝")}");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[登录] 回执发送失败: " + ex.Message);
        }
    }


    /// <summary>小功能组点了某个功能:绑定走覆盖层,好友开独立窗,其余先提示。</summary>
    private void OnWidgetActivated(string key)
    {
        switch (key)
        {
            case "bind":
                ShowBindOverlay();
                break;

            case "friends":
                ShowFriendsWindow();
                break;

            default:
                _ = MessageDialog.InfoAsync(this, "还没接", $"「{key}」这个功能还没接上,先把绑定/解绑做完。");
                break;
        }
    }

    /// <summary>打开绑定 / 解绑覆盖层(灰色遮罩 + 无边框面板),先显示两个大选项。</summary>
    private void ShowBindOverlay()
    {
        BindHeader.Text = "绑定 / 解绑";
        BindChoicePanel.IsVisible = true;
        BindInputPanel.IsVisible = false;
        UnbindPanel.IsVisible = false;
        BindHint.Text = "";
        foreach (var box in new[] { Code0, Code1, Code2, Code3, Code4, Code5 })
        {
            box.Text = "";
        }

        BindOverlay.IsVisible = true;

        // 顺手问一下服务端:这个账号现在绑了谁(解绑页要显示出来)
        QueryBindAsync();
    }

    /// <summary>关掉覆盖层。</summary>
    private void OnBindCloseClick(object? sender, RoutedEventArgs e)
    {
        BindOverlay.IsVisible = false;
    }

    /// <summary>选"绑定"或"解绑"。</summary>
    private void OnBindChoiceClick(object? sender, RoutedEventArgs e)
    {
        var tag = (sender as Button)?.Tag as string ?? "";
        BindChoicePanel.IsVisible = false;

        if (tag == "bind")
        {
            BindHeader.Text = "绑定";
            BindInputPanel.IsVisible = true;
            Code0.Focus();
        }
        else
        {
            BindHeader.Text = "解绑";
            UnbindPanel.IsVisible = true;
        }
    }

    /// <summary>提交绑定码(626)。</summary>
    private async void OnBindSubmitClick(object? sender, RoutedEventArgs e)
    {
        var code = string.Concat(Code0.Text, Code1.Text, Code2.Text, Code3.Text, Code4.Text, Code5.Text)
            .Trim().ToUpperInvariant();

        if (code.Length != 6)
        {
            BindHint.Text = "6 位都要填满";
            return;
        }

        if (Vm is not { } vm)
        {
            BindHint.Text = "界面还没准备好";
            return;
        }

        BindHint.Text = "正在提交…";
        BindHint.Foreground = Avalonia.Media.Brush.Parse("#4A6B78");
        if (!await vm.SendBindAsync(code))
        {
            BindHint.Text = "发送失败,检查连接";
            BindHint.Foreground = Avalonia.Media.Brush.Parse("#C62828");
        }
    }

    /// <summary>确认解绑(628)。</summary>
    private async void OnUnbindSubmitClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.SendUnbindAsync();
        }
    }

    private void OnServerPacket(Packet packet)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var text = Encoding.UTF8.GetString(packet.Data);   //包内容

        switch (packet.Type)
        {
            case Protocol.Notice:
                vm.ReceiveCard(text);
                _ = vm.AckMessageAsync(text);
                Logger.Log(LogLevel.Info, "[ZXMC] 收到公告:" + MessageCard.Summary(text));
                break;

            // 补发的一条历史消息(504):本地已经有的会自己跳过
            case Protocol.MessageHistory:
                _ = vm.AckMessageAsync(text);
                Logger.Log(LogLevel.Info, vm.AddHistoryCard(text)
                    ? "[ZXMC] 补齐一条历史消息:" + MessageCard.Summary(text)
                    : "[ZXMC] 这条历史消息本地已经有了,跳过");
                break;

            // 补发结束(506):负载是服务端一共发了多少条
            case Protocol.MessageHistoryDone:
                vm.OnHistoryDone(text);
                break;

            // 卡片动作的回执(503):把服务端的话显示在状态栏
            case Protocol.CardActionAck:
                vm.ShowCardReply(text);
                break;

            // ---- 登录请求(621):问玩家是/否,回答回 622 ----
            case Protocol.LoginAsk:
            {
                var askParts = text.Split('|');   //拆分结果
                var askId = askParts.Length > 0 ? askParts[0].Trim() : "";
                var askPlayer = askParts.Length > 1 ? askParts[1].Trim() : "";
                var askIp = askParts.Length > 2 ? askParts[2].Trim() : "";
                var askGeo = askParts.Length > 3 ? askParts[3].Trim() : "";
                _ = HandleLoginAskAsync(askId, askPlayer, askIp, askGeo);
                break;
            }

            // ---- 登录超时(624):五分钟没人点 ----
            case Protocol.LoginTimeout:
            {
                var lateParts = text.Split('|');   //拆分结果
                var latePlayer = lateParts.Length > 1 ? lateParts[1].Trim() : "";
                _ = MessageDialog.WarnAsync(this, "登录超时",
                    $"玩家 {latePlayer} 的登录请求超过 5 分钟未确认,已作废。\n让他重新进服即可重新发起。");
                break;
            }

            // ---- 绑定关系(630):回答"我这个账号绑了谁" ----
            case Protocol.BindList:
            {
                var line = text.Trim();

                // 服务端回 nobind —— 这个账号还没绑。服务端不再下发任何玩家名/绑定码:
                // 未绑定时代码属于玩家本人,自动填进来等于把别人的绑定码送到这个账号手上
                if (line.StartsWith("nobind", StringComparison.OrdinalIgnoreCase))
                {
                    _boundPlayer = "";
                    UnbindInfo.Text = "还没有绑定任何 MC 玩家。先让玩家进一次服,踢人提示里会给 6 位绑定码,填到下面的输入框即可。";
                    Logger.Log(LogLevel.Info, "[绑定] 当前账号未绑定");
                    break;
                }

                _boundPlayer = line.Length > 0 ? line.Split('=')[0].Trim() : "";
                UnbindInfo.Text = _boundPlayer.Length > 0
                    ? "当前账号绑定的是:玩家 " + _boundPlayer + "\n解绑后这名玩家再进服需要重新绑定。"
                    : "当前账号还没有绑定任何 MC 玩家。";
                Logger.Log(LogLevel.Info, _boundPlayer.Length > 0
                    ? $"[绑定] 当前账号绑定了玩家 {_boundPlayer}"
                    : "[绑定] 当前账号还没有绑定");
                break;
            }

            // ---- 绑定/解绑回执(627) ----
            case Protocol.BindAck:
            {
                // 服务端只校验了绑定码:真正落盘要用户在这里亲眼点一次确定(631)
                if (text.StartsWith("pending|", StringComparison.OrdinalIgnoreCase))
                {
                    var pendingParts = text.Split('|');   //拆分结果
                    var pendingToken = pendingParts.Length > 1 ? pendingParts[1].Trim() : "";
                    var pendingPlayer = pendingParts.Length > 2 ? pendingParts[2].Trim() : "";
                    Logger.Log(LogLevel.Info, $"[绑定] 绑定码已校验,等待用户确认玩家 {pendingPlayer}");
                    BindOverlay.IsVisible = false;
                    _ = ConfirmBindAsync(pendingToken, pendingPlayer);
                    break;
                }

                var bindOk = text.StartsWith("ok|", StringComparison.OrdinalIgnoreCase);
                var bindDetail = text.Length > 3 ? text[3..] : text;
                Logger.Log(LogLevel.Info, $"[绑定] 服务端回执:{(bindOk ? "成功" : "失败")} {bindDetail}");

                if (BindAckArrived is { } handler)
                {
                    handler(bindOk, bindDetail);
                }
                else
                {
                    if (bindOk)
                    {
                        _ = MessageDialog.InfoAsync(this, "绑定成功", bindDetail);
                    }
                    else
                    {
                        _ = MessageDialog.WarnAsync(this, "操作失败", bindDetail);
                    }
                }
                break;
            }

            // 服务端配置(202):目前是子服务端的消息保留天数
            case Protocol.ServerConfig:
                vm.ApplyServerConfig(text);
                break;

            // 聊天后端的门牌与令牌(510):拿到它本服好友的私聊才走聊天后端
            case Protocol.ChatHello:
                ChatBridge.Configure(text);
                break;

            case Protocol.Push:
                if (!MessageCard.TryParsePush(text, out var target, out var cardText))
                {
                    Logger.Log(LogLevel.Warning, "[ZXMC] 定向推送的负载格式不对,已忽略");
                    break;
                }

                // "@all" = 全体推送:不做账号核对;别的目标要不是自己就丢掉(服务端已经过滤过,这里多一层保险)
                if (target.Length > 0 && !target.Equals("@all", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(target, ClientSession.User, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log(LogLevel.Info, $"[ZXMC] 定向推送的目标是 {target},不是当前账号,已忽略");
                    break;
                }

                vm.ReceiveCard(cardText);
                _ = vm.AckMessageAsync(cardText);
                Logger.Log(LogLevel.Info, "[ZXMC] 收到定向推送:" + MessageCard.Summary(cardText));
                break;

            // 好友列表(651):列表窗开着就交给它画;开着的聊天窗顺带把备注同步一下
            case Protocol.FriendListReply:
                _friendsWindow?.FillList(text);
                SyncRemarks(text);
                break;

            // 好友备注回执(666):交给刚发过 665 的那个聊天窗
            case Protocol.FriendRemarkAck:
                ChatWindow.RemarkAck(text);
                break;

            // 好友申请列表(657):列表窗只要红点,通知窗要整张列表
            case Protocol.FriendRequestsReply:
                _friendsWindow?.FillRequests(text);
                FriendRequestsWindow.Current?.FillRequests(text);
                break;

            // 好友事件(662):有人加我 / 同意我 / 删了我 / 上线 / 下线
            case Protocol.FriendEvent:
                HandleFriendEvent(text);
                break;

            // 搜索结果(664)
            case Protocol.FriendSearchReply:
                AddFriendWindow.Current?.FillResults(text);
                break;

            // 头像(608):存本地,列表重画
            case Protocol.FriendAvatarAck:
                if (FriendCodec.TryParseAvatarAck(text, out var avatarName, out var avatarBytes))
                {
                    _friendsWindow?.SaveAvatar(avatarName, avatarBytes);
                }

                break;

            // 收到好友私聊(400):聊天窗开着就上屏,没开就只留个日志
            case Protocol.FriendMsg:
                HandleIncomingMessage(text);
                break;

            // 聊天记录(403):窗口自己按序号补上屏
            case Protocol.FriendMsgHistoryReply:
                ChatWindow.DeliverHistory(text);
                break;

            // 送达 / 已读回执(404)
            case Protocol.FriendMsgReceipt:
                ChatWindow.DeliverReceipt(text);
                break;

            // 发消息 / 发申请 / 回申请 / 删好友的回执(401 / 655 / 659 / 661)
            case Protocol.FriendMsgAck:
                HandleMessageAck(text);
                break;

            case Protocol.FriendRequestAck:
            case Protocol.FriendAnswerAck:
            case Protocol.FriendRemoveAck:
                HandleFriendReceipt(packet.Type, text);
                break;
        }
    }

    /// <summary>处理 662:按事件种类刷新列表 / 在线状态 / 红点。</summary>
    private void HandleFriendEvent(string payload)
    {
        var parts = payload.Split('|', 2);
        var kind = parts[0].Trim();
        var other = parts.Length > 1 ? parts[1].Trim() : "";
        Logger.Log(LogLevel.Info, $"[ZXMC] 好友事件:{kind} {other}");

        switch (kind)
        {
            case Protocol.FriendEventKind.Online:
                _friendsWindow?.SetOnline(other, online: true);
                break;
            case Protocol.FriendEventKind.Offline:
                _friendsWindow?.SetOnline(other, online: false);
                break;
            case Protocol.FriendEventKind.Remove:
                // 关系解除:列表重拉;开着的聊天窗必须立刻锁死,不然还能继续往里发
                _friendsWindow?.RefreshAll();
                ChatWindow.FriendRemoved(other);
                Logger.Log(LogLevel.Info, $"[好友] 收到解除通知:{other},聊天窗已停");
                break;
            default:
                // 申请 / 同意:列表和红点都可能变,直接重新拉一遍最省心
                _friendsWindow?.RefreshAll();
                break;
        }
    }

    /// <summary>处理收到的私聊:窗口开着就上屏;没开就什么也不做(记录本来就在服务端,打开时会拉)。</summary>
    private void HandleIncomingMessage(string payload)
    {
        if (!FriendCodec.TryParseIncomingMessage(payload, out var fromId, out var message))
        {
            Logger.Log(LogLevel.Warning, "[ZXMC] 收到的私聊格式不对,已忽略");
            return;
        }

        if (!ChatWindow.Deliver(fromId, message))
        {
            Logger.Log(LogLevel.Info, $"[ZXMC] 收到 {fromId} 的消息,聊天窗没开");
        }

        _friendsWindow?.RefreshAll();
    }

    /// <summary>处理 401:成功把序号与消息编号交回窗口;失败(err|编号|原因 或 err|原因)把红叹号标到对应气泡上。</summary>
    private void HandleMessageAck(string payload)
    {
        // 失败回包和成功回包格式完全不同,先分流再解析
        if (payload.StartsWith("err|", StringComparison.OrdinalIgnoreCase))
        {
            // 三段 err|编号|原因 能精确定位气泡;两段的旧格式只能标最近一条待回执的
            if (FriendCodec.TryParseMessageError(payload, out var failedId, out var reason))
            {
                Logger.Log(LogLevel.Warning, $"[ZXMC] {ClientSession.User} 私聊没发出去({failedId}):{reason}");
                ChatWindow.FailById(failedId, reason);
            }
            else
            {
                var fallbackReason = payload[4..].Trim();
                Logger.Log(LogLevel.Warning, $"[ZXMC] {ClientSession.User} 私聊没发出去:{fallbackReason}");
                ChatWindow.FailLastPending(fallbackReason);
            }

            return;
        }

        if (!FriendCodec.TryParseMessageAck(payload, out var messageId, out var seq, out var duplicate))
        {
            Logger.Log(LogLevel.Warning, $"[ZXMC] 私聊回执格式不对:{payload}");
            return;
        }

        ChatWindow.Ack(messageId, seq, duplicate);

        // 跨服那条已经落到服务端了,列表里的「最后一句」还是旧的:补一次 650(内部有节流)
        _friendsWindow?.RefreshList();
    }

    /// <summary>651 回来时把备注同步到开着的聊天窗:在别的窗口改了备注,这边标题也要跟着变。</summary>
    private static void SyncRemarks(string payload)
    {
        var entries = FriendCodec.ParseFriendList(payload, out var badLines);
        if (badLines > 0)
        {
            Logger.Log(LogLevel.Warning, $"[好友] 好友列表里有 {badLines} 行读不出来,备注同步跳过这些行");
        }

        foreach (var entry in entries)
        {
            ChatWindow.ForPeer(entry.Id)?.ApplyRemark(entry.Remark);
        }
    }

    /// <summary>处理好友相关的回执:失败就把服务端说的原因给玩家看。</summary>
    private void HandleFriendReceipt(ushort type, string payload)
    {
        var ok = payload.StartsWith("ok", StringComparison.OrdinalIgnoreCase);
        var reason = ok ? "" : payload.Replace("err|", "", StringComparison.OrdinalIgnoreCase);

        if (ok)
        {
            Logger.Log(LogLevel.Info, $"[ZXMC] 好友回执 {type} 成功");
            return;
        }

        Logger.Log(LogLevel.Warning, $"[ZXMC] 好友回执 {type} 失败:{reason}");
        _ = MessageDialog.ConfirmAsync(this, "操作没成功", reason.Length > 0 ? reason : "服务端没有说明原因");
    }




    /// <summary>
    /// 开关只换"材料",不碰窗口材质:
    /// 亚克力 = 壁纸按 Fluent 模糊 30;半透明 = 不模糊、表面 60% —— 就是单纯的半透明。
    /// 透明窗口必须给一个"几乎全透明的底",DWM 才会持续合成,不会残留上一帧(那看着就像复制了一层窗口)。
    /// </summary>
    private void ApplyMaterial(bool acrylic)
    {
        // BlurEffect 不是控件、拿不到 x:Name 字段,所以直接给壁纸这一层换效果
        WallpaperImage.Effect = acrylic ? new BlurEffect { Radius = ThemeManager.GlassBlurRadius } : null;

        Background = acrylic
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>一条提示条刚摆上来:下一帧加上 in 类,让它从左上角外面"滑进来";到点再摘掉 in 类滑回去, 动画放完才把这条从列表里去掉。</summary>
    private async void OnToastLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border toast)
        {
            return;
        }

        try
        {
            await Task.Delay(20);
            toast.Classes.Add("in");      // 触发 RenderTransform 过渡:-400px → 0

            if (toast.DataContext is not ToastItem item)
            {
                return;
            }

            await Task.Delay(item.Milliseconds);
            toast.Classes.Remove("in");   // 滑回左上角

            await Task.Delay(300);
            Vm?.RemoveToast(item);
        }
        catch (Exception ex)
        {
            // 窗口关掉 / 控件已销毁:提示条跟着窗口一起没了,不当错误
            Logger.Log(LogLevel.Info, "[ZXMC] 提示条收起时打扰了一下:" + ex.Message);
        }
    }

    /// <summary>启动检查更新:设置里关了就不查,查到新版本弹更新窗</summary>
    private async Task CheckUpdateOnStartupAsync()
    {
        if (ClientConfig.Get("update.mode", "prompt") == "off")
        {
            return;
        }

        // 等主界面稳下来再发请求,别跟首屏抢时间
        await Task.Delay(2500);
        if (!IsVisible)
        {
            return;
        }

        var info = await UpdateService.CheckAsync(useCache: true);
        if (info is null)
        {
            Vm?.ShowToast("检查更新失败,可在设置→下载里手动重试", ToastKind.Warning);
            return;
        }

        if (!info.HasUpdate)
        {
            return;
        }

        await UpdateWindow.ShowAsync(this, info);
    }

    /// <summary>标题栏拖动窗口;双击在最大化 / 还原之间切换。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        BeginMoveDrag(e);
    }

    /// <summary>点最小化按钮:窗口最小化。</summary>
    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>点右上角关闭按钮:关掉这个窗口。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>把各页的子功能清单交给通用子功能页</summary>
    private void RefreshFunctionPages()
    {
        if (Vm is null)
        {
            return;
        }

        this.FindControl<FunctionPickerView>("CommunityPage")?.SetContent("社区", Vm.CommunityItems);
        this.FindControl<FunctionPickerView>("OrganizationPage")?.SetContent("组织", Vm.OrganizationItems);
        this.FindControl<FunctionPickerView>("ManagePage")?.SetContent("管理", Vm.ManageItems);
    }

    /// <summary>回默认页。</summary>
    private void OnLogoClick(object? sender, RoutedEventArgs e) => Vm?.ClearSelection();

    /// <summary>切页面。</summary>
    private void OnNavItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: NavItem item }) return;

        // 好友是独立窗体(好友条 + 聊天窗),不进主界面内容区
        if (item.Label == "好友")
        {
            ShowFriendsWindow();
            return;
        }

        Vm?.SelectNav(item);
    }

    // 独立的好友窗口;打开过就复用。
    private FriendsWindow? _friendsWindow;

    /// <summary>打开(或激活)好友条窗体。</summary>
    private void ShowFriendsWindow()
    {
        if (_friendsWindow is not null)
        {
            // 最小化时 IsVisible 仍然是 true,Activate 不会把它还原 —— 得先把状态摆回 Normal
            if (_friendsWindow.WindowState == WindowState.Minimized)
            {
                _friendsWindow.WindowState = WindowState.Normal;
            }

            _friendsWindow.Activate();
            return;
        }

        _friendsWindow = new FriendsWindow();
        WindowPlacement.PlaceAt(_friendsWindow, this);
        _friendsWindow.Closed += (_, _) => _friendsWindow = null;
        _friendsWindow.Show();
    }

    /// <summary>左下角按钮:消息面板 500ms 内滑开铺满,再点收回 312</summary>
    private void OnOpenMessagesFullClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<Border>("MessagesPanel") is not { } panel)
        {
            return;
        }

        var full = Math.Abs(panel.Width - 312) < 1;

        // 宽度过渡:0.5 秒缓出,看着是平滑滑开
        panel.Transitions = new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.DoubleTransition
            {
                Property = Border.WidthProperty,
                Duration = System.TimeSpan.FromMilliseconds(500),
                Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
            },
        };

        // 图标跟着换状态:展开后显示「收起」,收回后显示「展开」
        if (this.FindControl<Avalonia.Controls.Shapes.Path>("PanelToggleIcon") is { } toggleIcon)
        {
            toggleIcon.Data = Avalonia.Media.Geometry.Parse(full ? Models.Icons.PanelExpand : Models.Icons.PanelCollapse);
        }

        if (full)
        {
            var hostWidth = (panel.Parent as Control)?.Bounds.Width ?? 1000;
            Grid.SetColumn(panel, 0);
            Grid.SetColumnSpan(panel, 2);
            panel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            panel.ZIndex = 10;
            panel.Margin = new Avalonia.Thickness(10);   // 四边都留 10,与右侧一致
            panel.Width = System.Math.Max(320, hostWidth - 20);   // 左 10 + 右 10
        }
        else
        {
            Grid.SetColumn(panel, 1);
            Grid.SetColumnSpan(panel, 1);
            panel.Width = 312;
            panel.ZIndex = 0;
        }
    }

    /// <summary>删除本地消息。</summary>
    private void OnDeleteMessageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MessageItem item } && Vm is { } vm)
        {
            vm.DeleteMessage(item);
        }
    }

    /// <summary>卡片上的按钮:把"点了哪个按钮 + 卡片里填 / 选的内容"上报给服务端。 选择卡片带的是下拉框里选的项,填空卡片带的是输入框里的字。</summary>
    private void OnCardActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string action, DataContext: MessageItem item } && Vm is { } vm)
        {
            var input = item.Kind == MessageKind.Select ? item.SelectedOption : item.InputText;
            _ = vm.SendCardActionAsync(item, action, input);
        }
    }

    /// <summary>帮助:打开在线帮助页(GitHub Pages 镜像;旧站 http://zxm.zincms.top:10/help.html 仍然可用)。</summary>
    private async void OnHelpClick(object? sender, RoutedEventArgs e)
        => await OpenUrlAsync("https://weiqi114514.github.io/ZincXManager/help.html", "帮助");

    /// <summary>反馈:打开 GitHub 仓库的 issues。</summary>
    private async void OnFeedbackClick(object? sender, RoutedEventArgs e)
        => await OpenUrlAsync("https://github.com/weiqi114514/ZincXManager/issues", "反馈");

    /// <summary>用系统默认浏览器打开链接(Avalonia 12 里 Launcher 是 TopLevel 的实例属性)。</summary>
    private async Task OpenUrlAsync(string url, string what)
    {
        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is null)
            {
                Logger.Log(LogLevel.Warning, $"[ZXMC] 拿不到 Launcher,无法打开{what}: {url}");
                return;
            }

            await launcher.LaunchUriAsync(new Uri(url));
            Logger.Log(LogLevel.Info, $"[ZXMC] 打开{what}: {url}");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, $"[ZXMC] 打开{what}失败 {url}: {ex.Message}");
        }
    }
    /// <summary>心跳 + 自动重连。 每 5 秒查一次连接:掉线先弹红色提示「与服务端的连接已断开,尝试重连」, 之后每 5 秒重连一次,提示「正在重连 N/5」;连上了弹绿色提示并清零计数。</summary>
    private Avalonia.Threading.DispatcherTimer? _heartbeat;
    private int _reconnectAttempt;
    private bool _reconnecting;
    private string _lastReconnectError = "";

    private void StartHeartbeat()
    {
        if (_heartbeat is not null) { return; }
        _heartbeat = new Avalonia.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(5) };
        _heartbeat.Tick += async (_, _) => await HeartbeatAsync();
        _heartbeat.Start();
    }

    /// <summary>心跳:断了重连,5 次失败切回连接页。</summary>
    private async Task HeartbeatAsync()
    {
        var connect = ClientSession.Connect;
        if (connect is null) { return; }

        // 还连着:如果之前断过,提示已恢复
        if (connect.LinkAlive)
        {
            if (_reconnectAttempt > 0)
            {
                _reconnectAttempt = 0;
                Vm?.ShowToast("已重新连接服务端", ToastKind.Success);
            }
            return;
        }

        if (_reconnecting) { return; }

        // 第一次发现断线:先明确告诉用户
        if (_reconnectAttempt == 0)
        {
            Vm?.ShowToast("与服务端的连接已断开,尝试重连", ToastKind.Error, 5000);
        }

        if (_reconnectAttempt >= 5)
        {
            // 5 次都没连上:关掉主界面,切回连接流程窗体,并把失败原因弹给用户
            var reason = string.IsNullOrWhiteSpace(_lastReconnectError)
                ? "与服务端的连接已断开,自动重连 5 次均未成功。"
                : ("与服务端的连接已断开,自动重连 5 次均未成功。\n\n最近一次错误:" + _lastReconnectError);
            _heartbeat?.Stop();
            AppWindows.ShowConnectWithError(reason);
            return;
        }

        _reconnectAttempt++;
        Vm?.ShowToast($"正在重连 {_reconnectAttempt}/5", ToastKind.Error, 5000);

        _reconnecting = true;
        try
        {
            await connect.ConnectAsync();
            if (connect.LinkAlive)
            {
                _reconnectAttempt = 0;
                Vm?.ShowToast("已重新连接服务端", ToastKind.Success);
                _ = connect.ReloginAsync();   // 重连后自动重登,不然绑定/头像这些会报「请先登录」
            }
        }
        catch (Exception ex)
        {
            _lastReconnectError = ex.Message;
            Logger.Log(LogLevel.Warning, "[ZXMC] 自动重连失败:" + ex.Message);
        }
        finally
        {
            _reconnecting = false;
        }
    }


    // ---- 右下角手柄拖拽 ----
    private Point _gripStart;
    private Size _gripStartSize;
    private bool _gripDragging;

    /// <summary>按下手柄,开始拉伸。</summary>
    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _gripDragging = true;
        _gripStart = e.GetPosition(this);
        _gripStartSize = new Size(Width, Height);
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    /// <summary>拖手柄改窗口大小。</summary>
    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_gripDragging) { return; }
        var p = e.GetPosition(this);
        Width = Math.Max(MinWidth, _gripStartSize.Width + (p.X - _gripStart.X));
        Height = Math.Max(MinHeight, _gripStartSize.Height + (p.Y - _gripStart.Y));
        e.Handled = true;
    }

    /// <summary>结束拉伸。</summary>
    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        _gripDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

}
