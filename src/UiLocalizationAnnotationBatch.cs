namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private static void AnnotationBatchCatalog()
        {
            Map(@"▦ 批量文件|▦ Batch files
批量重命名 / 图片队列|Batch rename / Image queue
批量文件|Batch files
批量文件处理|Batch file processing
批量重命名|Batch rename
批量图片处理|Batch images
选择或拖入文件，先预览再执行。重命名保留扩展名并可撤销；图片处理始终另存，不覆盖原图。|Choose or drop files, then preview before applying. Rename preserves extensions and supports undo; image processing always creates new files.
选择多个文件|Choose files
清空选择|Clear selection
文件名模板（{name} 原名，{n} 编号，保留扩展名）|Name template ({name} original name, {n} number; extension preserved)
查找原名中的文字|Find in original name
替换为|Replace with
起始编号|Start number
编号位数（1–9）|Number digits (1–9)
预览时校验文件内容。执行及撤销会锁定文件，不覆盖冲突目标；文件改变后会停止。撤销记录仅保留到退出或下次重命名。|Preview verifies content. Apply and undo lock files and stop if files changed or targets conflict. Undo lasts until exit or the next rename.
选择输出文件夹|Choose output folder
图片输出格式|Image output format
限制图片最大尺寸（保持比例、不放大）|Limit maximum dimensions (keep ratio, never enlarge)
最大宽度|Maximum width
最大高度|Maximum height
JPG 质量（1–100）|JPG quality (1–100)
JPG / PNG 最多 64 MB、2400 万像素。取消后保留已完成的新文件，原文件始终不变。|JPG / PNG: up to 64 MB and 24 megapixels. Cancel keeps completed outputs; original files remain unchanged.
生成处理预览|Preview changes
执行预览操作|Apply preview
撤销上次重命名|Undo last rename
数字参数请输入有效整数。|Enter valid whole numbers for numeric options.
正在校验并生成预览…|Verifying files and preparing preview…
预览已生成，请核对全部文件名后执行。|Preview is ready. Review every name before applying.
正在批量处理图片…|Processing image batch…
正在批量重命名…|Renaming files…
正在撤销重命名…|Undoing rename…
正在处理…|Processing…
重命名完成，可撤销上次操作。|Rename completed. The last operation can be undone.
已恢复原文件名。|Original names restored.
无法访问或锁定文件，请关闭正在编辑它的程序并检查权限。|Cannot access or lock a file. Close apps editing it and check permissions.
批量处理失败，原文件未被覆盖，请检查预览与输出位置。|Batch failed. Original files were not overwritten; check the preview and output folder.
演示模式不读取或更改本机文件。|Demo mode does not read or change local files.
批量任务正在安全结束，请稍后再次退出。|The batch is finishing safely. Try exiting again shortly.
请选择 1–200 个文件。|Choose 1–200 files.
仅支持现有本机普通文件，不处理文件夹、链接或网络路径。|Choose existing local regular files. Folders, links and network paths are not processed.
请填写有效模板、起始编号和 1–9 位编号宽度。|Enter a valid template, starting number and 1–9 number digits.
模板仅支持 {name} 和 {n}。|Templates support only {name} and {n}.
输出路径过长，请缩短文件名。|The output path is too long; shorten the name.
输出文件名包含无效字符、结尾空格或点。|Output names cannot contain invalid characters or end with a space or dot.
输出文件名属于 Windows 保留名称。|The output name is reserved by Windows.
预览存在重复目标文件名，请修改模板。|Preview contains duplicate target names; change the template.
目标文件已存在，不会覆盖，请修改模板。|A target already exists. It will not be overwritten; change the template.
请先生成包含实际变化的重命名预览。|Preview a rename with actual changes first.
文件在预览后已改变，操作已停止且没有修改文件。|A file changed after preview. The operation stopped without changing files.
部分文件无法恢复，已保留临时文件；请检查文件夹中的 .side-island-rename 文件。|Some files could not be restored. Temporary files were kept; check .side-island-rename files in the folder.
这次重命名已撤销。|This rename has already been undone.
不处理链接文件。|Link files are not processed.
请选择现有的输出文件夹。|Choose an existing output folder.
无法生成不重复的输出文件名。|Could not create a unique output name.
请先生成图片处理预览。|Preview image processing first.
标注与打码|Annotate and redact
选择工具后在图片上绘制；拖动模式保留原来的窗口拖拽。标注不会修改原图。|Choose a tool and draw on the image. Pan mode moves the window. Annotations do not change the source.
拖动|Pan
箭头|Arrow
矩形|Rectangle
序号|Number
文字|Text
马赛克|Mosaic
撤销标注|Undo mark
清空标注|Clear marks
复制标注图片|Copy annotated image
保存标注图片|Save annotated image
输入标注文字，再点击图片放置|Enter text, then click the image to place it
拖动图片移动窗口，滚轮调整缩放。|Drag the image to move the window; use the wheel to zoom.
输入文字后点击图片放置。|Enter text, then click the image to place it.
点击图片放置递增序号。|Click the image to place an increasing number.
在图片上按住并拖动，松开完成标注。|Drag on the image and release to finish a mark.
已复制标注图片。|Annotated image copied.
无法复制图片，请检查剪贴板是否正忙。|Could not copy the image; the clipboard may be busy.
标注图片已保存，原图未修改。|Annotated image saved; the source was unchanged.
图片保存失败，请选择可写的位置。|Could not save the image; choose a writable location.
一张截图最多保留 200 个标注。|A screenshot supports up to 200 marks.
请先输入标注文字。|Enter annotation text first.
标注文字最多 200 字符。|Annotation text supports up to 200 characters.
标注文字包含无效字符。|Annotation text contains invalid characters.
标注坐标或工具无效。|Invalid annotation coordinates or tool.");
            Pattern("已选择 {0} 个文件", "{0} files selected");
            Pattern("已取消，保留 {0} 个已完成文件。", "Cancelled; kept {0} completed files.");
            Pattern("已生成 {0} 张新图片，原图未修改。", "Created {0} new images; sources unchanged.");
        }
    }
}
