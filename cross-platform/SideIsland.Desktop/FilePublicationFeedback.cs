using SideIsland.Core;

namespace SideIsland.Desktop;

internal static class FilePublicationFeedback
{
    internal static string Unknown(FilePublicationException error, string language)
    {
        bool english = language == "en";
        string text = english
            ? "The save result could not be confirmed. Check the destination before trying again.\n" + error.TargetPath
            : "无法确认保存结果，请先核对目标文件再重试。\n" + error.TargetPath;
        if (!string.IsNullOrEmpty(error.RetainedTemporaryPath)) text += (english ? "\nRetained copy: " : "\n保留副本：") + error.RetainedTemporaryPath;
        return text;
    }
}
