# Side Island · macOS / Ubuntu 预览移植

[English](README.md) | 简体中文

首个发布版本 **1.0.0**；Mac／Linux 适配仍处于预览状态，发布编号不代表实机验收通过。这是独立的 Avalonia 12.1.4 / .NET 10 工程，面向 Apple Silicon Mac 和 Ubuntu，保留共享 C# 业务模型并重做跨平台窗口和平台适配。现有 Windows WPF 的源码、安装身份和发行包不由这些脚本替换。

这是首版移植预览，**不表示 Windows 38 个工具已全部等价迁移，也不表示已在 Mac 或 Ubuntu 实机运行通过**。构建、测试和目标系统验证分别记录；以项目 `artifacts/cross-platform/` 中的实际结果为准。

## 目前界面范围

当前开发版有 24 个入口：专注、媒体、Codex 额度、端口、日志追踪、便笺、剪贴板、文字对照、文本工具、二维码、计算器、随机工具、文件校验、截图钉屏、批量文件、批量图片、通知接入与规则、工作场景、任务守望、保持唤醒、系统音量、任务动态、数据状态、设置。已发布的 v1.0.0 预览版仍为 21 个入口，新改动见 [未发布记录](../docs/UNRELEASED.md)。中英文、明暗主题和紧凑卡片延续现有风格。

跨平台核心包含计时、文本／差异／计算／单位与随机工具、文件校验、二维码编码、任务状态和几何停靠策略。具体实现及限制见 [移植状态](docs/PORTING-STATUS.md)。

**文件校验**新增紧凑的[批量模式](docs/BATCH-CHECKSUMS.md#简体中文)，支持逐项校验、取消后保留完成结果、合并清单导出；**文字对照**支持[后台导出补丁](docs/PATCH-EXPORT.md#简体中文)，替换已有文件前明确确认所观测的版本。

## Mac：刘海优先

安装包最低要求 macOS 15.0，并遵守 [.NET 10 的系统支持政策](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)。2026-09-28 更新的支持表列出 macOS 15、26、27；Avalonia 的更低部署门槛不改变运行时支持范围。目标设备验证仍待完成。

默认优先检测带真实刘海的屏幕，使用 AppKit 的安全区和刘海左右可用区域，黑色岛身贴合摄像头缺口，再向下展开。文字和交互内容放在刘海两翼或下方；摄像头所在的物理区域本身没有可绘制像素。没有可用刘海时回退到普通顶边／所选边的停靠策略。

原生桥直接使用 macOS 的屏幕坐标，避免把跨屏 Retina 缩放错误地套用两次。屏幕变化后需要重新计算位置；多屏、不同缩放、全屏 Space、睡眠唤醒和外接屏仍需在目标 Mac 上实测。技术说明见 [MAC-NOTCH.md](docs/MAC-NOTCH.md)。

预览包 `Info.plist` 中 `NSPrefersDisplaySafeAreaCompatibilityMode=false`，让应用可以使用刘海所在顶部区域，而不是让 macOS 缩小整个显示区域。**尚未完成 Mac 实机布局审计**，这项设置不是兼容性通过的证明。它只属于本应用包，不修改系统全局设置。

Mac 媒体首版仅连接 **Apple Music**，且应由你在界面明确连接后，才发送读取歌曲信息和控制播放的 Apple Events；系统可能要求你手动授权。没有通用读取所有播放器媒体信息、歌词或其他应用通知的承诺。音量是系统音量，不是 Windows 那种逐应用会话混音器。

## Ubuntu

目标为 Ubuntu 22.04 及更新版本的 x64／arm64 桌面；实际系统库兼容性需在相应发行版确认。当前四边定位按 X11／XWayland 使用场景设计。Wayland 会话需要可用的 XWayland；**原生 Wayland 下窗口的任意定位、始终置顶及透明区域输入受合成器协议约束，尚不保证与 X11 相同**。

Linux 媒体通过支持 MPRIS 的播放器及 `playerctl`，音量通过兼容 PulseAudio 的 `pactl`（包括提供该接口的 PipeWire）。这些命令缺失时应显示不可用信息，不伪造媒体或音量状态，也不会自动安装系统组件。端口查询使用系统命令／进程信息，权限不足可能导致 PID 或路径不可见。

普通剪贴板读写可以通过桌面接口使用；不能把它当作所有 Wayland 桌面均支持的后台剪贴板历史服务。自动工作场景、截图标注和 Windows 全局媒体／歌词能力仍需单独适配。

## 通知、截图与批量文件

- **通知中心**：按应用禁用、优先级、重复合并、搜索、未读／重要筛选、标记已读、清空与可选本机历史保存。收起组件可选择未读数。Mac 由你主动启用辅助功能读取可见通知横幅或打开后的通知中心；不能保证获取隐藏历史。Ubuntu 通过 `dbus-monitor` 监听当前会话的新 `Notify` 调用，桌面总线可能拒绝监测。还提供默认关闭、令牌保护的本机 HTTP 通知输入。不会替换系统通知服务。详见 [通知接入](docs/NOTIFICATIONS.md)。
- **截图钉屏**：Mac 系统区域选择，Ubuntu Screenshot Portal 优先，接口不可用时尝试已安装的 GNOME Screenshot／Spectacle。图片可悬浮拖动、缩放、调整透明度、关闭和另存 PNG；取消或权限拒绝不会继续弹另一个截图器。详见 [截图钉屏](docs/SCREEN-PIN.md)。
- **批量文件与图片**：先预览重命名及冲突，再执行并保留操作日志；同会话可撤销，身份或内容变化时拒绝撤销。多张图片可以缩放、设置 JPG 质量、转换 PNG／JPG，另存到新的输出位置。详见 [批量文件](docs/BATCH-FILES.md)。
- **日志追踪**：主动开始读取本机日志，可填写尚未生成的文件路径，支持内容筛选、复制、另存显示快照、清除视图和新增关键词提醒。收起或切页后继续追踪，退出应用即停止；初始历史不提醒。详见 [范围与限制](docs/LOG-WATCH.md)。
- **工作场景**：保存网页、目录和软件快捷入口，组合场景并可选两个收起组件，一次点击启动已保存的场景；取消会跳过尚未发出的启动请求，保留已经打开的软件。详见 [保存、恢复与平台限制](docs/WORKSPACES.md#简体中文)。
- **数据状态**：查看设置与通知的保存状态，可重试、备份修改后重载或明确恢复坏文件；保存失败保留当前编辑，并检测外部修改。详见 [本机数据与恢复](docs/DATA-RECOVERY.md#简体中文)。

以上已经有代码和行为回归入口，仍需在目标 Mac／Ubuntu 上确认原生权限、桌面接口及文件系统调用。无头测试和注入测试结果均不会标记为原生消息或截图实测。

## 构建与打包

脚本需要 PowerShell 和 `global.json` 指定的 .NET 10 SDK。优先使用项目内 `.runtime/cross-platform-sdk/10.0.402/dotnet.exe`，也可通过 `-DotnetPath` 指定官方 SDK。只有 .NET 运行时不够。构建不会注册系统 SDK，不修改原 Windows 安装；CLI home 和 NuGet 缓存位于项目 `.runtime` 内。

在本目录执行：

```powershell
pwsh ./build.ps1 -Runtime osx-arm64
pwsh ./build.ps1 -Runtime linux-x64
pwsh ./build.ps1 -Runtime linux-arm64
```

可用 `-Runtime win-x64` 构建跨平台界面的 Windows 验证版，文件名为 `SideIsland-Preview-v1.0.0-win-x64.zip`，与原 WPF 产品使用不同名称。`-SkipPackage` 只发布到 `out/<rid>`；也可单独调用：

```powershell
pwsh ./package.ps1 -Runtime osx-arm64 -PublishDirectory ./out/osx-arm64
```

成功后发行 ZIP 在项目根目录 `release/`：

- `SideIsland-v1.0.0-osx-arm64.zip`，包含 `Side Island.app/Contents/MacOS` 下完整的自包含发布文件和 `Info.plist`。
- `SideIsland-v1.0.0-linux-x64.zip` 或 `SideIsland-v1.0.0-linux-arm64.zip`，包含 `SideIsland/` 及启动脚本 `side-island`。
- 每包的 `.sha256` 和 `.manifest.json` 记录解压后逐文件 SHA256、Unix 文件模式和构建目标；打包器会自检这些内容。

打包器遇到已有 ZIP、校验文件或清单会停止，避免覆盖。同版本本地产物需先检查并明确归档；新版本则修改 `VERSION` 后重新构建。已发布的发行包应保持不变。

ZIP 明确使用 Unix creator OS，目录和原生程序／动态库为 0755，普通文件为 0644。Ubuntu 解压后可从 `SideIsland/` 运行 `./side-island`；可选把完整目录放到自己的应用目录，并将其中的 `side-island` 脚本链接到用户 PATH 后，再使用 `side-island.desktop`。自包含程序仍依赖桌面系统的基础原生库。

Mac 包尚未经过 Apple Developer ID 签名或公证；Windows 上的交叉发布和 ZIP 哈希通过不能证明 Gatekeeper、Mach-O 签名或实际启动通过。正式分发需要在 Mac 完成运行验证以及正常签名／公证流程。本工程不会执行 `xattr`、`spctl`、关闭 Gatekeeper 或其他绕过操作。

## 验证

共享核心和无界面测试工程是 `SideIsland.Tests`。可使用本机 .NET 10 SDK执行 `dotnet run --project SideIsland.Tests`，具体测试结论以保留的原始日志为准。Windows 主机的核心／无头 UI 验证不替代 Mac 刘海、Apple Events、Ubuntu 桌面和音频服务实测。

项目内已提供 `test.ps1 -Rounds 2`：保留恢复、编译、每轮完整输出、渲染图片、前后源码哈希和测试程序集哈希。布局矩阵使用实际 Avalonia 渲染器及无头 DPI 变化，覆盖中英、明暗、320／480／760 点宽度、360／720 点高度和 100%／125%／150%／200% 缩放；这不等于真实窗口管理器或 Retina 显示器实测。

`build.ps1` 保留恢复、发布和源码 SHA256 清单；失败输出不被写成通过。`package.ps1` 另核对 apphost 的目标架构、完整发布文件复制、ZIP 解压字节以及执行权限。旧 Windows 的 87 套回归记录不能直接作为跨平台预览的验证记录。

## 版本维护

根目录 `VERSION` 是 Windows 与跨平台共用的唯一发布版本来源；`Directory.Build.props` 读取它生成程序集版本，运行时界面读取自身程序集版本，构建／打包／测试脚本直接读取同一根文件。打包器核对发布标记和程序集版本，不接受将其他版本二进制重新贴标。包内同时提供中英文 README。更新版本时同时检查文档示例和静态安装清单。
