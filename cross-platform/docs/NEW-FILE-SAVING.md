# Saving a new file

English | [中文](#中文)

These development changes apply to screenshot PNGs, QR PNGs, checksum lists and displayed-log snapshots after v1.0.0. Published v1.0.0 archives remain unchanged.

Saving prepares and flushes a complete sibling file before publishing it. Existing files, directories and symbolic links are rejected; another writer creating the destination during preparation cannot be overwritten by the final operation. macOS uses `renameatx_np` with `RENAME_EXCL`; Linux uses `renameat2` with `RENAME_NOREPLACE`. Windows retains its supported no-replace move. Unsupported filesystems fail without an ordinary-rename or copy fallback.

Cancellation is checked before publication. Once the atomic operation takes effect, cancelling or closing does not delete the saved destination. A system call can report an error after it has completed, particularly on network filesystems. The app checks the destination's file identity and complete contents: a confirmed result is reported as success; an uncertain result tells you to inspect the actual destination and any retained staging path. It does not blindly retry or roll back over somebody else's later changes.

Temporary files are unique siblings named `.side-island-publish-<id>.tmp`. Ordinary pre-publication failures clean up the app's recognized staging file; uncertain outcomes retain evidence. Local diagnostic records include the save state, destination and confirmed retained path, including when the window has closed. A retained copy is not build garbage. This does not isolate a directory from hostile writers, prevent all later external modification or prove crash durability on every filesystem. Cleanup can be blocked by an external handle. Confirmed retained paths are included in exception diagnostics even when publication definitely failed; an interrupted encoder may leave an incomplete staging file.

The shared checksum helper keeps its C# 5 Windows entry point. Portable callers explicitly supply the new publisher; an unadapted Unix call is rejected instead of silently using the old check-then-rename path.

Validation uses actual owned temporary files, file/directory/symlink conflicts, interrupted encoders, cancellation and deterministic native-result fixtures. UI tests exercise actual save actions, late changes, unknown-result feedback and small-window layouts. These Windows-hosted checks do not substitute for Mac/Ubuntu native filesystem and picker validation.

API references: [Microsoft .NET 10 Unix implementation](https://raw.githubusercontent.com/dotnet/runtime/v10.0.13/src/libraries/System.Private.CoreLib/src/System/IO/FileSystem.Unix.cs), [Linux renameat2 and uncertain NFS results](https://man7.org/linux/man-pages/man2/rename.2.html), [Apple exclusive rename](https://raw.githubusercontent.com/apple-oss-distributions/xnu/main/bsd/man/man2/rename.2).

## 中文

这是 v1.0.0 之后开发版对截图 PNG、二维码 PNG、校验清单和日志显示快照的改进，已发布安装包未替换。

先在同目录准备并刷新完整文件，再以原生“不覆盖”操作发布；已有文件、目录和符号链接均被拒绝。准备期间被其他程序创建的目标也不会被最终操作覆盖。Mac 使用 `RENAME_EXCL`，Linux 使用 `RENAME_NOREPLACE`；不支持的文件系统明确失败，不退回普通重命名或复制。

取消在发布前检查；原子操作已经生效后，取消或关闭不会删除已保存文件。某些系统调用可能执行成功却返回错误，因此程序会核验目标身份及完整内容。能确认成功就如实报告；不能确认则给出实际目标和可用的保留路径，提示核对，不盲目重试或回滚其他程序的新修改。

临时文件采用 `.side-island-publish-<id>.tmp` 名称。普通提交前失败清理已识别的自有文件；未知结果保留证据，本机诊断包含状态、目标和已确认保留路径，窗口关闭后仍可核对。恢复副本不属于构建垃圾。此流程不能隔离恶意同目录写入者、阻止之后的外部修改，或保证所有文件系统的崩溃耐久性；外部句柄也可能阻止清理。明确未发布但清理被阻挡时，异常诊断同样记录已确认的保留路径；中断编码留下的暂存内容可能不完整。

测试覆盖自有真实文件、目录／符号链接冲突、中断写入、取消、可控系统返回结果，以及真实保存控件和小窗口反馈；不等同于 Mac／Ubuntu 原生文件系统和选择器实测。
