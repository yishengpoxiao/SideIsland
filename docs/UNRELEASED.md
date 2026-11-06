# Unreleased improvements

[English README](../README.md) · [中文 README](../README.zh-CN.md)

These are development changes after v1.0.0. The published v1.0.0 archives have not been replaced.

## Portable log watch and asynchronous tool fixes

The preview now includes a log watch tool: explicitly start a local file tail, including a future path, follow or filter retained lines, copy the displayed content, clear the view and stop. New keyword matches produce local notifications subject to app exclusions; initial history is not replayed. The bounded reader supports UTF-8 and BOM-marked UTF-16/UTF-32, replacement/truncation detection and a single background worker. Slow OS reads cannot accumulate workers, and obsolete callbacks cannot update a stopped, restarted or closed session. Native Mac/Ubuntu filesystem validation remains outstanding. See [limits and behavior](../cross-platform/docs/LOG-WATCH.md).

Delayed clipboard reads no longer overwrite manual edits or resurrect detached/closed drafts. Editing QR input invalidates the old generated code; delayed save pickers cannot save obsolete results. QR and screenshot exports create a new PNG without replacing an existing file. Notification search is captured synchronously, preserving same-turn navigation edits. Screenshot operations retain one shared pending state through capture, decoding and pin handoff, including cancellation and page reconstruction.

The Windows/shared log reader now waits for a fragmented BOM before choosing an encoding, fixing garbled text and missed keyword alerts when a new file's header is written in pieces. The portable controller also handles clearing before its initial read without redisplaying historical lines. Invalid incremental keyword input stays in the editor with localized feedback instead of escaping its change handler.

Validation for this fifth batch: seven related Windows suites passed with unchanged inputs and no automatic retry. Two consecutive complete preview rounds passed with unchanged source hashes and zero build warnings: 4,224 general tool layouts, 57,792 general action-caption checks, and 192 populated log layouts, in addition to existing populated checksum/export/port/watch matrices. Each round includes 174 log backend checks, 3,295 log UI assertions, 16 clipboard/QR/search scenarios, 67 new screenshot lifecycle/publication checks and 198 existing screenshot behavior/layout checks. Actual rendered English light and Chinese dark log pages were inspected. First failures, controlled reproducers, assembly/source hashes and final logs are retained locally. Native Windows alpha-hit testing remains blocked by the lock screen; Mac/Ubuntu native validation remains outstanding.

## Checksum export and background controls

Windows and the preview can now copy or save a calculated SHA-256 / SHA-512 checksum list. Exports use the GNU binary format, contain only the basename and digest, and save UTF-8 without a BOM using LF line endings. Existing files, directories and symlinks are never replaced. A save picker returning after a changed calculation, navigation or closure cannot export an obsolete result. Disk publication runs in the background; duplicate save requests are suppressed. A native filename that cannot be represented faithfully in UTF-8 still supports hashing and copying the digest, with manifest export unavailable.

The parser now supports GNU escaped filenames (`\\`, `\n`, `\r`) while retaining exact filename matching and rejecting malformed escape sequences. See the [GNU checksum output format](https://www.gnu.org/software/coreutils/manual/html_node/cksum-output-modes.html). File-name Unicode and whitespace are not normalized or silently replaced.

The preview's keep-awake countdown now uses the actual helper launch deadline. Stop/replace/close operations invalidate older replies, and closing the service cancels queued operations. Volume reads cannot overwrite a newer slider/mute edit; one write can run across page reconstruction without accumulating obsolete writes. Batch image output and collision suffixes now fit both UTF-8 byte and UTF-16 length limits without splitting Unicode characters. Path lists retain meaningful trailing spaces on Unix.

## Preview port reports and task watches

The macOS/Linux preview now provides full port reports with capture time, local/peer endpoints, address family, socket state, process IDs and executable paths. Usable records are retained alongside command, permission, parsing or incomplete-output warnings. The UI shows up to 200 cards; copying includes every parsed record, subject to an explicit 1 Mi character report limit. Input changes, cancellation, navigation and newer requests invalidate late replies. A missing record is never presented as a guarantee that a port is free.

Task watches now have separate limits of 24 active targets and 24 finished records. Finished watches no longer exhaust the active limit. Users can cancel a watch, remove a record or clear finished history. File/process probes run in the background with at most eight concurrent checks; slow probes do not delay other completed targets. A stuck system call retains its slot until it returns, and the UI shows when other checks are queued. Only one initial target check can run at a time, including after navigation. Duplicate active targets and already existing files are rejected; process watches retain the original process identity so PID reuse completes the old watch, while inaccessible identity data is retried. Completed cards do not claim notification delivery when notifications may be disabled. Closing the application stops watching; file appearance does not establish that writing is complete.

## Port reports and diff export

Windows port lookup now queues the latest explicit query when a previous read is in progress. Editing the input invalidates the previous snapshot, and closing the tool prevents late results from appearing. **Copy full report** includes the queried ports, capture time, every grouped owner/PID/path/state and protocol read failures. The report includes records beyond the 200-card display limit; a report exceeding 1 Mi characters is rejected explicitly rather than truncated. Missing results after a partial read are labelled unknown.

Text comparison on Windows and the preview can copy or save a complete UTF-8 `.patch`, using three lines of context and preserving missing-final-newline information. Export is tied to the current comparison and remains unavailable while inputs change. The patch targets `text.txt` and normalizes CR/CRLF line endings to LF; it does not apply changes or modify either input. Output is not limited by the on-screen preview. Saving uses a temporary file and replacement so a failed write does not truncate an existing patch. See the [Git patch-format background](https://git-scm.com/docs/diff-generate-patch).

The preview also invalidates in-flight media/quota requests when disconnected, reconnected or closed. Older replies and errors cannot restore a disconnected session or replace a newer result. Text drafts are saved on the property change instead of a deferred UI event, preserving same-turn edits during navigation and closing.

## File verification

The checksum tool accepts a raw SHA-256 / SHA-512 value or a GNU/BSD checksum list. Paste a release's `SHA256SUMS` or `SHA512SUMS` text and select the downloaded file. Matching uses the exact, case-sensitive filename; different paths with the same filename or conflicting values require an unambiguous entry. Filenames in the list are never opened. Lists are limited to 128 Ki characters and 1,024 lines, including escaped filename text.

## Correctness fixes

- Windows settings recovery preserves an unreadable original when saving unchanged fallback defaults. A changed recovery save atomically retains the displaced bytes in a unique `.unreadable-*.bak`; failed writes keep recovery pending. Diagnostics expose only the recovery-pending flag.
- Windows compiler diagnostics use UTF-8 output, retaining Chinese error text in both PowerShell 7 and Windows PowerShell 5.1 logs.
- Log tracking distinguishes historical unfinished lines from new content. An observed empty file's first error is reported; later completion of an already matched line does not repeat its alert.
- Preview preferences can round-trip maximum-length Chinese and emoji notes without resetting settings. Oversized saves retain the previous file. An unreadable preference file is preserved as a local `.unreadable-*.bak` before a recovery save.
- The Windows Avalonia preview uses a distinct `SideIsland-Preview-v<version>-win-x64.zip` filename. Preview packaging refuses existing archives and their checksum/manifest sidecars.

Native macOS and Ubuntu execution still requires target-device validation. Automated packaging fixtures and headless rendering do not establish native compatibility.

## Development validation (2026-11-06)

- The fourth batch passed seven affected Windows suites with unchanged source hashes, including 182 export checks, actual GNU `sha256sum`/`sha512sum` output and `--check --strict` interoperability, and a real filesystem filename that cannot be exported as strict UTF-8. Controlled blocked writes proved that the UI remains responsive; earlier dispatcher-blocking evidence is retained.
- Two complete preview rounds then passed with unchanged source hashes and no compiler warnings: 4,032 general layouts, 192 populated checksum comparison layouts, 192 successful checksum export layouts, 192 port layouts, 24 watch layouts and 49,920 general action-caption checks. Saved headless light-English and dark-Chinese renderings were visually reviewed. The appearance fixtures now invoke actual header actions and assert shell background, navigation text/colors and framework theme; changing preferences alone in some earlier populated fixtures did not establish a full shell transition.
- Batch checks passed 64 Core / 91 cumulative Core-and-image cases using real owned Windows files and Avalonia codecs. Unix trailing-space filename execution remains conditional on running the suite on Unix. Keep-awake tests launched only owned inert helper processes and compared their actual start times with deadlines; controlled volume/awake UI providers reproduced and verified the three original ordering/deadline failures. Native system volume and sleep policies were not changed.
- The third batch passed nine affected Windows suites with unchanged source hashes, including 46 settings recovery checks and the settings/notification and tool-page layout matrices. Chinese compiler diagnostics were independently verified in PowerShell 7 and Windows PowerShell 5.1. Cross-platform targeted checks cover real owned helper-process success/failure/output-limit/timeout/cancellation, delayed port replies, 48 watch-model checks, blocked-probe fairness and bounds, and real UI add/remove/clear/close races. Clearing history cannot recreate cleared activities through late callbacks.
- The third batch passed two consecutive complete preview rounds with unchanged source hashes and no compiler warnings: 4,032 general tool layouts, 192 populated checksum layouts, 192 populated port layouts, 24 populated watch layouts and 49,536 general action-caption checks. The general/port/checksum matrices cover English/Chinese, light/dark themes, three widths, two heights and 100/125/150/200% headless scaling. Native Mac/Ubuntu testing and the blocked Windows alpha test remain outstanding.
- The second improvement batch passed ten affected Windows suites, including the complete tool-page and settings/notification layout matrices, theme checks, tool transitions and mode workload. Independent verification of the actual diff exporter applied 299 generated patches with Git and compared the resulting file bytes; all passed. Two unchanged cases returned no patch. Port report/queue checks cover 250 owners, partial protocol failures, Unicode paths, cancellation and disposal.
- The second batch then passed two consecutive full preview rounds with unchanged source hashes: 4,032 general tool layouts, 192 populated checksum layouts and 48,768 action-caption checks. Controlled delayed providers reproduce the media/quota races; same-turn edit/navigation/close tests cover draft and rename-plan invalidation. These are deterministic integration tests, not fresh real-player or native Mac/Ubuntu tests.
- The initial continuous Windows run passed 77 of 87 suites, then stopped at `AnimationFrameTests`: Windows `LockApp` covered both native alpha hit-test points. It was an assertion failure, not a native process crash; no automatic retry was used.
- After correcting additional checksum parser boundaries, 13 Windows suites passed: the checksum, log, appearance and full tool-page checks, plus the nine suites after the blocked alpha check. This covers 86 distinct suites across the two phases, **not a successful continuous 87-suite run**. Native alpha validation remains outstanding until the desktop is unlocked.
- The first batch's cross-platform suite passed two consecutive rounds with unchanged source hashes: 4,032 general tool layouts, 192 populated checksum layouts and 48,384 general action-caption checks. The new real-file tests exercise cancellation, input/page/language/theme changes, and stale-result rejection.
- Twenty isolated packaging cases passed; existing published archive hashes remained unchanged. These cases use synthetic native headers to exercise packaging policy, not executable compatibility.
- Full logs, first-failure evidence and source hashes remain in ignored local diagnostic directories. Generated executables and packages are not included in source commits.

## 中文说明

这些是 v1.0.0 之后的开发改动，已发布的 v1.0.0 安装包保持不变。

跨平台开发版新增日志追踪入口，可主动读取本机日志或等待未来文件，支持跟随、筛选、复制显示内容、清除视图和停止。只有新增关键词匹配才发出本地通知，并遵守禁用规则；历史不补发。有界读取支持 UTF-8 和带 BOM 的 UTF-16／UTF-32、文件替换及截断识别；只允许一个后台读取，慢系统调用不会堆积线程，停止、重启或关闭后拒绝旧结果回写。原生 Mac／Ubuntu 文件系统行为仍待实机验证，详细限制见 [日志追踪](../cross-platform/docs/LOG-WATCH.md)。

修复剪贴板迟到结果覆盖手动输入、恢复旧页面草稿的问题。修改二维码输入后旧码立即失效，迟到的保存对话框不能导出旧内容；二维码及截图另存均不替换已有文件。通知搜索改为同步记录，快速切页不再丢失。截图从捕获、解码到新钉屏窗口移交始终保留唯一进行中状态，切页重建及取消不会启动重复操作。

Windows／共享日志读取器还修复了 BOM 分批写入时过早选择编码、产生乱码和漏报关键词的问题。跨平台控制器修复首次读取前清除视图却重新显示历史的边界；关键词输入超过限制时保留编辑文字、显示本地化提示，不再从属性事件抛出异常。

第五批最终通过 7 套相关 Windows 回归，无自动复跑、输入哈希不变。跨平台连续两轮完整回归通过、编译零警告、源码哈希不变：共 4,224 个常规工具布局、57,792 项常规按钮文字检查、192 个填充日志页布局，以及已有填充校验／导出／端口／守望矩阵。每轮包含 174 项日志后端检查、3,295 项日志 UI 断言、16 个剪贴板／二维码／搜索场景、67 项新增截图生命周期／发布检查和 198 项原有截图行为／布局检查。英文亮色、中文暗色日志渲染图已实际查看。首次失败、复现、源码与程序集哈希及最终日志均保留；Windows 锁屏仍阻挡原生透明点击测试，Mac／Ubuntu 实机验证仍待补齐。

Windows 和跨平台预览版新增校验清单复制／另存功能，支持 SHA-256／SHA-512。清单使用 GNU 二进制格式，只包含文件名与校验值，以 UTF-8 无 BOM、LF 换行保存；不会替换已有文件、文件夹或符号链接。保存对话框返回前若计算、输入、页面或窗口状态已改变，旧结果不会导出。磁盘保存改为后台执行，并阻止重复保存。无法完整编码为 UTF-8 的本机文件名仍可计算和复制校验值，仅禁用清单导出。

清单解析新增 GNU 文件名转义支持（反斜杠、换行与回车），错误转义会被拒绝，不会规范化或悄悄替换文件名。跨平台保持唤醒改为使用辅助进程的实际启动截止时间，迟到的启动结果不能覆盖停止操作；关闭服务会取消排队请求。旧音量读取不能覆盖刚编辑的滑块／静音状态，切页不会堆积重复写入。批量图片的生成名称及重名编号同时遵守 UTF-8 字节和 UTF-16 长度限制，不切断 Unicode 字符，并保留 Unix 路径中有意义的末尾空格。

第四批 Windows 最终通过 7 套相关回归，包含 182 项导出检查、真实 GNU 工具互操作和无法编码为 UTF-8 的真实文件名；阻塞保存测试证明磁盘操作不再挡住界面。跨平台最终连续两轮完整回归通过：4,032 个常规布局、192 个填充校验对照页、192 个已完成的清单导出页、192 个端口页、24 个守望页，以及 49,920 项常规按钮文字检查，源码哈希未变化。测试现在点击实际语言／主题按钮，并验证背景、导航文字与颜色；部分旧填充矩阵只改设置值，不能单独证明整页主题已切换。已实际查看英文亮色与中文暗色渲染图。

批量文件／图片通过 64 项 Core、91 项累计检查；真实 Unix 末尾空格文件测试仍需在 Unix 主机执行。保持唤醒测试只启动自有无副作用的测试辅助进程，音量与唤醒界面使用可控制返回顺序的测试源；没有改变实际系统音量或睡眠策略。修复前失败、首次测试、最终结果和源码哈希均已保留。

Mac／Linux 预览版新增端口完整报告，包含采集时间、本地／远端地址、地址族、状态、PID 和程序路径；命令失败、权限不足、解析失败或输出不完整时保留可用记录并说明问题。界面最多显示 200 张卡片，复制包含全部已解析记录，超过 1 Mi 字符会提示。修改输入、取消、切页或新查询会使旧结果失效；没有记录不能证明端口空闲。

跨平台任务守望现在分别保留最多 24 个正在守望的目标和 24 条完成记录，已完成任务不再占用活动名额；可以取消守望、移除单条记录或清除已完成项。后台最多同时执行 8 项检查，慢路径不会拖住其他目标的完成提醒；未返回的系统检查继续占用名额，排队时会显示提示。新增目标的初次检查一次只运行一项，切页后也不会重复启动。拒绝重复目标和已存在的文件；PID 被复用时原进程视为已退出，身份信息不可读取时继续重试。完成卡片不会在可能禁用通知的情况下声称“已提醒”。退出应用即停止守望；文件出现不等于写入完成。

Windows 设置读取失败后，保存未修改的默认值不会覆盖原文件；修改后的恢复保存会以原子替换方式备份被替换的完整字节，失败时保留恢复状态。诊断只包含是否待恢复的标记。编译器改为输出 UTF-8，已分别验证 PowerShell 7 与 Windows PowerShell 5.1 中的中文错误日志。

第三批 Windows 改动通过 9 套相关回归，包含 46 项设置恢复检查，源码哈希未变化。跨平台专项检查包含真实隔离测试子进程的成功／失败／超时／超限／取消、端口延迟回写、48 项守望模型检查，以及实际控件的新增／移除／清空／关闭竞态；慢路径受并发上限约束，迟到回调不会重新创建已清除的活动历史。

第三批最终连续两轮跨平台完整回归通过，源码哈希未变化、编译零警告：4,032 个常规工具布局、192 个填充校验页布局、192 个填充端口页布局、24 个填充守望页布局，以及 49,536 项常规按钮文字检查。常规工具／端口／校验页矩阵覆盖中英文、明暗主题、三种宽度、两种高度及 100%／125%／150%／200% 无界面渲染缩放；不等同于 Mac／Ubuntu 实机或 Windows 原生透明点击验证。

新增 Windows 端口“复制完整报告”：记录查询端口、采集时间、全部占用程序／PID／路径／状态及读取错误，不受界面 200 张卡片限制；超出 1 Mi 字符会明确提示。连续快速查询会保留最后一次请求，避免页面被旧查询清空；部分协议读取失败时不会把未发现的记录误报为空闲。

Windows 和跨平台预览版的文字对照支持复制、保存完整 UTF-8 补丁，保留末尾换行差异，超过预览限制的行也会完整导出。补丁目标名为 `text.txt`，统一使用 LF 换行；不会自动应用或修改原文。输入变化后旧补丁立即失效。实际导出的 299 个补丁已通过 Git 应用及结果字节验证；第二批 Windows 改动通过 10 套相关回归。

跨平台版还修复了断开连接后旧音乐／额度结果回写的问题，并改为在文字属性变化时立即保存草稿，防止快速切页、关闭时丢失最后一次编辑。

第二批改动的跨平台最终连续两轮回归通过：4,032 个常规工具布局、192 个填充内容的校验页布局、48,768 项按钮文字检查，源码哈希未变。异步回写使用可控制返回顺序的测试源验证，不等同于新的播放器实测或 Mac／Ubuntu 实机验证。

文件校验支持粘贴单个 SHA-256 / SHA-512 值，或 GNU/BSD 格式的整份校验清单，再按所选下载文件的名称匹配。文件名区分大小写；不同目录的同名文件或相互冲突的校验值需要先消除歧义。程序只解析文字，不读取清单中的路径。最多支持 128 Ki 字符、1,024 行，GNU 转义文件名同样计入限制。

日志守望修复了历史末行误报、空文件首次追加漏报，以及同一行后续追加重复提醒。跨平台预览版修复了长中文／emoji 笔记导致重启后设置重置的问题；超大保存会保留原文件，无法读取的设置会在恢复保存前备份。Windows Avalonia 预览包采用独立文件名，打包时拒绝覆盖现有安装包及校验记录。

Mac 和 Ubuntu 仍需实机验证；打包测试和无界面布局测试不能证明原生系统兼容性。

验证记录（2026-11-06）：Windows 首次连续回归通过前 77 套，在第 78 套被 LockApp 遮挡原生透明区域测试点后停止，没有自动复跑，也没有原生崩溃。后续校验解析边界修正后，13 套专项与剩余功能测试通过；两阶段合计覆盖 86 个套件，**不能等同于一次连续 87 套全部通过**，原生透明区域仍待解锁后补测。跨平台最终连续两轮回归、20 项隔离打包测试通过。完整日志、首次失败证据和源码哈希保留本机。
