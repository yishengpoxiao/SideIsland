# 跨平台通知中心 / Cross-platform notification center

通知中心合并 Side Island 内部提醒与用户主动连接的真实来源。收集开关默认关闭，重启后也不会自行恢复采集；开启系统通知来源前，应确认愿意让灵动岛读取该来源中的通知内容。不会申请提权、修改系统 D-Bus 策略、安装扩展、读取微信/QQ 数据库或绕过隐藏通知预览设置。

The center combines internal reminders with explicitly enabled platform inputs. Capture starts disabled and is not automatically restored on restart. This is not Windows-style universal notification access: macOS and Linux have different permission and delivery boundaries.

## 已实现的中心功能

- 每应用禁用、普通/重要/最高三档优先级；重要筛选包含后两档。
- 全部、未读、重要筛选，搜索，单条/全部已读，清空消息。
- 同来源的相同标题和正文在 30 秒内合并，保留次数。带原生 ID 的更新替换原条目；同一 ID 同内容重复采样不会重新变成未读。清空保留应用规则，且仍在屏幕上的已清空卡片不会马上复活。
- 最多 200 条消息、512 条应用规则；标题 512 个 UTF-16 单元、正文 6000 单元，截断保留完整代理对。正文只显示纯文本。
- 应用规则会保存在用户配置目录的 `notifications.json`。**默认不保存消息正文**；主动开启“保存通知历史”后，当前消息内容会以明文写入本机配置文件。关闭保存会重写状态文件以移除正文；如写入失败会明确显示错误，并保留原开关状态。没有上传通知内容的功能。
- 保存采用同目录临时文件、刷新和替换，状态读取上限 1 MiB。无法读取的旧文件在下一次成功保存前备份为 `.unreadable-*.bak`，避免静默丢弃诊断证据。无法保存时消息继续留在内存，界面显示 `LastError`。

Application rules persist locally; message history is opt-in, local plaintext. Disable history to remove message contents from the normal state file. Storage failures are reported instead of pretending the change was saved. A recovery backup may still contain previously saved history and should be handled as private data.

## macOS：可见通知的辅助功能桥

在工具 → 通知汇集里主动启用 macOS 辅助功能来源。若没有权限，请手动到“系统设置 → 隐私与安全性 → 辅助功能”允许**当前安装的 Side Island 应用**，再回来启用。应用只调用 `AXIsProcessTrusted` 检查现状，不会自动打开授权框或更改权限。

桥使用公开的 `AXUIElement` API，仅读取 `NotificationCenter` 进程的屏幕可见窗口，采样可识别通知卡片中的可访问文本；窗口几何通过公开的 `CGWindowListCopyWindowInfo` 交叉检查。每 1.5 秒采样一次，树深度、节点数、原生调用超时和每轮时间都有上限。辅助功能超时只影响本应用进程。

**覆盖边界：**仅可能得到正在显示的横幅，或用户打开通知中心后可见、且当前 macOS 版本暴露给辅助功能的卡片。不会主动打开通知中心。隐藏正文、未显示的后台历史、其他桌面空间、系统不暴露的 AX 节点和无法识别的版本布局均不保证读取。来源名称取自可见卡片，属于尽力识别，不能保证等同应用 bundle ID；系统语言/布局改变可能改变分组键。无法识别时显示明确状态，不生成虚构消息。

macOS uses public Accessibility APIs to sample recognizable **on-screen** Notification Center cards after explicit enablement and a manual permission grant. There is no supported promise to read all other apps' background notification history. Changes to Apple's Accessibility hierarchy can reduce coverage; an unsupported visible layout is reported.

Apple's `UNUserNotificationCenter.getDeliveredNotifications` is scoped to the calling app's delivered notifications, so it is not used as a universal reader. No protected notification database, private API, AppleScript UI permission bypass, screen capture, or global input hook is used.

## Linux / Ubuntu：会话 D-Bus 桥

主动启用后，启动本应用拥有的 `dbus-monitor --session --monitor` 子进程，匹配 `org.freedesktop.Notifications.Notify` 调用。只消费匹配的通知调用，不抢占 `org.freedesktop.Notifications` 服务，不替换 GNOME/KDE 通知守护进程。解析器按顶层参数边界处理消息，嵌套 `image-data` 中的整数不会误判为通知结束；`desktop-entry` 优先作为稳定应用标识。

**覆盖边界：**仅能看到启用之后、当前登录会话总线允许监视的标准通知调用。总线实现/策略可能拒绝 `BecomeMonitor` 或隐藏消息；没有安装 `dbus-monitor`、进程启动失败和监视退出均显示状态，不会自动安装或修改策略。通过其他桌面私有通道发送的通知和先前历史不保证覆盖。当前文本适配不获取服务器 `Notify` 回复，因此首次调用没有服务端 ID；后续 `replaces_id` 按其已有值尽力更新，同内容短时合并仍生效。不会执行通知 actions 或从通知中的路径加载图片。

Linux monitors only future standard `Notify` calls allowed by the session bus. The helper is owned by this app and is stopped when disabled or closed. Missing tools or restricted policies are surfaced; no package installation or policy weakening occurs.

## 可选的本机 HTTP 输入 / Optional localhost feed

适用于用户自己维护的 GNOME 扩展、自动化脚本或其他明确授权的桥。此版本提供真实 HTTP 接入，**没有安装或附带 GNOME 扩展**。接入者必须自行获得其平台的合法通知来源，不能把这个输入端口当成全局读取权限。

1. 主动开启“本机通知桥”。UI 显示实际 URL 与令牌；仅绑定 `127.0.0.1`。端口 `0` 表示系统选择空闲端口。
2. 从 UI 复制 URL 和 token 到你信任的本机发送程序。令牌只保存在内存；暂停、重新开启或退出后旧令牌失效。不要在网页中公开、截图分享或放入仓库。
3. POST JSON（UTF-8），带 `Authorization: Bearer <token>` 和 `Content-Type: application/json`。`sourceId` 必填且最长 150 字符；`sourceName/title/body/nativeId` 可选，但标题和正文不能同时为空。HTTP 来源自动加 `http:` 前缀，与其他来源隔离。

Example using synthetic content only; replace URL and token with the current values shown in the app:

```sh
curl --request POST 'http://127.0.0.1:YOUR_PORT/notifications' \
  --header 'Authorization: Bearer YOUR_CURRENT_TOKEN' \
  --header 'Content-Type: application/json' \
  --data '{"sourceId":"my-script","sourceName":"My script","title":"Synthetic task complete","body":"Generated example, no personal messages","nativeId":"task-123"}'
```

状态 200 的 `accepted` 表示已加入中心；`ignored by source policy or duplicate rules` 表示禁用来源、重复或空内容等规则拒绝显示，未绕过应用禁用设置。缺少/错误令牌为 401，不合法请求为 400。限制 8 KiB 请求头、16000 字节正文、4 个并发连接和 4 秒请求期限；仅接受精确本机 Host、POST 固定路径、JSON MIME、单一 Content-Length。带 Origin 的网页请求、分块传输、重复请求头均拒绝；不提供 CORS。关闭桥会关闭正在接收的连接。

The local feed is an authenticated transport, not an OS notification permission. Bearer tokens are memory-only, rotated on every enable, and invalidated on disable. Web-origin requests are rejected. Any process already running as the same local user can generally inspect that user's apps; the token is intended to prevent accidental or unauthenticated submissions, not to isolate a compromised user account.

## 验证范围 / Verification boundary

`SideIsland.Tests/NotificationTests.cs` 覆盖中心规则、未读、去重、优先级、持久化/损坏状态恢复、UTF-16 边界和含嵌套图片 hint 的 D-Bus **fixture**。另通过真实 `127.0.0.1` TCP/HTTP 往返发送明确标记为 synthetic 的生成消息，检查正确/缺失令牌、禁用应用、网页 Origin、大正文、坏 JSON、暂停、重启和旧令牌失效。测试不启用 AX、不启动 D-Bus 监视器、不读取用户消息。

这些结果不能代替目标 macOS 版本/Ubuntu 桌面的真实通知实测。目标平台应由用户明确启用来源后，使用专门生成的通知验证可见性、手动授权、关闭桥和布局；不可把 parser fixture 或 localhost feed 通过写成 macOS/Ubuntu 系统通知全覆盖。

## 公开接口依据 / Primary references

- [Apple: AXIsProcessTrusted](https://developer.apple.com/documentation/applicationservices/1459186-axisprocesstrusted)
- [Apple: AXUIElementCopyAttributeValue](https://developer.apple.com/documentation/applicationservices/1462085-axuielementcopyattributevalue)
- [Apple: AXUIElementSetMessagingTimeout](https://developer.apple.com/documentation/applicationservices/1459345-axuielementsetmessagingtimeout)
- [Apple: CGWindowListCopyWindowInfo](https://developer.apple.com/documentation/coregraphics/1455137-cgwindowlistcopywindowinfo)
- [Apple: getDeliveredNotifications](https://developer.apple.com/documentation/usernotifications/unusernotificationcenter/getdeliverednotifications(completionhandler:))
- [D-Bus: dbus-monitor](https://dbus.freedesktop.org/doc/dbus-monitor.1.html)
- [D-Bus specification: Monitoring](https://dbus.freedesktop.org/doc/dbus-specification.html#bus-messages-become-monitor)
- [freedesktop.org: Desktop Notifications specification](https://specifications.freedesktop.org/notification/latest-single/)
