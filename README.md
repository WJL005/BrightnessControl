# Brightness Control（Windows 屏幕亮度控制工具）

[![Platform](https://img.shields.io/badge/platform-Windows-blue.svg)](https://www.microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-Framework%204.x-512BD4.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

一个给 Windows 笔记本用的屏幕亮度控制小工具：图形界面 + 命令行 + 一键快捷脚本，支持内置屏（WMI）与外接屏（DDC/CI）双屏控制、电源方案管理、开机自启、托盘驻留与全局快捷键。附带「亮度体检」诊断脚本，帮你找出到底是什么在偷偷改变亮度。

> 全部为脱敏后的开源版本：不含任何本机用户名、机器名、显示器型号或私有路径。

---

## 目录

- [它解决什么问题](#它解决什么问题)
- [功能一览](#功能一览)
- [快速开始](#快速开始)
- [三种用法](#三种用法)
- [亮度体检：找出谁在改亮度](#亮度体检找出谁在改亮度)
- [日志与示例](#日志与示例)
- [从源码编译](#从源码编译)
- [文件说明](#文件说明)
- [常见问题](#常见问题)
- [安全与隐私](#安全与隐私)
- [贡献与许可](#贡献与许可)

---

## 它解决什么问题

Windows 笔记本的亮度经常「不听话」：

- 系统自带的亮度滑块不好用，或没有精细调节；
- 插上/拔掉电源后亮度突然变化；
- 外接显示器和内置屏幕需要分别控制；
- AMD Vari-Bright、节电模式的亮度系数在悄悄压暗屏幕；
- 想用快捷键或双击脚本快速设到某个固定亮度。

这个工具把这些需求收拢到一个托盘程序里，并提供诊断脚本定位亮度异常的来源。

## 功能一览

**图形界面（BrightnessControl.exe）**

| 功能 | 说明 |
|---|---|
| 亮度滑条 + 微调 | 拖动设定，或 `+5` / `-5` 精细调整，实时回读实际亮度并复核 |
| 双屏控制 | 可分别选内置屏（WMI）/ 外接屏（DDC/CI），或「两块屏一起调」 |
| 托盘驻留 | 关闭窗口后驻留系统托盘，双击图标唤回 |
| 全局快捷键 | `Ctrl+Alt+↑ / ↓` 微调 5% |
| 开机自启 | 一键开关 |
| 电源方案管理 | 省电 / 均衡 / 性能模式切换、插拔电源自动设亮度；**修改前自动备份，可一键撤销 / 全部还原** |
| 供电状态同步 | 「复制到插电 / 复制到电池」：关屏、睡眠、亮度在两种供电状态间互相同步 |
| Vari-Bright 开关 | 一键关闭 / 恢复 AMD Vari-Bright（内容自适应背光） |
| 节电模式常开 | 让节电模式插电时也生效，但不压暗屏幕；改动「立即应用」，托盘菜单可快捷开关 |
| 复制状态 | 把当前电源/亮度状态整理成文字复制到剪贴板，方便粘贴排查（远程桌面也可用） |
| 诊断与日志 | 导出系统状态报告、查看操作日志 |
| 管理员提权 | 需要改电源方案时可「以管理员身份重启」 |

**命令行（BrightnessTool.exe）**

```
BrightnessTool.exe show          # 显示当前亮度
BrightnessTool.exe set 80        # 设为 80%
BrightnessTool.exe monitor 2     # 监控亮度变化，间隔 2 秒
```

**快捷脚本**：`亮度-50.ps1` ~ `亮度-100.ps1`，双击即设到对应百分比。

## 快速开始

1. 下载 `BrightnessControl.exe`（见 Releases）直接双击运行，出现托盘图标即成功。
2. 点开窗口，拖动滑条试试能否改变亮度；滑条下方会显示回读的实际值。
3. 如果你的笔记本没有反应，先跑一下 [亮度体检脚本](#亮度体检找出谁在改亮度) 看看亮度接口是否可用。
4. 想改电源方案 / Vari-Bright 这些系统设置，点「以管理员身份重启」。

> 需要 .NET Framework 4.x（Windows 10/11 通常已内置）。外接屏 DDC/CI 控制需要显示器本身支持 DDC/CI。

## 三种用法

| 场景 | 用什么 |
|---|---|
| 日常调亮度、改电源方案 | GUI：`BrightnessControl.exe` |
| 脚本 / 自动化里设亮度 | CLI：`BrightnessTool.exe set 80` |
| 一键设到固定值 | 双击 `亮度-80.ps1` 等快捷脚本 |

GUI 与 CLI 都会写操作日志到 `logs\` 目录（首次运行自动生成，已加入 `.gitignore`）。

## 亮度体检：找出谁在改亮度

`Diagnose-Brightness.ps1` 是**只读**诊断脚本，逐层检查 Windows 上所有可能改变亮度的位置，并给出结论：

```powershell
powershell -ExecutionPolicy Bypass -File .\Diagnose-Brightness.ps1
```

它会检查：内置屏亮度接口（WMI）、外接屏 DDC、电源方案亮度系数、AMD Vari-Bright、自适应亮度、虚拟显示器/远控组件等。加 `-Test` 参数会额外做一次写入/回读一致性测试（临时设为 100% 再还原），用来判断是否存在「亮度被二次按比例压低」。

确认来源后，`Fix-Brightness-Source.ps1` 可以处理两个常见元凶（Vari-Bright 与节电亮度系数）——**默认只演练（DryRun），加 `-Apply` 才真正写入，且需要管理员权限**。

## 日志与示例

程序运行时会在 `logs\` 生成当天日志（如 `brightness-YYYYMMDD.log`），记录每一次设置、回读结果、警告和错误，含操作类型、请求值/前值/后值、用户名与主机名标识。诊断日志为 `diagnose-YYYYMMDD.log`。这些文件已加入 `.gitignore`，不会进版本库。

`examples\` 目录提供脱敏后的日志样例，可在不运行程序的情况下了解日志格式：

- `示例操作日志.txt`：SET / CHANGE / FAIL / WARN / FIX / REPORT 等各类记录
- `示例诊断日志.txt`：体检脚本输出样例

## 从源码编译

需要 .NET Framework 4.x 的 C# 编译器（`csc.exe`，随 Visual Studio 或 .NET Framework SDK 安装）：

```bat
build.bat
```

会生成两个文件：

| 输出 | 源码 | 说明 |
|---|---|---|
| `BrightnessControl.exe` | `src/BrightnessGui.cs` | 图形界面版 |
| `BrightnessTool.exe` | `src/BrightnessTool.cs` | 命令行版 |

编译脚本会自动在 64 位 / 32 位 .NET Framework 目录中寻找 `csc.exe`。

## 文件说明

| 文件 | 作用 |
|---|---|
| `BrightnessControl.exe` | 主程序（GUI），双击运行 |
| `BrightnessTool.exe` | 命令行版 |
| `src/BrightnessGui.cs` | GUI 源码 |
| `src/BrightnessTool.cs` | CLI 源码 |
| `Diagnose-Brightness.ps1` | 亮度体检（只读诊断） |
| `Fix-Brightness-Source.ps1` | 修复已知亮度来源（默认演练） |
| `Set-Brightness.ps1` | 设置内置屏亮度的底层脚本 |
| `亮度-50.ps1` ~ `亮度-100.ps1` | 双击设到固定亮度的快捷脚本 |
| `build.bat` | 一键编译 |
| `ui-settings.txt` | UI 偏好（如锁定模式） |
| `examples/` | 脱敏日志示例 |

## 常见问题

**Q：拖滑条没反应？**
A：先跑体检脚本确认亮度接口可用。部分台式机/外接屏不暴露 WMI 背光接口；外接屏需要支持 DDC/CI。

**Q：为什么设了 80%，回读显示 60%？**
A：说明有另一层在按比例压低亮度，常见是电源方案里的「节电模式亮度系数」或 Vari-Bright。跑体检脚本会给出结论，再用 `Fix-Brightness-Source.ps1` 处理。

**Q：改电源方案提示需要管理员？**
A：电源方案属于系统设置，点程序里的「以管理员身份重启」即可。

**Q：可执行文件会被 SmartScreen 拦截？**
A：开源编译的 exe 未做代码签名，首次运行可能弹出警告，选择「更多信息 → 仍要运行」。详见 [SECURITY.md](SECURITY.md)。

## 安全与隐私

- 诊断脚本默认**只读**；修复脚本默认**演练**，写操作需显式 `-Apply` 且要管理员权限。
- 电源方案修改前自动备份，可一键撤销。
- 发布版本已彻底脱敏：无本机用户名、机器名、显示器型号、私有路径。
- 运行时日志含你的本机标识，**请勿在公开 issue 里直接贴原始日志**，必要时先脱敏。详见 [SECURITY.md](SECURITY.md)。

## 贡献与许可

欢迎提 issue 和 PR。提交前请确认不含个人信息。MIT 许可，详见 [LICENSE](LICENSE) 与 [CONTRIBUTING.md](CONTRIBUTING.md)。

⭐ 如果这个工具帮到你，欢迎点个 Star。
