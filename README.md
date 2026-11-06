# ZincXManager(锌X管理面板 · ZXM)

> [!WARNING]
> **开发状态:早期(Beta)** 大致能用   
> **不建议用在正式的生产服务器上** 数据结构等会变动

面向 **Minecraft 服主与社区管理员** 的一体化管理面板:客户端连上服务端以后,既能管 MC 服务端,
也能做社区运营 —— 账号与权限、白名单、经济与商店、蓝图库、公告与消息推送等等。ZXMS/ZXMC/ZXMOS使用C#编写 ZXMM使用JAVA编写,采用分布式架构。

**官网**:<http://zxm.zincms.top> · **官方服务端 (暂时使用隧道)**:`IP：frp-bag.com 端口：48335` · **交流群**:QQ `1129776132` · **开源协议**:GPL-2.0

---

## 下载与部署

| 你要装的东西 | 从哪拿 |
| --- | --- |
| 客户端 ZXMC | 官网[下载页](http://zxm.zincms.top/download.html):安装包(MSI,全机器安装)或免安装 ZIP |
| 子服务端 ZXMS(自建才需要) | 官网下载页 → 子服务端压缩包;部署步骤见官网[子服教程](http://zxm.zincms.top/zxms.html) |
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
    -d Version=0.1.1 -d StageDir=<暂存目录> -d IconPath=<zxm.ico> -arch x64
```

## 项目结构

| 目录 | 说明 |
| --- | --- |
| `ZincXManagerClient` | 客户端 **ZXMC**:Avalonia 桌面界面(登录 / OOBE 向导 / 主界面 / 设置) |
| `ZincXManagerServer` | 子服务端 **ZXMS**:本服账号、封禁、消息、蓝图,负责与客户端和官方端通信 |
| `ZincXManagerOServerNO` | 官方服务端(废案) |
| `ZincXManagerShared` | 三端共用:协议、账号与权限、命令分发、日志、官方验证 |
| `installer/` | 客户端 MSI 定义(WiX)与打包说明 |

## 四端架构与运行模式

- **客户端 ZXMC** —— 用户操作的桌面程序,一般跑在个人 PC 上
- **子服务端 ZXMS** —— 承担本服的数据与计算(账号、封禁、商店、蓝图、消息),一般和 MC 服务端在同一个网络里
- **官方服务端 ZXMOS** —— 官方社区与官方蓝图库,**仅由 ZXM 官方运行**
- **模组端 ZXMM** —— 与 ZXMS 通信、在 MC 服务端里执行指令(如发放物品、查看背包)。

通信全部走网络。ZXMC 有三种连接模式:

- **OS**(OnlyServer):只连子服务端
- **OOS**(OnlyOServer):只连官方服务端
- **SAOS**(ServerAndOServer):同时连子服务端与官方服务端
  > 处于 SAOS 且已连上 ZXMS 时,ZXMC 经由 ZXMS 中转与 ZXMOS 通信;否则直连 ZXMOS。

ZXMS 有四种权限账号:Player(玩家)/ LAdmin(低管)/ HAdmin(高管)/ System(系统,最高权限,不兼容低/高管功能)。
ZXMOS 暂不透露

> TIPS:此 OS(OnlyServer)非彼 OS(ZincXManagerOfficialServer)。
> TIPS:**官方服务端 ≠ 子服务端** —— 一个是官方提供、所有人共用的社区端,一个是各服自己部署的子端。

## 相关链接

- 官网:<http://zxm.zincms.top/>
- 更新日志: [官网日志页](http://zxm.zincms.top/changelog.html)
- 子服务端部署教程: <http://zxm.zincms.top/guide-03.html>
- 交流群:QQ `1129776132`
- 有意加入开发:QQ `835964502`

## 许可证

[GPL-2.0](LICENSE)。ZXMC/ZXMM/ZXMS 是自由软件:你可以自由使用、修改与再分发,
但衍生作品必须同样以 GPL 授权,并保留版权声明。
