# 亮度 100%
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot\Set-Brightness.ps1" -Percent 100

try {
    $parent = Get-Process -Id (Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId -ErrorAction Stop
    if ($parent.ProcessName -eq 'explorer') { Write-Host ''; [void](Read-Host '按回车退出') }
} catch { }
