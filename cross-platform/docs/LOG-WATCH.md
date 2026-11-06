# Log watch / 日志追踪

Development feature after v1.0.0; not included in the published v1.0.0 preview archives.

Choose a local file, or type an absolute path to a file that will be created later, then click **Start / restart**. Editing the path does not switch the running watch until you start again. Every restart reads the current tail as history. Stop retains the last lines; Clear view discards the displayed history without modifying the source file. A stopped watch does not resume automatically on app launch.

The app polls every second while running, including on another page or while collapsed. One background read is allowed at a time. Slow system reads are not forcibly aborted; stopping, restarting, changing keywords, clearing or closing invalidates old results, and a replacement read waits for the occupied worker to return. The UI never waits synchronously for that filesystem operation.

UTF-8 (with or without BOM) and BOM-marked UTF-16 / UTF-32 are supported. File identity and boundary bytes detect replacements and truncation. Without a BOM the file is treated as UTF-8; legacy ANSI encodings are not auto-detected. Unix paths retain meaningful whitespace.

- Initial tail: at most 64 KiB; at most 64 KiB is read per poll. A backlog over 512 KiB skips to the recent tail.
- Retained text: at most 1,000 lines, 4,000 characters per line and 512 Ki characters overall. A partial first line is skipped when starting within a large file.
- Keywords: at most 20 terms of 100 characters each, case-insensitive, separated by commas, semicolons or newlines. Invalid edits leave the previous valid settings active.
- Alerts: only newly read matching content, at most once every 10 seconds; historical tail after startup, rotation or truncation does not alert. This is a bounded monitor, not a lossless log or alert delivery service.
- Filter only changes displayed/copyable lines; it does not change the keyword rules. Follow latest can be disabled while inspecting earlier retained content.

**Save shown** exports all filtered, retained lines displayed in the text area as a new UTF-8 file without a BOM. It captures the text when clicked, so new log lines arriving while the picker is open do not change that snapshot. It does not export the whole source file, and does not replace existing files. The source path remains protected even if the source disappears before saving. Line breaks match the displayed text.

Editing the path, filter or keywords, clearing/restarting/stopping, rebuilding the page or closing cancels any export not yet committed. A delayed picker cannot save an obsolete selection. If publication has already completed, the app reports the earlier saved snapshot instead of calling it cancelled. Unknown save results retain their destination/recovery paths in the local diagnostic log; inspect them before retrying. [New-file publication behavior](NEW-FILE-SAVING.md).

Alerts use the local Side Island notification source and respect its disabled setting. Notification body history is saved only when explicitly enabled in notification settings. Log contents and the active target are held in memory unless you explicitly copy or save a snapshot; starting a watch does not upload the file or its contents.

Windows-hosted tests exercise real temporary files, encodings, rotation/truncation, bounded reads and controlled delayed callbacks; actual Avalonia controls are exercised across languages, themes, sizes and headless DPI. Native Mac / Ubuntu filesystem, permission and window-manager verification remains outstanding.

## 中文

这是 v1.0.0 之后的开发功能，已发布的预览安装包尚未包含。

选择本机文件或输入未来文件的绝对路径，再点击“开始 / 重新开始”。修改路径不会直接切换当前追踪；重新开始会把当前末尾视作历史。停止后保留最后内容，清除视图不会改动文件；重新启动应用不会自动恢复读取。

应用运行时每秒检查，收起或切换页面后仍继续。最多同时执行一个后台读取，慢系统调用返回前不会创建新的读取线程；停止、重启、改关键词、清除和关闭使旧结果失效，界面不等待磁盘读取完成。

支持 UTF-8，以及有 BOM 的 UTF-16／UTF-32；无 BOM 时按 UTF-8 读取，不自动判断旧式 ANSI 编码。平台文件身份与边界字节用于检测文件替换、截断；Unix 路径保留有意义的空格。

首次最多读末尾 64 KiB，每次最多读 64 KiB，积压超过 512 KiB 时跳到最近末尾。保留最多 1,000 行、每行 4,000 字符、总计 512 Ki 字符；从文件中间开始时跳过首个不完整行。最多 20 个关键词、每词 100 字符，不区分大小写，可用逗号、分号或换行分隔；无效输入保持上一次有效设置。

只对新读取的匹配内容提醒，每 10 秒最多一次；启动、轮换或截断后的历史不提醒。筛选只影响显示和复制，跟随最新可关闭。此功能有明确容量和提醒频率限制，不保证日志或提醒完整保留。

“另存显示内容”把点击时文本区全部筛选后的保留内容写为新的 UTF-8 无 BOM 文件，换行与显示内容一致；不导出整份源文件，也不覆盖已有文件。选择位置期间新增日志不会改变已捕获快照；源文件路径即使随后消失仍受保护。

修改路径、筛选或关键词，清除、重新开始、停止、页面重建或关闭，会取消尚未提交的导出；迟到的选择器不能保存旧选择。如果提交已经完成，界面明确提示先前快照已保存，不会误报取消。无法确认结果时，本机诊断日志保留目标和恢复路径，先核对再重试。详见 [新文件保存行为](NEW-FILE-SAVING.md#中文)。

提醒使用岛内 Side Island 通知来源，遵守禁用规则；只有主动开启通知正文历史后才保存提醒正文。除主动复制／另存快照外，当前日志内容和目标仅在内存中，不上传。真实 Mac／Ubuntu 权限、文件系统和桌面行为仍需目标机器验证。
