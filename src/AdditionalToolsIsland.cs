using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        private PortMonitorPanel portMonitorPanel;
        private ScreenPinPanel screenPinPanel;
        private ImageQuickPanel imageQuickPanel;
        private TextDiffPanel textDiffPanel;
        private TaskWatchPanel taskWatchPanel;
        private KeepAwakePanel keepAwakePanel;
        private QrTransferPanel qrTransferPanel;
        private WorkspaceScenePanel workspaceScenePanel;
        private bool restoreAfterCapture;

        private void BuildAdditionalTools(Panel navigation)
        {
            portMonitorPanel = new PortMonitorPanel(demo); screenPinPanel = new ScreenPinPanel(demo);
            imageQuickPanel = new ImageQuickPanel(demo); textDiffPanel = new TextDiffPanel(demo);
            taskWatchPanel = new TaskWatchPanel(demo); keepAwakePanel = new KeepAwakePanel(demo);
            qrTransferPanel = new QrTransferPanel(demo); workspaceScenePanel = new WorkspaceScenePanel(demo);
            AddTool(navigation, "ports", portMonitorPanel); AddTool(navigation, "screenpin", screenPinPanel);
            AddTool(navigation, "image", imageQuickPanel); AddTool(navigation, "diff", textDiffPanel);
            AddTool(navigation, "watch", taskWatchPanel); AddTool(navigation, "awake", keepAwakePanel);
            AddTool(navigation, "qr", qrTransferPanel); AddTool(navigation, "scenes", workspaceScenePanel);
            taskWatchPanel.OnNotify += NotifyTaskWatch;
            keepAwakePanel.StatusChanged += () => UpdateToolkitCompact();
            screenPinPanel.CaptureStateChanged += OnCaptureStateChanged;
            AllowDrop = true;
            PreviewDragOver += OnSmartDragOver;
            PreviewDrop += OnSmartDrop;
        }
        private void OpenDroppedImage(string path)
        {
            if (!IsToolVisible("image")) { settings.HiddenTools = String.Join(",", (settings.HiddenTools ?? "").Split(',').Where(key => key != "image")); SaveSettings(); ConfigureToolkit(); }
            SelectToolsPage(); SelectTool("image"); RequestExpanded(true); imageQuickPanel.LoadFile(path);
        }
        private static bool TryDroppedImage(IDataObject data, out string path)
        {
            path = null;
            try
            {
                if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return false;
                var files = data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length != 1 || String.IsNullOrWhiteSpace(files[0])) return false;
                string extension = Path.GetExtension(files[0]);
                if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) return false;
                path = files[0]; return true;
            }
            catch { return false; }
        }
        private bool IsFileShelfDrop(DependencyObject source)
        {
            while (source != null)
            {
                if (ReferenceEquals(source, fileShelfPanel)) return true;
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            return false;
        }
        private void OnCaptureStateChanged(bool capturing)
        {
            if (capturing)
            {
                restoreAfterCapture = IsVisible; dialogOpen = true; hoverTimer.Stop(); collapseTimer.Stop(); Hide();
            }
            else
            {
                dialogOpen = false;
                if (!shuttingDown && restoreAfterCapture) { Show(); SetExpanded(expanded, false); Dock(); }
                restoreAfterCapture = false;
            }
        }
        private void NotifyTaskWatch(string title, string body)
        { NotifyBackgroundTool("watch", title, body); }
        private void NotifyBackgroundTool(string tool, string title, string body)
        {
            if (demo || shuttingDown) return;
            bool log = tool == "logwatch", ready = tool == "ready";
            tray.ShowBalloonTip(7000, settings.HideNotificationBody ? UiLocalization.Localize(log ? "日志关键词提醒" : ready ? "服务已就绪" : "任务守望已完成") : title,
                settings.HideNotificationBody ? UiLocalization.Localize(log ? "打开灵动岛的日志追踪查看。" : ready ? "打开灵动岛的服务就绪查看。" : "打开灵动岛的任务守望查看。") : body, System.Windows.Forms.ToolTipIcon.Info);
            if (!expanded && !dialogOpen && IsVisible && !settings.QuietPreviews)
                ShowPeek(new IslandNotification { AppName = UiLocalization.Localize(log ? "日志实时追踪" : ready ? "服务就绪" : "任务守望"), AppId = "sideisland." + tool, Title = title, Body = body, CreatedAt = DateTime.UtcNow, Id = Guid.NewGuid().ToString("N") });
        }
        private void StartAdditionalTools() { portMonitorPanel.Start(); taskWatchPanel.Start(); keepAwakePanel.Start(); }
        private void StopAdditionalTools()
        {
            if (portMonitorPanel != null) portMonitorPanel.Dispose(); if (screenPinPanel != null) screenPinPanel.Dispose();
            if (imageQuickPanel != null) imageQuickPanel.Dispose(); if (textDiffPanel != null) textDiffPanel.Dispose();
            if (taskWatchPanel != null) taskWatchPanel.Dispose(); if (keepAwakePanel != null) keepAwakePanel.Dispose();
            if (qrTransferPanel != null) qrTransferPanel.Dispose(); if (workspaceScenePanel != null) workspaceScenePanel.Dispose();
        }
    }
}
