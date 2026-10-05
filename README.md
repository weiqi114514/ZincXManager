# ZincXManager
> [!WARNING]
开发状态：ZincXManager 目前处于极早期开发阶段，ZXMOS能够进行最基础的功能运行并测试

ZincXManager 锌X管理面板 简称ZXM   
ZincXManager 是一款面向 Minecraft/社区 管理员、服主与玩家的一体化综合性使用C#开发(不包括ZXMM)的管理面板/软件。它不仅能够连接 MC 服务端进行管理与交互，还内置了丰富的社区功能，如白名单管理、账号绑定、经济系统等。
采用分布式架构：客户端(ZXMC)，子服务端(ZXMS)和官方服务端(ZXMOS)  
- 客户端(ZXMC)提供用户操作界面和与子服务端(ZXMS)或官方服务端(ZXMOS)通信的媒介，是桌面程序 | 一般运行在个人PC上  
- 子服务端(ZXMS)负责大部分的数据操作和计算，如商店，蓝图库 | 一般和MC服务端运行在一个网络下或一个服务器上 
- 官方服务端(ZXMOS)负责提供官方社区，官方蓝图库等功能 | 仅由ZXM官方运行，运行在ZXM官方的服务器上 
- 模组端(ZXMM)与ZXMS通信，负责执行ZXMS传来的指令，如寄予玩家物品，查看玩家背包等 | 运行在MC服务端上  
  > TIPS：通信均为网络通信
   
ZXMC分为3个连接模式  
- OS（OnlyServer）模式：仅连接子服务端  
- OOS（OnlyOServer）模式：仅连接官方服务端  
- SAOS（ServerAndOServer）模式：可连接子服务端和官方服务端  
> TIPS：当ZXMC处于SAOS模式并且连接到ZXMS时那么ZXMC将通过ZXMS中转与ZXMOS通信，否则直接与ZXMOS通信
   
ZXMC分为4个权限模式  
- Player模式：玩家模式，使用玩家需要的相关功能，如社区经济
- HAdmin模式：高管模式，权限比较高的管理模式，可以控制MC服务端等高权限功能，同时也可使用低权限功能
- LAdmin模式：低管模式，权限比较低的管理模式，可以进行一些消息的发布和玩家的申请处理等低权限功能
- System模式：系统模式，具有最高的权限的管理模式，可直接控制系统相关功能，如报备后登录远程桌面的启用和设置，文件操作，重启软件等，不兼容低权限和高权限功能，
   
> TIPS：当ZXMC处于SAOS模式并且连接到ZXMS时那么ZXMC将通过ZXMS中转与ZXMOS通信，否则直接与ZXMOS通信
> TIPS：此OS(OnlyServer)非彼OS(ZincXManagerOfficialServer)

欢迎有意向合作/开发者加入我们 QQ群835964502
