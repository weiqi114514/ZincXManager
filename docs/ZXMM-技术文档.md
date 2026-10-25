# ZXMM 模组端技术文档

> 适用版本：ZXMM **b0.1.0**（toml 机器版 `0.1.0`）/ MC **1.21.1** / NeoForge **21.1.264** / JDK **21**
> 最后更新：2026-10-25（全链路修 bug + 实机验证后定稿，基线：回归 128 通过 / 0 失败 / 13 套件）

---

## 1. 定位

ZXMM 是跑在 **Minecraft 服务端**里的模组，是整条链路里唯一接触游戏世界的一端，但它**不做任何判断**。

| 它做什么 | 它**不**做什么 |
|---|---|
| 把「玩家进服」报到 ZXMS（`evt|joined`） | 不查登录数组、不建缓存、不查绑定关系 |
| 提供执行接口给 ZXMS 调用（踢人 / 列表 / 信息 / 广播 / 探活） | 不决定"放不放行"，不自己踢人 |

所有业务决策（登录数组、持久登录表、绑定码、账号主人确认）都在 **ZXS** 侧。模组越权做判断 = 架构建反。

> 这张边界表是项目里唯一一份（原 `规范.txt` 里的那份已并入本节），改边界以这里为准。

---

## 2. 拓扑与端口

```
        ┌──── 专用长连接 21200（只听 127.0.0.1）────┐
        │                                          │
  ZXMS ─┤ hello|令牌                    → 模组校验身份
   │     │ evt|joined|玩家名|IP          ← 模组上报进服
   │     │ req|id|方法|参数…             → 模组执行
   │     └ res|id|ok/err|结果            ← 模组回执
   │
   └── 21000 ← ZXMC（玩家管理面板）
```

| 端口 | 方向 | 绑定地址 | 说明 |
|---|---|---|---|
| `21200` | ZXMS → ZXMM | **`api.host`，默认 `127.0.0.1`** | 专用通道，ZXMM 监听、ZXMS 反向连入，断了 3 秒重连 |
| `25565` | MC 客户端 → 服务端 | `*` | 玩家进服；模组在 `PlayerLoggedInEvent` 里上报 |

> 不要把 `api.host` 改成 `0.0.0.0`：执行接口能踢人、能全服广播，暴露到公网等于后门。要跨机部署就配 `api.token` 并用防火墙只放行 ZXMS 的 IP。

---

## 3. 部署

1. 把 `ZXMM-b0.1.0.jar` 放进**服务端**的 `mods/`（客户端不需要装）。
2. 启动一次，生成 `config/zxmm.properties`（见 §4）。
3. 在 ZXMS 的 `config.ini` 里配两个键：

   ```ini
   zxmmapi=127.0.0.1:21200     ; 模组专用通道地址，支持 [::1]:21200
   zxmmtoken=              ; 与模组 api.token 一致；两边都空 = 不校验
   ```

4. 启动顺序无所谓：ZXMS 先起会每 3 秒重连；ZXMM 先起则把进服事件先攒着（见 §7）。

启动日志应看到：

```
[ZXMM] 已加载，专用通道 127.0.0.1:21200，校验开，令牌已启用
[ZXMM] 与 ZXMS 的专用通道已启动，监听 127.0.0.1:21200，令牌已启用
[ZXMM] ZXMS 已连上专用通道: /127.0.0.1:xxxxx        ← ZXMS 连进来时
```

---

## 4. 配置（`config/zxmm.properties`）

| 键 | 默认 | 说明 |
|---|---|---|
| `api.host` | `127.0.0.1` | 监听地址。**默认只听本机** |
| `api.port` | `21200` | 专用通道端口 |
| `api.token` | 空 | 与 ZXMS 的 `zxmmtoken` 一致的共享令牌；空 = 不校验 |
| `check.enabled` | `true` | 总开关。`false` = 不上报进服事件（只有执行接口还活着，调试用） |
| `check.failopen` | `false` | ZXMS 在 `check.grace` 内仍连不上时：`false` = 踢出，`true` = 放行并留日志 |
| `check.grace` | `10` | 等 ZXMS 的秒数（3–300），超时按上面那条处理 |

**旧键已删除并会自动迁移**：`zxms.host`、`zxms.port`、`check.remember` 从来没被读过（模组不往外连，登录数组在 ZXMS 那边）。模组启动时若发现旧键，会重写成上面这份带中文注释的文件，并保留你改过的有效值。

---

## 5. 专用通道协议（21200，UTF-8 文本行）

> **别和 .NET 端的包头混了**：`Id(u64) | Type(u16) | Flags(u16) | Length(u32) | Data` 那套 16 字节大端包头只用于 ZXMC ↔ ZXMS ↔ ZXMOS 之间；ZXMM 走的是这里的**文本行**协议，两者不通用，Java 侧没有 `Protocol.cs` 那样的协议号表。

| 行 | 方向 | 说明 |
|---|---|---|
| `hello\|令牌` | ZXMS → ZXMM | 仅当模组配了 `api.token`。**5 秒内没收到就断开**，令牌不符也断开 |
| `evt\|joined\|玩家名\|IP` | ZXMM → ZXMS | 进服事件。**IP 是纯地址**（不带 `/`、不带端口） |
| `req\|id\|方法\|参数…` | ZXMS → ZXMM | 下发任务，`id` 由 ZXMS 自增 |
| `res\|id\|ok\|结果` | ZXMM → ZXMS | 任务成功 |
| `res\|id\|err\|原因` | ZXMM → ZXMS | 任务失败。**ZXMS 以 `ok`/`err` 判定这件事有没有真的做成** |

### 5.1 执行方法

| 方法 | 参数 | 返回 |
|---|---|---|
| `kick` | 玩家名, 理由 | `ok`=`已踢出`；`err`=`没有这个在线玩家` / `服务器主线程忙，踢人没执行` |
| `list` | — | `ok`=逗号分隔在线名单（空服给空串） |
| `info` | 玩家名 | `ok`=`名\|维度\|X\|Y\|Z`；不在线给空串 |
| `online` | 玩家名 | `ok`=`1`/`0` |
| `say` | 文本 | `ok`=`已广播`；空内容回 `err` |
| `ping` | — | `ok`=`pong` |
| 其它 | — | `err`=`没有这个方法: x` |

约定：

- **理由/广播里的 `|` 不会被截断**：ZXMS 发送前把 `|`、换行替换成 `/` 和空格；模组侧把第 4 段之后的**全部**拼回去。
- **碰游戏状态的活都在服务器主线程**：`list/info/online/kick/say` 统一 `mc.submit(...)` 回主线程执行，最多等 3 秒。直接跨线程读玩家表会撞 `ConcurrentModificationException`。
- **新增方法要同时改两端**：Java `ZxmmApi.execute` 的 `switch` + C# `ZxmmApi` 的封装 + 测试套件 13。

### 5.2 连接维护

| 机制 | 行为 |
|---|---|
| 重连 | ZXMS 断开后每 3 秒重连；模组侧监听失败也会每 3 秒重试（不会一辈子哑掉） |
| 探活 | ZXMS 每 30 秒发一次 `ping`，没回 `pong` 就主动断开重连（半开连接不能只靠 10 秒超时发现） |
| keepalive | 两端都开 `SO_KEEPALIVE`，对端进程被强杀时能尽快发现 |
| 去重 | 重连重叠期同一条事件可能投两遍：连接层 2 秒内按「玩家名+IP」去重；ZXMS 侧再按"同一玩家已有待确认请求"去重 |

---

## 6. 进服判定流程（**全部在 ZXMS**）

```
玩家进服 → 模组上报 evt|joined
   ├ ① 登录数组（内存，5 分钟）命中        → 放行，不做事
   ├ ② PersistentLogin.txt 命中            → 写登录数组 → 放行
   ├ ③ 未绑定                            → 生成 6 位绑定码（去掉 0/O/1/I）
   │      → kick("请先绑定账号。绑定码：XXXXXX —— 在 ZXMC 的「小功能组 → 绑定/解绑」里输入这 6 位")
   └ ④ 已绑定                            → 621 弹给账号的 ZXMC
          ├ 同意   → 登录数组 + 持久登录表（同一玩家只留一行）→ 放行
          ├ 拒绝   → kick("账号主人拒绝了这次登录")
          └ 5 分钟未答 → 624 通知客户端 + kick("登录确认超时(5 分钟)，请重新进入服务器")
```

补充规则（都踩过坑）：

- **只有被问的那个账号能回答 622**。别的账号抢答会被记 `[安全] 越权回答登录请求 …` 并忽略，且不会吃掉请求。
- **改绑要清旧玩家**：账号从 A 改绑到 B 时，清 A 的登录数组 + 持久登录表 + 把在线的 A 踢掉，否则 A 不在任何账号名下却还能免确认进服。
- **解绑同理**：删绑定 → 删持久登录 → 清登录数组 → 踢人。
- **踢人不要 `await`**：报文处理是串行的，等踢人回执会把这条连接后面的包堵到 10 秒超时。

---

## 7. 断链行为（不能变成免验证通道）

| 场景 | 行为 |
|---|---|
| 进服时 ZXMS 没连上 | 事件进队列（最多 200 条，超了丢最老的并告警），日志 `ZXMS 还没连上，<玩家> 的进服事件先攒着(待报 N 条)` |
| ZXMS 在 `check.grace` 内连上 | 立刻按顺序补报：`断线期间攒的 N 条进服事件已补报`，随后按 §6 正常判定 |
| 超过 `check.grace` 仍没连上 | `check.failopen=false`（默认）→ 踢出，理由"账号验证服务未连接，请稍后再进服务器"；`true` → 放行并告警 |

实测（2026-10-25，ZXMS 停着时进服）：

```
[ZXMM] ZXMS 还没连上，smoketest 的进服事件先攒着(待报 1 条)
[ZXMM] 等了 10 秒 ZXMS 还是没连上，按 check.failopen=false 踢出: smoketest
smoketest lost connection: 账号验证服务未连接，请稍后再进服务器
```

---

## 8. 排障对照表

| 日志 | 含义 | 处置 |
|---|---|---|
| `专用通道监听 127.0.0.1:21200 失败(…)，3 秒后重试` | 端口被占用或无权限绑定 | `netstat -ano \| findstr 21200` 查占用；确认 `api.host` 是本机地址 |
| `拒绝未通过令牌校验的连接` | 对端没发 `hello`/令牌不对 | 两边令牌必须一致；ZXMS 侧键名是 `zxmmtoken` |
| `连接 … 5 秒内没上报令牌，已断开` | 有程序连上 21200 但不自报身份 | 正常现象（端口扫描/误连）；若 ZXMS 一直连不上，检查它的 `zxmmtoken` |
| `ZXMS 还没连上，… 先攒着` | ZXMS 没起来/连不上 | 看 ZXMS 日志里的 `专用通道已连上`；确认 `zxmmapi=` 地址端口 |
| `下发踢人任务 X → 没有这个在线玩家` | 人已经走了，踢不到 | 正常，不用处理 |
| `下发踢人任务 X → 专用通道没连上` | 通道断了 | 看 ZXMS 的重连日志与 21200 监听 |
| 玩家弹窗里归属地显示"未知" | 归属地查询超时（上限 1.2 秒）或 IP 是私网 | 私网正常显示"内网"；超时不影响登录 |
| `登录请求 … 被非绑定账号回答，已忽略` | 有人用别的账号抢答 | 安全拦截生效，顺藤查那台 ZXMC |

---

## 9. 版本与产物

| 位置 | 要求 |
|---|---|
| `neoforge.mods.toml` 的 `version` | **`0.1.0`**（纯数字。写成 `b0.1.0` 会崩服——唯一例外） |
| `neoforge.mods.toml` 的 `displayName` | 含 **`b0.1.0`**（用户可见） |
| `build.gradle` 的 `version` | **`b0.1.0`**（决定产物名 `ZXMM-b0.1.0.jar`，用户可见） |
| 代码里的版本常量 | `ZxmmMod.VERSION = "b0.1.0"`，启动日志用它；改版本只改这一处 |
| 产物 | `build/libs/ZXMM-b0.1.0.jar`（约 16 KB） |
| 依赖 | `modId="neoforge" versionRange="[21.1.0,)"`；`modId="minecraft" versionRange="[1.21.1,1.22)"`；`side=SERVER` |
| modid 三处一致 | `gradle.properties` 的 `mod_id`、`@Mod(ZxmmMod.MODID)`、`neoforge.mods.toml` 的 `modId` |

**版本号规则**（模块 / 三端统一）：

| 场合 | 写什么 | 说明 |
|---|---|---|
| 用户能看到的：界面 / 启动日志 / 产物名 / `displayName` | **`b0.1.0`** | 用户侧必须显示完整版本号 |
| `neoforge.mods.toml` 的 `version` | `0.1.0` | 纯数字硬要求，写 b 会崩服 |
| C# 三端对照 | `<Version>`/`<FileVersion>` = `0.1.0`、`<InformationalVersion>` = `b0.1.0` | 同一条规则的另一半 |
| 启动日志 | `[ZXMM] 已加载 b0.1.0，专用通道 127.0.0.1:21200，…` | 由 `ZxmmMod.VERSION` 提供 |

构建：`gradlew.bat build`（JDK 21，`org.gradle.java.home=G:/jdk21`）。
本机无代理时 `maven.neoforged.net` 会卡在 `:createMinecraftArtifacts`，按 `gradle.properties` 注释开代理。

---

## 10. 测试覆盖

回归套件 **13 ZXMM 链路**（`tests/ZincXManagerTests/ZxmmChain.cs`，34 项）用一个「假 ZXMM」替身 + 协议探针，覆盖：

- 未绑定 → 下发 kick（理由带 6 位码）→ 踢人失败被如实记成 `err`
- 绑定码绑定 → 已绑定 → 收到 621 → 同意 → 写持久登录（纯 IP、单行）
- 持久登录命中不再弹窗；同 IP / 换端口两种重复上报都被拦
- 越权回答 622 被拒且不吃掉请求
- 改绑 → 旧玩家免登录清掉且被要求重新绑定；解绑 → 踢人 + 持久表清空 + 要重新绑码
- 配了 `zxmmtoken` 后 ZXMS 先发 `hello|令牌`

真实进服另用 `_tools/mcbot`（MC 1.21.1 协议 767 最小登录机器人，走完 login → configuration 进 play）验证，命令：`mcbot <host> <port> <玩家名> [停留秒数]`。

---

## 11. 开发约定（仅 ZXMM / Java 侧）

> 摘自项目规范 `规范.txt`，只保留与**这个 Java 仓库**相关的条目；ZXMS / ZXMC / ZXMOS / Shared 的 C# 条目不在本页。

### 11.1 注释

- 短，但不能丢信息；写成**活人话**，别写成模板腔。
- 变量注释只写它**表示什么**，一句话就够：

  ```java
  private static int port;      //表示监听端口
  private static String token;  //表示共享令牌
  ```

  反例（太长、等于复述代码）：`//ZXMM 令牌变量存储，用于验证登录流程`。
- 方法、常量同理：一句话说清"干什么"或"什么意思"，不写"这个方法的作用是…"。
- **import 行尾注明用到了里面的什么**（规范第 8 条的 Java 版）：

  ```java
  import java.util.List;                     //导入List(列表接口)
  import java.nio.charset.StandardCharsets;  //导入StandardCharsets(UTF_8)
  import net.minecraft.network.chat.Component; //导入Component(踢人理由)
  ```

  用到的是静态成员时，把它点出来（`//导入StandardCharsets(UTF_8)`），别只写"导入工具类"。

### 11.2 改代码的范围

- **只做被要求的事**，一步不许多做；觉得哪里该顺手改，先问，不许先做再汇报。
- 动手前先找成熟实现（GitHub / CSDN / 官方文档）再落笔，不要凭记忆写 API。
- 需求有歧义必须问；对话话题跨度过大也要先确认。

### 11.3 构建与超时

- 构建统一 `gradlew.bat build`（JDK 21，`org.gradle.java.home=G:/jdk21`），**超时 10 分钟**（规范：JAVA 构建 10 分钟、其他 30 秒、AOT 打包 60 秒）。
- 首次构建要下 NeoForm 反编译，慢是正常的；卡在 `:createMinecraftArtifacts` 就按 `gradle.properties` 注释开代理。
- **已有工具 / 插件优先**（规范第 6 条）：构建走 DSH 的 `mc_gradle` 插件（它会调项目自带 wrapper），链路测试走 `tests/ZincXManagerTests`；有现成插件或脚本能做的，不许再写一套。

### 11.4 测试

- 每写完一个功能必须自测，不能"看起来对"就交。
- 用户说"全量测试"= **全部函数 / 全部类 / 全部功能**都跑，不是抽测。
- 图形界面一律用注入方式操控测试，不要手点。

### 11.5 危险动作要先拿到授权

- 删除文件、AOT 打包、推送、发 Release —— 必须用户明确发话才做。
- 打包是**四端一起**（ZXMS / ZXMC / ZXMM / ZXMOS）；发 Release 时要写更新日志并更新官网技术文档（本页就是"Java 那部分"，不同语言的内容分开写）。

### 11.6 仓库与文档入口

| 项 | 值 |
|---|---|
| 模组仓库 | `E:\report\ZincXManagerMod`（本地工程，**无 git**） |
| 技术文档 | 本文件 + `docs/tech.html` 的「ZXMM 模组端」一节（官网可见） |
| 主仓（C# 三端 + tests + docs） | `E:\report\ZincXManager` |
| 官方端 | `E:\report\ZincXManager-Official` |

动手前先查文档，不要凭记忆写 API。

