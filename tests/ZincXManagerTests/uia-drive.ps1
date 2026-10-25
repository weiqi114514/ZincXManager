# ZincXManager GUI 注入驱动(系统 UIAutomation,不碰真实鼠标键盘)
# 用法: pwsh -File uia-drive.ps1 -TargetPid <客户端进程> -Mode <login|mirror>
# 退出码: 0 成功;测试按退出码判定,不看控制台文本(避免 GBK 编码问题)
param(
    [Parameter(Mandatory = $true)][int]$TargetPid,
    [string]$Mode = 'mirror',
    [string]$User = 'upduser',
    [string]$Pass = 'upd123'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$IP = [System.Windows.Automation.InvokePattern]
$VP = [System.Windows.Automation.ValuePattern]

function Get-Win {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $TargetPid)
    return $AE::RootElement.FindFirst($TS::Children, $cond)
}

function Invoke-Button([string]$name) {
    $win = Get-Win
    if ($null -eq $win) { Write-Output 'ERR no window'; return $false }

    $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
    $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    $and = New-Object System.Windows.Automation.AndCondition($c1, $c2)
    $el = $win.FindFirst($TS::Descendants, $and)
    if ($null -eq $el) { Write-Output ('ERR button not found: ' + $name); return $false }

    try {
        $pat = $el.GetCurrentPattern($IP::Pattern)
        $pat.Invoke()
    } catch {
        Write-Output ('ERR invoke failed: ' + $name + ' / ' + $_.Exception.Message)
        return $false
    }

    Start-Sleep -Milliseconds 600
    return $true
}

if ($Mode -eq 'login') {
    $win = Get-Win
    if ($null -eq $win) { Write-Output 'ERR no window'; exit 2 }

    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Edit)
    $edits = $win.FindAll($TS::Descendants, $cond)
    if ($edits.Count -lt 2) { Write-Output ('ERR edits=' + $edits.Count); exit 3 }

    $edits[0].GetCurrentPattern($VP::Pattern).SetValue($User)
    $edits[1].GetCurrentPattern($VP::Pattern).SetValue($Pass)
    Start-Sleep -Milliseconds 300

    if (-not (Invoke-Button '注册(只能是 Player)')) { exit 6 }
    Write-Output 'OK login-injected'
    exit 0
}

if ($Mode -eq 'mirror') {
    # 不靠标题判断:主界面有「设置」按钮,登录窗没有
    $win = $null
    for ($i = 0; $i -lt 40; $i++) {
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $TargetPid)
        $all = $AE::RootElement.FindAll($TS::Children, $cond)
        foreach ($w in $all) {
            $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
            $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, '设置')
            $and = New-Object System.Windows.Automation.AndCondition($c1, $c2)
            if ($null -ne $w.FindFirst($TS::Descendants, $and)) { $win = $w; break }
        }
        if ($null -ne $win) { break }
        Start-Sleep -Milliseconds 500
    }
    if ($null -eq $win) {
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $TargetPid)
        $all = $AE::RootElement.FindAll($TS::Children, $cond)
        $names = @()
        foreach ($w in $all) { $names += $w.Current.Name }
        Write-Output ('ERR no main window; windows: ' + ($names -join ' | '))
        exit 7
    }
    Write-Output ('OK main: ' + $win.Current.Name)

    if (-not (Invoke-Button '设置')) { exit 8 }
    if (-not (Invoke-Button '高级')) { exit 9 }
    if (-not (Invoke-Button '设置镜像…')) { exit 10 }
    Start-Sleep -Milliseconds 1200

    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $TargetPid)
    $wins = $AE::RootElement.FindAll($TS::Children, $cond)
    foreach ($w in $wins) {
        if ($w.Current.Name -like '*下载镜像*') {
            $r = $w.Current.BoundingRectangle
            Write-Output ('OK mirror-window ' + [int]$r.Width + 'x' + [int]$r.Height)
            exit 0
        }
    }
    Write-Output 'ERR no mirror window'
    exit 11
}

Write-Output 'ERR bad mode'
exit 1
