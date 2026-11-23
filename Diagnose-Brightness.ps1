<#
.SYNOPSIS
    屏幕亮度体检：找出「到底是谁在调整亮度」。

.DESCRIPTION
    逐层检查 Windows 上所有能改变屏幕亮度的位置，并给出结论。
    默认完全只读，不修改任何设置。

.PARAMETER Test
    额外做一次写入/回读一致性测试（会把亮度临时设为 100%，随后还原）。
    用于判断是否存在「亮度被二次按比例压低」的情况。

.PARAMETER Watch
    连续采样亮度的秒数，用于观察亮度是否在自动漂移。

.EXAMPLE
    .\Diagnose-Brightness.ps1

.EXAMPLE
    .\Diagnose-Brightness.ps1 -Test -Watch 30
#>
param(
    [switch]$Test,
    [int]$Watch = 0
)

$ErrorActionPreference = 'SilentlyContinue'

$findings = New-Object System.Collections.ArrayList

function Section([string]$t) {
    Write-Host ''
    Write-Host ("── $t " + ('─' * [Math]::Max(2, 46 - $t.Length))) -ForegroundColor Cyan
}

function Line([string]$k, $v, [string]$color = 'Gray') {
    Write-Host ('  {0,-16}' -f ($k + '：')) -NoNewline
    Write-Host $v -ForegroundColor $color
}

function AddFinding([string]$text) { [void]$findings.Add($text) }

function Get-BrightnessValue {
    $mon = @(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness)
    if ($mon.Count -eq 0) { return $null }
    return [int]($mon | Where-Object { $_.Active } | Select-Object -First 1).CurrentBrightness
}

function Get-PowerSetting([string]$sub, [string]$setting) {
    $out = (powercfg /q SCHEME_CURRENT $sub $setting 2>$null | Out-String)
    $acM = [regex]::Match($out, '当前交流电源设置索引:\s*0x([0-9a-fA-F]+)')
    $dcM = [regex]::Match($out, '当前直流电源设置索引:\s*0x([0-9a-fA-F]+)')
    $ac = $null; $dc = $null
    if ($acM.Success) { $ac = [Convert]::ToInt32($acM.Groups[1].Value, 16) }
    if ($dcM.Success) { $dc = [Convert]::ToInt32($dcM.Groups[1].Value, 16) }
    return [pscustomobject]@{ AC = $ac; DC = $dc }
}

Write-Host ''
Write-Host '  屏幕亮度体检报告' -ForegroundColor White
Write-Host ('  ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')) -ForegroundColor DarkGray

# ── 1. 基本环境 ─────────────────────────────────────────────
Section '1. 硬件与驱动'
$cs = Get-CimInstance Win32_ComputerSystem
$os = Get-CimInstance Win32_OperatingSystem
Line '机型' ("{0} {1}" -f $cs.Manufacturer, $cs.Model)
Line '系统' ("{0} (Build {1})" -f $os.Caption, $os.BuildNumber)
$gpu = Get-CimInstance Win32_VideoController | Where-Object { $_.CurrentHorizontalResolution } | Select-Object -First 1
if ($gpu) { Line '显卡' ("{0} / 驱动 {1}" -f $gpu.Name, $gpu.DriverVersion) }
if ($gpu) { Line '分辨率' ("{0} x {1}" -f $gpu.CurrentHorizontalResolution, $gpu.CurrentVerticalResolution) }

# ── 2. 亮度接口能力 ─────────────────────────────────────────
Section '2. 内置屏亮度接口'
$methods = @(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods)
$monitors = @(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness)
if ($methods.Count -gt 0) {
    Line 'WMI 亮度接口' '可用（脚本可以控制）' Green
    Line '面板' ($monitors | Select-Object -First 1).InstanceName
    Line '支持范围' '0 - 100（步进 1）'
} else {
    Line 'WMI 亮度接口' '不可用（脚本无法控制）' Red
    AddFinding '系统未暴露 WMI 亮度接口，脚本无法控制本机背光。'
}
$cur = Get-BrightnessValue
if ($null -ne $cur) { Line '当前亮度' ("{0}%" -f $cur) 'White' }

# 当前电源方案 GUID（后续多处要用）
$schemeRaw = (powercfg /getactivescheme 2>$null | Out-String)
$schemeGid = ''
$mGid = [regex]::Match($schemeRaw, '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})')
if ($mGid.Success) { $schemeGid = $mGid.Groups[1].Value }

# ── 3. 电池 / 节电模式 ──────────────────────────────────────
Section '3. 电池与节电模式'
$esStatus = $null
try {
    $pm = [Windows.System.Power.PowerManager, Windows.System, ContentType = WindowsRuntime]
    $esStatus = [string]$pm::EnergySaverStatus
    Line '节电模式' $esStatus 'White'
    Line '电池状态' ([string]$pm::BatteryStatus)
} catch {
    Line '节电模式' '读取失败（WinRT 不可用）' DarkGray
}

# 节电模式下的亮度系数（节电生效时：实际亮度 = 请求值 × 系数/100）
$bsCoeff = $null
if ($schemeGid) {
    $bsKey = "HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\$schemeGid\de830923-a562-41af-a086-e3a2c6bad2da\13d09884-f74e-474a-a852-b6bde8ad03a8"
    if (Test-Path $bsKey) {
        $kv = Get-ItemProperty -LiteralPath $bsKey -ErrorAction SilentlyContinue
        if ($null -ne $kv) {
            if ($null -ne $kv.DCSettingIndex) { $bsCoeff = [int]$kv.DCSettingIndex }
            elseif ($null -ne $kv.ACSettingIndex) { $bsCoeff = [int]$kv.ACSettingIndex }
        }
    }
}
if ($null -eq $bsCoeff) { $bsCoeff = 70 }
$bsColor = 'Yellow'
if ($bsCoeff -ge 100) { $bsColor = 'Green' }
Line '节电亮度系数' ("{0}%（节电时亮度 = 请求值 × {1}%）" -f $bsCoeff, $bsCoeff) $bsColor

if ($esStatus -eq 'On') {
    if ($bsCoeff -ge 100) {
        Write-Host '  · 节电模式开启，但亮度系数为 100%，屏幕不会被压暗。' -ForegroundColor Green
    }
    else {
        Write-Host ("  ★ 节电模式正在开启，亮度会被压到约 {0}%" -f $bsCoeff) -ForegroundColor Yellow
        Write-Host ("    （请求 100% 时，实际只能得到约 {0}%）" -f $bsCoeff) -ForegroundColor DarkYellow
        AddFinding ("电池节电模式处于开启状态 —— 亮度被整体按约 {0}% 折减。" -f $bsCoeff)
    }
}

# ── 4. 电源方案 ─────────────────────────────────────────────
Section '4. 电源方案中的亮度设置'
if ($schemeGid) {
    $gk = "HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\$schemeGid"
    $friendly = (Get-ItemProperty -LiteralPath $gk).FriendlyName
    Line '当前方案' ("{0}  ({1})" -f $friendly, $schemeGid)
}
$videoSub = '7516b95f-f776-4464-8c53-06167f40cc99'
$pBright = Get-PowerSetting $videoSub 'aded5e82-b909-4619-9949-f5d71dac0bcb'
$pDim = Get-PowerSetting $videoSub 'f1fbfde2-a960-4165-9f88-50667911ce96'
$pAdapt = Get-PowerSetting $videoSub 'fbd9aa66-9553-4097-ba44-ed6e9d65eab8'
$pIdle = Get-PowerSetting $videoSub '3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e'
Line '显示器亮度' ("交流 {0}%  /  电池 {1}%" -f $pBright.AC, $pBright.DC)
Line '变暗后亮度' ("交流 {0}%  /  电池 {1}%" -f $pDim.AC, $pDim.DC)
Line '关闭显示' ("交流 {0} 秒  /  电池 {1} 秒" -f $pIdle.AC, $pIdle.DC)
$adaptiveState = 'off'
if ($pAdapt.AC -eq 1 -or $pAdapt.DC -eq 1) {
    $adaptiveState = 'on'
    Line '自适应亮度' '已启用' Yellow
    AddFinding '电源方案里的「启用自适应亮度」处于开启状态。'
} else {
    Line '自适应亮度' '已关闭' Green
}
if ($pDim.AC -ne $null -and $pDim.AC -le 60) {
    Write-Host ("  · 「变暗后亮度」被设为 {0}%，若闲置后变暗生效，看起来就是间歇性变暗。" -f $pDim.AC) -ForegroundColor DarkGray
}

# ── 5. AMD Vari-Bright ─────────────────────────────────────
Section '5. 显卡层：AMD Vari-Bright（内容自适应背光）'
$vbHits = @()
$scanRoots = @(
    'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}',
    'HKLM:\SYSTEM\CurrentControlSet\Control\Video'
)
foreach ($root in $scanRoots) {
    Get-ChildItem $root -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
        $props = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
        if ($null -ne $props -and $null -ne $props.PP_UserVariBrightLevel) {
            $vbHits += [pscustomobject]@{
                Path  = ($_.Name -replace '^Microsoft\.PowerShell\.Core\\Registry::', '')
                Level = $props.PP_UserVariBrightLevel
            }
        }
    }
}
$vbLevels = 'none'
if ($vbHits.Count -eq 0) {
    Line 'Vari-Bright' '未发现相关配置' Green
} else {
    $levels = ($vbHits | Select-Object -ExpandProperty Level -Unique) -join ', '
    $vbLevels = $levels
    if ($levels -eq '0') {
        Line 'Vari-Bright' '已关闭 (0)' Green
    } else {
        Line 'Vari-Bright' ("已启用，等级 $levels（出现在 $($vbHits.Count) 处注册表项）") Yellow
        Write-Host '  ★ Vari-Bright 是 AMD 的「内容自适应背光」：画面偏亮时自动压低背光省电，' -ForegroundColor Yellow
        Write-Host '    画面变暗后再抬回来 —— 这正是「看不同内容时忽明忽暗」的典型来源。' -ForegroundColor DarkYellow
        AddFinding ('AMD Vari-Bright（内容自适应背光）处于启用状态，等级 ' + $levels + '。')
    }
    $sample = $vbHits | Select-Object -First 1
    Write-Host ("    示例位置：" + $sample.Path) -ForegroundColor DarkGray
}

# ── 6. 夜灯 ────────────────────────────────────────────────
Section '6. 夜灯（蓝光过滤）'
$nlKey = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate'
$nlData = (Get-ItemProperty -LiteralPath $nlKey -ErrorAction SilentlyContinue).Data
if ($null -ne $nlData) {
    $nlOn = $false
    for ($i = 0; $i -lt ($nlData.Count - 1); $i++) {
        if ($nlData[$i] -eq 0x15 -and $nlData[$i + 1] -eq 0x80) { $nlOn = $true }
    }
    if ($nlOn) {
        Line '夜灯' '开启（画面会偏黄偏暗）' Yellow
        AddFinding '夜灯已开启，会让屏幕显得更暗更黄。'
    } else {
        Line '夜灯' '关闭' Green
    }
} else {
    Line '夜灯' '未配置' DarkGray
}

# ── 7. 虚拟显示 / 远控类驱动 ────────────────────────────────
Section '7. 显示栈里的第三方驱动与远控软件'
$vdevs = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
    $_.FriendlyName -match 'Oray|Luminon|Virtual Display|IDDCX|Splashtop|VNC|Idd' -and $_.Status -eq 'OK'
} | Select-Object -ExpandProperty FriendlyName -Unique
if ($vdevs) {
    foreach ($d in $vdevs) { Write-Host ("  · " + $d) -ForegroundColor DarkGray }
    Write-Host '  这些是虚拟显示器/远控组件，通常不直接改亮度，但会在显示栈里多挂一层。' -ForegroundColor DarkGray
} else {
    Line '第三方虚拟显示' '无' Green
}
$remote = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'Sunlogin|Oray|Transcreen|Splashtop|vnc|winvnc|todesk|AnyDesk' } | Select-Object -ExpandProperty ProcessName -Unique
if ($remote) {
    Line '运行中的远控' ($remote -join ', ') Yellow
    Write-Host '  提示：远控连接期间，远端可以改动本机亮度，看起来就像自己在闪。' -ForegroundColor DarkGray
}

# ── 8. 一致性测试 ──────────────────────────────────────────
if ($Test -and $methods.Count -gt 0) {
    Section '8. 写入 / 回读一致性测试'
    $before = Get-BrightnessValue
    Write-Host ("  原始亮度 {0}% ，临时设为 100% ..." -f $before) -ForegroundColor DarkGray
    foreach ($m in $methods) {
        Invoke-CimMethod -InputObject $m -MethodName WmiSetBrightness -Arguments @{ Timeout = 1; Brightness = 100 } | Out-Null
    }
    Start-Sleep -Milliseconds 500
    $peak = Get-BrightnessValue

    # 按实测比例反推还原值，避免还原时被同一层折减再次拉低
    $scale = 1.0
    if ($peak -gt 0) { $scale = $peak / 100.0 }
    $restore = [int][math]::Ceiling($before / $scale)
    if ($restore -gt 100) { $restore = 100 }
    if ($restore -lt 0) { $restore = 0 }

    foreach ($m in $methods) {
        Invoke-CimMethod -InputObject $m -MethodName WmiSetBrightness -Arguments @{ Timeout = 1; Brightness = $restore } | Out-Null
    }
    Line '请求 100%' ('实际 ' + $peak + '%')
    if ($peak -lt 98) {
        $pct = [math]::Round($peak, 0)
        Write-Host ("  ★ 请求 100% 只得到 {0}%，说明还有一层只保留了约 {1}% 的亮度。" -f $peak, $pct) -ForegroundColor Yellow
        AddFinding ('亮度被二次折减：请求 100% 只得到 ' + $peak + '%。')
    } else {
        Write-Host '  写入与回读一致，亮度没有被额外折减。' -ForegroundColor Green
    }
}

# ── 9. 采样 ────────────────────────────────────────────────
if ($Watch -gt 0) {
    Section ("9. 亮度采样（{0} 秒）" -f $Watch)
    $begin = Get-Date
    $values = @()
    while (((Get-Date) - $begin).TotalSeconds -lt $Watch) {
        $v = Get-BrightnessValue
        $values += $v
        Write-Host ("  {0:HH:mm:ss}   {1}%" -f (Get-Date), $v)
        Start-Sleep -Seconds 1
    }
    $uniq = $values | Sort-Object -Unique
    Line '出现过的值' (($uniq | ForEach-Object { "$_%" }) -join ', ')
    if ($uniq.Count -gt 1) {
        AddFinding '采样期间亮度自行发生变化，存在自动调光行为。'
    }
}

# ── 结论 ───────────────────────────────────────────────────
Section '结论'
if ($findings.Count -eq 0) {
    Write-Host '  没有发现明显的自动调光来源。' -ForegroundColor Green
    Write-Host '  若主观上仍觉得忽明忽暗，请用 -Watch 60 采样一次，并在过程中切换窗口/视频。' -ForegroundColor DarkGray
} else {
    $n = 1
    foreach ($f in $findings) {
        Write-Host ("  {0}. {1}" -f $n, $f) -ForegroundColor Yellow
        $n++
    }
}
Write-Host ''
Write-Host '  修复建议：运行 .\Fix-Brightness-Source.ps1（默认只演练，加 -Apply 才写入）' -ForegroundColor DarkGray

# ── 记录本次体检结果 ────────────────────────────────────────
try {
    $logDir = Join-Path $PSScriptRoot 'logs'
    if (-not (Test-Path -LiteralPath $logDir)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }
    $logFile = Join-Path $logDir ('diagnose-{0}.log' -f (Get-Date -Format 'yyyyMMdd'))
    $findingText = 'none'
    if ($findings.Count -gt 0) { $findingText = ($findings -join ' ; ') }
    $line = '{0} | DIAG | brightness={1} | es={2} | bsCoeff={3} | variBright={4} | adaptive={5} | findings={6} | {7}' -f `
        (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $cur, $esStatus, $bsCoeff, $vbLevels, $adaptiveState, $findings.Count, $findingText
    [System.IO.File]::AppendAllText($logFile, $line + [Environment]::NewLine,
        (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("  本次体检已记录：{0}" -f $logFile) -ForegroundColor DarkGray
}
catch { }
Write-Host ''
