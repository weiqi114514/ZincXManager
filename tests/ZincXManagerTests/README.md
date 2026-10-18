# C# 全量测试(_tests/zxm-tests)

> 替代原来那套 PowerShell 脚本。全部 C# 写,直接引用 ZincXManagerShared 源码,起真实进程、走真实协议。

## 怎么跑

    cd G:\文档\deepseek-harness\default-workspace\_tests
    dotnet run -c Debug

- 退出码:0 = 全绿,非 0 = 失败项数
- 每个套件自己起服务端/客户端,跑完自动清理
- 临时目录在 ../_zxt/<套件>/ ,不会污染交付包

## 套件清单

| # | 套件 | 覆盖 |
|---|---|---|
| 1 | 冒烟 | ZXMS 起服、380/381 加密协商、200/201/202 身份包 |
| 2 | 注册/登录 | 注册、登录、错密码被拒、users.json 记机器码与 IP |
| 3 | 公告/推送/补发 | notice、push、卡片分段、不在线忽略、断线重连补收 |
| 4 | 封禁/解封/踢线 | bans.json 机器码封禁、ban del 解封、ban user 踢线 |
| 5 | 官方验证 | ZXMTL 机器码/签证、无证退出码 2、521 证明验签 |
| 6 | 消息存档 | messages.json 结构、过期不补发、未过期照常补发 |
| 7 | 改名 | 201 下发、info 展示、config 落盘 |
| 8 | 身份码 | 32 位服务端码生成、64 位机器码、重启不变 |
| 9 | AOT 冒烟 | 四个交付包单文件、ZXMS 能起、ZXMOS 无证退出、客户端出窗 |
| 10 | MSI | 体积、数字签名;msiserver 不可用时跳过安装(环境备注) |
| 11 | GUI 注入 | 起客户端、摆窗、Win32 点击/键盘注入通道 |
| 12 | 官网 | docs 页面存在、静态资源、站内死链、首页关键入口 |

## 基础设施(Infra.cs)

- Svc:起服务端(整目录复制 + config.ini)、等端口、发控制台命令、读输出
- Probe:纯 C# 客户端探针 —— ECDH 握手(380/381)、上线(300)、注册、登录(nonce/proof)、收包
- SuiteResult:断言收集,区分「通过/失败」与「环境备注(不计分)」

## 踩过的坑(别重复)

| 坑 | 结论 |
|---|---|
| 数据目录 | ZXMS/ZXMOS 的 config.ini 与 logs 在 exe 同目录,必须整目录复制再起 |
| 控制台编码 | 服务端控制台是 GBK(936),stdin/stdout 都按 GBK |
| 包头 | Id 是 8 字节大端;Type/Flags/Length 也大端 |
| 握手时序 | 收到 381 后要 Task.Delay(150) 再发加密包 |
| 首包 | 服务端连上会先推 200/201/202,要缓存下来 |
| 命令名 | 封禁命令是 ban add / ban user / ban del / ban list |
| 卡片格式 | 种类|标题|正文|来源|…,种类在第 0 段 |
| 配置覆盖 | 重启不要再用 ini 覆盖,否则 servercode 被重置 |
| 文件占用 | 服务端运行时读 config.ini 要 FileShare.ReadWrite |
