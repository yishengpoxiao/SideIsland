# Workspaces

English | [简体中文](#简体中文)

This development tool is available after v1.0.0; the published preview archives do not include it.

Open **Tools → Workspaces**. Expand **Shortcuts**, add a website, folder or program, and save it. Select the saved shortcuts for a workspace, optionally choose its two compact widgets, and save the workspace. **Launch saved workspace** uses the saved entries in shortcut-list order. Unsaved editor changes are not launched. Save before selecting another item; **Reload** discards unsaved edits and loads the file again.

Program arguments use separate fields. Each field is one literal argument, including an empty value, quotes or embedded newlines; no shell command is constructed. Removing a shortcut removes its scene references without deleting any target file. Removing a workspace does not remove its shortcuts.

Launch results distinguish hand-off, failure, skipped items and an unconfirmed request. A helper returning successfully does not prove the target app finished loading or its work completed. **Cancel remaining** skips requests not yet sent; it never terminates an opened application. Choosing compact widgets does not connect accounts or start a focus timer. Launching and saving remain single operations across page, language and theme changes. A close request during saving is refused with feedback; close again after saving finishes. Closing during a launch cancels remaining requests.

## Limits and local storage

- Up to 60 shortcuts, 20 workspaces and 30 shortcuts in a workspace.
- Names: 1–60 UTF-16 code units. Program arguments: up to 32 fields, 1,024 code units per field. NUL and invalid Unicode are rejected.
- Website targets must be complete HTTP(S) URLs; folder/program targets must be absolute native paths. Significant Unix spaces are preserved.
- The versioned JSON document is limited to 2 MiB. Paths, URLs and arguments are stored locally in plaintext in `workspace-scenes.json` under the app data directory. They are not a credential store.

The editor saves explicitly. An unreadable document is preserved; when a validated original is available, **Back up & rebuild** retains it before creating an empty catalog. An oversized or inaccessible file requires local restoration or moving it aside, followed by reload. Existing content is not silently replaced with defaults.

Saving checks the loaded file's bytes and identity. A detected external change blocks a stale save. Windows uses an atomic replacement with a backup; macOS/Linux use atomic exchange, retaining the displaced file before naming its backup. First creation and backup naming use a no-replace native operation. Unsupported filesystems fail without a non-atomic fallback. If an operation reports an error after changing the file, the app checks the actual state and distinguishes a confirmed saved draft from an uncertain result requiring reload. A recovery copy can retain its `.tmp` name if naming the backup failed; use the exact path shown by the app. Do not treat these retained recovery files as disposable build outputs.

These checks do not stop another process from continuing to write an already open file. Cooperating app saves use a lock file; a leftover lock is not automatically deleted because ownership cannot be inferred. Recovery copies remain local for review and are not automatically rotated away.

## Platform scope and validation

macOS opens websites/folders through `/usr/bin/open`, `.app` bundles through `open -a … --args`, and executable files directly. Ubuntu uses `xdg-open` for websites/folders and starts executable files directly. Arguments remain separate OS arguments. Native helper availability, file permissions, app associations and desktop-session policies determine the actual result.

The tests exercise real owned test executables and files, deterministic asynchronous replies, conflict/recovery paths and populated Avalonia layouts in both languages/themes and multiple sizes/scales. Exchange failure fixtures on Windows do not establish that Mac/Linux filesystem calls passed on native hardware. App launch, native choosers, Retina/window-manager behavior and target filesystem validation remain outstanding. Automatic scene rules and the full Windows layout editor are not part of this tool.

Primary references: [ProcessStartInfo.ArgumentList](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist?view=net-10.0), [Apple Terminal open](https://support.apple.com/en-mz/guide/terminal/apdb66b5242-0d18-49fc-9c47-a2498b7c91d5/mac), [Ubuntu xdg-open](https://manpages.ubuntu.com/manpages/noble/man1/xdg-open.1.html), [Apple atomic rename flags](https://raw.githubusercontent.com/apple-oss-distributions/xnu/main/bsd/man/man2/rename.2), [Linux renameat2](https://man7.org/linux/man-pages/man2/rename.2.html).

## 简体中文

这是 v1.0.0 之后的开发功能，已发布的预览安装包尚未包含。

打开 **工具 → 工作场景**，展开快捷入口区，添加并保存网页、目录或软件。为场景选择已保存入口，可选两个收起组件，再保存场景。**启动已保存场景**按快捷入口列表顺序启动，未保存的编辑不会被启动。切换条目前先保存；**重新加载**会放弃未保存编辑，读取磁盘文件。

软件参数使用独立输入框，一框对应一个原样参数，保留空值、引号及参数内换行，不拼接 shell 命令。删除入口会移除场景中的引用，不删除目标文件；删除场景不删除快捷入口。

逐项结果区分已交给系统、失败、跳过及尚未确认接收。辅助程序返回成功不代表目标软件已加载完成或其中任务已完成。**取消后续启动**只跳过未发出的请求，不终止已打开的软件。选择收起组件不会连接账号或开始专注计时。切页、切换语言／主题时仍保持单次保存与启动；保存期间关闭会被暂缓，需保存完成后再次关闭，启动期间关闭则取消剩余请求。

最多保存 60 个入口、20 个场景，每个场景 30 个入口。名称最长 60 个 UTF-16 单元；程序最多 32 个参数，每个 1,024 单元。目标需为完整 HTTP(S) 网址或本机绝对路径，保留 Unix 有意义的空格。数据文件上限 2 MiB，位于应用数据目录的 `workspace-scenes.json`；路径、网址与参数按本机明文保存，不用于保管凭据。

手动保存；读取失败不会悄悄覆盖为默认值。具备可核验原文件时，可显式“备份并重建”；超大或不可访问的文件需先在本机恢复或移走，再重新加载。保存前检查原文件身份及内容，检测到外部修改即阻止过时保存。Windows 原子替换并备份，Mac／Linux 原子交换后保留被替换的文件，再以不覆盖方式命名备份；不支持的文件系统明确报错，不退回非原子流程。

若操作已执行却返回错误，会核对实际文件，区分已确认保存和必须重新加载的未知结果。备份命名失败时恢复文件可能保留 `.tmp` 后缀，以界面显示的实际路径为准；不要把这些恢复文件当作构建垃圾清除。恢复副本留在本机，不自动轮换删除。已有文件句柄仍可能被其他进程继续写入；遗留保存锁也不会擅自删除。

Mac 使用系统 `open` 打开网页／目录／`.app`，可执行文件直接启动；Ubuntu 使用 `xdg-open` 打开网页／目录，可执行文件直接启动。系统辅助程序、权限、默认关联和桌面策略决定实际结果。测试覆盖自有程序／文件、可控异步竞态、恢复流程及双语、双主题、多尺寸与模拟 DPI 布局；Windows 上的交换故障测试不等同于 Mac／Linux 原生验证。目标机启动、原生选择器、Retina／窗口管理和文件系统实测仍待补齐；自动场景规则及完整 Windows 布局编辑尚未等价迁移。
