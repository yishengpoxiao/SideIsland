# v1.0.0 跨平台预览状态

目标：Apple Silicon Mac（最低 macOS 15.0）、Ubuntu x64／arm64。首个发布版本：`1.0.0`。UI 为 Avalonia 12.1.4，运行时为 .NET 10。Windows WPF 保持独立；`v1.0.0` 是发布编号，Mac／Linux 适配仍处于预览状态，不代表目标设备验收通过。

此表描述代码迁移范围，不代表目标系统实测结果。当前工作主机为 Windows；Mac 与 Ubuntu 原生运行、权限、窗口管理和硬件适配尚未在这些设备上确认。实际构建／测试结论必须对应 `artifacts/cross-platform/` 的日志和二进制哈希。

| 入口／能力 | 预览迁移范围 | 目标平台边界 |
| --- | --- | --- |
| 专注 `focus` | 共享计时模型、时长与倒计时 UI | 休眠恢复、通知呈现和系统电源事件需目标机确认 |
| 媒体 `media` | 显式连接、歌曲元数据和播放控制平台适配 | Mac 仅 Apple Music Apple Events；Linux MPRIS／playerctl。未宣称全局所有播放器、封面和歌词等价 |
| 额度 `quota` | Unix 可执行文件与 Codex 协议适配入口 | 需要目标系统已安装且已登录的原生 Codex CLI；不能把 Windows 的 `.exe` 发现逻辑当作完成 |
| 端口 `ports` | Mac lsof、Linux ss／procfs 的查询适配 | 系统命令和权限决定是否能提供 PID、名称与可执行路径 |
| 便笺 `notes` | 文本编辑及本机偏好／数据存储 | 跨系统用户数据目录独立，不自动搬动 Windows 原数据 |
| 剪贴板 `clipboard` | 桌面剪贴板读写 UI | 未等价迁移后台历史、来源识别和全部 Wayland 桌面支持 |
| 对照 `diff` | 共享文本差异模型及 UI | 各平台字体与长文本布局需目标机验证 |
| 文本 `text` | 共享文本／编码处理模型及 UI | 不把用户文本当界面语言重写 |
| 二维码 `qr` | 共享编码算法，跨平台图像／UI 输出 | 遵守单张二维码容量，未使用 System.Drawing 的 Unix 不支持路径 |
| 计算器 `calculator` | 共享计算模型及 UI | 不宣称输入方法与所有本地化环境已实机验证 |
| 随机 `random` | 共享随机工具模型及 UI | 目标机运行仍需独立验证 |
| 文件校验 `hash` | 共享异步文件校验模型及 UI | 选择器、取消和目标文件权限需目标机验证 |
| 截图 `screen-pin` | 系统选区、独立钉屏、缩放、透明度和 PNG 另存 | Mac screencapture；Ubuntu Screenshot Portal，接口不可用才回退已安装截图器。权限和桌面实测待完成 |
| 批量 `batch-files` | 预览、冲突检查、原生身份校验、两阶段重命名、日志和同会话撤销 | Apple Silicon fstat／renameatx_np，Ubuntu statx／renameat2；非配合进程并发修改仍有系统边界，必须目标文件系统验证 |
| 图片 `batch-images` | 多选、缩放、JPG 质量、PNG／JPG 转换和新位置输出 | 使用 Avalonia 跨平台解码编码，不调用 Unix 不支持的 System.Drawing |
| 通知 `notifications` | 来源开关、应用禁用／优先级、合并、搜索、未读管理及可选历史 | Mac 仅当前可访问的 AX 通知卡片；Linux D-Bus 监测需桌面允许。不是保证读取全部应用历史的后台服务 |
| 工作场景 `workspaces` | 本机快捷入口、手动组合启动、可选两个收起组件、逐项启动结果与取消后续请求 | Mac open／Linux xdg-open 与独立 argv；原生桌面接收仍需实测，不等同于自动场景规则。详见 [工作场景](WORKSPACES.md) |
| 守望 `watch` | 文件／进程等待与状态 UI | 使用目标系统的进程身份和文件语义，不复制 Windows PID 假设 |
| 日志 `logs` | 主动开始的有界末尾读取、关键词提醒、筛选／复制／另存显示快照、清除与停止 | 复用共享编码与截断处理；已打开文件的身份通过各平台 stat 查询。单个后台读取，过时结果不回写；导出用独占新文件发布，原生 Mac／Ubuntu 文件系统仍需实测 |
| 唤醒 `awake` | Mac caffeinate／Linux inhibitor 子进程适配 | 只管理本应用创建的持有进程；桌面电源政策可能不接受请求 |
| 音量 `volume` | Mac 系统音量、Linux pactl 的系统输出音量 | 没有通用 Mac 分应用混音器，不自动安装虚拟音频驱动 |
| 动态 `activities` | 共享任务状态与本地任务输入策略 | 不是远程命令执行接口；访问令牌和任务内容不随预览包分发 |
| 设置 `settings` | 中英文、明暗、悬停与停靠设置 | 窗口权限、全屏 Space、多屏与原生 DPI 需要实机确认 |
| 数据状态 `data-status` | 设置／通知状态、重试、草稿备份重载、显式坏文件恢复与外部冲突检测 | 后台操作与无头布局已纳入验证；原生文件系统仍需目标机确认。详见 [数据恢复](DATA-RECOVERY.md) |

## Mac 刘海

通过公开 AppKit 屏幕安全区和左右可用区识别真实摄像头区域，优先选择带刘海显示器。黑色轮廓包住刘海，交互内容避开缺口；物理摄像头位置不能显示像素。原生桥直接操作 NSWindow 的屏幕坐标，不以 Windows 的物理像素乘除来猜测 Retina 多屏位置。

纯几何策略包含不同刘海宽度、高度、左右可见内容、无刘海回退、负坐标、缩放、小屏和异常输入保护。即便纯几何测试通过，也不证明 AppKit 层级、Space 切换、屏幕重排、自动隐藏菜单栏和全屏应用都已经在真实 Mac 上通过。

预览 `Info.plist` 设置 `NSPrefersDisplaySafeAreaCompatibilityMode=false`，允许使用顶部完整区域；尚未完成 Apple 建议的实机刘海兼容审计。没有系统全局配置改动。包未执行 Developer ID 签名或公证，不提供 Gatekeeper 绕过脚本。

## 尚未等价迁移的 Windows 功能

- 保证读取所有微信／QQ后台历史、通知转待办和原 Windows 通知 API 等价能力；现有读取范围和限制见 [通知接入](NOTIFICATIONS.md)。
- Windows SMTC 全局媒体会话、全部播放器歌词、Windows Core Audio 分应用音量与输出切换。
- 截图标注／马赛克及全部原截图编辑流程；当前已提供独立钉屏和批量文件／图片工具。
- 原有自动工作场景、完整组件布局编辑、临时文件收纳和全部 38 个工具的等价 UI。
- 所有 Wayland 桌面的任意四边定位、窗口层级、透明区域穿透和后台剪贴板历史。

这些能力必须分别实现、验证和标记可用性；不能因核心程序集编译成功或界面能显示，就宣称已经支持。

## 构建产物与验证含义

`cross-platform/out/<rid>` 是自包含原生目标发布目录；项目根 `release/SideIsland-v1.0.0-<rid>.zip` 是分发包。Mac 包装为 `.app`，Ubuntu 包装为带执行权限的原生目录和启动脚本。根目录 `VERSION` 是跨平台与 Windows 共用的发布版本来源；打包脚本会拒绝旧版本发布标记或程序集，避免把旧二进制改名为新版本。

打包器逐项核对发布文件与复制结果，设置并验证 ZIP Unix creator OS、0755／0644 模式和解压后 SHA256，同时记录清单。它核对 apphost 的 Mach-O ARM64、ELF x64／arm64 或 PE AMD64 架构，但不会把这些静态检查称为“目标 OS 运行成功”。

构建、打包、无头 UI／模型测试和实机验证是不同层次。包清单 `TargetRuntimeExecuted=false` 表示该打包步骤没有运行目标程序；未来实机测试应新增独立日志，保留系统版本、显示器／缩放、权限结果和实际二进制哈希。

## 平台文档依据

- [Apple：显示安全区兼容模式及左右辅助区域](https://developer.apple.com/documentation/BundleResources/Information-Property-List/NSPrefersDisplaySafeAreaCompatibilityMode)
- [Apple：NSScreen.safeAreaInsets](https://developer.apple.com/documentation/appkit/nsscreen/safeareainsets)
- [Apple：UserNotifications 查询的是本应用的通知](https://developer.apple.com/documentation/usernotifications/unusernotificationcenter/getdeliverednotifications(completionhandler:))
- [Linux：MPRIS 播放器接口](https://specifications.freedesktop.org/mpris/latest/Player_Interface.html)
- [Linux：桌面通知服务规范](https://specifications.freedesktop.org/notification/latest-single/)
- [Avalonia：macOS 后端及交叉编译范围](https://docs.avaloniaui.net/docs/platform-specific-guides/macos)
