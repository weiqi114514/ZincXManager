#!/usr/bin/env pwsh
<#
.SYNOPSIS
    按版本把 ZXM 安装包与同版本模组 jar 归到同一目录。
.DESCRIPTION
    从文件名解析版本号（容忍 a0.2.0 / b0.4.0 / v0.2.0 / 裸 0.3.x 四种写法），
    把安装包移动到 <根>\<归档>\<版本>\，把同版本 ZXMM-*.jar 复制进同一目录；
    每个版本目录重建 SHA256.txt，归档根写 索引.tsv。
.PARAMETER Root
    安装包根目录。
.PARAMETER ModDir
    模组 jar 所在目录。
.PARAMETER ArchiveName
    归档子目录名。
.PARAMETER WhatIfOnly
    只预览，不动任何文件。
#>
[CmdletBinding()]
param(
    [string]$Root = 'G:\桌面\ZXM-AOT-测试\安装包',
    [string]$ModDir = 'G:\桌面\ZXM-AOT-测试\6-模组 ZXMM',
    [string]$ArchiveName = '归档',
    [switch]$WhatIfOnly
)

$ErrorActionPreference = 'Stop'

function Get-PackageVersion {
    param([string]$FileName)
    if ($FileName -match '(?<v>[A-Za-z]?[vV]?\d+\.\d+\.\d+[A-Za-z]?)') {
        return ($Matches['v'] -replace '^[vV]', '')
    }
    return $null
}

function Get-NumericPart {
    param([string]$Version)
    return ($Version -replace '^[A-Za-z]+', '')
}

if (-not (Test-Path -LiteralPath $Root)) { throw "安装包根目录不存在：$Root" }

$archiveRoot = Join-Path $Root $ArchiveName
Write-Host "[1/5] 扫描安装包：$Root"
$files = @(Get-ChildItem -LiteralPath $Root -File | Where-Object {
    $n = $_.Name
    if ($n -ieq 'SHA256.txt') { return $false }
    return ($n -imatch '\.(msi|zip)$' -or $n -imatch '\.tar\.gz$')
})
Write-Host "      找到 $($files.Count) 个安装包文件"

Write-Host "[2/5] 扫描模组 jar：$ModDir"
$jarByVersion = @{}
if (Test-Path -LiteralPath $ModDir) {
    foreach ($jar in @(Get-ChildItem -LiteralPath $ModDir -File -Filter 'ZXMM-*.jar')) {
        $v = Get-PackageVersion $jar.Name
        if ($v) { $jarByVersion[$v] = $jar.FullName }
    }
}
Write-Host "      找到 $($jarByVersion.Count) 个模组 jar 版本：$(($jarByVersion.Keys | Sort-Object) -join ', ')"

Write-Host '[3/5] 按版本分组'
$grouped = @{}
$unparsed = @()
foreach ($f in $files) {
    $v = Get-PackageVersion $f.Name
    if (-not $v) { $unparsed += $f.Name; continue }
    if (-not $grouped.ContainsKey($v)) { $grouped[$v] = New-Object System.Collections.ArrayList }
    [void]$grouped[$v].Add($f)
}

$moved = 0
$copiedJar = 0
$missingJar = @()

Write-Host '[4/5] 归档'
foreach ($v in ($grouped.Keys | Sort-Object)) {
    $destDir = Join-Path $archiveRoot $v
    Write-Host "      - $v ：$($grouped[$v].Count) 个安装包"
    if ($WhatIfOnly) {
        foreach ($f in $grouped[$v]) { Write-Host "        [预览] 移动 $($f.Name)" }
    } else {
        if (-not (Test-Path -LiteralPath $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
        foreach ($f in $grouped[$v]) {
            Move-Item -LiteralPath $f.FullName -Destination (Join-Path $destDir $f.Name) -Force
            $moved++
        }
    }

    # 同版本模组 jar 只认版本号完全相同的:b0.5.0 与 0.5.0 是两条发布线,数字相同不代表是同一个包
    $jarPath = $null
    if ($jarByVersion.ContainsKey($v)) {
        $jarPath = $jarByVersion[$v]
    } else {
        $numeric = Get-NumericPart $v
        $near = @($jarByVersion.Keys | Where-Object { (Get-NumericPart $_) -eq $numeric })
        if ($near.Count -gt 0) { Write-Host "        [jar] 数字部分与 $($near -join ',') 相同但版本号不同，不复制" }
    }
    if ($jarPath) {
        Write-Host "        [jar] $(Split-Path $jarPath -Leaf)"
        if (-not $WhatIfOnly) {
            Copy-Item -LiteralPath $jarPath -Destination (Join-Path $destDir (Split-Path $jarPath -Leaf)) -Force
            $copiedJar++
        }
    } else {
        $missingJar += $v
        Write-Host '        [jar] 没有同版本模组 jar'
    }
}

if (-not $WhatIfOnly) {
    Write-Host '[5/5] 重建 SHA256.txt 与 索引.tsv'
    $indexLines = @("版本`t文件名`t字节`tSHA256")
    foreach ($v in (@($grouped.Keys) | Sort-Object)) {
        $destDir = Join-Path $archiveRoot $v
        $sumLines = @()
        foreach ($f in @(Get-ChildItem -LiteralPath $destDir -File | Sort-Object Name)) {
            if ($f.Name -ieq 'SHA256.txt') { continue }
            $hash = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLower()
            $sumLines += "$hash  $($f.Name)"
            $indexLines += "$v`t$($f.Name)`t$($f.Length)`t$hash"
        }
        $sumLines | Set-Content -LiteralPath (Join-Path $destDir 'SHA256.txt') -Encoding ASCII
    }
    $indexLines | Set-Content -LiteralPath (Join-Path $archiveRoot '索引.tsv') -Encoding utf8
} else {
    Write-Host '[5/5] 预览模式：未写 SHA256.txt 与 索引.tsv'
}

Write-Host ''
Write-Host '==== 汇总 ===='
Write-Host "版本目录数：$($grouped.Keys.Count)"
if ($WhatIfOnly) {
    Write-Host '预览模式：没有移动任何文件'
} else {
    Write-Host "已移动安装包：$moved"
    Write-Host "已复制模组 jar：$copiedJar"
}
if ($missingJar.Count -gt 0) { Write-Host "缺同版本 jar 的版本：$($missingJar -join ', ')" }
if ($unparsed.Count -gt 0) {
    Write-Host "无法解析版本号（留在根目录）："
    foreach ($u in $unparsed) { Write-Host "  - $u" }
} else {
    Write-Host '无法解析版本号的文件：无'
}
