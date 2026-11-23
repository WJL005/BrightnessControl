<#
.SYNOPSIS
    处理已确认的两个「屏幕变暗 / 忽明忽暗」来源。

.DESCRIPTION
    来源一：AMD Vari-Bright（内容自适应背光）—— 写注册表关闭，重启后生效。
    来源二：节电模式下的屏幕亮度系数 —— 改为 100%，节电模式保持开启但不再压暗屏幕。

    默认只演练（DryRun），不会修改任何东西；加 -Apply 才真正写入。
    需要管理员权限。

.PARAMETER Target
    VariBright / BatterySaverDim / All（默认 All）

.PARAMETER Apply
    真正执行修改。

.PARAMETER Restore
    回退：把 Vari-Bright 恢复为等级 2，把节电亮度系数恢复为系统的默认 70%。

.EXAMPLE
    .\Fix-Brightness-Source.ps1
    只看会改什么。

.EXAMPLE
    .\Fix-Brightness-Source.ps1 -Apply
    真正执行。
#>
param(
    [ValidateSet('VariBright', 'BatterySaverDim', 'All')]
    [string]$Target = 'All',

    [switch]$Apply,
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

$mode = '演练（不会修改任何设置）'
if ($Apply) { $mode = '实际执行' }
if ($Restore) { $mode = '回退' }

Write-Host ''
Write-Host ("  亮度来源修复 —— 当前模式：{0}" -f $mode) -ForegroundColor White
Write-Host ''

if ($Apply -and -not $isAdmin) {
    Write-Host '  需要管理员权限。请右键「以管理员身份运行 PowerShell」后再执行。' -ForegroundColor Red
    exit 1
}

$doVb = ($Target -eq 'VariBright' -or $Target -eq 'All')
$doBs = ($Target -eq 'BatterySaverDim' -or $Target -eq 'All')

$newVbLevel = 0
$newBsPercent = 100
if ($Restore) {
    $newVbLevel = 2
    $newBsPercent = 70
}

# ── Vari-Bright ───────────────────────────────────────────
if ($doVb) {
    Write-Host '  [1] AMD Vari-Bright' -ForegroundColor Cyan

    $targets = @()
    $scanRoots = @(
        'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}',
        'HKLM:\SYSTEM\CurrentControlSet\Control\Video'
    )
    foreach ($root in $scanRoots) {
        Get-ChildItem $root -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
            $props = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
            if ($null -ne $props -and $null -ne $props.PP_UserVariBrightLevel) {
                $targets += [pscustomobject]@{
                    Path  = $_.PSPath
                    Label = ($_.Name -replace '^Microsoft\.PowerShell\.Core\\Registry::', '')
                    Level = $props.PP_UserVariBrightLevel
                }
            }
        }
    }

    if ($targets.Count -eq 0) {
        Write-Host '      未发现 Vari-Bright 配置项，跳过。' -ForegroundColor DarkGray
    }
    else {
        foreach ($t in $targets) {
            Write-Host ("      {0}   [{1} -> {2}]" -f $t.Label, $t.Level, $newVbLevel) -ForegroundColor DarkGray
            if ($Apply) {
                Set-ItemProperty -LiteralPath $t.Path -Name 'PP_UserVariBrightLevel' -Value $newVbLevel -Type DWord
                if ($null -ne (Get-ItemProperty -LiteralPath $t.Path -Name 'Dal_UserVariBrightLevel' -ErrorAction SilentlyContinue)) {
                    Set-ItemProperty -LiteralPath $t.Path -Name 'Dal_UserVariBrightLevel' -Value $newVbLevel -Type DWord
                }
            }
        }
        if (-not $Apply) {
            Write-Host '      （演练模式：以上改动未写入）' -ForegroundColor DarkGray
        }
        else {
            Write-Host '      已写入。需要重启（或重启显卡驱动）后生效。' -ForegroundColor Green
        }
    }
    Write-Host ''
}

# ── 节电模式亮度折减 ───────────────────────────────────────
if ($doBs) {
    Write-Host '  [2] 节电模式下的屏幕亮度系数' -ForegroundColor Cyan
    $sub = 'de830923-a562-41af-a086-e3a2c6bad2da'
    $setting = '13d09884-f74e-474a-a852-b6bde8ad03a8'

    $gidM = [regex]::Match(((powercfg /getactivescheme 2>$null | Out-String)), '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})')
    $curCoeff = $null
    if ($gidM.Success) {
        $rk = "HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\$($gidM.Groups[1].Value)\$sub\$setting"
        if (Test-Path $rk) {
            $kv = Get-ItemProperty -LiteralPath $rk -ErrorAction SilentlyContinue
            if ($null -ne $kv.DCSettingIndex) { $curCoeff = [int]$kv.DCSettingIndex }
            elseif ($null -ne $kv.ACSettingIndex) { $curCoeff = [int]$kv.ACSettingIndex }
        }
    }
    if ($null -eq $curCoeff) { $curCoeff = 70 }

    Write-Host ("      节电时亮度系数：{0}% -> {1}%" -f $curCoeff, $newBsPercent) -ForegroundColor DarkGray

    if ($Apply) {
        powercfg /setacvalueindex SCHEME_CURRENT $sub $setting $newBsPercent | Out-Null
        powercfg /setdcvalueindex SCHEME_CURRENT $sub $setting $newBsPercent | Out-Null
        powercfg /setactive SCHEME_CURRENT | Out-Null
        Write-Host '      已写入并重新应用当前电源方案。' -ForegroundColor Green
    }
    else {
        Write-Host '      （演练模式：以上改动未写入）' -ForegroundColor DarkGray
    }
    Write-Host ('      对应命令：powercfg /setacvalueindex SCHEME_CURRENT {0} {1} {2}' -f $sub, $setting, $newBsPercent) -ForegroundColor DarkGray
    Write-Host ''
}

Write-Host '  提示：' -ForegroundColor White
Write-Host '    · 节电模式本身仍处于开启状态，只是不再压低屏幕亮度，其它省电项不受影响。' -ForegroundColor DarkGray
Write-Host '    · Vari-Bright 改动需重启生效；回退请加 -Restore 参数。' -ForegroundColor DarkGray
Write-Host ''
