namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private static void BatchHashCatalog()
        {
            Map(@"单个文件|Single file
批量校验|Batch checksums
批量文件校验|Batch file checksums
最多选择 200 个文件，依次计算 SHA-256 / SHA-512。取消后保留已完成结果。|Choose up to 200 files for sequential SHA-256 / SHA-512 checksums. Completed results remain after cancellation.
可选：粘贴 GNU / BSD 校验清单，逐个匹配准确文件名。|Optional: paste a GNU / BSD checksum list to match each exact filename.
计算 / 校验|Calculate / verify
所有文件完成且没有同名歧义时可导出总清单。只读源文件，另存不覆盖现有文件。|Export a combined list after every file completes with unique names. Source files are read only; saving creates a new file.
一次最多选择 200 个文件，原选择已保留。|Choose up to 200 files. The previous selection is retained.
上一次文件读取尚未结束，请稍后重试。|The previous file read is still finishing. Try again shortly.
正在批量计算…|Calculating checksums…
批量计算未完成，已显示的结果仍保留。|The batch did not finish. Displayed results are retained.
未开始|Not started
找不到文件|File not found
文件在读取期间发生变化，请重试。|The file changed during reading. Try again.
无法读取文件：请检查访问权限。|Cannot read the file. Check access permissions.
请选择普通文件，不支持文件夹或符号链接。|Choose a regular file. Folders and symbolic links are not supported.
无法读取此文件，其他结果已保留。|This file could not be read. Other results are retained.
所选文件同名，无法唯一匹配清单。|Selected files share a name and cannot be matched uniquely.
多个文件需要校验清单，不能共用单个校验值。|Multiple files need a checksum list, not one shared digest.
校验清单无效或存在歧义，校验值已保留。|The checksum list is invalid or ambiguous. The computed digest is retained.
同名文件无法导出为总清单，请分别处理。|Files with duplicate names cannot share one list. Process them separately.
总清单超过容量限制，请减少选择的文件。|The combined list exceeds its limit. Select fewer files.
此文件名无法导出为 UTF-8 清单。|This filename cannot be exported as a UTF-8 list.
完整计算后可导出总清单；可选中已完成项复制校验值。|A complete batch can be exported. Select a completed entry to copy its digest.");
            Pattern("已选择 {0} 个文件。", "Selected {0} files.");
            Pattern("文件 {0}/{1} · 已读取 {2} 字节", "File {0}/{1} · Read {2} bytes");
            Pattern("已取消 · 完成 {0}/{1} 个文件", "Cancelled · Completed {0}/{1} files");
            Pattern("计算结束 · 完成 {0}/{1} 个文件", "Finished · Completed {0}/{1} files");
        }
    }
}
