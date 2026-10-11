# ZincXManager(锌X管理面板 · ZXM)

> [!WARNING]
> **开发状态:极早期(alpha)** —— 核心链路已经能跑通、也有自动化测试兜着,但界面与数据格式仍会变动,
> **不建议用在正式的生产服务器上**;升级后偶尔需要重新登录一次。

面向 **Minecraft 服主与社区管理员** 的一体化管理面板:客户端连上服务端以后,既能管 MC 服务端,
也能做社区运营 —— 账号与权限、白名单、经济与商店、蓝图库、公告与消息推送等等。主要用 C# 写,采用分布式架构。

**官网**:<http://zxm.zincms.top:10> · **GitHub Pages 镜像**:<https://weiqi114514.github.io/ZincXManager/> · **官方服务端**:`zxm.zincms.top:25000` · **文档**:[分类文档(5 篇)](https://weiqi114514.github.io/ZincXManager/guide/) · 官网[帮助页](http://zxm.zincms.top:10/help.html) · **交流群**:QQ `835964502` · **开源协议**:GPL-2.0

---

## 现在能用到什么程度

**✅ 已经能用**

- 账号:注册 / 登录 / 退出,四种权限等级(Player / LAdmin / HAdmin / System)
- 连接:连官方服务端(默认)或自建子服务端(SAOS 模式)
- 消息:公告、定向推送、五种卡片(文本 / 更新 / 选择 / 填空 / 下拉);卡片按钮点了会回报服务端
- 消息存档:本地按来源分档保留、过期自动清理、可单条删除、重开客户端还在
- 界面:首次使用向导、设置页各分区、毛玻璃 / 半透明两档、左上角提示条
- 社区功能:经济、商店、蓝图画布、好友与组织等分区的基础版本
- 自建 ZXMS:自动接入官方服务端、账号与封禁(账号 / IP / 机器码连坐)、公告与推送、控制台命令

**⚠ 还不能用 / 正在做**

- 模组端 ZXMM:协议还没定,尚未实现(打算做,不是砍掉)
- 官方验证目前只做到"验对端是不是官方",在线令牌与吊销还没做
- 只提供 64 位 Windows 客户端(单文件 AOT),没有移动端
- 没做长期运行压测;遇到问题请进群反馈

> 客户端的数据(配置 / 消息 / 日志 / 头像)都生成在 exe 同目录,不写注册表业务项。

## 下载与部署

| 你要装的东西 | 从哪拿 |
| --- | --- |
| 客户端 ZXMC | 官网[下载页](http://zxm.zincms.top:10/download.html):安装包(MSI,全机器安装)或免安装 ZIP;也可从 [GitHub Releases](https://github.com/weiqi114514/ZincXManager/releases) 下(同一批文件,哈希一致) |
| 子服务端 ZXMS(自建才需要) | 官网下载页 → 子服务端压缩包;部署步骤见官网[子服教程](http://zxm.zincms.top:10/zxms.html) |
| 官方服务端 ZXMOS | 由官方运行,**不需要你自建**;客户端默认连 `zxm.zincms.top:25000` |

## 从源码构建

前置:.NET SDK 10(GUI 目前只在 Windows 上验证过)。

```powershell
git clone https://github.com/weiqi114514/ZincXManager.git
cd ZincXManager
dotnet build ZincXManager.slnx              # 编译全部
dotnet run --project ZincXManagerClient     # 开发态起客户端
```

单文件 AOT 发布(客户端):

```powershell
dotnet publish ZincXManagerClient -c Release -r win-x64
```

> 注意:客户端发布产物必须让 `ZincXManagerClient.exe` 与 `libSkiaSharp.dll`、`av_libglesv2.dll`、
> `libHarfBuzzSharp.dll` 待在同一个目录里,少一个起不来。

打 MSI(WiX v5):

```powershell
dotnet tool install --global wix --version 5.*
wix build installer\zxmc.wxs -o out\ZincXManagerClient-x64.msi `
    -d Version=0.1.0 -d StageDir=<暂存目录> -d IconPath=<zxm.ico> -arch x64
```

## 项目结构

| 目录 | 说明 |
| --- | --- |
| `ZincXManagerClient` | 客户端 **ZXMC**:Avalonia 桌面界面(登录 / OOBE 向导 / 主界面 / 设置) |
| `ZincXManagerServer` | 子服务端 **ZXMS**:本服账号、封禁、消息、蓝图,负责与客户端和官方端通信 |
| `ZincXManagerOServer` | 官方服务端 **ZXMOS**(带控制台) |
| `ZincXManagerOServerNO` | 官方服务端(裁剪版) |
| `ZincXManagerShared` | 三端共用:协议、账号与权限、命令分发、日志、官方验证 |
| `ZincXManagerLicTool` | 官方许可证签发工具(仅官方使用) |
| `installer/` | 客户端 MSI 定义(WiX)与打包说明 |

## 四端架构与运行模式

- **客户端 ZXMC** —— 用户操作的桌面程序,一般跑在个人 PC 上
- **子服务端 ZXMS** —— 承担本服的数据与计算(账号、封禁、商店、蓝图、消息),一般和 MC 服务端在同一个网络里
- **官方服务端 ZXMOS** —— 官方社区与官方蓝图库,**仅由 ZXM 官方运行**
- **模组端 ZXMM** —— 与 ZXMS 通信、在 MC 服务端里执行指令(如发放物品、查看背包),*尚未实现*

通信全部走网络。ZXMC 有三种连接模式:

- **OS**(OnlyServer):只连子服务端
- **OOS**(OnlyOServer):只连官方服务端
- **SAOS**(ServerAndOServer):同时连子服务端与官方服务端
  > 处于 SAOS 且已连上 ZXMS 时,ZXMC 经由 ZXMS 中转与 ZXMOS 通信;否则直连 ZXMOS。

ZXMC 有四种权限模式:Player(玩家)/ LAdmin(低管)/ HAdmin(高管)/ System(系统,最高权限,不兼容低/高管功能)。

> TIPS:此 OS(OnlyServer)非彼 OS(ZincXManagerOfficialServer)。
> TIPS:**官方服务端 ≠ 子服务端** —— 一个是官方提供、所有人共用的社区端,一个是各服自己部署的子端。

## 官方验证(防止别人冒名官方服务端)

- **第一层**:ZXMS / ZXMC 内置官方公钥,启动时自校验发行产物是否被改过
- **第二层**:接入后走 `520` / `521` 对端验证,确认对面确实是官方 ZXMOS
- 官方发行版验证不过会直接断开;开发构建只记警告
  (运维开关:子服 `officialcheck=0`,客户端 `allowunverified=1`)

## 相关链接

- 官网:<http://zxm.zincms.top:10/>
- 更新日志:[CHANGELOG.md](CHANGELOG.md)
- 子服务端部署教程:<http://zxm.zincms.top:10/zxms.html>
- 交流群:QQ `835964502`

## 许可证

[GPL-2.0](LICENSE)。ZXM 是自由软件:你可以自由使用、修改与再分发,
但衍生作品必须同样以 GPL 授权,并保留版权声明。
