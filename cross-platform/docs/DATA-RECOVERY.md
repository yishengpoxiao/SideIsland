# Local data and recovery

English | [简体中文](#简体中文)

The development preview adds **Data status**, also accessible from Settings. Preferences and notification state have separate cards showing their local paths, unsaved changes, read failures and conflicts. These changes are not in the published v1.0.0 downloads.

| Action | What it does |
| --- | --- |
| Retry save | Saves the current in-memory edits after a temporary failure. An external change or unreadable original must be resolved first. |
| Back up edits & reload | Preserves unsaved preferences or notification rules in a unique draft file, then reads the current disk version. Failed backup or reload keeps the in-memory edits. |
| Back up damaged file & save edits | Explicitly replaces a damaged file only after preserving the exact displaced original. Available only when the original bytes were readable and can still be identified. |

Notification reload requires confirmation because memory-only messages are not written to a draft backup. Drafts include message content only when history recording actually took effect and has not been switched off. A failed request to enable history stays pending without activating recording; a failed request to disable stops future recording but reports that old content may remain in the active file. Existing recovery copies are retained and may contain earlier content. Turning history off does not delete those copies.

Disk operations from this page run in the background. Edits made while an operation is pending remain in memory, and a late reload does not replace them. A completed publication is distinguished from an unconfirmed outcome; the page provides available recovery paths. A successful preference reload invalidates old media and quota requests before applying the loaded connections.

The stores compare file identity, length, timestamp and complete content against the loaded version. Windows replacement preserves the displaced file atomically; Mac/Linux use the shared native exchange/no-replace operations. A late external edit can be displaced during the atomic operation, in which case its exact content is retained for inspection. This is not isolation against hostile same-directory writers. Unsupported filesystem operations fail without a weaker overwrite fallback.

Normal, fully verified saves remove their temporary previous-version copies. Damaged originals, draft rescues, concurrent versions and unconfirmed outcomes are retained. If cleanup is blocked, diagnostics identify confirmed retained files. A leftover `.lock` prevents another save; it is not automatically assumed stale. Do not delete recovery files as build output. Preferences and active notification state are bounded to 1 MiB each; a notification rescue draft can be larger and is not loaded automatically.

Windows-hosted regression covers real owned files and sharing failures, controlled commit races, delayed UI operations, language/theme changes and simulated DPI. It does not establish native Mac/Ubuntu filesystem, window-manager or permission behavior.

## 简体中文

开发预览版新增**数据状态**，也可从设置进入。偏好设置与通知数据分别显示本机路径、未保存修改、读取失败和外部冲突。这些改动尚未进入已发布的 v1.0.0 下载包。

| 操作 | 行为 |
| --- | --- |
| 重试保存 | 临时故障恢复后保存当前修改。外部冲突或无法读取的原文件需要先处理。 |
| 备份修改并重载 | 将未保存的设置或通知规则另存为唯一草稿，再读取磁盘版本。备份或读取失败时保留当前编辑。 |
| 备份坏文件并保存当前修改 | 明确恢复损坏文件，保留实际被替换原件的完整字节；仅在原字节可读取、身份仍可确认时可用。 |

通知重载需要确认：仅保存在内存的消息不会写入草稿。只有历史记录实际启用、且没有待生效的关闭请求时，草稿才包含正文。开启失败不会激活正文记录；关闭失败会停止继续记录，但提示当前磁盘文件可能仍含旧正文。已有恢复副本继续保留，关闭历史不会自动删除这些副本。

页面中的磁盘操作在后台进行。期间新增编辑继续保留，迟到的重载结果不会覆盖它们。已确认保存与结果未能确认会分别报告，并显示可用的恢复路径。设置重载成功后会使旧媒体／额度请求失效，再应用加载的连接设置。

保存会比对加载时的文件身份、长度、时间与完整内容。Windows 使用原子替换，Mac／Linux 复用原生交换和不覆盖操作。极晚发生的外部修改可能在原子操作时被替换，但其完整内容会保留为副本供核对；这不能隔离恶意同目录写入者。不支持的文件系统明确失败，不退回较弱的覆盖流程。

普通成功保存会清理已完全核验的旧版本临时副本；损坏原件、编辑草稿、并发版本和未知结果继续保留。清理被阻挡时，诊断记录给出已确认的保留路径。残留 `.lock` 会阻止新保存，不会被自动当作过期锁删除。恢复文件不属于构建垃圾。设置和通知活动文件各限 1 MiB；通知救援草稿可以更大，不会自动加载。

Windows 主机验证覆盖自有真实文件和共享锁、可控提交竞态、迟到界面操作、中英文／明暗切换与模拟 DPI；不能代替 Mac／Ubuntu 原生文件系统、桌面和权限实测。
