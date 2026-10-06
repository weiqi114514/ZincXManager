# 更新日志

ZincXManager(锌X管理面板)的变更记录。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

目前尚未开始打版本标签,变更先累积在「未发布」下,发布时再归档为版本号。

## [未发布] - 2026-10-06

> 本批次主题:**抽出三个端共用的 `ZincXManagerShared` 共享类库,并修复 `ZFile` 的已知缺陷**。
> 涉及端:ZXMC(客户端)、ZXMS(子服务端)、ZXMOS(官方服务端)。

### 新增

**共享类库 `ZincXManagerShared`**(新建项目,位于三个端项目目录之外)

- `Logging/LogLevel.cs`:三个端统一的日志等级枚举(`Debug < Trace < Info < Warning < Error < Fatal`)与名称解析 `GetName()` / `TryParse()`。
- `Logging/Logger.cs`:静态日志器,同时输出到控制台与全局日志文件,支持 `AddSink()` 注册额外输出目标(供 GUI 把日志显示到界面),`EchoToConsole` 可关闭控制台输出。
- `Logging/ILogger.cs`:日志输出目标抽象。
- `Logging/StreamLogger.cs`:独立文件的日志写入器(一个实例一份日志,不占用全局日志文件)。
- `FileIO/ZFile.cs`:文件读写工具(见下方新增 API、变更与修复)。
- `Command/CommandHandler.cs`:控制台命令框架,ZXMS 与 ZXMOS 共用同一份实现。

**`ZFile` 新增读取 API(用于区分「文件为空」与「读取失败」)**

- `byte[]? ReadFileBytes()`:`null` 表示读取失败,空数组表示文件确实为空。
- `bool TryReadFileBin(out string data)`:`true` 表示读取成功(空文件也算成功)。
- `string ReadFileBin(string fileName)`:按路径读取,不需要先打开文件。

**其它**

- `Logger.OpenDefaultLogFile(前缀, out 文件名, 目录?)` 与 `Logger.DefaultLogFileName(前缀, 目录?)`:三个端统一的日志文件命名入口。
- ZXMOS(官方服务端)补齐 C# 实现:移植 ZXMS 的日志、文件与命令能力,入口带官方服务端横幅与欢迎语。
- `ZFile` 功能测试用例 140 项(直接链接共享类库源码、未打桩),覆盖打开/关闭/删除、文本/MAP/二进制读取、目录列举、三种写入、日志联动与日志器。测试工程位于本地会话工作区,尚未入库。

### 变更

- **三个端改为引用共享类库**:ZXMC / ZXMS / ZXMOS 的 `csproj` 增加对 `ZincXManagerShared` 的 `ProjectReference`,并删除各自目录里的重复实现。
- **`ZFile` 打开文件改用 `FileShare.ReadWrite`**(原为 `FileShare.Read`):同一文件现在可以被多个 `ZFile` 实例或其它进程同时打开。
- **`ZFile` 内部加锁访问**:公开方法可跨线程调用;日志一律在释放锁之后写出,避免与 `Logger` 互相持锁。
- **写文件保留原有换行风格**:按行写、写键值、追加写都会沿用文件原本的 CRLF / LF / CR,不再把 CRLF 文件写成混合换行;统一 UTF-8 无 BOM。
- **`ZFile.ReadDir` 结果按名称排序**(原为文件系统返回顺序,不确定),子目录仍带 `/` 后缀、每项以 `\n` 结尾。
- **日志文件名统一带端类型前缀**:`ZXMS…log` / `ZXMOS…log` / `ZXMC…log`(ZXMS 此前没有前缀,本次补齐)。
- **ZXMC 日志只写文件**:客户端关闭控制台输出(`Logger.EchoToConsole = false`),日志直接落文件;客户端原有的 `Class/Logging` 实现由共享类库取代。
- ⚠ **`WriteFileTxt(txt, newline, line)` 的 `newline` 语义明确化**:现在只决定「新写入的这一行是否以换行结尾」,`false` 时不再额外补末尾换行,与追加写重载的 `endl` 含义保持一致。

### 修复

- **`ReadFileBin()` 在文件打开状态下永远读不到内容**(严重):旧实现用 `File.ReadAllBytes(_currentFile)` 重新打开文件,而当前实例已持有该文件的写句柄,第二次打开的 `FileShare.Read` 触发共享冲突,异常被 `catch` 吞掉后返回空字符串;由于 `_currentFile` 只在打开期间非空,该方法此前在任何状态下都不可用。现改为直接从已打开的流读取。
- **`WriteFileTxt(txt, newline, line)` 多写一个空行**:旧实现行内容自带 `\n` 后又由拼接补了一次,导致文件出现连续两个换行(`a\nb\nc\n\n`);现由统一的写回函数控制换行。
- **`WriteFileMap` 会丢弃文件中的空行**:重写整个文件时把空行过滤掉了,现改为空行、注释行与其它行全部原样保留。
- **`FileOperate(fileName, "del")` 后 `CurrentFile` 未清空**:删除后仍指向已删除的文件,现统一在关闭流时清空文件名与换行缓存。
- **写键值时键两侧空格会产生重复键**:`ReadFileMap` 会裁剪键两侧空格,而 `WriteFileMap` 只做前缀匹配,导致 `name = old` 与 `name=new` 并存;现两边都按「忽略两侧空格」比较同一个键。
- **`Dispose()` 会产生「关闭文件为无效操作」告警**:重复释放时在退出阶段刷出无意义的 Warning,现改为静默关闭。
- **日志等级顺序与文档不一致**:旧枚举把 `Info` 排在 `Trace` 之前,导致门槛设为 `Trace` 时 `Info` 反被过滤,而命令 `help` 里写的顺序是 `Debug|Trace|Info|Warning|Error|Fatal`;现枚举顺序与文档一致。
- **ZXMC 的 `MainWindow.axaml` 编译错误**(既有问题,被本次 `csproj` 改动暴露):`Click="OnMyButtonClick"` 缺少对应方法、`Command="{Binding ClickCommand}"` 缺少 `x:DataType` 与 `ClickCommand`;现补上事件处理器、`x:DataType` 与 `[RelayCommand]`。

### 移除

- 删除 `ZincXManagerServer/` 下的 `Logging/`、`FileIO/`、`Command/`(迁入共享类库)。
- 删除 `ZincXManagerClient/Class/Logging/`(`ILogger.cs`、`LogLevel.cs`、`Logger.cs`,由共享类库取代)。
- 旧 C++ 版实现迁出 `ZincXManagerOServer/`(见下方说明)。

### 需要注意的兼容性变化

- **日志等级数值变化**:`LogLevel` 的数值顺序调整为 `Debug=0, Trace=1, Info=2, Warning=3, Error=4, Fatal=5`。仓库内没有持久化等级数值的地方,但外部若记录过旧数值需要重新确认。
- **三参数 `WriteFileTxt` 的末尾换行**:`newline=false` 时文件不再自动补末尾换行。
- **日志文件名**:ZXMS 的日志文件名现在带 `ZXMS` 前缀。
- **文件共享模式放宽的副作用**:`FileShare.ReadWrite` 允许同一文件被多实例/多进程同时打开,库内只保证单实例的线程安全,没有做跨进程互斥,请勿让多个进程同时写同一个数据文件。

### 验证

- `dotnet build ZincXManager.slnx`:**共享类库、ZXMC、ZXMS、ZXMOS 四个项目全部编译通过,0 警告 0 错误**。
- 功能测试(直接链接共享类库源码、未打桩):**140 项用例,139 通过 / 0 失败 / 1 条设计说明**(说明项为 `ReadDir` 只依赖传入路径、不需要先打开文件)。
- ZXMS / ZXMOS 命令行实测:6 字节文件的 `file readfilebin` 输出 `已读取 6 字节`(修复前为 `(空文件)`);`file writefilemap` 后 `map.ini` 保留注释与空行;`file writefiletxt X true 2` 写入 `lines.txt` 得到 `a\nX\n`(无多余空行);两个端的日志文件名已带各自前缀。
- ZXMC 通过编译验证,未做界面运行验证。

### 本批次中在工作区既有的改动(非本次工作)

- 旧 C++ 版 ZXMOS 源码(`ZFile.cpp/h`、`ZincXManagerOServer.cpp`、`vcxproj`、`src/logging/*`、`config.ini`)从 `ZincXManagerOServer/` 移出,完整副本存放于 `ZincXManagerOServerNO/`。
- 上述文件与本次改动属于同一批次提交,故在此一并记录。
