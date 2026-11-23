<#
.SYNOPSIS
    设置笔记本内置屏幕的亮度（按百分比）。

.DESCRIPTION
    通过 WMI 接口 WmiMonitorBrightnessMethods 修改内置面板背光，
    设置完成后立刻回读实际值并复核。若实际值与请求值不符，会提示可能的原因。

.PARAMETER Percent
    目标亮度，0-100。

.PARAMETER Show
    只显示当前亮度，不做任何修改。

.PARAMETER SettleMs
    设置后等待多少毫秒再回读（默认 400）。

.EXAMPLE
    .\Set-Brightness.ps1 -Percent 80

.EXAMPLE
    .\Set-Brightness.ps1 -Show
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateRange(0, 100)]
    [int]$Percent,

    [switch]$Show,

    [int]$SettleMs = 400
)

$ErrorActionPreference = 'Stop'

$LogDir = Join-Path $PSScriptRoot 'logs'

function Write-OpLog {
    param([string]$Action, [string]$Detail)
    try {
        if (-not (Test-Path -LiteralPath $LogDir)) {
            New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
        }
        $file = Join-Path $LogDir ('brightness-{0}.log' -f (Get-Date -Format 'yyyyMMdd'))
        $line = '{0} | {1,-5} | {2} | user={3} | host={4}' -f `
            (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Action, $Detail, $env:USERNAME, $env:COMPUTERNAME
        [System.IO.File]::AppendAllText($file, $line + [Environment]::NewLine,
            (New-Object System.Text.UTF8Encoding($false)))
    }
    catch { }
}

function Get-CurrentBrightness {
    try {
        $mon = @(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness -ErrorAction Stop)
        if ($mon.Count -eq 0) { return $null }
        $pick = $mon | Where-Object { $_.Active } | Select-Object -First 1
        if (-not $pick) { $pick = $mon[0] }
        return [int]$pick.CurrentBrightness
    }
    catch { return $null }
}

$methods = @(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods -ErrorAction SilentlyContinue)

if ($methods.Count -eq 0) {
    Write-Host '未能控制亮度：系统没有对外暴露内置屏幕的亮度接口。' -ForegroundColor Red
    Write-Host '常见原因：1) 只接了外接显示器；2) 显卡驱动未提供 WMI 亮度接口。' -ForegroundColor DarkGray
    Write-OpLog 'FAIL' 'no-brightness-interface'
    exit 1
}

# 只查看
if ($Show -or -not $PSBoundParameters.ContainsKey('Percent')) {
    $cur = Get-CurrentBrightness
    if ($null -eq $cur) {
        Write-Host '当前亮度：读取失败' -ForegroundColor Yellow
        exit 1
    }
    Write-Host ('当前亮度：{0}%' -f $cur) -ForegroundColor Cyan
    Write-OpLog 'SHOW' ("current=$cur")
    exit 0
}

# 写入
$before = Get-CurrentBrightness

foreach ($m in $methods) {
    try {
        Invoke-CimMethod -InputObject $m -MethodName WmiSetBrightness `
            -Arguments @{ Timeout = 1; Brightness = $Percent } -ErrorAction Stop | Out-Null
    }
    catch {
        Write-Host ('设置亮度失败：' + $_.Exception.Message) -ForegroundColor Red
        Write-OpLog 'FAIL' ("req={0}; before={1}; error={2}" -f $Percent, $before, $_.Exception.Message)
        exit 1
    }
}

Start-Sleep -Milliseconds $SettleMs
$after = Get-CurrentBrightness

Write-Host ''
if ($null -eq $after) {
    Write-Host ('已请求 {0}%，但回读失败。' -f $Percent) -ForegroundColor Yellow
    Write-OpLog 'SET' ("req={0}; before={1}; after=read-failed; result=UNKNOWN" -f $Percent, $before)
    exit 0
}

if ($after -eq $Percent) {
    Write-Host ('亮度已设为 {0}%' -f $Percent) -ForegroundColor Green
    Write-OpLog 'SET' ("req={0}; before={1}; after={2}; result=OK" -f $Percent, $before, $after)
    exit 0
}

Write-Host ('请求亮度 : {0}%' -f $Percent)
Write-Host ('实际亮度 : {0}%   (Windows 回读值)' -f $after) -ForegroundColor Yellow

if ($Percent -gt 0) {
    $ratio = $after / $Percent
    if ($ratio -lt 0.95) {
        $pct = [math]::Round($ratio * 100)
        Write-Host ("提示：实际值约为请求值的 {0}%，说明另有一层在按该比例压低亮度。" -f $pct) -ForegroundColor DarkYellow
        Write-Host '      常见来源：电源方案里的「节电模式亮度系数」。可用 Diagnose-Brightness.ps1 定位。' -ForegroundColor DarkGray
    }
    Write-OpLog 'SET' ("req={0}; before={1}; after={2}; ratio={3}%; result=MISMATCH" -f $Percent, $before, $after, [math]::Round($ratio * 100))
}
else {
    Write-OpLog 'SET' ("req={0}; before={1}; after={2}; result=MISMATCH" -f $Percent, $before, $after)
}
