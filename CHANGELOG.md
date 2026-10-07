# 更新日志

ZincXManager(锌X管理面板)的变更记录。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。
版本号尚未启用,本次改动记在「未发布」下,发布时再归档为版本号。

## [未发布] - 2026-10-07

> **本批次主题**:ZXMOS 封禁体系收尾(拉黑整台 ZXMS + 账号归档)、注册阶段的封禁/机器码补齐、
> 服务端名称改由服务端下发、客户端个性化(巨硬蓝配色 + 毛玻璃↔半透明开关)与窗口材质统一。
> 涉及端:ZXMC(客户端)、ZXMS(子服务端)、ZXMOS(官方服务端)、ZincXManagerShared(共享库)。

### 新增

**ZXMOS · 黑名单(srv ban)与账号归档**
- 新文件 `ZincXManagerOServer/Blacklist.cs`:归档实现,把被拉黑 ZXMS 名下的玩家账号连同拉黑信息一起挪到
  `blacklist/zxms/<名称>/`:
  - `accounts.json`:名下账号(字段与账号表一致,解封时能原样读回)
  - `info.json`:拉黑时间 / 到期时间 / 原因 / 来源 IP / 账号数
- ZXMOS 新增命令:
  - `srv ban <名称或ID> [perm|30d|12h] [原因]` —— ① 归档该 ZXMS 名下账号 → ② 按「账号名 + 它的 IP」封禁 → ③ 断开它已连上的连接
  - `srv unban <名称或ID>` —— 解封,并从 `blacklist/zxms/<名称>/accounts.json` 把账号读回账号表(保留 owner / 最后登录 IP / 机器码)
  - `srv blacklist` —— 列出已归档的 ZXMS 与拉黑信息
  - `ban user <名称或ID> [时长] [原因] [--ip] [--machine]` —— 与 ZXMS 侧对齐,支持连坐最后登录 IP / 机器码

**协议**
- `Protocol.ServerName = 201`:服务端 → 对方,负载为服务端显示名。

**客户端 · 个性化**
- 新增配色 **巨硬蓝**(微软 Fluent 蓝):`#0078D4 / #005A9E / #2899F5 / #A9D3F2 / #DEECF9 / #7FA8C9`。
- 新增 **毛玻璃(亚克力)** 开关(设置 → 个性化,以及 OOBE 第 2 步「个性化」):
  - 开 = 亚克力:背景按 Fluent 亚克力模糊(半径 30)、表面着色 80%
  - 关 = 半透明:背景不模糊、表面着色 60%
  - 默认开,记在 `config.ini` 的 `glass=1/0`

### 变更

- **服务端名称不再由用户填写**,改为连接时由服务端下发:
  - ZXMOS 读 `config.ini` 的 `name`(缺省自动写入「锌能源官方服务端」),连接时随身份包(200)发出 201;
  - ZXMS 同样发自己的 `name`(缺省「锌能源服务器」);收到 ZXMOS 的 201 后写入本端 `ozname`,并在 `net status` 里显示「名称:…」;
  - ZXMC 收到 201 后写入 `czname` / `ozname`,设置 → 模式与数据源显示的就是服务端给的名称;
  - OOBE 第 3 步的「子服务端名称 / 官方服务端名称」输入框删除,改为提示「连接后由服务端提供」。
- 登录 / OOBE / 连接 三个入口窗口的底由不透明(`ThemeWindow`)改为半透明 veil(`ThemePage`),
  并声明窗口材质:毛玻璃开 = 要系统 `AcrylicBlur`,关 = 只要 `Transparent` —— 窗口本身就是半透明/亚克力,
  不再是一大块白底;材质与「壁纸」解耦(不设壁纸就是直接透出桌面)。
- 窗口材质统一到 `ThemeManager.ApplyWindowMaterial(window)`,且**只在开窗时定一次**:
  运行中切换开关只改表面不透明度,不再改 `TransparencyLevelHint`(避免 Windows 重建窗口、屏幕上闪出「像多了一个窗口」)。
- ZXMS `net status` 输出增加官方服务端名称。

### 修复

- **ZXMOS 拉黑整台 ZXMS 时取不到 IP、也踢不掉在线连接**:首次接入走的是「注册」路径,而 `LastIp` 与连接登记
  只在「登录」成功时才写。现在注册成功后同样登记连接账号并记录 IP / 机器码(替玩家注册的中转连接不登记,避免把 ZXMS 的连接记成玩家)。
- **注册阶段不做机器码封禁检查**:ZXMS / ZXMOS 注册分支原来按 `machine: null` 查封禁,现在按注册载荷第 3 段的机器码查;
  客户端注册载荷相应改为 `名称|密文|机器码`(登录载荷不变)。
- **OOBE / 登录窗口的「权限等级」列表错误**:
  - 「哪一端的管理账号」下拉原本把 `SelectedItem` 绑在 `ComboBoxItem` 对象上,选 ZXMOS 静默失效 → 列表一直是 ZXMS 的 `LAdmin/HAdmin/System`。
    改为绑 `SelectedIndex`(0=ZXMS,1=ZXMOS)再由视图模型折算;
  - `Mode`(SAOS/OS/OOS)变化不通知等级列表 → OOS 下列表不刷新。补上通知并复位;
  - 等级复位发生在列表通知之前,ComboBox 会把选择当「不在列表里」清空(勾上「登录管理账号」后等级框空白)。改为 UI 线程延后一拍复位。
  - 现在:SAOS + ZXMS 侧 = `LAdmin…`;SAOS + ZXMOS 侧 / OOS = **只有 `Admin`、`System`**。
- ZXMOS `Program.cs` 中被封禁表插入挤错位的账号表 XML 注释归位。
- 协议注释修正:`AccountMigrate`(610)的文档注释不再张冠李戴。

### 验证

四端编译:`ZincXManagerShared` / `ZincXManagerOServer` / `ZincXManagerServer` / `ZincXManagerClient` 均 **0 错误**
(仅剩改动前既有的警告:ZXMOS 静态初始化顺序 CS8602、ZXMS 重复 using CS0105、客户端 CS0108/CS0618)。

| 套件 | 覆盖 | 结果 |
|---|---|---|
| 单元测试 | `zxfile-tests` / `zxnet-tests` / `zxm-registry-test` | 147 / 17 / 14,全通过 |
| 测试① | ZXMOS 账号命令、JSON 迁移、封禁(账号·IP·机器码·连坐)、拉黑归档、解封恢复 | 29 / 29 |
| 测试② | ZXMS 接入 → 拉黑 → 踢线 → 拒连 → 解封恢复(含注册即接入的回归) | 24 / 24 |
| 测试③ | 注册阶段机器码封禁、中转注册不记混连接账号 | 12 / 12 |
| 测试⑤ | 服务端名称下发(ZXMOS → ZXMS) | 5 / 5 |
| GUI 实测 | 巨硬蓝配色、毛玻璃开关(开/关视觉差异与落盘 `glass=1/0`)、登录窗口透窗、等级列表(Admin/System)、名称下发(`czname=猫娘子服`) | 通过 |

### 已知问题(未修,已记录)

1. **经 ZXMS 中转的玩家登录会被按「服务端账号」查表**:ZXMOS 登录分支用连接身份判断账号表,中转登录没有 `|relay` 标记,
   于是去 `zxms.json` 找玩家账号 → 回 `err|账号不存在`。注册路径已有 `|relay` 标记,登录路径还没有。
2. 中转注册成功时 ZXMOS 的日志标签显示为 `[子服务端账号]`(应为玩家账号),仅文案问题。
3. 注册阶段的机器码封禁依赖客户端新载荷:老客户端(第 3 段不带机器码)在注册这一步仍不会被机器码拦。

### 兼容性注意

- 新增 201 包:老客户端会把它当作未知包记一条日志,不影响连接与登录;老服务端不发 201,新客户端只是拿不到名称。
- 注册载荷新增第 3 段(机器码):服务端按「第 3 段是否为 `relay`」兼容老式 `名称|密文|relay` 写法。
- 数据文件除 `config.ini` 外一律 JSON(`osusers.json` / `zxms.json` / `users.json` / `bans.json` / `blacklist/**`),老 `.ini` 会自动迁移。

### 本批次涉及文件

```
ZincXManagerShared/Network/Protocol.cs              + ServerName = 201
ZincXManagerOServer/Blacklist.cs                    新增:黑名单归档
ZincXManagerOServer/Program.cs                      srv ban/unban/blacklist 落地、注册补 IP/机器码、名称下发
ZincXManagerOServer/Command/ZxmosCommands.cs        srv ban/unban/blacklist、ban user、黑名单钩子
ZincXManagerServer/Program.cs                       注册按机器码查封禁、名称发/收
ZincXManagerServer/Command/ZxmsCommands.cs          net status 显示服务端名称
ZincXManagerClient/Models/ThemeOption.cs            + 巨硬蓝
ZincXManagerClient/ThemeManager.cs                  毛玻璃开关、表面不透明度、窗口材质
ZincXManagerClient/ViewModels/{Connect,Oobe,Login,Settings}ViewModel.cs   201 处理、等级列表修复、名称显示
ZincXManagerClient/Views/{Main,Login,Oobe,Connect}Window.axaml(.cs)        半透明/亚克力窗口、删除名称输入框
ZincXManagerClient/Views/SettingsView.axaml         个性化:毛玻璃开关
```
