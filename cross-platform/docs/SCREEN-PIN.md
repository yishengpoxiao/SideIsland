# 跨平台截图钉屏

这是 macOS / Ubuntu 预览适配：当前开发主机是 Windows，未在目标 Mac、GNOME、KDE 或 Wayland 合成器上实际截图。托管行为测试及无桌面的 Avalonia 布局测试不代表原生截图、权限、置顶或移动已验证。

点击“截取区域并钉屏”后，岛身先隐藏并异步让出渲染机会，然后启动系统选择。macOS 使用 `/usr/sbin/screencapture -i -x -t png <独立文件>`；Esc 取消时不显示错误。命令通过 `ProcessStartInfo.ArgumentList` 传参，不经过 shell，也不修改截图默认设置或系统权限。[Apple 截图说明](https://support.apple.com/guide/mac-help/take-a-screenshot-mh26782/mac)及[屏幕录制权限](https://support.apple.com/guide/mac-help/control-access-to-screen-and-system-audio-recording-mchld6aa7d23/mac)说明系统选区和权限设置；目标机仍需用自带 `man screencapture` 核对命令行为，首次许可可能要求用户在系统设置中完成后重试。

Ubuntu 首选公开 `org.freedesktop.portal.Screenshot.Screenshot`，传 `interactive=true`，在同一 session bus 连接上先订阅 `org.freedesktop.portal.Request.Response` 再请求，按返回句柄匹配结果。不同 Portal 后端提供的选区界面可能不同，应在系统界面选择区域；旧后端不保证区域默认选中。取消返回 1，其他失败保持为错误，不再启动另一个工具。只有缺少 Portal 服务／方法时才回退到 PATH 中已安装的 `gnome-screenshot --area --file <文件>` 或 `spectacle --background --region --nonotify --output <文件>`。不安装工具，不调用 GNOME Shell 私有 Eval，不读取 X11 根窗口，也不绕过 Wayland 权限。[Screenshot 接口](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.Screenshot.html)、[Request 取消和响应](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.Request.html)、[Ubuntu 的 GNOME Screenshot 手册](https://manpages.ubuntu.com/manpages/jammy/man1/gnome-screenshot.1.html)、[Spectacle 官方命令定义](https://github.com/KDE/spectacle/blob/master/src/CommandLineOptions.h)。

Portal 使用 Avalonia 已传递依赖的 `Tmds.DBus.Protocol 0.94.1`；代码核对该版本 XML，使用 `DBusConnection`、`ValueTask` API，独立连接随请求释放。[库的协议 API 文档](https://github.com/tmds/Tmds.DBus/blob/main/docs/protocol.md)。关闭应用或取消时关闭自己的 Portal Request；外部命令最多等待三分钟，取消时仅终止本次创建的进程树。

截图先存到 `PreferenceStore.DirectoryPath/screen-pin-temp/capture-<随机 UUID>.png`。Portal 的 `file:` URI 是借用来源，只读取并复制到自己的文件；不会删除或覆盖 Portal／用户文件。只接受本地 PNG，解码前检查签名和尺寸，限制 64 MB、32768 单边像素和 4800 万总像素。生成的临时文件在解码或失败后按确切路径清理，不递归扫描其他历史文件。

截图、打开图片和解码共用一个进行中的操作；页面或语言／主题重建不会再启动第二个选择器。“取消截图”和“关闭全部钉屏”也会取消尚未显示的图片，忽略并释放迟到结果。图片先读取成有大小上限的字节快照，再验证和解码同一份内容，避免验证后重新打开已被替换的文件。

钉屏窗口支持拖动图片或标题移动、滚轮和加减按钮缩放、右键适合窗口／原始大小、20%–100% 透明度、Esc 关闭，以及另存 PNG。截图缩放不改变窗口位置或窗口尺寸，较大图像通过滚动查看；透明度和预览缩放不改变保存的原始像素。窗口申请置顶，但 Wayland 的置顶、移动和绝对位置由合成器决定；预览不承诺绕过这一限制。可直接打开本地 PNG 作为系统截图接口缺失时的入口。

另存 PNG 只接受新的本地文件名，不覆盖原图、现有文件或符号链接。后台编码完成后，通过同目录临时文件和原生不覆盖操作发布完整 PNG；普通提交前失败或取消清理已识别的自有临时文件，无法确认结果则保留证据并提示实际核对路径。窗口关闭后的诊断也包含这些路径。保存选择器未返回时关闭钉屏不会继续写入；菜单、按钮和快捷键不能重复排队保存。详见 [新文件保存行为](NEW-FILE-SAVING.md#中文)。长提示在小窗口底部滚动查看，不把操作按钮挤出窗口；新增双语／主题／尺寸／模拟 DPI 检查保留完整恢复路径。

`ScreenPinTests.Run()` 覆盖真实生产 PNG 校验／临时所有权流程、取消和权限失败不回退、模拟命令输出的损坏处理、安全参数传递、缩放透明度，以及中英文、明暗主题和 250/320/800 点窗口布局。测试模式不会触发系统截图或权限弹窗。目标机待测：首次授权、拒绝、Esc、Portal 选区、多屏／Retina、GNOME／KDE、Wayland 拖动与置顶、长截图缩放、保存选择器、关闭应用时取消及临时文件清理。
