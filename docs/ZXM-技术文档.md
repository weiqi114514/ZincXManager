# ZincXManager 技术文档

> 版本 0.1.0 · 仓库 <https://github.com/weiqi114514/ZincXManager> · 协议 GPL-2.0
> 本文面向开发者与运维:架构、协议、数据格式、构建、测试、官方验证与部署。

---

## 1. 架构总览

```
                 ┌──────────────┐
   玩家/管理员 ──▶│ ZXMC 客户端  │ 桌面程序(Avalonia,Windows)
                 └──────┬───────┘
             SAOS/OS    │            OOS(直连)
                 ┌──────▼───────┐    │
                 │ ZXMS 子服务端 │    │
                 └──────┬───────┘    │
                        │ 100/101 握手 + 600~612 账号
                 ┌──────▼────────────▼──────┐
                 │ ZXMOS 官方服务端(官方运行)│
                 └──────────────────────────┘
```

| 端 | 项目 | 职责 |
| --- | --- | --- |
| **ZXMC** | `ZincXManagerClient` | 桌面界面;连子服或官方端;消息面板与卡片;本地消息存档 |
| **ZXMS** | `ZincXManagerServer` | 本服账号/封禁/消息/蓝图;向官方端登记身份;替客户端中转 |
| **ZXMOS** | `ZincXManagerOServer`(+`…NO` 为早期 C++ 原型) | 官方社区、官方蓝图库、账号与子服登记;**仅官方运行** |
| **ZXMM** | 规划中(协议预留) | 模组端:在 MC 服务端里执行 ZXMS 传来的指令,**未实现** |

- 三端共用 `ZincXManagerShared`:协议常量、账号与密码哈希、命令框架、日志、网络库、官方验证。
- 客户端三种连接方式:**OS**(只连子服)/ **OOS**(只连官方端)/ **SAOS**(都连;连上子服时经子服中转)。
- 权限等级:ZXMS 侧 `Player / LAdmin / HAdmin / System`;官方端侧 `Player / Admin / System`;子服接入账号等级为占位符 `-`。

## 2. 通信协议

### 2.1 包结构

每包 = `长度(4 字节,含头)` + `序号(4 字节)` + `类型(2 字节)` + `负载(UTF-8 或字节)`。
收包由 `Packet.ReadAsync` 解析完整包后交给 `PacketDispatcher` 的处理器按顺序执行(处理器慢不会卡住收包)。

### 2.2 类型编号(三端共用,`ZincXManagerShared/Network/Protocol.cs`)

| 段 | 编号 | 名称 | 方向 / 负载 |
| --- | --- | --- | --- |
| 1xx 子服↔官方 | 100 | `SOnline` | ZXMS → ZXMOS 子服上线握手 |
| | 101 | `SBack` | ZXMOS → ZXMS 收到 |
| 2xx 服务端声明 | 200 | `ServerIdent` | 服务端 → 对方:`"ZXMS"` / `"ZXMOS"` |
| | 201 | `ServerName` | 服务端 → 对方:显示名(每次接入现读 config) |
| | 202 | `ServerConfig` | 服务端 → 客户端:配置下发(如 `msgdays`) |
| 3xx 客户端上线 | 300 | `Conline` | ZXMC → 服务端:`"ZXMC:<模式>"` |
| | 301 | `CBack` | 服务端 → ZXMC:收到 |
| 4xx 业务 | 400/401 | `FriendMsg`/`Ack` | **未实施**(编号先占) |
| 5xx 消息 | 500 | `Notice` | 服务端 → 客户端:公告卡片,逐跳原样转发 |
| | 501 | `Push` | 服务端 → 客户端:定向推送,`目标账号|卡片`,每跳按账号过滤 |
| | 502 | `CardAction` | 客户端 → 服务端:点了卡片按钮,`动作|输入|原卡片` |
| | 503 | `CardActionAck` | 服务端 → 客户端:`ok|提示` 或 `err|原因` |
| | 504 | `MessageHistory` | 服务端 → 客户端:历史消息(补发) |
| | 505 | `MessageHistoryReq` | 客户端/子服 → 服务端:请求补发(负载 `self` = 收"给本端"的) |
| | 506 | `MessageHistoryDone` | 服务端 → 请求方:补发结束 |
| 6xx 账号 | 600/601 | `AccountLogin`/`Ack` | 登录:`名称|密文|等级|端|机器码|服务端码` |
| | 602/603 | `AccountRegister`/`Ack` | 注册:`名称|密文|机器码|服务端码` |
| | 604/605 | `AccountAvatarReq`/`Ack` | 取头像(字节)/ 设置结果 |
| | 606 | `AccountAvatarSet` | 设置头像 |
| | 610 | `AccountMigrate` | ZXMOS → ZXMS:拉黑前把用户账号移交给子服 |
| | 611/612 | `AccountRename`/`Ack` | 改子服账号名(顺带改密),控制台 `osuser` 发起 |
| 官方验证 | 520 | `OfficialChallenge` | 客户端/子服 → 官方端:发 nonce 要证明 |
| | 521 | `OfficialProof` | 官方端 → 请求方:证明或 `err|…` |

> 4xx 及以上的包会被自动记进 `clients.log`,排查问题时先看它。

### 2.3 消息卡片负载

`种类|标题|正文|来源|主动作|次动作|选项`,种类 = `text` / `update` / `choice` / `input` / `select`。
动作回传:`动作|输入|原卡片原文` —— 服务端用"原卡片原文"识别是哪张卡,不需要额外序号。
子服收到来源为 `ZXMOS-…` 卡片动作时,会在负载前多带一段账号转交给官方端。

## 3. 身份与账号

- **机器码(设备)**:`MachineCode.Value`,由硬件信息派生,许可证与封禁都用它;每次启动打进日志。
- **服务端码(实例)**:ZXMS 首次生成的 32 位十六进制,存 `config.ini` 的 `servercode`;**丢了就会被判"身份不一致"**。
- **密码**:只存密文,`SHA-256(小写账号名 + ":" + 明文密码)`;`oshash` 同一套算法。
- **账号表**:
  | 文件 | 谁 | 等级表 |
  | --- | --- | --- |
  | `users.json` | 子服的玩家/管理账号 | Player / LAdmin / HAdmin / System |
  | `osusers.json` | 官方端的玩家/管理账号 | Player / Admin / System |
  | `zxms.json` | 官方端登记的子服接入账号(等级 `-`) | `-` |
  两张表**互相查重**:官方端的玩家账号与服务端账号不能同名。
- **改名协议**:`AccountRename(611)` 校验"账号存在 + 机器码 + 服务端码与登记一致"才允许改;
  直接改 `config.ini` 会被判成"换了台实例",接入时报「身份不一致」。

## 4. 目录与数据文件

| 端 | 运行时生成(与 exe 同目录) |
| --- | --- |
| ZXMC | `config.ini`、`messages.json`(消息存档)、`logs\`、头像缓存 |
| ZXMS | `config.ini`、`users.json`、`bans.json`、`messages.json`、`logs\` |
| ZXMOS | `config.ini`、`osusers.json`、`zxms.json`、`bans.json`、`messages.json`、`blacklist\`(拉黑归档)、`logs\` |

配置键(常用):

| 键 | 端 | 含义 / 默认 |
| --- | --- | --- |
| `port` | ZXMOS | 监听端口 / **25000** |
| `name` | ZXMS·ZXMOS | 本端显示名(下发给客户端) |
| `msgdays` | ZXMS·ZXMOS | 消息存档保留天数(ZXMS 默认 7,官方端默认 50) |
| `czport` | ZXMS | 玩家接入端口 / 21000 |
| `connectOS` | ZXMS | 0 = 启动就连官方端 |
| `ozip` / `ozport` | ZXMS·ZXMC | 官方端地址与端口(**可写域名**) / 25000 |
| `osuser` / `oshash` | ZXMS | 子服在官方端的账号与密码密文 |
| `servercode` | ZXMS | 本实例身份证(首次自动生成,**别手改**) |
| `mode` / `czip` / `czport` / `ozip` / `ozport` | ZXMC | 连接方式与两组地址 |
| `officialcheck` | ZXMS | 0 = 放行非官方对端(默认 1 强制) |
| `allowunverified` | ZXMC | 1 = 允许连非官方服务端 |
| `testaction` / `startpage` / `startsection` / `skipauth` | ZXMC | 自动化测试用开关 |

## 5. 构建与发布

```powershell
# 编译全部
dotnet build ZincXManager.slnx

# 单文件 AOT 发布(win-x64,目标机器无需 .NET 运行时)
dotnet publish ZincXManagerClient  -c Release -r win-x64
dotnet publish ZincXManagerServer  -c Release -r win-x64
dotnet publish ZincXManagerOServer -c Release -r win-x64
# 客户端产物必须让 exe 与 libSkiaSharp.dll / av_libglesv2.dll / libHarfBuzzSharp.dll 同目录
```

- **官方验证是强制的,没有编译开关**:去掉了 `-p:OfficialBuild` / `OFFICIAL_BUILD` 条件编译,
  所有构建行为一致 —— ZXMOS 没有合法 `official.lic` 直接退出(码 2),ZXMS/ZXMC 连非官方端会断开。
- 自动化测试用环境变量 `ZXM_ALLOW_UNOFFICIAL=1` 放行(见第 6 节);**正式部署不要设**。
- 工作区里的打包脚本(不在仓库内):`_publish-aot.ps1`(三端 AOT + 组包)、`_build-installer.ps1`(MSI + 便携 ZIP)、
  `_pack-oserver.ps1`(官方端部署包)、`_make-icon.ps1`(生成 `zxm.ico`)、`_sign.ps1`(自签证书签名)。

## 6. 测试

入口单一:**`_run-all.ps1`**(21 个套件,自带端口预检、每套件明细落盘、退出码 = 失败套件数)。

```powershell
pwsh -File _run-all.ps1                    # 全部
pwsh -File _run-all.ps1 -List              # 只列套件
pwsh -File _run-all.ps1 -Only test2,test9b # 只跑匹配的(逗号分隔,名字/脚本名均可)
pwsh -File _run-all.ps1 -Skip 界面,冒烟     # 跳过匹配的
```

覆盖:单元测试(ZFile / Network / 客户端注册表,178 项)、场景 ①~⑫(账号·封禁·机器码·身份两码·服务端消息·存档·补发·定向转发)、
身份码排障、官方验证①(许可证自校验 10 项)②(对端 520/521,13 项)、消息本地化、提示条、GUI 五种卡片、毛玻璃、选择卡片、弹窗居中、AOT 交付包冒烟。
GUI 类套件需要**已登录的桌面会话**,跑的时候别抢鼠标键盘。
测试放行:总入口会给所有套件设 `ZXM_ALLOW_UNOFFICIAL=1`(官方验证永远强制,测试靠它放行)。

## 7. 官方验证机制

设计目标:**别人自己编一个"不验"的 ZXMOS 也拿不出官方证明**。

1. **第一层 自校验**:ZXMS / ZXMC 内置官方公钥(RSA-2048,`OfficialKey` 里打散存放,二进制里搜不到明文模数),
   启动/接入时校验官方签发的许可证。
2. **第二层 对端证明(520/521)**:请求方发一个 `nonce`,官方端用私钥对 `nonce` 做 HMAC/SHA-256 签名并连同许可证信息回;
   请求方用内置公钥验签。**只对已登录的连接出示证明**(不给证书收集者可乘之机)。
3. **许可证文件 `official.lic`**(INI 风格,手改任何字段都会验签失败):

```ini
[official]
product=ZXMOS
name=ZXM 官方服务端
machine=<64 位机器码,绑定设备>
issued=2026-10-11
expires=            ; 空 = 永久
serial=ZX-2026-ZXMOS-001
sig=<Base64 签名>
```

4. **签发工具 `ZincXManagerLicTool`**:

```
ZincXManagerLicTool machine                       # 读本机机器码
ZincXManagerLicTool keygen <目录> [--emit <路径>]  # 生成密钥对并输出内置公钥代码
ZincXManagerLicTool sign <私钥.pem> <机器码> <名称> <天数|0=永久> <输出.lic> [序列号]
ZincXManagerLicTool verify <公钥.pem> <证书.lic> <机器码>
```

> ⚠️ 私钥必须离线保管;**换私钥 = 所有旧证立刻失效**;丢了就再也签不出新证。

## 8. 部署与运维

| 事项 | 做法 |
| --- | --- |
| 端口 | 子服开放 `czport`(默认 21000);官方端开放 `port`(默认 25000);客户端不需要开端口 |
| 防火墙 | Windows 防火墙 + 云安全组都要放行;对外还需在路由器把公网端口**转发**到内网机器 |
| 常驻 | 控制台程序,建议用计划任务(登录后启动可保留交互控制台)或 nssm 做成服务 |
| 域名 | 各端地址都支持域名:连的是域名时由系统 DNS 解析,解析出多个地址会**依次尝试**(IPv6 不通自动落 IPv4) |
| 备份 | `config.ini`(含 `servercode`/`oshash`)、各 `*users.json`、`bans.json`、`messages.json` |
| 排错 | 先看 `logs\` 最新日志 + `clients.log`(4xx 以上业务包);控制台 `net status` / `net clients` / `info` |
| 常见故障 | 「身份不一致」= `servercode` 丢了;「密码错误」= 用 `osuser <同名> <新密码>` 改;连不上 = 端口/防火墙/DNS |

## 9. 已知限制与路线图

- **ZXMM 模组端未实现**(协议编号与分支已预留);
- 官方验证第三层(**在线短时令牌 + 吊销**)未做,目前是"离线许可证 + 对端证明";
- 只有 64 位 Windows 客户端,无移动端;
- 界面与数据格式仍可能变动;未做长期运行压测;
- 好友消息(400/401)只占了编号,未实施。

## 10. 开发约定与目录结构

```
ZincXManagerClient/    客户端(Avalonia;Views 界面 / ViewModels 逻辑 / Models 数据)
ZincXManagerServer/    子服务端(Command 控制台命令 / Program.cs 主流程)
ZincXManagerOServer/   官方服务端(同上;ZincXManagerOServerNO 是早期 C++ 原型)
ZincXManagerShared/    三端共用:Network(协议/包/TcpClient|TcpServer)、Account(账号/密码/机器码)、
                       Command(命令框架)、FileIO(ZFile 配置读写)、Logging、Verify(官方验证)
ZincXManagerLicTool/   许可证签发工具
installer/             客户端 MSI 定义(WiX)
```

- **命令**:`CommandHandler.Register("命令名", 处理方法, "用途", "用法")` 一处注册,`help` 自动生成;
- **配置**:`ZFile` 读写 `config.ini`(MapGet/WriteFileMap),缺项由 `AskIfMissing` 询问并写回;
- **日志**:`Logger.Log(等级, 文本)`,等级 Fatal/Error/Warning/Info/Debug;
- **网络**:`TcpClient`/`TcpServer` + `PacketDispatcher`;业务处理器都在线程池上排队执行,不阻塞收包;
- **提交约定**:中文提交信息;改协议要同时更新 `Protocol.cs` 与本文件。
