<#
  一键打包: 五个 Windows AOT 产物 + ZXMS/ZXMC 的 Linux AOT + 客户端 MSI/便携 zip + 刷新桌面目录。

  用法:
    pwsh -File _publish-aot.ps1                    # 按 csproj 里的 InformationalVersion 打包,五个 Windows 产物并行编
    pwsh -File _publish-aot.ps1 -Jobs 5            # 并行度(默认取 CPU 核数的一半,最多 5)
    pwsh -File _publish-aot.ps1 -SkipLinux         # 不出 Linux
    pwsh -File _publish-aot.ps1 -SkipInstaller     # 不出 MSI/zip,只刷程序目录
    pwsh -File _publish-aot.ps1 -WindowsOnly ZXMC  # 只重编某几个 Windows 产物(逗号分隔)

  产物:
    publish\<名字>-win-x64\            各端 AOT 单文件
    publish\ZXMS-linux-x64\ 等         Linux AOT(在 WSL 里编,AOT 不能交叉编译)
    <桌面>\安装包\*.msi / *.zip / *.tar.gz / SHA256.txt
#>
param(
    [string]$Version = "",
    [string]$Desktop = "G:\桌面\ZXM-AOT-测试",
    [string]$WslDistro = "Ubuntu",
    [int]$Jobs = 0,
    [string[]]$WindowsOnly = @(),
    [switch]$SkipLinux,
    [switch]$SkipMac,
    [switch]$SkipInstaller,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$parent = Split-Path -Parent $root
$official = Join-Path $parent 'ZincXManager-Official'
$modDir = Join-Path $parent 'ZincXManagerMod'
$out = Join-Path $root 'publish'
$total = [System.Diagnostics.Stopwatch]::StartNew()

if ($Jobs -le 0) { $Jobs = 2 }
if ($Jobs -gt 3) { $Jobs = 3 }

function Step($text) {
    Write-Host ""
    Write-Host ("[{0,5:N1}s] ==> {1}" -f $total.Elapsed.TotalSeconds, $text) -ForegroundColor Cyan
}

function Assert-LastExit($what) {
    if ($LASTEXITCODE -ne 0) { throw "$what 失败(exit=$LASTEXITCODE)" }
}

if (-not $Version) {
    $text = Get-Content (Join-Path $root 'ZincXManagerServer\ZincXManagerServer.csproj') -Raw
    if ($text -match '<InformationalVersion>([^<]+)</InformationalVersion>') { $Version = $Matches[1] }
    else { throw '读不到版本号,请用 -Version 指定' }
}
Write-Host "版本 $Version,并行度 $Jobs" -ForegroundColor Green

# MSI 只认 x.y.z:third = 小版本*100 + 修复字母序号(a=1)
function Get-MsiVersion([string]$v) {
    if ($v -notmatch '^[a-z]?(\d+)\.(\d+)\.(\d+)([a-z]?)$') { throw "版本号 $v 不符合 [类型][大].[中].[小][极小]" }
    $major = [int]$Matches[1]; $minor = [int]$Matches[2]; $patch = [int]$Matches[3]
    $fix = if ($Matches[4]) { [int][char]$Matches[4] - [int][char]'a' + 1 } else { 0 }
    return "$major.$minor.$($patch * 100 + $fix)"
}

# Windows 盘符路径转 WSL 路径:E:\a\b -> /mnt/e/a/b(不走 wslpath,反斜杠传参会丢)
function To-WslPath([string]$p) {
    $full = [System.IO.Path]::GetFullPath($p)
    $drive = $full.Substring(0, 1).ToLower()
    $rest = $full.Substring(2) -replace '\\', '/'
    return "/mnt/$drive$rest"
}

# 从 CHANGELOG 抽出当前版本的段,给客户端「更新日志」用
function Get-UpdateLog([string]$v) {
    $lines = Get-Content (Join-Path $root 'CHANGELOG.md')
    $start = -1; $collected = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "^## \[$([regex]::Escape($v))\]") { $start = $i; continue }
        if ($start -ge 0) {
            if ($lines[$i] -match '^## \[') { break }
            $collected += $lines[$i]
        }
    }
    if ($start -lt 0) { throw "CHANGELOG 里没有 [$v] 段,先补上再打包(客户端 UpdateLog.MD 和 MSI 都要它)" }
    return ("## [$v]" + ($collected -join "`r`n")).Trim()
}

$winProjects = @(    [pscustomobject]@{ Name = 'ZXMOS'; Project = (Join-Path $official 'ZincXManagerOServer\ZincXManagerOServer.csproj') },
    [pscustomobject]@{ Name = 'ZXMS';  Project = (Join-Path $root 'ZincXManagerServer\ZincXManagerServer.csproj') },
    [pscustomobject]@{ Name = 'ZXMC';  Project = (Join-Path $root 'ZincXManagerClient\ZincXManagerClient.csproj') },
    [pscustomobject]@{ Name = 'ZXMTL'; Project = (Join-Path $official 'ZincXManagerLicTool\ZincXManagerLicTool.csproj') },
    [pscustomobject]@{ Name = 'Tests'; Project = (Join-Path $root 'tests\ZincXManagerTests\ZincXManagerTests.csproj') }
)
if ($WindowsOnly.Count -gt 0) {
    $winProjects = $winProjects | Where-Object { $WindowsOnly -contains $_.Name }
    if (-not $winProjects) { throw "-WindowsOnly 里没有匹配的产物名(可选:ZXMOS/ZXMS/ZXMC/ZXMTL/Tests)" }
}

# 源码指纹:项目自身 + Shared 的源文件最后写入时间,用来跳过没改过的端(AOT 一次要几十秒到几分钟)
function Get-SourceHash([string]$projectPath, [string]$repoRoot) {
    $dirs = @((Split-Path $projectPath -Parent), (Join-Path $repoRoot 'ZincXManagerShared')) | Select-Object -Unique
    $sb = [System.Text.StringBuilder]::new()
    foreach ($d in $dirs) {
        if (-not (Test-Path $d)) { continue }
        Get-ChildItem $d -Recurse -File -Include *.cs, *.csproj, *.axaml, *.json, *.resx -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
            Sort-Object FullName |
            ForEach-Object { [void]$sb.Append($_.FullName).Append(':').Append($_.LastWriteTimeUtc.Ticks).Append(';') }
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
    $sha = [System.Security.Cryptography.SHA256]::Create()
    return ([System.BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLower()
}
$rootDir = $root
$forceBuild = [bool]$Force
# 指纹在父进程算好带进去:并行 runspace 与后台 job 里拿不到父进程的函数
foreach ($p in $winProjects) { $p | Add-Member -NotePropertyName Hash -NotePropertyValue (Get-SourceHash $p.Project $root) -Force }

Step "1/6 Windows AOT 并行编译($($winProjects.Count) 个,并行度 $Jobs),Linux AOT 同时开跑"
$outDir = $out

# Linux 两个端共用 Shared 的 obj,并行会抢 project.assets.json;用 --artifacts-path 各给一份中间目录后才能真正并行
$linuxJobs = @()
if (-not $SkipLinux) {
    foreach ($lp in @(
        [pscustomobject]@{ Name = 'ZXMS'; Project = (Join-Path $root 'ZincXManagerServer\ZincXManagerServer.csproj') },
        [pscustomobject]@{ Name = 'ZXMC'; Project = (Join-Path $root 'ZincXManagerClient\ZincXManagerClient.csproj') }
    )) {
        $lp | Add-Member -NotePropertyName Hash -NotePropertyValue (Get-SourceHash $lp.Project $root) -Force
        $linuxJobs += Start-Job -ArgumentList $lp, $out, $WslDistro, $forceBuild -ScriptBlock {
            param($lp, $outDir, $distro, $force)
            $name = $lp.Name; $project = $lp.Project; $hash = $lp.Hash
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $target = Join-Path $outDir "$name-linux-x64"
            $stamp = Join-Path $target '.buildhash'
            if (-not $force -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $hash)) {
                Write-Host ("    {0,-6} 源码没变,跳过 (linux)" -f $name) -ForegroundColor DarkYellow
                return
            }
            if (Test-Path $target) { Remove-Item $target -Recurse -Force }
            $full = [System.IO.Path]::GetFullPath($project)
            $wslProj = "/mnt/" + $full.Substring(0, 1).ToLower() + ($full.Substring(2) -replace '\\', '/')
            $fullOut = [System.IO.Path]::GetFullPath($target)
            $wslOut = "/mnt/" + $fullOut.Substring(0, 1).ToLower() + ($fullOut.Substring(2) -replace '\\', '/')
            $arc = "/tmp/zxm-arc-$($name.ToLower())"
            $log = Join-Path $outDir ("_log_linux_" + $name + ".txt")
            wsl -d $distro -- bash -lc "dotnet publish '$wslProj' -c Release -f net10.0 -r linux-x64 -p:PublishAot=true --artifacts-path '$arc' -o '$wslOut' --nologo" *> $log
            if ($LASTEXITCODE -ne 0) {
                Get-Content $log -Tail 12 | ForEach-Object { Write-Host $_ -ForegroundColor Red }
                throw "$name linux publish 失败"
            }
            Remove-Item $log -Force -ErrorAction SilentlyContinue
            Get-ChildItem $target -Filter *.dbg -File -ErrorAction SilentlyContinue | Remove-Item -Force
            $hash | Set-Content $stamp -Encoding ASCII
            $size = [math]::Round((Get-ChildItem $target -File | Measure-Object Length -Sum).Sum / 1MB, 1)
            Write-Host ("    {0,-6} {1,5:N1}s  {2} MB (linux)" -f $name, $sw.Elapsed.TotalSeconds, $size) -ForegroundColor DarkGray
        }
    }
}
$winProjects | ForEach-Object -Parallel {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $target = Join-Path $using:outDir "$($_.Name)-win-x64"
    $stamp = Join-Path $target '.buildhash'
    if (-not $using:forceBuild -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $_.Hash)) {
        Write-Host ("    {0,-6} 源码没变,跳过" -f $_.Name) -ForegroundColor DarkYellow
        return
    }
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    $log = Join-Path $using:outDir ("_log_" + $_.Name + ".txt")
    # 各端共用 Shared 的 obj,并行会抢同一个 dll;--artifacts-path 给每个端一份独立中间目录
    $arc = Join-Path $using:outDir ("_arc_" + $_.Name)
    if (Test-Path $arc) { Remove-Item $arc -Recurse -Force }
    dotnet publish $_.Project -c Release -f net10.0 -r win-x64 -p:PublishAot=true --artifacts-path $arc -o $target --nologo *> $log
    if ($LASTEXITCODE -ne 0) {
        Get-Content $log -Tail 12 | ForEach-Object { Write-Host $_ -ForegroundColor Red }
        throw "$($_.Name) publish 失败"
    }
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    Remove-Item $arc -Recurse -Force -ErrorAction SilentlyContinue
    $_.Hash | Set-Content $stamp -Encoding ASCII
    $size = [math]::Round((Get-ChildItem $target -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host ("    {0,-6} {1,5:N1}s  {2} MB" -f $_.Name, $sw.Elapsed.TotalSeconds, $size) -ForegroundColor DarkGray
} -ThrottleLimit $Jobs

if ($linuxJobs.Count -gt 0) {
    Step "2/6 等 Linux AOT 收尾"
    $failed = $false
    foreach ($j in $linuxJobs) {
        Receive-Job $j -Wait -AutoRemoveJob | Out-Host
        if ($j.State -eq 'Failed') { $failed = $true }
    }
    if ($failed) { throw 'Linux AOT 失败,见上方红字' }
} else {
    Step "2/6 跳过 Linux"
}

# macOS:.NET 的 AOT 不能跨操作系统编译,所以 mac 走「自包含单文件 + ad-hoc 签名」,
# 客户端再套一层 .app 结构(macOS 上双击才有窗口,裸二进制只能命令行跑)
if (-not $SkipMac) {
    Step "2.5/6 macOS(自包含单文件,osx-arm64 + osx-x64)"
    $macProjects = @(
        [pscustomobject]@{ Name = 'ZXMS'; Project = (Join-Path $root 'ZincXManagerServer\ZincXManagerServer.csproj') },
        [pscustomobject]@{ Name = 'ZXMC'; Project = (Join-Path $root 'ZincXManagerClient\ZincXManagerClient.csproj') }
    )
    $macRids = @(@{ Rid = 'osx-arm64'; Tag = 'macOS-arm64' }, @{ Rid = 'osx-x64'; Tag = 'macOS-x64' })
    $macPairs = foreach ($p in $macProjects) { foreach ($r in $macRids) { [pscustomobject]@{ Name = $p.Name; Project = $p.Project; Rid = $r.Rid; Tag = $r.Tag; Hash = (Get-SourceHash $p.Project $root) + "|" + $r.Rid } } }
    $macPairs | ForEach-Object -Parallel {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $target = Join-Path $using:outDir "$($_.Name)-$($_.Tag)"
        $stamp = Join-Path $target '.buildhash'
        if (-not $using:forceBuild -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $_.Hash)) {
            Write-Host ("    {0,-6} {1} 源码没变,跳过" -f $_.Name, $_.Tag) -ForegroundColor DarkYellow
            return
        }
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        $log = Join-Path $using:outDir ("_log_mac_" + $_.Name + "_" + $_.Tag + ".txt")
        $arc = Join-Path $using:outDir ("_arc_mac_" + $_.Name + "_" + $_.Tag)
        if (Test-Path $arc) { Remove-Item $arc -Recurse -Force }
        # csproj 里写死了 PublishAot,这里必须显式关掉,否则报 Cross-OS native compilation is not supported
        dotnet publish $_.Project -c Release -f net10.0 -r $_.Rid --self-contained true -p:PublishAot=false -p:PublishSingleFile=true --artifacts-path $arc -o $target --nologo *> $log
        if ($LASTEXITCODE -ne 0) {
            Get-Content $log -Tail 12 | ForEach-Object { Write-Host $_ -ForegroundColor Red }
            throw "$($_.Name) $($_.Tag) publish 失败"
        }
        Remove-Item $log -Force -ErrorAction SilentlyContinue
        Remove-Item $arc -Recurse -Force -ErrorAction SilentlyContinue
        $_.Hash | Set-Content $stamp -Encoding ASCII
        $size = [math]::Round((Get-ChildItem $target -File | Measure-Object Length -Sum).Sum / 1MB, 1)
        Write-Host ("    {0,-6} {1,-12} {2,5:N1}s  {3} MB" -f $_.Name, $_.Tag, $sw.Elapsed.TotalSeconds, $size) -ForegroundColor DarkGray
    } -ThrottleLimit $Jobs
} else {
    Step "2.5/6 跳过 macOS"
}

Step "3/6 模组 jar + 客户端更新日志"
$jar = Get-ChildItem (Join-Path $modDir 'build\libs') -Filter "ZXMM-$Version.jar" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $jar) { throw "没有 build\libs\ZXMM-$Version.jar,先去模组工程 gradle build" }
Copy-Item $jar.FullName (Join-Path $out $jar.Name) -Force
Write-Host "    $($jar.Name) ($([math]::Round($jar.Length/1KB,1)) KB)" -ForegroundColor DarkGray

# 客户端目录必须带本版本更新日志:主界面「更新日志」页读的就是 exe 同目录的 UpdateLog.MD
$updateLog = Get-UpdateLog $Version
$clientWin = Join-Path $out 'ZXMC-win-x64'
if (Test-Path $clientWin) { $updateLog | Set-Content (Join-Path $clientWin 'UpdateLog.MD') -Encoding UTF8 }
$clientDesktopDir = Join-Path $Desktop '3-客户端 ZXMC'
if (-not (Test-Path $clientDesktopDir)) { New-Item -ItemType Directory -Path $clientDesktopDir -Force | Out-Null }
$updateLog | Set-Content (Join-Path $clientDesktopDir 'UpdateLog.MD') -Encoding UTF8
Write-Host "    UpdateLog.MD 已写入 publish\ZXMC-win-x64 与桌面 3-客户端 ZXMC" -ForegroundColor DarkGray

$pkgDir = Join-Path $Desktop '安装包'
New-Item -ItemType Directory -Path $pkgDir -Force | Out-Null

if (-not $SkipInstaller) {
    Step "4/6 MSI 暂存目录"
    $stage = Join-Path $out 'stage\msi'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    foreach ($f in 'ZincXManagerClient.exe', 'libSkiaSharp.dll', 'av_libglesv2.dll', 'libHarfBuzzSharp.dll', 'UpdateLog.MD') {
        Copy-Item (Join-Path $clientWin $f) $stage -Force
    }
    Copy-Item (Join-Path $Desktop '说明.txt') $stage -Force

    Step "5/6 MSI + 便携 zip + Linux tar.gz"
    $msiVersion = Get-MsiVersion $Version
    $msi = Join-Path $pkgDir "ZincXManagerClient-$Version-x64.msi"
    wix build (Join-Path $root 'installer\zxmc.wxs') -o $msi -ext WixToolset.UI.wixext `
        -d "Version=$msiVersion" -d "StageDir=$stage" -d "IconPath=$(Join-Path $root 'assets\zxm.ico')" | Out-Host
    Assert-LastExit 'wix build'
    # wix 会在 msi 旁边留一份调试符号,交付不要
    Get-ChildItem $pkgDir -Filter '*.wixpdb' -File | Remove-Item -Force
    Write-Host "    ZincXManagerClient-$Version-x64.msi (MSI $msiVersion)" -ForegroundColor DarkGray
} else {
    Step "4/6 5/6 跳过安装包"
}

Step "6/6 刷新桌面目录"
$map = @{
    'ZXMOS' = '1-官方服务端 ZXMOS'
    'ZXMS'  = '2-子服务端 ZXMS'
    'ZXMC'  = '3-客户端 ZXMC'
    'ZXMTL' = '4-许可证工具 ZXMTL'
    'Tests' = '5-全量测试 ZincXManagerTests'
}
foreach ($name in $map.Keys) {
    $src = Join-Path $out "$name-win-x64"
    $dst = Join-Path $Desktop $map[$name]
    if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Path $dst -Force | Out-Null }
    # 只覆盖程序文件,config.ini / logs 这些运行期的内容不动
    Get-ChildItem $src -File -Include *.exe, *.dll, *.pdb, *.MD -Recurse | Copy-Item -Destination $dst -Force
    Write-Host "    $($map[$name])" -ForegroundColor DarkGray
}
$modDesktop = Join-Path $Desktop '6-模组 ZXMM'
Get-ChildItem $modDesktop -Filter 'ZXMM-*.jar' -File | Where-Object { $_.Name -ne $jar.Name } | Remove-Item -Force
Copy-Item $jar.FullName $modDesktop -Force
Write-Host "    6-模组 ZXMM ($($jar.Name))" -ForegroundColor DarkGray

if (-not $SkipInstaller) {
    $portableStage = Join-Path $out 'stage\portable'
    if (Test-Path $portableStage) { Remove-Item $portableStage -Recurse -Force }
    New-Item -ItemType Directory -Path $portableStage -Force | Out-Null
    Get-ChildItem $clientWin -File | Where-Object { $_.Extension -notin '.pdb', '.log' } | Copy-Item -Destination $portableStage -Force
    Copy-Item (Join-Path $Desktop '说明.txt') $portableStage -Force
    $portable = Join-Path $pkgDir "ZincXManagerClient-$Version-win-x64-portable.zip"
    if (Test-Path $portable) { Remove-Item $portable -Force }
    Compress-Archive -Path (Join-Path $portableStage '*') -DestinationPath $portable -CompressionLevel Optimal

    foreach ($pair in @(@('ZXMS', "ZincXManagerServer-$Version-win-x64.zip"), @('Tests', "ZincXManagerTests-$Version-win-x64.zip"))) {
        $zip = Join-Path $pkgDir $pair[1]
        if (Test-Path $zip) { Remove-Item $zip -Force }
        $files = Get-ChildItem (Join-Path $out "$($pair[0])-win-x64") -File | Where-Object { $_.Extension -ne '.pdb' }
        Compress-Archive -Path $files.FullName -DestinationPath $zip -CompressionLevel Optimal
    }

    if (-not $SkipLinux) {
        foreach ($pair in @(@('ZXMS', "ZincXManagerServer-$Version-linux-x64.tar.gz"), @('ZXMC', "ZincXManagerClient-$Version-linux-x64.tar.gz"))) {
            $tarPath = Join-Path $pkgDir $pair[1]
            if (Test-Path $tarPath) { Remove-Item $tarPath -Force }
            $wslSrc = To-WslPath (Join-Path $out "$($pair[0])-linux-x64")
            $wslDst = To-WslPath $tarPath
            wsl -d $WslDistro -- bash -lc "cd '$wslSrc' && chmod 755 * && tar --mode=755 -czf '$wslDst' *" | Out-Host
            Assert-LastExit "tar $($pair[0])"
        }
    }

    if (-not $SkipMac) {
        # 客户端要塞进 .app 才能双击启动;签名用 rcodesign(Windows 上给 Mach-O 做 ad-hoc 签名)
        # 火绒会拦 rcodesign.exe 的原位写入(os error 5),复制成 rsign.exe 再跑就正常
        $rcodesign = 'G:\文档\deepseek-harness\default-workspace\tools\rcodesign\apple-codesign-0.29.0-x86_64-pc-windows-msvc\rcodesign.exe'
        $signer = Join-Path $env:TEMP 'rsign.exe'
        if (Test-Path $rcodesign) { Copy-Item $rcodesign $signer -Force }
        $shortVersion = ($Version -replace '^[a-z]', '') -replace '([a-z]+)$', ''
        foreach ($tag in 'macOS-arm64', 'macOS-x64') {
            $src = Join-Path $out "ZXMC-$tag"
            if (-not (Test-Path $src)) { throw "没有 $src,先跑 macOS 步骤" }
            $app = Join-Path $src 'ZincXManager.app'
            if (Test-Path $app) { Remove-Item $app -Recurse -Force }
            $macos = Join-Path $app 'Contents\MacOS'
            New-Item -ItemType Directory -Path $macos -Force | Out-Null
            New-Item -ItemType Directory -Path (Join-Path $app 'Contents\Resources') -Force | Out-Null
            Get-ChildItem $src -File | Where-Object { $_.Name -eq 'ZincXManagerClient' -or $_.Extension -eq '.dylib' } | Copy-Item -Destination $macos -Force
            $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key><string>zh_CN</string>
    <key>CFBundleDisplayName</key><string>ZincXManager 客户端</string>
    <key>CFBundleExecutable</key><string>ZincXManagerClient</string>
    <key>CFBundleIdentifier</key><string>top.zincms.zxmc</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>CFBundleName</key><string>ZincXManager</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$shortVersion</string>
    <key>CFBundleVersion</key><string>$shortVersion</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
"@
            $plist | Set-Content (Join-Path $app 'Contents\Info.plist') -Encoding UTF8
            'APPL????' | Set-Content (Join-Path $app 'Contents\PkgInfo') -Encoding ASCII -NoNewline
            if (Test-Path $signer) {
                # rcodesign 不给 --ad-hoc 参数:不传签名身份时默认就是 ad-hoc 签名
                & $signer sign $app 2>&1 | Select-Object -Last 3 | Out-Host
                if ($LASTEXITCODE -ne 0) { Write-Host '    rcodesign 签名失败(mac 上首次打开要手动右键→打开)' -ForegroundColor Yellow }
                elseif (-not (Test-Path (Join-Path $app 'Contents\_CodeSignature\CodeResources'))) {
                    Write-Host '    rcodesign 退出码 0 但没生成 _CodeSignature,按未签名处理' -ForegroundColor Yellow
                } else { Write-Host '    .app 已 ad-hoc 签名' -ForegroundColor DarkGray }
            } else {
                Write-Host '    没找到 rcodesign,.app 未签名' -ForegroundColor Yellow
            }
            $tar = Join-Path $pkgDir "ZincXManagerClient-$Version-$tag.tar.gz"
            if (Test-Path $tar) { Remove-Item $tar -Force }
            # 用 WSL 里的 GNU tar:Windows 自带的 tar 会写 0666,解出来在 mac 上双击没反应
            $wslApp = To-WslPath $src
            $wslTar = To-WslPath $tar
            wsl -d $WslDistro -- bash -lc "chmod -R 755 '$wslApp/ZincXManager.app' && tar --mode=755 -czf '$wslTar' -C '$wslApp' ZincXManager.app" | Out-Host
            Assert-LastExit "tar ZXMC $tag"

            $srvSrc = Join-Path $out "ZXMS-$tag"
            $srvTar = Join-Path $pkgDir "ZincXManagerServer-$Version-$tag.tar.gz"
            if (Test-Path $srvTar) { Remove-Item $srvTar -Force }
            $wslSrv = To-WslPath $srvSrc
            $wslSrvTar = To-WslPath $srvTar
            wsl -d $WslDistro -- bash -lc "chmod -R 755 '$wslSrv' && tar --mode=755 -czf '$wslSrvTar' -C '$wslSrv' ZincXManagerServer" | Out-Host
            Assert-LastExit "tar ZXMS $tag"
            Write-Host "    ZXMC/ZXMS $tag" -ForegroundColor DarkGray
        }
    }

    # 校验值只算当前版本,历史包不重复列
    $lines = foreach ($f in (Get-ChildItem $pkgDir -File | Where-Object { $_.Name -like "*$Version*" -and $_.Extension -in '.msi', '.zip', '.gz' })) {
        "{0}  {1}" -f (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower(), $f.Name
    }
    $lines | Set-Content (Join-Path $pkgDir 'SHA256.txt') -Encoding ASCII
    Write-Host "    安装包: $pkgDir" -ForegroundColor DarkGray
}

$total.Stop()
Write-Host ""
Write-Host ("打包结束,用时 {0:N1} 分钟 {1:N0} 秒" -f $total.Elapsed.TotalMinutes, $total.Elapsed.TotalSeconds) -ForegroundColor Green
