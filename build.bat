@echo off
cd /d "%~dp0"
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe

echo ============================================
echo   Brightness Control - Build
echo ============================================

rem GUI edition (tray + panel + power scheme)
"%CSC%" /nologo /target:winexe /optimize+ /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /out:BrightnessControl.exe src\BrightnessGui.cs
if errorlevel 1 goto fail

rem CLI edition (set / show / monitor subcommands)
"%CSC%" /nologo /target:exe /optimize+ /r:System.dll /r:System.Management.dll /out:BrightnessTool.exe src\BrightnessTool.cs
if errorlevel 1 goto fail

echo BUILD OK
echo   GUI  : BrightnessControl.exe  (src\BrightnessGui.cs)
echo   CLI  : BrightnessTool.exe     (src\BrightnessTool.cs)
exit /b 0

:fail
echo BUILD FAILED
exit /b 1
