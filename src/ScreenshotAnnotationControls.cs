using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    internal sealed class ScreenshotAnnotationControls : StackPanel
    {
        private readonly ScreenshotAnnotationDocument document;
        private readonly ScreenshotAnnotationSurface surface;
        private readonly Dictionary<string, Button> modes = new Dictionary<string, Button>();
        private readonly TextBox text = ToolStyle.Input();
        private readonly TextBlock status = ToolStyle.Hint("");
        private readonly Button undo, clear;
        internal ScreenshotAnnotationControls(ScreenshotAnnotationDocument model, ScreenshotAnnotationSurface view)
        {
            document = model; surface = view;
            Children.Add(ToolStyle.Hint("选择工具后在图片上绘制；拖动模式保留原来的窗口拖拽。标注不会修改原图。"));
            var first = new[] { ModeButton("拖动", "pan"), ModeButton("箭头", "arrow"), ModeButton("矩形", "rectangle") };
            var second = new[] { ModeButton("序号", "number"), ModeButton("文字", "text"), ModeButton("马赛克", "mosaic") };
            Children.Add(PanelActionLayout.Create(3, first)); var secondRow = PanelActionLayout.Create(3, second); secondRow.Margin = new Thickness(0, 6, 0, 0); Children.Add(secondRow);
            text.MaxLength = 200; text.AcceptsReturn = true; text.Visibility = Visibility.Collapsed; text.TextWrapping = TextWrapping.Wrap; text.MinHeight = 32; text.MaxHeight = 80;
            text.TextChanged += (s, e) => surface.AnnotationText = text.Text;
            UiLocalization.ToolTip(text, "输入标注文字，再点击图片放置"); Children.Add(text);
            undo = IslandWindow.Button("撤销标注", document.Undo); clear = IslandWindow.Button("清空标注", document.Clear);
            var history = PanelActionLayout.Create(2, undo, clear); history.Margin = new Thickness(0, 6, 0, 0); Children.Add(history);
            var export = PanelActionLayout.Create(2, IslandWindow.Button("复制标注图片", Copy), IslandWindow.Button("保存标注图片", Save)); export.Margin = new Thickness(0, 6, 0, 0); Children.Add(export); Children.Add(status);
            document.Changed += Refresh; surface.Error += message => status.Text = message; Refresh();
        }
        private Button ModeButton(string label, string mode)
        {
            var button = IslandWindow.Button(label, () => {
                surface.SelectMode(mode); text.Visibility = mode == "text" ? Visibility.Visible : Visibility.Collapsed;
                status.Text = mode == "pan" ? "拖动图片移动窗口，滚轮调整缩放。" : mode == "text" ? "输入文字后点击图片放置。" : mode == "number" ? "点击图片放置递增序号。" : "在图片上按住并拖动，松开完成标注。";
                Refresh();
            }); modes[mode] = button; return button;
        }
        private void Refresh()
        {
            undo.IsEnabled = clear.IsEnabled = document.Count > 0;
            foreach (var item in modes) item.Value.Background = UiTheme.Brush(item.Key == surface.Mode ? "#365649" : "#202C27");
        }
        private void Copy()
        {
            try { Clipboard.SetImage(document.Flatten()); status.Text = "已复制标注图片。"; }
            catch { status.Text = "无法复制图片，请检查剪贴板是否正忙。"; }
        }
        private void Save()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = UiLocalization.Localize("保存标注图片"), Filter = UiLocalization.FileFilter("PNG 图片|*.png"), DefaultExt = ".png", AddExtension = true, OverwritePrompt = true, FileName = "Side-Island-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            string target = Path.GetFullPath(dialog.FileName), temporary = Path.Combine(Path.GetDirectoryName(target), ".side-island-annotation-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(document.Flatten()));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { png.Save(stream); stream.Flush(true); }
                if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
                status.Text = "标注图片已保存，原图未修改。";
            }
            catch { status.Text = "图片保存失败，请选择可写的位置。"; }
            finally { if (File.Exists(temporary)) try { File.Delete(temporary); } catch { } }
        }
    }
}
