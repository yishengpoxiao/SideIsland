namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private static void ProductivityV3Catalog()
        {
            Map(@"复制差异|Copy diff
导出补丁|Export patch
导出完整差异为 UTF-8 补丁，目标名 text.txt，统一使用 LF 换行；不修改原文。|Export the full diff as a UTF-8 patch for text.txt using LF line endings. Original text stays unchanged.
已复制完整差异补丁。|Full diff patch copied.
演示模式不保存文件。|Demo mode does not save files.
导出差异补丁|Export diff patch
差异补丁|Diff patch
差异补丁已保存。|Diff patch saved.
补丁保存失败，请检查文件权限或选择其他位置。|Could not save the patch. Check permissions or choose another location.
没有可导出的差异。|There are no changes to export.");
            Map(@"粘贴校验值或 SHA256SUMS / SHA512SUMS 清单，自动匹配所选文件名。|Paste a checksum or SHA256SUMS / SHA512SUMS list to match the selected filename.
校验清单最多支持 128 Ki 字符和 1,024 行。|Checksum lists support up to 128 Ki characters and 1,024 lines.
请先选择文件，再匹配校验清单。|Choose a file before matching a checksum list.
清单中没有与所选文件名完全一致的记录。|No entry exactly matches the selected filename.
清单中存在同名文件或冲突校验值，请仅保留目标记录。|Duplicate filenames or conflicting checksums: keep only the intended entry.
清单格式无效，请使用所选算法的 GNU 或 BSD 校验清单。|Use a valid GNU or BSD checksum list for the selected algorithm.");
            Map(@"▣ 截图提字|▣ Screen text
截图 OCR / 图片文字识别|Screen OCR / Image text recognition
≡ 文本模板|≡ Text templates
常用文本 / 日期占位符|Text snippets / Date placeholders
◉ 服务就绪|◉ Service readiness
端口 / 网页 / 文件稳定提醒|Port / Web / Stable file alerts
▱ 设置迁移|▱ Settings migration
偏好设置备份 / 导入|Preference backup / Import
专注结束后自动恢复上个布局和通知预览|Restore the previous layout and notification previews after focus ends
可直接输入：专注 45 分钟 / 查端口 8080 / 生成二维码 网址 / 复制模板 名称|Commands: focus 45 min / port 8080 / qr URL / template name
按 Enter 执行：|Press Enter to run: 
命令内容过长。|The command is too long.
模板草稿尚未保存，请先保存模板，或点击新建 / 清空草稿后退出。|The template draft has not been saved. Save it, or clear the draft before exiting.
未找到唯一匹配的模板名称。|No single matching template was found.
未找到唯一匹配的布局名称。|No single matching layout was found.
未找到唯一匹配的工作场景，或正在启动。|No single matching workspace was found, or launch is in progress.
演示命令已执行，不改变剪贴板或启动程序。|Demo command completed without changing the clipboard or launching apps.
快捷命令已执行。|Command completed.
服务已就绪|Service is ready
服务就绪|Service readiness
打开灵动岛的服务就绪查看。|Open Service readiness in Side Island to view the result.
待导入设置未能应用，已保留原偏好；可在设置迁移中取消或重试。|Pending settings could not be imported. Existing preferences were retained; cancel or retry in Settings migration.
文字超过对照工具的长度限制。|The text exceeds the comparison tool limit.");
        }
    }
}
