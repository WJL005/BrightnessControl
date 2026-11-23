# 贡献指南

感谢你考虑为 Brightness Control 做贡献。

## 环境

- Windows 10 / 11
- .NET Framework 4.x 的 C# 编译器（`csc.exe`）
- PowerShell 5.1+（脚本与体检工具）

## 编码约定

- 源码为 C# 5 语法（配合 `.NET Framework 4.x` 自带的 `csc.exe`，不使用新语言特性）。
- UI 文案使用中文；代码注释简明即可。
- GUI 与 CLI 是两个独立入口（`Program.Main`），互不引用；修改时保持这一点。
- 涉及系统设置（注册表、电源方案）的改动必须：默认不写、写前备份、提供撤销。

## 提交流程

1. Fork 仓库并建分支。
2. 修改后用 `build.bat` 确认能编译通过。
3. 自测 GUI 与 CLI 的核心功能。
4. 提交 PR，说明改动目的与测试方法。

## 隐私红线（重要）

本项目源自一次脱敏开源，请把隐私放在第一位：

- 不要把你的用户名、机器名、显示器型号、序列号写进源码、注释或文档。
- 不要提交 `logs\`、`backup\`、`power-lock-backup.txt`、`battery-saver-backup.txt`、`power-backup.txt` 等运行时产物（已在 `.gitignore`）。
- 在 issue / PR 里贴日志前先脱敏。
- 新增示例数据请使用 `examples\` 下的占位符风格（`user=<用户名>`、`host=<主机名>`、`YYYY-MM-DD`）。

## 报告问题

提 issue 时请附上：Windows 版本、笔记本/台式机、内置屏还是外接屏、是否插电、体检脚本输出（脱敏后）、相关日志片段（脱敏后）。
