# ZXMM —— ZincXManager 登录验证模组

把「玩家进服」这件事报给子服务端 **ZXMS**，由账号主人（ZXMC 客户端）确认后再放行。

- 游戏版本：**Minecraft 1.21.1**
- 模组加载器：**NeoForge 21.1.264**（1.21.1 对应的版本号）
- 只有服务端逻辑，玩家不需要装

## 进服流程

1. 玩家进服 → 模组查「确认名单 / 持久登录表」，确认过就直接放行
2. 没确认过 → 给 ZXMS 发 **620**（玩家名 + IP）
3. ZXMS 回 **623**：

| 回包 | 意思 | 模组动作 |
|---|---|---|
| ok | 账号主人点了允许 | 记进确认名单，踢出并提示「请重新进入」 |
| nobind\|码 | 这个玩家还没绑账号 | 踢出，并把 6 位绑定码打在提示里 |
| no | 账号主人拒绝 | 踢出 |
| timeout | 5 分钟没人点 | 踢出 |
| err\|原因 | 其它错误 | 踢出并显示原因 |

## 配置文件

服务端目录下 `config/zxmm.properties`（第一次启动自动生成）：

```properties
# ZXMS 地址（子服务端的 czport）
zxms.host=127.0.0.1
zxms.port=21000

# 关掉就完全不校验（调试用）
check.enabled=true

# 连不上 ZXMS 时：true=放行，false=踢出
check.failopen=false

# 确认后多少分钟内免再次校验（0 = 每次都要重新确认）
check.remember=5
```

持久登录表在 `config/zxmm-confirmed.txt`（一行一个玩家名）。

## 代码结构

`src/main/java/com/zincxmanager/zxmm/`：

| 文件 | 作用 |
|---|---|
| ZxmmMod.java | NeoForge 主类：事件订阅、踢人、确认名单 |
| ZxmsLink.java | 连 ZXMS、发请求等回包（协议号 620 / 623） |
| Packet.java | 16 字节大端头 + UTF-8 负载（和各端一致） |
| ZxmmConfig.java | 读写 `config/zxmm.properties` |

## 怎么编译

NeoForge 模组必须在「官方映射」下重映射 MC 才能编译，这一步官方只提供了 Gradle 插件，所以还是要有 Gradle 工程。最小 `build.gradle`：

```groovy
plugins {
    id 'java'
    id 'net.neoforged.moddev' version '2.0.78'
}

java {
    toolchain.languageVersion = JavaLanguageVersion.of(21)
}

neoForge {
    version = "21.1.264"
}

// 依赖下载走国内镜像（maven.neoforged.net 直连常常超时）
repositories {
    maven { name = 'QLU-BMCLAPI'; url = uri('https://mirrors.qlu.edu.cn/bmclapi/') }
    maven { name = 'Minecraft';   url = uri('https://libraries.minecraft.net/') }
    mavenCentral()
}
```

`gradle.properties`：

```properties
org.gradle.java.home=G:/jdk21
```

然后 `gradlew build`，产物在 `build/libs/ZXMM-b0.1.0.jar`，丢进服务端 `mods/` 即可。

> 如果 Gradle 还去连 `maven.neoforged.net` 超时，可以在 `gradle.properties` 里配代理（`systemProp.http.proxyHost` / `systemProp.http.proxyPort`），或者给 Gradle 全局 init 脚本里加同一个镜像。

### 不用 Gradle 行不行

不行得很彻底：MC 1.21.1 的 jar 是混淆的，必须先按官方映射重映射才能对得上 `net.minecraft.*` 里的类名，而 remap 这一步只有 Gradle 插件给做好了。手搓等价于自己实现一遍 remapper，不比装个 Gradle 省事。

## 和 ZXMS 的协议

| 号 | 名称 | 方向 | 负载 |
|---|---|---|---|
| 620 | LoginRequest | ZXMM → ZXMS | 玩家名\|IP |
| 623 | LoginResult | ZXMS → ZXMM | ok\|玩家\|账号名\|ID / nobind\|码\|玩家 / no\|… / timeout\|玩家 / err\|原因 |
| 625 | BindNotice | ZXMM → ZXMS | 绑定码\|玩家名（可选） |

包头：`Id(u64) Type(u16) Flags(u16) Length(u32)`，全部大端，负载 UTF-8。
