# Changelog

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [1.0.0] - 2026-11-23

首个公开发布版本（脱敏开源版）。

### 新增
- GUI 版 `BrightnessControl.exe`：亮度滑条 / ±5 微调 / 实时回读复核、内置屏(WMI) 与外接屏(DDC) 双屏控制、两块屏一起调、托盘驻留、`Ctrl+Alt+↑/↓` 全局快捷键、开机自启。
- 电源方案管理：省电/均衡/性能模式、插拔电源自动设亮度，修改前自动备份、可一键撤销。
- 一键开关 AMD Vari-Bright。
- 系统状态面板与诊断报告导出、操作日志查看。
- 命令行版 `BrightnessTool.exe`：`show` / `set <0-100>` / `monitor [间隔]` 子命令，支持按可执行文件名自动触发（亮度-80.exe 等）。
- PowerShell 脚本：`Diagnose-Brightness.ps1`（只读体检）、`Fix-Brightness-Source.ps1`（默认演练的修复）、`Set-Brightness.ps1`（底层设置）、`亮度-50~100.ps1`（快捷脚本）。
- 详细操作日志与诊断日志，按天分文件。
- `build.bat` 一键编译 GUI 与 CLI 两个版本。
- 脱敏处理：移除本机用户名、机器名、显示器型号与私有路径，提供脱敏日志示例。

[1.0.0]: https://github.com/WJL005/BrightnessControl/releases/tag/v1.0.0
