# Unreleased improvements

[English README](../README.md) · [中文 README](../README.zh-CN.md)

These are development changes after v1.0.0. The published v1.0.0 archives have not been replaced.

## Port reports and diff export

Windows port lookup now queues the latest explicit query when a previous read is in progress. Editing the input invalidates the previous snapshot, and closing the tool prevents late results from appearing. **Copy full report** includes the queried ports, capture time, every grouped owner/PID/path/state and protocol read failures. The report includes records beyond the 200-card display limit; a report exceeding 1 Mi characters is rejected explicitly rather than truncated. Missing results after a partial read are labelled unknown.

Text comparison on Windows and the preview can copy or save a complete UTF-8 `.patch`, using three lines of context and preserving missing-final-newline information. Export is tied to the current comparison and remains unavailable while inputs change. The patch targets `text.txt` and normalizes CR/CRLF line endings to LF; it does not apply changes or modify either input. Output is not limited by the on-screen preview. Saving uses a temporary file and replacement so a failed write does not truncate an existing patch. See the [Git patch-format background](https://git-scm.com/docs/diff-generate-patch).

The preview also invalidates in-flight media/quota requests when disconnected, reconnected or closed. Older replies and errors cannot restore a disconnected session or replace a newer result. Text drafts are saved on the property change instead of a deferred UI event, preserving same-turn edits during navigation and closing.

## File verification

The checksum tool accepts a raw SHA-256 / SHA-512 value or a GNU/BSD checksum list. Paste a release's `SHA256SUMS` or `SHA512SUMS` text and select the downloaded file. Matching uses the exact, case-sensitive filename; different paths with the same filename or conflicting values require an unambiguous entry. Filenames in the list are never opened. Lists are limited to 128 Ki characters and 1,024 lines; GNU escaped filenames are not supported.

## Correctness fixes

- Log tracking distinguishes historical unfinished lines from new content. An observed empty file's first error is reported; later completion of an already matched line does not repeat its alert.
- Preview preferences can round-trip maximum-length Chinese and emoji notes without resetting settings. Oversized saves retain the previous file. An unreadable preference file is preserved as a local `.unreadable-*.bak` before a recovery save.
- The Windows Avalonia preview uses a distinct `SideIsland-Preview-v<version>-win-x64.zip` filename. Preview packaging refuses existing archives and their checksum/manifest sidecars.

Native macOS and Ubuntu execution still requires target-device validation. Automated packaging fixtures and headless rendering do not establish native compatibility.

## Development validation (2026-11-06)

- The second improvement batch passed ten affected Windows suites, including the complete tool-page and settings/notification layout matrices, theme checks, tool transitions and mode workload. Independent verification of the actual diff exporter applied 299 generated patches with Git and compared the resulting file bytes; all passed. Two unchanged cases returned no patch. Port report/queue checks cover 250 owners, partial protocol failures, Unicode paths, cancellation and disposal.
- The second batch then passed two consecutive full preview rounds with unchanged source hashes: 4,032 general tool layouts, 192 populated checksum layouts and 48,768 action-caption checks. Controlled delayed providers reproduce the media/quota races; same-turn edit/navigation/close tests cover draft and rename-plan invalidation. These are deterministic integration tests, not fresh real-player or native Mac/Ubuntu tests.
- The initial continuous Windows run passed 77 of 87 suites, then stopped at `AnimationFrameTests`: Windows `LockApp` covered both native alpha hit-test points. It was an assertion failure, not a native process crash; no automatic retry was used.
- After correcting additional checksum parser boundaries, 13 Windows suites passed: the checksum, log, appearance and full tool-page checks, plus the nine suites after the blocked alpha check. This covers 86 distinct suites across the two phases, **not a successful continuous 87-suite run**. Native alpha validation remains outstanding until the desktop is unlocked.
- The first batch's cross-platform suite passed two consecutive rounds with unchanged source hashes: 4,032 general tool layouts, 192 populated checksum layouts and 48,384 general action-caption checks. The new real-file tests exercise cancellation, input/page/language/theme changes, and stale-result rejection.
- Twenty isolated packaging cases passed; existing published archive hashes remained unchanged. These cases use synthetic native headers to exercise packaging policy, not executable compatibility.
- Full logs, first-failure evidence and source hashes remain in ignored local diagnostic directories. Generated executables and packages are not included in source commits.

## 中文说明

这些是 v1.0.0 之后的开发改动，已发布的 v1.0.0 安装包保持不变。

新增 Windows 端口“复制完整报告”：记录查询端口、采集时间、全部占用程序／PID／路径／状态及读取错误，不受界面 200 张卡片限制；超出 1 Mi 字符会明确提示。连续快速查询会保留最后一次请求，避免页面被旧查询清空；部分协议读取失败时不会把未发现的记录误报为空闲。

Windows 和跨平台预览版的文字对照支持复制、保存完整 UTF-8 补丁，保留末尾换行差异，超过预览限制的行也会完整导出。补丁目标名为 `text.txt`，统一使用 LF 换行；不会自动应用或修改原文。输入变化后旧补丁立即失效。实际导出的 299 个补丁已通过 Git 应用及结果字节验证；第二批 Windows 改动通过 10 套相关回归。

跨平台版还修复了断开连接后旧音乐／额度结果回写的问题，并改为在文字属性变化时立即保存草稿，防止快速切页、关闭时丢失最后一次编辑。

第二批改动的跨平台最终连续两轮回归通过：4,032 个常规工具布局、192 个填充内容的校验页布局、48,768 项按钮文字检查，源码哈希未变。异步回写使用可控制返回顺序的测试源验证，不等同于新的播放器实测或 Mac／Ubuntu 实机验证。

文件校验支持粘贴单个 SHA-256 / SHA-512 值，或 GNU/BSD 格式的整份校验清单，再按所选下载文件的名称匹配。文件名区分大小写；不同目录的同名文件或相互冲突的校验值需要先消除歧义。程序只解析文字，不读取清单中的路径。最多支持 128 Ki 字符、1,024 行，暂不支持 GNU 转义文件名。

日志守望修复了历史末行误报、空文件首次追加漏报，以及同一行后续追加重复提醒。跨平台预览版修复了长中文／emoji 笔记导致重启后设置重置的问题；超大保存会保留原文件，无法读取的设置会在恢复保存前备份。Windows Avalonia 预览包采用独立文件名，打包时拒绝覆盖现有安装包及校验记录。

Mac 和 Ubuntu 仍需实机验证；打包测试和无界面布局测试不能证明原生系统兼容性。

验证记录（2026-11-06）：Windows 首次连续回归通过前 77 套，在第 78 套被 LockApp 遮挡原生透明区域测试点后停止，没有自动复跑，也没有原生崩溃。后续校验解析边界修正后，13 套专项与剩余功能测试通过；两阶段合计覆盖 86 个套件，**不能等同于一次连续 87 套全部通过**，原生透明区域仍待解锁后补测。跨平台最终连续两轮回归、20 项隔离打包测试通过。完整日志、首次失败证据和源码哈希保留本机。
