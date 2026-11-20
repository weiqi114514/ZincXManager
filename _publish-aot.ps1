<#
  一键打包: 五个 Windows AOT 产物 + ZXMS/ZXMC 的 Linux AOT + 客户端 MSI/便携 zip
            + macOS 自包含单文件(客户端套 .app) + 刷新桌面目录。

  用法:
    pwsh -File _publish-aot.ps1                    # 按 csproj 里的 InformationalVersion 打包,五个 Windows 产物并行编
    pwsh -File _publish-aot.ps1 -Jobs 3            # 并行度(默认 2,最多 3)
    pwsh -File _publish-aot.ps1 -SkipLinux         # 不出 Linux
    pwsh -File _publish-aot.ps1 -SkipMac           # 不出 macOS
    pwsh -File _publish-aot.ps1 -SkipInstaller     # 不出 MSI/zip/tar.gz,只刷程序目录
    pwsh -File _publish-aot.ps1 -WindowsOnly ZXMC  # 只重编某几个 Windows 产物(逗号分隔)
    pwsh -File _publish-aot.ps1 -Force             # 忽略源码指纹强制重编

  产物:
    publish\<名字>-win-x64\            各端 AOT 单文件
    publish\ZXMS-linux-x64\ 等         Linux AOT(在 WSL 里编,AOT 不能交叉编译)
    publish\ZXMC-macOS-arm64\ 等       macOS 自包含单文件(客户端已套 .app)
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

Set-StrictMode -Off
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ---------------- 常量(沿用) ----------------
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$parent = Split-Path -Parent $root
$official = Join-Path $parent 'ZincXManager-Official'
$modDir = Join-Path $root 'ZincXManagerMod'
$modDirAlt = Join-Path $parent 'ZincXManagerMod'
if (-not (Test-Path $modDir) -and (Test-Path $modDirAlt)) { $modDir = $modDirAlt }
$out = Join-Path $root 'publish'
$rcodesignPath = 'G:\文档\deepseek-harness\default-workspace\tools\rcodesign\apple-codesign-0.29.0-x86_64-pc-windows-msvc\rcodesign.exe'
$total = [System.Diagnostics.Stopwatch]::StartNew()
$script:warnings = New-Object System.Collections.Generic.List[string]
$script:generatedPackages = New-Object System.Collections.Generic.List[string]

if ($Jobs -le 0) { $Jobs = 2 }
if ($Jobs -gt 3) { $Jobs = 3 }

# ---------------- 小工具 ----------------
function Step([string]$text) {
    Write-Host ""
    Write-Host ("[{0,5:N1}s] ==> {1}" -f $total.Elapsed.TotalSeconds, $text) -ForegroundColor Cyan
}
function Info([string]$text) { Write-Host "    $text" -ForegroundColor DarkGray }
function Ok([string]$text)   { Write-Host "    $text" -ForegroundColor Green }
function Warn([string]$text) { Write-Host "    $text" -ForegroundColor Yellow; $script:warnings.Add($text) }
function Assert-LastExit([string]$what) {
    if ($LASTEXITCODE -ne 0) { throw "$what 失败(exit=$LASTEXITCODE)" }
}

# Windows 盘符路径转 WSL 路径:E:\a\b -> /mnt/e/a/b(不走 wslpath,反斜杠传参会丢)
function To-WslPath([string]$p) {
    $full = [System.IO.Path]::GetFullPath($p)
    if ($full -notmatch '^[A-Za-z]:\\') { throw "只支持盘符绝对路径:$full" }
    $drive = $full.Substring(0, 1).ToLower()
    $rest = $full.Substring(2) -replace '\\', '/'
    return "/mnt/$drive$rest"
}

# 从 CHANGELOG 抽出当前版本的段,给客户端「更新日志」用
function Get-UpdateLog([string]$v) {
    $cl = Join-Path $root 'CHANGELOG.md'
    if (-not (Test-Path $cl)) { throw "找不到 CHANGELOG.md:$cl" }
    $lines = Get-Content $cl
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

# 源码指纹:项目自身 + Shared + 根目录构建配置文件,用来跳过没改过的端
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
    foreach ($f in 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'NuGet.config') {
        $fp = Join-Path $repoRoot $f
        if (Test-Path $fp) {
            [void]$sb.Append($fp).Append(':').Append((Get-Item $fp).LastWriteTimeUtc.Ticks).Append(';')
        }
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLower() }
    finally { $sha.Dispose() }
}

# 版本号单一来源:所有项目都按 $Version 写一遍,保留原文件 BOM 状态
function Sync-Versions([string]$v) {
    $numeric = $v -replace '^[a-z]+', ''
    $targets = @(
        (Join-Path $root 'ZincXManagerClient\ZincXManagerClient.csproj'),
        (Join-Path $root 'ZincXManagerServer\ZincXManagerServer.csproj'),
        (Join-Path $root 'ZincXManagerShared\ZincXManagerShared.csproj'),
        (Join-Path $root 'ZincXManagerChat\ZincXManagerChat.csproj'),
        (Join-Path $root 'tests\ZincXManagerTests\ZincXManagerTests.csproj'),
        (Join-Path $official 'ZincXManagerOServer\ZincXManagerOServer.csproj'),
        (Join-Path $official 'ZincXManagerLicTool\ZincXManagerLicTool.csproj')
    )
    foreach ($p in $targets) {
        if (-not (Test-Path $p)) { continue }
        $t = [System.IO.File]::ReadAllText($p)
        $new = $t -replace '<Version>[^<]*</Version>', "<Version>$numeric</Version>"
        $new = $new -replace '<FileVersion>[^<]*</FileVersion>', "<FileVersion>$numeric.0</FileVersion>"
        $new = $new -replace '<InformationalVersion>[^<]*</InformationalVersion>', "<InformationalVersion>$v</InformationalVersion>"
        if ($new -ne $t) {
            $bytes = [System.IO.File]::ReadAllBytes($p)
            $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
            $enc = New-Object System.Text.UTF8Encoding($hasBom)
            [System.IO.File]::WriteAllText($p, $new, $enc)
            Info "版本同步:$(Split-Path $p -Leaf) -> $v"
        }
    }
}

# MSI 只认 x.y.z:third = 小版本*100 + 修复字母序号(a=1);三段都查 65535 上限
function Get-MsiVersion([string]$v) {
    if ($v -notmatch '^[a-z]?(\d+)\.(\d+)\.(\d+)([a-z]?)$') { throw "版本号 $v 不符合 [类型][大].[中].[小][极小]" }
    $major = [int]$Matches[1]; $minor = [int]$Matches[2]; $patch = [int]$Matches[3]
    $fix = if ($Matches[4]) { [int][char]$Matches[4] - [int][char]'a' + 1 } else { 0 }
    $third = $patch * 100 + $fix
    if ($major -gt 255 -or $minor -gt 255 -or $third -gt 65535) {
        throw "版本号 $v 换算成 MSI($major.$minor.$third)超过分段上限(255.255.65535)"
    }
    return "$major.$minor.$third"
}

# ---------------- 0/7 前置自检 ----------------
Step "0/7 前置自检"

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "需要 PowerShell 7+,当前 $($PSVersionTable.PSVersion.Major).$($PSVersionTable.PSVersion.Minor);ForEach-Object -Parallel 依赖它"
}
Info "PowerShell $($PSVersionTable.PSVersion)"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '找不到 dotnet,请确认 .NET SDK 10 已安装且在 PATH 里' }
if (-not (Test-Path $root)) { throw "找不到脚本目录:$root" }
New-Item -ItemType Directory -Path $out -Force | Out-Null

$needWsl = (-not $SkipLinux) -or (-not $SkipMac)
if ($needWsl -and -not (Get-Command wsl -ErrorAction SilentlyContinue)) {
    throw '需要 WSL 但没找到 wsl.exe;可用 -SkipLinux -SkipMac 跳过'
}

# ---------------- 1/7 版本 / 项目 / 任务 ----------------
Step "1/7 解析版本与任务"

if (-not $Version) {
    $csproj = Join-Path $root 'ZincXManagerServer\ZincXManagerServer.csproj'
    if (-not (Test-Path $csproj)) { throw "找不到 $csproj,请用 -Version 指定" }
    $text = Get-Content $csproj -Raw
    if ($text -match '<InformationalVersion>([^<]+)</InformationalVersion>') { $Version = $Matches[1] }
    else { throw '读不到版本号,请用 -Version 指定' }
}
Write-Host "版本 $Version,并行度 $Jobs" -ForegroundColor Green
Sync-Versions $Version

$winProjects = @(
    [pscustomobject]@{ Name = 'ZXMOS'; Project = (Join-Path $official 'ZincXManagerOServer\ZincXManagerOServer.csproj') },
    [pscustomobject]@{ Name = 'ZXMS';  Project = (Join-Path $root 'ZincXManagerServer\ZincXManagerServer.csproj') },
    [pscustomobject]@{ Name = 'ZXMC';  Project = (Join-Path $root 'ZincXManagerClient\ZincXManagerClient.csproj') },
    [pscustomobject]@{ Name = 'ZXMTL'; Project = (Join-Path $official 'ZincXManagerLicTool\ZincXManagerLicTool.csproj') },
    [pscustomobject]@{ Name = 'Tests'; Project = (Join-Path $root 'tests\ZincXManagerTests\ZincXManagerTests.csproj') }
)
if ($WindowsOnly.Count -gt 0) {
    $winProjects = @($winProjects | Where-Object { $WindowsOnly -contains $_.Name })
    if ($winProjects.Count -eq 0) { throw "-WindowsOnly 里没有匹配的产物名(可选:ZXMOS/ZXMS/ZXMC/ZXMTL/Tests)" }
}
foreach ($p in $winProjects) {
    if (-not (Test-Path $p.Project)) { throw "找不到项目:$($p.Project)" }
}
$needOfficial = @($winProjects | Where-Object { $_.Project.StartsWith($official, [System.StringComparison]::OrdinalIgnoreCase) })
if ($needOfficial.Count -gt 0 -and -not (Test-Path $official)) {
    throw "本次需要官方端项目但目录不存在:$official"
}
$winNames = @($winProjects | ForEach-Object { $_.Name })
Info "Windows 产物:$($winNames -join ', ')"

$forceBuild = [bool]$Force
foreach ($p in $winProjects) {
    $p | Add-Member -NotePropertyName Hash -NotePropertyValue (Get-SourceHash $p.Project $root) -Force
}

# ---------------- 2/7 Windows AOT 并行 + Linux job 串行 ----------------
Step "2/7 Windows AOT 并行编译($($winProjects.Count) 个,并行度 $Jobs),Linux AOT 同时开跑"
$outDir = $out

# Linux 两个端共用一个 job 串行跑:ILCompiler 很吃内存,两路同时跑会 OOM 且日志只剩 Generating native code
$linuxJobs = @()
if (-not $SkipLinux) {
    $linuxPairs = foreach ($lp in @(
        [pscustomobject]@{ Name = 'ZXMS'; Project = (Join-Path $root 'ZincXManagerServer\ZincXManagerServer.csproj') },
        [pscustomobject]@{ Name = 'ZXMC'; Project = (Join-Path $root 'ZincXManagerClient\ZincXManagerClient.csproj') }
    )) { $lp | Add-Member -NotePropertyName Hash -NotePropertyValue (Get-SourceHash $lp.Project $root) -Force -PassThru }

    $linuxJobs += Start-Job -ArgumentList $linuxPairs, $out, $WslDistro, $forceBuild -ScriptBlock {
        param($pairs, $outDir, $distro, $force)
        foreach ($lp in $pairs) {
            $name = $lp.Name; $project = $lp.Project; $hash = $lp.Hash
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $target = Join-Path $outDir "$name-linux-x64"
            $stamp = Join-Path $target '.buildhash'

            $existing = if (Test-Path $stamp) { Get-Content $stamp -Raw -ErrorAction SilentlyContinue } else { $null }
            if (-not $force -and $null -ne $existing -and $existing.Trim() -eq $hash) {
                Write-Host ("    {0,-6} 源码没变,跳过 (linux)" -f $name) -ForegroundColor DarkYellow
                continue
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
                throw "$name linux publish 失败(日志:$log)"
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

    $existing = if (Test-Path $stamp) { Get-Content $stamp -Raw -ErrorAction SilentlyContinue } else { $null }
    if (-not $using:forceBuild -and $null -ne $existing -and $existing.Trim() -eq $_.Hash) {
        Write-Host ("    {0,-6} 源码没变,跳过" -f $_.Name) -ForegroundColor DarkYellow
        return
    }
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    $log = Join-Path $using:outDir ("_log_" + $_.Name + ".txt")
    $arc = Join-Path $using:outDir ("_arc_" + $_.Name)
    if (Test-Path $arc) { Remove-Item $arc -Recurse -Force }

    dotnet publish $_.Project -c Release -f net10.0 -r win-x64 -p:PublishAot=true --artifacts-path $arc -o $target --nologo *> $log
    if ($LASTEXITCODE -ne 0) {
        Get-Content $log -Tail 12 | ForEach-Object { Write-Host $_ -ForegroundColor Red }
        throw "$($_.Name) publish 失败(日志:$log)"
    }
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    Remove-Item $arc -Recurse -Force -ErrorAction SilentlyContinue
    $_.Hash | Set-Content $stamp -Encoding ASCII
    $size = [math]::Round((Get-ChildItem $target -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host ("    {0,-6} {1,5:N1}s  {2} MB" -f $_.Name, $sw.Elapsed.TotalSeconds, $size) -ForegroundColor DarkGray
} -ThrottleLimit $Jobs

# ---------------- 2.5/7 聊天后端 ----------------
$chatProject = Join-Path $root 'ZincXManagerChat\ZincXManagerChat.csproj'
$zxmsWin = Join-Path $out 'ZXMS-win-x64'
if ((Test-Path $chatProject) -and ($winNames -contains 'ZXMS') -and (Test-Path $zxmsWin)) {
    Step "2.5/7 聊天后端(非 AOT 自包含单文件)"
    $chatLog = Join-Path $out '_log_chat.txt'
    $chatArc = Join-Path $out '_arc_ZXMChat'
    dotnet publish $chatProject -c Release -f net10.0 -r win-x64 --self-contained true `
        -p:PublishAot=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        --artifacts-path $chatArc -o $zxmsWin --nologo *> $chatLog
    if ($LASTEXITCODE -ne 0) {
        Get-Content $chatLog -Tail 20 | Out-Host
        throw '聊天后端 publish 失败'
    }
    Remove-Item $chatLog -Force -ErrorAction SilentlyContinue
    Remove-Item $chatArc -Recurse -Force -ErrorAction SilentlyContinue
    $chatExe = Join-Path $zxmsWin 'ZincXManagerChat.exe'
    if (-not (Test-Path $chatExe)) { throw "聊天后端产物缺失:$chatExe" }
    $chatSize = [math]::Round((Get-Item $chatExe).Length / 1MB, 1)
    Info "ZincXManagerChat.exe  $chatSize MB"
} else {
    Step "2.5/7 跳过聊天后端"
}

# ---------------- 3/7 等 Linux job ----------------
if ($linuxJobs.Count -gt 0) {
    Step "3/7 等 Linux AOT 收尾"
    $failed = $false
    foreach ($j in $linuxJobs) {
        Receive-Job $j -Wait | Out-Host
        $state = $j.State
        Remove-Job $j -Force
        if ($state -eq 'Failed') { $failed = $true }
    }
    if ($failed) { throw 'Linux AOT 失败,见上方红字' }
} else {
    Step "3/7 跳过 Linux"
}

# ---------------- 4/7 macOS ----------------
if (-not $SkipMac) {
    Step "4/7 macOS(自包含单文件,osx-arm64 + osx-x64)"
    $macProjects = @(
        [pscustomobject]@{ Name = 'ZXMS'; Project = (Join-Path $root 'ZincXManagerServer\ZincXManagerServer.csproj') },
        [pscustomobject]@{ Name = 'ZXMC'; Project = (Join-Path $root 'ZincXManagerClient\ZincXManagerClient.csproj') }
    )
    $macRids = @(@{ Rid = 'osx-arm64'; Tag = 'macOS-arm64' }, @{ Rid = 'osx-x64'; Tag = 'macOS-x64' })
    $macPairs = foreach ($p in $macProjects) {
        foreach ($r in $macRids) {
            [pscustomobject]@{ Name = $p.Name; Project = $p.Project; Rid = $r.Rid; Tag = $r.Tag
                Hash = (Get-SourceHash $p.Project $root) + '|' + $r.Rid }
        }
    }
    $macPairs | ForEach-Object -Parallel {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $target = Join-Path $using:outDir "$($_.Name)-$($_.Tag)"
        $stamp = Join-Path $target '.buildhash'

        $existing = if (Test-Path $stamp) { Get-Content $stamp -Raw -ErrorAction SilentlyContinue } else { $null }
        if (-not $using:forceBuild -and $null -ne $existing -and $existing.Trim() -eq $_.Hash) {
            Write-Host ("    {0,-6} {1,-12} 源码没变,跳过" -f $_.Name, $_.Tag) -ForegroundColor DarkYellow
            return
        }
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        $log = Join-Path $using:outDir ("_log_mac_" + $_.Name + "_" + $_.Tag + ".txt")
        $arc = Join-Path $using:outDir ("_arc_mac_" + $_.Name + "_" + $_.Tag)
        if (Test-Path $arc) { Remove-Item $arc -Recurse -Force }

        # csproj 里写死了 PublishAot,这里必须显式关掉,否则报 Cross-OS native compilation is not supported
        dotnet publish $_.Project -c Release -f net10.0 -r $_.Rid --self-contained true `
            -p:PublishAot=false -p:PublishSingleFile=true `
            --artifacts-path $arc -o $target --nologo *> $log
        if ($LASTEXITCODE -ne 0) {
            Get-Content $log -Tail 12 | ForEach-Object { Write-Host $_ -ForegroundColor Red }
            throw "$($_.Name) $($_.Tag) publish 失败(日志:$log)"
        }
        Remove-Item $log -Force -ErrorAction SilentlyContinue
        Remove-Item $arc -Recurse -Force -ErrorAction SilentlyContinue
        $_.Hash | Set-Content $stamp -Encoding ASCII
        $size = [math]::Round((Get-ChildItem $target -File | Measure-Object Length -Sum).Sum / 1MB, 1)
        Write-Host ("    {0,-6} {1,-12} {2,5:N1}s  {3} MB" -f $_.Name, $_.Tag, $sw.Elapsed.TotalSeconds, $size) -ForegroundColor DarkGray
    } -ThrottleLimit $Jobs
} else {
    Step "4/7 跳过 macOS"
}

# ---------------- 5/7 模组 jar + 客户端更新日志 ----------------
Step "5/7 模组 jar + 客户端更新日志"

$jar = $null
foreach ($candidate in @($modDir, $modDirAlt)) {
    if (-not $candidate) { continue }
    $libs = Join-Path $candidate 'build\libs'
    if (-not (Test-Path $libs)) { continue }
    $jar = Get-ChildItem $libs -Filter "ZXMM-$Version.jar" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($jar) { break }
}
if (-not $jar) { throw "没有 build\libs\ZXMM-$Version.jar,先去模组工程 gradle build(找过 $modDir 与 $modDirAlt)" }
Copy-Item $jar.FullName (Join-Path $out $jar.Name) -Force
Info "$($jar.Name) ($([math]::Round($jar.Length/1KB,1)) KB)"

$updateLog = Get-UpdateLog $Version
$clientWin = Join-Path $out 'ZXMC-win-x64'
if (($winNames -contains 'ZXMC') -and (Test-Path $clientWin)) {
    $updateLog | Set-Content (Join-Path $clientWin 'UpdateLog.MD') -Encoding UTF8
    $clientDesktopDir = Join-Path $Desktop '3-客户端 ZXMC'
    New-Item -ItemType Directory -Path $clientDesktopDir -Force | Out-Null
    $updateLog | Set-Content (Join-Path $clientDesktopDir 'UpdateLog.MD') -Encoding UTF8
    Info 'UpdateLog.MD 已写入 publish\ZXMC-win-x64 与桌面 3-客户端 ZXMC'
} else {
    Warn 'ZXMC 未参与或无产物,跳过 Windows 客户端 UpdateLog.MD'
}

$pkgDir = Join-Path $Desktop '安装包'
New-Item -ItemType Directory -Path $pkgDir -Force | Out-Null

# ---------------- 6/7 安装包(MSI + zip + tar.gz) ----------------
if (-not $SkipInstaller) {
    Step "6/7 MSI 暂存目录"
    $stage = Join-Path $out 'stage\msi'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    $stageFiles = 'ZincXManagerClient.exe', 'libSkiaSharp.dll', 'av_libglesv2.dll', 'libHarfBuzzSharp.dll', 'UpdateLog.MD'
    $missing = @($stageFiles | Where-Object { -not (Test-Path (Join-Path $clientWin $_)) })
    if (($winNames -notcontains 'ZXMC') -or -not (Test-Path $clientWin) -or $missing.Count -gt 0) {
        if ($missing.Count -gt 0) { Warn "MSI 暂存缺文件:$($missing -join ', ')" }
        Warn '跳过 MSI(ZXMC 未参与/产物缺失)'
    } else {
        foreach ($f in $stageFiles) { Copy-Item (Join-Path $clientWin $f) $stage -Force }
        $readme = Join-Path $Desktop '说明.txt'
        if (Test-Path $readme) { Copy-Item $readme $stage -Force } else { Warn "说明.txt 不存在:$readme" }

        Step "6.5/7 MSI + 便携 zip + Linux tar.gz + macOS tar.gz"
        $msiVersion = Get-MsiVersion $Version
        $msi = Join-Path $pkgDir "ZincXManagerClient-$Version-x64.msi"
        wix build (Join-Path $root 'installer\zxmc.wxs') -o $msi -ext WixToolset.UI.wixext `
            -d "Version=$msiVersion" -d "StageDir=$stage" -d "IconPath=$(Join-Path $root 'assets\zxm.ico')" | Out-Host
        Assert-LastExit 'wix build'
        Get-ChildItem $pkgDir -Filter '*.wixpdb' -File | Remove-Item -Force
        if (-not (Test-Path $msi)) { throw "MSI 未生成:$msi" }
        $script:generatedPackages.Add($msi)
        Info "ZincXManagerClient-$Version-x64.msi (MSI $msiVersion)"
    }
} else {
    Step "6/7 跳过安装包"
}

# 便携 zip / Linux tar.gz / macOS tar.gz
if (-not $SkipInstaller) {
    # 便携 zip(ZXMC)
    if (($winNames -contains 'ZXMC') -and (Test-Path $clientWin)) {
        $portableStage = Join-Path $out 'stage\portable'
        if (Test-Path $portableStage) { Remove-Item $portableStage -Recurse -Force }
        New-Item -ItemType Directory -Path $portableStage -Force | Out-Null
        Get-ChildItem $clientWin -File | Where-Object { $_.Extension -notin '.pdb', '.log' } | Copy-Item -Destination $portableStage -Force
        $readme = Join-Path $Desktop '说明.txt'
        if (Test-Path $readme) { Copy-Item $readme $portableStage -Force } else { Warn "说明.txt 不存在:$readme" }

        $portable = Join-Path $pkgDir "ZincXManagerClient-$Version-win-x64-portable.zip"
        if (Test-Path $portable) { Remove-Item $portable -Force }
        Compress-Archive -Path (Join-Path $portableStage '*') -DestinationPath $portable -CompressionLevel Optimal
        $script:generatedPackages.Add($portable)
        Info "ZincXManagerClient-$Version-win-x64-portable.zip"
    } else {
        Warn '跳过便携 zip(ZXMC 未参与或无产物)'
    }

    # ZXMS / Tests win zip
    foreach ($pair in @(@('ZXMS', "ZincXManagerServer-$Version-win-x64.zip"), @('Tests', "ZincXManagerTests-$Version-win-x64.zip"))) {
        $name = $pair[0]; $zipName = $pair[1]
        if ($winNames -notcontains $name) { Info "跳过 $name zip(本次未参与)"; continue }
        $src = Join-Path $out "$name-win-x64"
        if (-not (Test-Path $src)) { Warn "跳过 $name zip(源目录缺失:$src)"; continue }
        $zip = Join-Path $pkgDir $zipName
        if (Test-Path $zip) { Remove-Item $zip -Force }
        $files = Get-ChildItem $src -File | Where-Object { $_.Extension -ne '.pdb' }
        if (-not $files) { Warn "跳过 $name zip(源目录为空)"; continue }
        Compress-Archive -Path $files.FullName -DestinationPath $zip -CompressionLevel Optimal
        $script:generatedPackages.Add($zip)
        Info $zipName
    }

    # Linux tar.gz
    if (-not $SkipLinux) {
        foreach ($pair in @(@('ZXMS', "ZincXManagerServer-$Version-linux-x64.tar.gz"), @('ZXMC', "ZincXManagerClient-$Version-linux-x64.tar.gz"))) {
            $name = $pair[0]; $tarName = $pair[1]
            $src = Join-Path $out "$name-linux-x64"
            if (-not (Test-Path $src)) { Warn "跳过 $name linux tar.gz(产物缺失:$src)"; continue }
            $tarPath = Join-Path $pkgDir $tarName
            if (Test-Path $tarPath) { Remove-Item $tarPath -Force }
            $wslSrc = To-WslPath $src
            $wslDst = To-WslPath $tarPath
            # /mnt/e 是 drvfs,chmod 改不动权限(报 Operation not permitted);包内权限由 tar --mode=755 统一设定,
            # 所以这里把 chmod 降级成尽力而为,失败不阻断打包
            wsl -d $WslDistro -- bash -lc "cd '$wslSrc' && (chmod 755 * 2>/dev/null || true) && tar --mode=755 -czf '$wslDst' *" | Out-Host
            Assert-LastExit "tar $name"
            if (-not (Test-Path $tarPath)) { throw "Linux tar 未生成:$tarPath" }
            $script:generatedPackages.Add($tarPath)
            Info $tarName
        }
    }

    # macOS tar.gz
    if (-not $SkipMac) {
        $signer = Join-Path $env:TEMP 'rsign.exe'
        if (Test-Path $rcodesignPath) {
            # 火绒会拦 rcodesign.exe 的原位写入(os error 5),复制成 rsign.exe 再跑
            Copy-Item $rcodesignPath $signer -Force
        } else {
            Warn "没找到 rcodesign:$rcodesignPath"
        }

        # WSL 里的 Linux 版 rcodesign(musl 静态链接),与 Windows 版同为 0.29.0
        $linuxRcodesign = '/home/exit/codesign/apple-codesign-0.29.0-x86_64-unknown-linux-musl/rcodesign'

        $shortVersion = ($Version -replace '^[a-z]', '') -replace '([a-z]+)$', ''

        foreach ($tag in 'macOS-arm64', 'macOS-x64') {
            $src = Join-Path $out "ZXMC-$tag"
            if (-not (Test-Path $src)) { Warn "跳过 ZXMC $tag(产物缺失:$src)"; continue }

            # 客户端 .app 内也要有 UpdateLog.MD,否则客户端「更新日志」页空白
            $updateLog | Set-Content (Join-Path $src 'UpdateLog.MD') -Encoding UTF8

            $app = Join-Path $src 'ZincXManager.app'
            if (Test-Path $app) { Remove-Item $app -Recurse -Force }
            $macos = Join-Path $app 'Contents\MacOS'
            New-Item -ItemType Directory -Path $macos -Force | Out-Null
            New-Item -ItemType Directory -Path (Join-Path $app 'Contents\Resources') -Force | Out-Null

            Get-ChildItem $src -File |
                Where-Object { $_.Name -eq 'ZincXManagerClient' -or $_.Extension -eq '.dylib' -or $_.Name -eq 'UpdateLog.MD' } |
                Copy-Item -Destination $macos -Force
            if (-not (Test-Path (Join-Path $macos 'ZincXManagerClient'))) {
                Warn "ZXMC $tag 目录里没有 ZincXManagerClient,跳过 .app"
                continue
            }

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

            $tar = Join-Path $pkgDir "ZincXManagerClient-$Version-$tag.tar.gz"
            if (Test-Path $tar) { Remove-Item $tar -Force }
            $wslApp = To-WslPath $src
            $wslTar = To-WslPath $tar

            # 先在 WSL 原生盘签+打包:/mnt/e 是 drvfs,chmod 与 rcodesign 改权限位一律报 Operation not permitted,
            # 所以在 /home 下重建一份再动它
            $wslWork = "/home/exit/zxm-pack/$tag"
            # 用 tar 管道而不是 cp:cp 会把 drvfs 上 0777 无 x 位的属性原样带过去,解出来执行不了
            $signLog = wsl -d $WslDistro -- bash -lc "set -o pipefail; rm -rf '$wslWork'; mkdir -p '$wslWork' && tar -cf - -C '$wslApp' ZincXManager.app | tar -xf - -C '$wslWork' && cd '$wslWork' && '$linuxRcodesign' sign ZincXManager.app" 2>&1 | Out-String
            $signExit = $LASTEXITCODE
            $signedInWsl = ($signExit -eq 0)
            if ($signedInWsl) {
                # 退出码 0 也要实查封印文件:rcodesign 会静默跳过非 Mach-O 条目,只看返回码会误判成已签
                wsl -d $WslDistro -- bash -lc "[ -f '$wslWork/ZincXManager.app/Contents/_CodeSignature/CodeResources' ]" | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    Warn "WSL rcodesign 退出码 0 但没生成 _CodeSignature($tag),按未签名处理"
                } else {
                    Info "$tag .app 已在 WSL 原生盘 ad-hoc 签名"
                    # 把 rcodesign 的关键输出留在日志里:只看退出码看不出哪些文件被跳过封印
                    $signLog.Trim() -split "`n" | Where-Object { $_ -match 'signing bundle at|non Mach-O|signing Mach-O|signing main' } |
                        ForEach-Object { Info "  $($_.Trim())" }
                }
                wsl -d $WslDistro -- bash -lc "tar --mode=755 -czf '$wslTar' -C '$wslWork' ZincXManager.app" | Out-Host
                Assert-LastExit "tar ZXMC $tag(wsl)"
                if (-not (Test-Path $tar)) { throw "mac tar 未生成:$tar" }
                $script:generatedPackages.Add($tar)
                Info "ZXMC $tag"
                wsl -d $WslDistro -- bash -lc "rm -rf '$wslWork'" | Out-Null
            } else {
                # 退出码非 0 只 Warn 不中断(保留原有容错),stderr 原文进日志
                Warn "WSL rcodesign 签名失败($tag,退出码$signExit),回退 Windows 版。原始输出:$($signLog.Trim())"

                if (Test-Path $signer) {
                    & $signer sign $app 2>&1 | Select-Object -Last 3 | Out-Host
                    if ($LASTEXITCODE -ne 0) {
                        Warn "rcodesign 签名失败($tag),mac 上首次打开要手动右键→打开"
                    } elseif (-not (Test-Path (Join-Path $app 'Contents\_CodeSignature\CodeResources'))) {
                        Warn "rcodesign 退出码 0 但没生成 _CodeSignature($tag),按未签名处理"
                    } else {
                        Info "$tag .app 已 ad-hoc 签名"
                    }
                } else {
                    Warn "$tag .app 未签名(没找到 rcodesign)"
                }

                wsl -d $WslDistro -- bash -lc "rm -rf '$wslWork'" | Out-Null
                # /mnt/e 是 drvfs,chmod -R 改不动权限位(报 Operation not permitted);包内权限由 tar --mode=755 统一设定,
                # 所以这里把 chmod 降级成尽力而为,失败不阻断打包
                wsl -d $WslDistro -- bash -lc "(chmod -R 755 '$wslApp/ZincXManager.app' 2>/dev/null || true) && tar --mode=755 -czf '$wslTar' -C '$wslApp' ZincXManager.app" | Out-Host
                Assert-LastExit "tar ZXMC $tag"
                if (-not (Test-Path $tar)) { throw "mac tar 未生成:$tar" }
                $script:generatedPackages.Add($tar)
                Info "ZXMC $tag"
            }

            # 服务端:打整个目录内容,避免漏掉 dylib
            $srvSrc = Join-Path $out "ZXMS-$tag"
            if (-not (Test-Path $srvSrc)) { Warn "跳过 ZXMS $tag(产物缺失:$srvSrc)"; continue }
            $srvTar = Join-Path $pkgDir "ZincXManagerServer-$Version-$tag.tar.gz"
            if (Test-Path $srvTar) { Remove-Item $srvTar -Force }
            $wslSrv = To-WslPath $srvSrc
            $wslSrvTar = To-WslPath $srvTar
            # 同上:chmod 只是尽力而为,权限由 tar --mode=755 设定
            wsl -d $WslDistro -- bash -lc "(chmod -R 755 '$wslSrv' 2>/dev/null || true) && tar --mode=755 --exclude='.buildhash' -czf '$wslSrvTar' -C '$wslSrv' ." | Out-Host
            Assert-LastExit "tar ZXMS $tag"
            if (-not (Test-Path $srvTar)) { throw "mac tar 未生成:$srvTar" }
            $script:generatedPackages.Add($srvTar)
            Info "ZXMS $tag"
        }
    }
}

# ---------------- 7/7 刷新桌面目录 + SHA256 ----------------
Step "7/7 刷新桌面目录"

$map = @{
    'ZXMOS' = '1-官方服务端 ZXMOS'
    'ZXMS'  = '2-子服务端 ZXMS'
    'ZXMC'  = '3-客户端 ZXMC'
    'ZXMTL' = '4-许可证工具 ZXMTL'
    'Tests' = '5-全量测试 ZincXManagerTests'
}
foreach ($name in $map.Keys) {
    if ($winNames -notcontains $name) { Info "跳过 $($map[$name])(本次未参与 Windows 构建)"; continue }
    $src = Join-Path $out "$name-win-x64"
    if (-not (Test-Path $src)) { Warn "$($map[$name]) 源目录缺失:$src"; continue }
    $dst = Join-Path $Desktop $map[$name]
    New-Item -ItemType Directory -Path $dst -Force | Out-Null
    Get-ChildItem $src -File -Include *.exe, *.dll, *.pdb, *.MD -Recurse | Copy-Item -Destination $dst -Force
    Info $map[$name]
}

# 「测试」是老布局留下的客户端目录
$legacyTest = Join-Path $Desktop '测试'
if ((Test-Path $legacyTest) -and ($winNames -contains 'ZXMC')) {
    $src = Join-Path $out 'ZXMC-win-x64'
    if (Test-Path $src) {
        Get-ChildItem $src -File -Include *.exe, *.dll, *.pdb, *.MD -Recurse | Copy-Item -Destination $legacyTest -Force
        Info '测试(旧布局目录,同步成最新客户端)'
    }
}

$modDesktop = Join-Path $Desktop '6-模组 ZXMM'
if (Test-Path $modDesktop) {
    Get-ChildItem $modDesktop -Filter 'ZXMM-*.jar' -File | Where-Object { $_.Name -ne $jar.Name } | Remove-Item -Force
    Copy-Item $jar.FullName $modDesktop -Force
    Info "6-模组 ZXMM ($($jar.Name))"
} else {
    Warn "桌面模组目录不存在:$modDesktop"
}

# SHA256:只算本次生成的包,不重复列历史包
if ($script:generatedPackages.Count -gt 0) {
    $lines = foreach ($f in $script:generatedPackages) {
        if (Test-Path $f) { "{0}  {1}" -f (Get-FileHash $f -Algorithm SHA256).Hash.ToLower(), (Split-Path $f -Leaf) }
    }
    if ($lines) {
        $lines | Set-Content (Join-Path $pkgDir 'SHA256.txt') -Encoding ASCII
        Info "SHA256.txt"
    }
}

# ---------------- 摘要 ----------------
$total.Stop()
Write-Host ""
Write-Host "产物目录:$out" -ForegroundColor DarkGray
Write-Host "安装包目录:$pkgDir" -ForegroundColor DarkGray
foreach ($f in $script:generatedPackages) {
    if (Test-Path $f) { Write-Host "  $(Split-Path $f -Leaf)" -ForegroundColor DarkGray }
}
if ($script:warnings.Count -gt 0) {
    Write-Host ""
    Write-Host "警告($($script:warnings.Count) 条):" -ForegroundColor Yellow
    foreach ($w in $script:warnings) { Write-Host "  - $w" -ForegroundColor Yellow }
}
Write-Host ""
Write-Host ("打包结束,用时 {0:N1} 分钟 {1:N0} 秒" -f $total.Elapsed.TotalMinutes, $total.Elapsed.TotalSeconds) -ForegroundColor Green