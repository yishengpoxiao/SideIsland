# Batch checksums

[English](#english) · [简体中文](#简体中文)

## English

In the development preview, open **File checksums → Batch checksums**. Select up to 200 files, choose SHA-256 or SHA-512, optionally paste a GNU/BSD checksum list, then calculate. Files are read locally and sequentially. Nothing is uploaded or modified.

The Windows development build also includes **Single file / Batch checksums** modes. Multiple dropped files open batch mode. The compact result list exposes each digest, retains completed items when cancelled, and shares one active read guard with the single-file tool. Switching modes requests cancellation; Windows appearance reconstruction does not promise to retain the old panel's result list.

Each result distinguishes a successful calculation, a match, a mismatch, an invalid expected entry and a read failure. Matching uses exact filenames. A bare digest cannot verify several files, and duplicate selected filenames are reported as ambiguous. Cancelling preserves completed results and marks remaining work; changing pages or appearance also cancels pending work while retaining completed results.

Copy or save a combined GNU manifest after every file has been read successfully and the filenames are unique. The list contains only filenames and actual calculated digests. It can be exported even when a supplied expected checksum did not match; inspect the per-file verification results. A failed or incomplete batch cannot export a misleading partial list. Saving creates a new file and refuses all original source paths, even if a source has since disappeared. Confirmed saved data is not reported as cancelled if cancellation arrives after publication. Unconfirmed results retain diagnostic paths.

Limits: 200 files, 128 Ki characters / 1,024 physical lines of expected checksum text, and the existing manifest parser's strict filename and escaping rules. Reads stop at the observed length plus one byte and compare open-file identity, size and modification time with the final path. This detects ordinary file changes; it is not an atomic snapshot against deliberately restored metadata or every network filesystem. Cancellation is checked between reads and cannot forcibly interrupt an operating-system read already blocked on a device or network share.

The Windows-hosted preview checks use owned real files and actual headless Avalonia controls across Chinese/English, light/dark themes, window sizes and scaling. They do not establish native macOS or Ubuntu behavior. Published v1.0.0 archives are unchanged.

## 简体中文

开发预览版中打开 **文件校验 → 批量校验**，最多选择 200 个文件，选用 SHA-256 或 SHA-512，可粘贴 GNU/BSD 校验清单后计算。文件在本机依次读取，不上传、不修改。

Windows 开发版也包含 **单个文件／批量校验** 两种模式，拖入多个文件会打开批量模式。紧凑结果列表可查看各项校验值，取消时保留已完成项，并与单文件工具共用一个读取任务限制。切换模式会请求取消；Windows 外观重建不承诺保留旧面板的结果列表。

每个文件分别显示计算成功、校验一致、不一致、预期条目无效或读取失败。清单严格匹配文件名；一个裸校验值不能用于多个文件，所选文件重名时会提示匹配歧义。取消后保留已完成结果，并标记尚未完成的项；切换页面、语言或主题也会取消尚在执行的工作并保留完成结果。

全部文件成功读取且文件名唯一时，可复制或另存合并的 GNU 清单。清单只含文件名和实际计算值。即使与用户提供的预期值不一致，也能导出实际值，请留意各项校验结果；失败或未完成批次不会导出不完整清单。保存只创建新文件，并拒绝所有原始文件路径，即使该源文件已经被删除。实际提交后才收到取消请求时，仍准确显示已保存；不能确认的保存保留诊断路径。

限制为 200 个文件、128 Ki 字符／1,024 个物理行的预期清单，并遵循原有解析器的严格文件名与转义规则。读取以开始时的长度加一个字节为限，同时核对打开文件及最终路径的身份、大小和修改时间。这能检测常见文件变化，但不保证抵御刻意恢复元数据的修改，也不等于所有网络文件系统上的原子快照。取消在读取间隙检查，无法强制打断已经阻塞在设备或网络上的系统读取。

Windows 主机上的预览版验证使用自有真实文件及实际 Avalonia 无头控件，覆盖中英文、明暗主题、窗口尺寸和缩放；不代表已经通过 macOS／Ubuntu 原生实测。已发布的 v1.0.0 包保持不变。
