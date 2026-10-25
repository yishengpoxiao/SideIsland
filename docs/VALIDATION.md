# v1.0.0 validation

[English](#english) · [简体中文](#简体中文)

## English

This is the first public release. Earlier 1.12–1.14 labels in archived documents
were internal development labels, not earlier public GitHub releases.

The release preparation ran on Windows x64:

- Windows application and test assemblies rebuilt as **1.0.0.0** from `VERSION`.
- **17 related Windows suites passed**, including the legacy QR bitmap exporter,
  secure random generation, shared text/time utilities, image processing,
  batch rename/undo, task activity and notification models.
- **Two complete portable regression rounds passed** after source consolidation:
  4,032 actual Avalonia layouts and 48,000 action-caption checks across English /
  Chinese, light / dark, three widths, two heights and 100 / 125 / 150 / 200%
  headless DPI changes. Both builds had zero warnings and zero errors.
- Native screenshot, macOS Accessibility and Linux notification inputs used
  explicitly identified fixtures. Local notification HTTP integration used
  real loopback requests with synthetic messages.
- All four release archives were rebuilt as **v1.0.0**. Packaging verifies their
  decompressed files and hashes; macOS/Linux archives also verify the native
  apphost architecture and Unix execution modes. Application PDBs are excluded.
- Staged-source review excludes local diagnostics, captures, account data,
  dependency caches and signing material. `SHA256SUMS.txt` accompanies the assets.

The Windows WPF test registry still contains 87 suites; this release preparation
did **not** rerun all 87. Historical full runs remain local alongside the new
logs. Raw logs, dumps, personal paths and desktop images are not public assets.

**macOS / Ubuntu remain previews.** They have not been launched or validated on
target desktops. Physical-notch placement, native notification access,
permissions, capture and window-manager behavior need target-device checks.
Mac packages are not Developer ID signed or notarized. Windows optional
notification registration requires Developer Mode or an already trusted signed
package; this release does not supply a trusted signed MSIX.

## 简体中文

这是首次公开发布。归档文档中的 1.12–1.14 为此前的内部开发编号。

本轮在 Windows x64 主机上重新构建 v1.0.0，Windows 程序及测试程序集版本为
1.0.0.0。与共享源码整理有关的 **17 套 Windows 回归通过**；合并去重后的
跨平台工程 **连续两轮完整回归通过**，共覆盖 4,032 组实际 Avalonia 布局与
48,000 次按钮文字检查，包含中英文、明暗、三档宽度、两档高度与四档无头 DPI。
构建零警告、零错误。

四个发行包都已重新构建、解压逐文件核对哈希；Mac/Linux 还检查目标架构与执行
权限。安装包不带应用 PDB，旁附 SHA256SUMS.txt。全部原始诊断及新旧测试日志
保留本机，公开仓库和 Release 排除了原始桌面截图、账户资料、私钥与转储。
本轮没有重新运行完整的 87 套 Windows 注册测试。

Mac／Ubuntu 仍是预览版，尚未进行目标桌面启动、真实刘海、权限、通知、截图和
窗口管理器验证。Mac 包未完成 Developer ID 签名、公证；Windows 可选通知接入
需要开发者模式或目标账户已信任的签名包，本次不附带可信签名 MSIX。
