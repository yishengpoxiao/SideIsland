# Diff patch exports

[English](#english) · [简体中文](#简体中文)

## English

Compare two texts, then choose **Export patch**. The development preview writes a UTF-8 unified patch using the compared snapshot. Editing, comparing again or rebuilding the page cancels an uncommitted export; a late file picker cannot save an obsolete comparison. Copying also uses a frozen snapshot.

New destinations are created without overwriting another file that appears in the meantime. For an existing destination, the compact confirmation card shows its path and observed byte count. **Replace this file** authorizes replacement of that observed version. An external change before publication rejects the save; a concurrent displaced version discovered during publication is retained and reported. An unreadable, oversized or unsupported destination cannot be replaced by guessing its state. Cancelling before native publication leaves the destination intact and cleans owned staging files when possible. After publication, the actual saved or unconfirmed result takes precedence over a late cancellation.

Destination inspection and writes run in the background. The patch is limited to 2 MiB, and an existing destination to 64 MiB for bounded identity/content validation. Unsupported native publication methods fail without an unsafe fallback. A failed save with retained data, a confirmed save with a warning and an unknown result are distinct; inspect the displayed destination/retained paths before retrying. Diagnostics also record the full exception and paths after the page or window closes.

These changes apply to the portable development preview, not the unchanged published v1.0.0 archives or the Windows native diff helper. Windows-hosted tests use real owned files and headless UI controls; they do not establish native Mac/Ubuntu execution or real system-picker behavior.

## 简体中文

比较两段文字后选择 **导出补丁**。开发预览版使用比较完成时的快照，生成 UTF-8 unified patch。修改输入、重新比较或重建页面都会取消尚未提交的导出；迟到的文件选择结果不会写入过期比较。复制同样使用冻结快照。

新目标通过不覆盖方式创建，即使中途有其他程序创建同名文件也不会覆盖。目标已经存在时，紧凑确认卡显示路径和观测到的字节数；点击 **替换该文件** 才授权替换这一版本。提交前发现外部修改会拒绝保存；提交期间发现被替换的是并发修改版本时，会保留并报告副本。无法读取、过大或不受支持的目标不会靠猜测状态来覆盖。原生提交前取消会保留目标，并尽可能清理自己创建的临时文件；提交后收到取消，则以实际保存或待核对结果为准。

目标检查和写入在后台执行。补丁上限 2 MiB，已有目标上限 64 MiB，以限制身份和完整内容验证的开销。不支持安全原生提交时直接失败，不降级为不安全覆盖。未保存但保留副本、已保存但有警告、无法确认结果会分别提示；重试前请核对显示的目标与副本路径。页面或窗口关闭后，诊断记录仍保留完整异常和路径。

这些修改适用于跨平台开发预览版，已发布的 v1.0.0 包和 Windows 原生文字对照辅助函数未改动。Windows 主机上的测试使用自有真实文件和无头界面控件，不代表 Mac／Ubuntu 原生实测或真实系统文件选择框验证。
