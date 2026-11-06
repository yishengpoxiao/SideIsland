[English](README.md) | 简体中文

# Side Island · 桌面灵动岛

一个紧凑、具有 iOS 视觉风格的桌面灵动岛，集中显示通知、音乐、专注状态与常用工具。

**v1.0.0** · **Windows 38 个工具** · **macOS / Ubuntu 预览版**

可停靠在屏幕四边，收起时显示自定义组件，展开后使用工具箱。支持拖动、展开窗口拉伸、中英文切换及明暗主题。采用 C# 实现，应用本身不需要 Python、Electron 或 Node.js 运行时。

[下载 v1.0.0](https://github.com/yishengpoxiao/SideIsland/releases/latest) · [源码构建](docs/BUILDING.md) · [项目架构](docs/ARCHITECTURE.md)

v1.0.0 之后的开发改动新增跨平台工作场景、日志追踪及其他修复，见 [未发布改进](docs/UNRELEASED.md)；已发布的 v1.0.0 下载包保持不变。

## 选择版本

| 平台 | 状态 | 系统要求 | 下载文件 |
| --- | --- | --- | --- |
| Windows | 成熟实现，已有回归及真实桌面／播放器测试 | Windows 10 2004 / build 19041 及以上，或 Windows 11；.NET Framework 4.8；已实测环境为 x64 桌面 | `SideIsland-v1.0.0-win-x64.zip` |
| Apple Silicon Mac | **预览：尚未在目标 Mac 实机验证** | macOS 15 及以上；ARM64 | `SideIsland-v1.0.0-osx-arm64.zip` |
| Ubuntu | **预览：尚未在目标 Ubuntu 桌面实机验证** | Ubuntu 22.04 及以上；x64 或 ARM64；X11 或可用的 XWayland 会话 | `SideIsland-v1.0.0-linux-x64.zip` / `SideIsland-v1.0.0-linux-arm64.zip` |

跨平台预览有 **21 个工具入口**，尚未等价迁移 Windows 全部功能。构建、打包和无头测试通过不代表已在 Mac／Ubuntu 上运行通过。Mac 包**尚未经过 Developer ID 签名或公证**。使用前请查看[移植状态](cross-platform/docs/PORTING-STATUS.md)。

## 开始使用

### Windows

1. 从 [Releases](https://github.com/yishengpoxiao/SideIsland/releases/latest) 下载 Windows ZIP，完整解压到长期保留的目录。
2. 运行 `CodexIsland.exe`。点击岛身展开，拖动收起的岛身移动；托盘或右键菜单可以找回窗口、退出程序。
3. 在设置中选择组件、停靠方式、悬停等待时间、语言和主题。`Ctrl + Alt + Space` 切换展开／收起；岛内按 `Ctrl + K` 搜索工具。

多数工具不需要 Codex 账户。额度监控需要已安装并登录的 Codex 桌面版或 CLI；自动发现失败时，可在设置中指定程序与数据目录。

**可选：接入 Windows 系统通知**

便携 EXE 本身不能读取其他应用的系统通知。退出灵动岛后，在完整解压包中运行 `install-notifications.cmd`。本地开发注册需要 Windows 开发者模式；也可以使用系统已经信任的签名 MSIX。安装器不会自行开启开发者模式或导入证书。

从开始菜单的 **Side Island** 或 `启动通知版.cmd` 启动，在消息页点击“连接系统通知”，再由你亲自允许 Windows 授权。注册后请保留安装目录。只能汇集实际通过 Windows 发送的通知，应用内部自绘弹窗不属于系统通知。[安装详情](packaging/README.md)

### macOS 预览

解压后将完整的 `Side Island.app` 放到应用目录。未签名、未公证的预览可能被 macOS 阻止；项目不提供关闭或绕过 Gatekeeper 的命令。Mac 实机启动验证、签名与公证仍待完成。

默认优先贴合真实刘海，文字与操作区域放在两翼或下方；摄像头缺口本身没有可绘制像素。连接 Apple Music 需显式操作，并可能要求“自动化”授权；读取可见通知需要另外手动授予辅助功能权限。[刘海适配说明](cross-platform/docs/MAC-NOTCH.md)

### Ubuntu 预览

解压对应架构的包，进入 `SideIsland` 目录运行 `./side-island`。预览 ZIP 保留执行权限并包含 .NET 运行时，仍需桌面的基础原生库。

可选集成分别使用 `playerctl` 读取 MPRIS 媒体、`pactl` 控制 PulseAudio／PipeWire 系统音量、`ss` 查询端口、`dbus-monitor` 监听会话允许的通知，以及 `systemd-inhibit` 保持唤醒。缺少命令会明确提示，不自动安装。预览尚不保证原生 Wayland 下的任意定位、置顶与透明区域输入行为。[预览指南](cross-platform/README.md)

## 主要功能

| 类别 | Windows 功能 |
| --- | --- |
| 状态与通知 | 自定义收起组件、Codex 额度、CPU／内存／电池、通知预览、按应用禁用与优先级、任务动态 |
| 音乐与声音 | 播放控制、播放器提供的封面与信息、本地 LRC 歌词、可选在线歌词匹配、系统及分应用音量 |
| 专注与工作 | 自定义专注时长、秒表、提醒、待办便签、工作场景启动、场景布局及自动规则 |
| 开发辅助 | 端口程序／PID／路径查询、实时日志、文件／进程守望、服务就绪提醒、文字对照、模板与文件校验 |
| 图片与文件 | 截图钉屏及标注、本机 Windows OCR、图片压缩／缩放／转换、批量重命名、文件收纳与局域网传输 |
| 日常工具 | 二维码、剪贴板历史、计算器、单位换算、日期时区、文字处理、随机／密码及屏幕取色 |

详细操作和容量限制见 [Windows 完整指南](docs/windows-guide.zh-CN.md)。其中旧开发版本编号作为历史记录保留，当前公开发行版本为 **v1.0.0**。

QQ 音乐、网易云音乐和 Apple Music 已进行真实 Windows 播放控制与封面测试；Cider 当时未安装，**未宣称实测通过**。歌名、进度、控制和歌词取决于播放器提供的信息，未提供的进度不会伪造显示。

## 跨平台能力差异

| 能力 | Windows | macOS / Ubuntu 预览 |
| --- | --- | --- |
| 工具覆盖 | 38 个模块 | 21 个入口，部分迁移 |
| 通知 | 已注册身份与用户授权下的 Windows 通知监听 | Mac 仅可识别、可见的辅助功能通知卡片；Linux 仅会话总线允许的后续标准 Notify 调用；另有令牌保护的本机输入 |
| 媒体 | Windows 系统媒体会话，取决于播放器支持 | Mac 仅 Apple Music；Linux 通过 playerctl / MPRIS；不承诺封面和同步歌词等价 |
| 音量 | 系统及分应用混音 | 系统输出音量，未等价迁移分应用混音 |
| 截图 | 选区、钉屏、标注和 OCR | 系统截图／Portal 与钉屏；原生权限及桌面行为待实测 |
| 剪贴板 | 主动启用的文字／图片历史、搜索、收藏与应用排除 | 普通剪贴板工具，不保证通用后台历史 |
| 停靠 | 四边、多屏位置与全屏规则 | Mac 刘海优先；X11／XWayland 定位；全屏 Space 与原生 Wayland 行为待实测 |

预览的具体边界见[通知接入](cross-platform/docs/NOTIFICATIONS.md)、[截图钉屏](cross-platform/docs/SCREEN-PIN.md)和[批量文件](cross-platform/docs/BATCH-FILES.md)。

## 本地数据与网络连接

设置、便笺和其他保存的数据位于本机。Windows 通知历史仅保留在运行内存，手动转为待办的通知除外；跨平台预览默认不保存通知历史，可主动开启本机明文保存。剪贴板记录需主动开启；保存的便笺、收藏等不会加密。

Codex 额度通过已登录的本机 Codex 服务查询。可选的 LRCLIB 在线歌词匹配默认关闭，开启后发送歌名、歌手、专辑和时长。局域网传输仅在主动分享后开始。启用数据来源前可先查看对应工具设置。

## 构建与目录

Windows：

```powershell
.\build.ps1
.\build.ps1 -Test
```

预览工程需要 PowerShell 7，以及 `cross-platform/global.json` 指定的 .NET SDK：

```powershell
pwsh ./cross-platform/test.ps1 -Rounds 2
pwsh ./cross-platform/build.ps1 -Runtime osx-arm64
pwsh ./cross-platform/build.ps1 -Runtime linux-x64
```

[BUILDING.md](docs/BUILDING.md) 说明具体依赖、Linux ARM64、打包、原生 UI 测试与验证范围。

- `src/`、`tests/`：Windows 应用与回归套件。
- `cross-platform/`：共享模型、Avalonia 桌面、平台适配、测试与预览打包。
- `packaging/`：可选 Windows 通知身份与 MSIX 工具。
- `docs/`、`tools/`、`assets/`：文档、开发辅助与产品资源。

二进制、ZIP 和校验文件应作为 **GitHub Release 附件**发布，不放入源码仓库。构建输出、依赖缓存、本机设置与原始诊断文件不属于源码交付内容。

## 许可说明

依赖许可见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) 和[预览依赖说明](cross-platform/licenses/)。项目本身尚未选定许可证，不能把第三方组件的许可视为整个项目的许可。
