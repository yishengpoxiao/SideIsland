using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        private OcrPanel ocrPanel;
        private ContextMenu smartDropMenu;
        private bool smartDropStopped;
        private CancellationTokenSource smartDropWork;

        private void BuildOcrDropTools(Panel navigation)
        {
            ocrPanel = new OcrPanel(demo); AddTool(navigation, "ocr", ocrPanel);
            ocrPanel.CaptureStateChanged += OnCaptureStateChanged;
            ocrPanel.CompareRequested += text => { ShowSmartDropTool("diff"); textDiffPanel.AcceptExternalText(text); };
        }

        private bool SmartDropNativeTarget(DependencyObject source)
        {
            while (source != null)
            {
                if (source is TextBoxBase || source is PasswordBox || ReferenceEquals(source, fileShelfPanel) || ReferenceEquals(source, fileHashPanel) || ReferenceEquals(source, batchFilesPanel) || ReferenceEquals(source, lanTransferPanel)) return true;
                source = source is Visual || source is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            return false;
        }
        private void OnSmartDragOver(object sender, DragEventArgs e)
        {
            if (demo || shuttingDown || smartDropStopped || SmartDropNativeTarget(e.OriginalSource as DependencyObject) || !SmartDropParser.Supports(e.Data)) return;
            e.Effects = DragDropEffects.Copy; e.Handled = true;
        }
        private void OnSmartDrop(object sender, DragEventArgs e)
        {
            if (demo || shuttingDown || smartDropStopped || SmartDropNativeTarget(e.OriginalSource as DependencyObject)) return;
            SmartDropPayload payload = SmartDropParser.Read(e.Data); if (payload.Kind == SmartDropKind.None) return;
            e.Handled = true;
            // Let OLE finish its drop loop before opening a nonmodal choice menu.
            Dispatcher.BeginInvoke(new Action(() => ShowSmartDropMenu(payload)), DispatcherPriority.Background);
        }
        private void ShowSmartDropMenu(SmartDropPayload payload)
        {
            if (shuttingDown || smartDropStopped) return;
            if (smartDropMenu != null) smartDropMenu.IsOpen = false;
            var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint }; UiTheme.Install(menu.Resources); smartDropMenu = menu;
            if (payload.Kind == SmartDropKind.Image)
            {
                AddMenu(menu, "压缩或调整图片", () => OpenDroppedImage(payload.Paths[0]));
                AddMenu(menu, "将图片钉在桌面", async () => await PinDroppedImageAsync(payload.Paths[0]));
                AddMenu(menu, "识别图片中的文字", () => { ShowSmartDropTool("ocr"); ocrPanel.AcceptFile(payload.Paths[0]); });
            }
            if (payload.Kind == SmartDropKind.Image || payload.Kind == SmartDropKind.Files)
            {
                if (payload.Paths.Length == 1) AddMenu(menu, "计算文件校验值", async () => { ShowSmartDropTool("hash"); fileHashPanel.AcceptExternalFile(payload.Paths[0]); await fileHashPanel.Calculate(); });
                AddMenu(menu, "收纳文件引用", () => { ShowSmartDropTool("files"); fileShelfPanel.AcceptExternalFiles(payload.Paths); });
                AddMenu(menu, "批量处理文件", () => { ShowSmartDropTool("batch"); AcceptDroppedFiles(() => batchFilesPanel.AcceptFiles(payload.Paths)); });
                AddMenu(menu, "局域网分享文件", () => { ShowSmartDropTool("lan-transfer"); AcceptDroppedFiles(() => lanTransferPanel.SetFiles(payload.Paths)); });
            }
            else if (payload.Kind == SmartDropKind.Text)
            {
                AddMenu(menu, "文字对照", () => { ShowSmartDropTool("diff"); textDiffPanel.AcceptExternalText(payload.Text); });
                AddMenu(menu, "生成二维码", () => { ShowSmartDropTool("qr"); qrTransferPanel.AcceptExternalText(payload.Text); });
            }
            AddMenu(menu, "取消", () => { });
            menu.Opened += (s, e) => { hoverTimer.Stop(); collapseTimer.Stop(); QueueInteractionRecheck(); };
            menu.Closed += (s, e) => { if (ReferenceEquals(smartDropMenu, menu)) smartDropMenu = null; QueueInteractionRecheck(); };
            menu.IsOpen = true;
        }
        private void ShowSmartDropTool(string key)
        {
            if (shuttingDown || smartDropStopped) return;
            if (!IsToolVisible(key)) { settings.HiddenTools = String.Join(",", (settings.HiddenTools ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Where(item => item != key)); SaveSettings(); ConfigureToolkit(); }
            SelectToolsPage(); SelectTool(key); RequestExpanded(true);
        }
        private void AcceptDroppedFiles(Action accept)
        {
            try { accept(); }
            catch (Exception error) { if (toolkitHint != null) toolkitHint.Text = UiLocalization.Localize("无法接收文件：") + error.Message; }
        }
        private async Task PinDroppedImageAsync(string path)
        {
            if (demo || shuttingDown || smartDropStopped || smartDropWork != null) return;
            var work = new CancellationTokenSource(); smartDropWork = work;
            try
            {
                var image = await Task.Run(() => ImageQuickTools.Load(path, work.Token)); work.Token.ThrowIfCancellationRequested();
                if (!shuttingDown && !smartDropStopped) { ShowSmartDropTool("screenpin"); screenPinPanel.PinImage(image); }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!shuttingDown && !smartDropStopped) { ShowSmartDropTool("screenpin"); screenPinPanel.ReportExternalError("图片读取失败，请确认文件是有效的 JPG / PNG 图片。"); } }
            finally { if (ReferenceEquals(smartDropWork, work)) smartDropWork = null; work.Dispose(); }
        }
        private void StopOcrDropTools()
        {
            smartDropStopped = true; if (smartDropMenu != null) smartDropMenu.IsOpen = false;
            if (smartDropWork != null) smartDropWork.Cancel(); if (ocrPanel != null) ocrPanel.Dispose();
        }
    }

}
