using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class ColorPanel : StackPanel, IDisposable
    {
        private readonly bool demo;
        private Color current = Color.FromRgb(161, 234, 207);
        private readonly TextBox input = ToolStyle.Input("#A1EACF");
        private readonly TextBlock status = ToolStyle.Hint(""), swatchLabel = IslandWindow.Text("", 20, "#111518", true), contrast = ToolStyle.Hint("");
        private readonly Border swatch = new Border { Height = 64, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 6, 0, 6) };
        private readonly TextBox hex = ToolStyle.Input(), rgb = ToolStyle.Input(), hsl = ToolStyle.Input();
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private Button pick, cancel;
        private DateTime sampleAt;
        public ColorPanel(bool demoMode)
        {
            demo = demoMode;
            UiTheme.ProtectColor(swatch);
            Children.Add(ToolStyle.Heading("取一点颜色，留住灵感"));
            Children.Add(ToolStyle.Hint("点击取色后，将鼠标移到目标位置，3 秒后读取指针下的一个像素。也可输入 HEX 色值。"));
            pick = IslandWindow.Button("3 秒后屏幕取色", BeginPick); cancel = IslandWindow.Button("取消", CancelPick); cancel.IsEnabled = false;
            var actions = PanelActionLayout.Create(2, pick, cancel); actions.Margin = new Thickness(0, 0, 0, 6); Children.Add(actions);
            var edit = new DockPanel(); var apply = PanelActionLayout.Caption(IslandWindow.Button("应用", ApplyInput)); DockPanel.SetDock(apply, Dock.Right); apply.Margin = new Thickness(6, 0, 0, 0); edit.Children.Add(apply); input.MaxLength = 16; input.ToolTip = "#RGB 或 #RRGGBB"; edit.Children.Add(input); Children.Add(edit);
            input.KeyDown += (s, e) => { if (e.Key == Key.Enter) { ApplyInput(); e.Handled = true; } };
            swatchLabel.HorizontalAlignment = HorizontalAlignment.Center; swatchLabel.VerticalAlignment = VerticalAlignment.Center; swatch.Child = swatchLabel; Children.Add(swatch);
            var results = new StackPanel(); AddResult(results, "HEX", hex); AddResult(results, "RGB", rgb); AddResult(results, "HSL", hsl); Children.Add(ToolStyle.Card(results));
            Children.Add(contrast);
            Children.Add(PanelActionLayout.Caption(IslandWindow.Button("查看 RGB 反色", () => { CancelPick(); SetColor(ColorTools.Complement(current)); })));
            Children.Add(status);
            Children.Add(ToolStyle.Hint(demo ? "演示模式 · 不读取屏幕或写入剪贴板" : "仅手动取色，不连续监控、不保存截图。受保护画面或 HDR 内容的色值可能不可用或存在差异。"));
            timer.Tick += (s, e) => Tick(); SetColor(current);
        }
        private void AddResult(Panel parent, string label, TextBox field)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, label == "HSL" ? 0 : 6) };
            var copy = IslandWindow.Button("复制", () => { if (demo) { status.Text = "演示模式不修改剪贴板。"; return; } try { Clipboard.SetText(field.Text); status.Text = "已复制 " + label; } catch { status.Text = "剪贴板正忙，请重试。"; } });
            PanelActionLayout.Caption(copy); copy.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(copy, Dock.Right); row.Children.Add(copy); field.IsReadOnly = true; field.FontSize = 11; field.ToolTip = label; row.Children.Add(field); parent.Children.Add(row);
        }
        private void ApplyInput() { Color color; if (!ColorTools.TryHex(input.Text, out color)) { status.Text = "请输入 3 位或 6 位 HEX，例如 #8AC 或 #88AACC。"; return; } CancelPick(); SetColor(color); status.Text = "已更新颜色"; }
        private void SetColor(Color color)
        {
            current = color; input.Text = hex.Text = ColorTools.Hex(color); rgb.Text = ColorTools.Rgb(color); hsl.Text = ColorTools.Hsl(color);
            double black = ColorTools.Contrast(color, Colors.Black), white = ColorTools.Contrast(color, Colors.White);
            swatch.Background = new SolidColorBrush(color); swatchLabel.Foreground = black >= white ? Brushes.Black : Brushes.White; swatchLabel.Text = hex.Text;
            contrast.Text = String.Format(CultureInfo.InvariantCulture, "与黑色对比 {0:0.00}:1 · 与白色对比 {1:0.00}:1\n搭配{2}文字更清晰。", black, white, black >= white ? "黑色" : "白色");
        }
        private void BeginPick()
        {
            if (demo) { status.Text = "演示模式不读取屏幕，请使用 HEX 输入体验。"; return; }
            sampleAt = DateTime.UtcNow.AddSeconds(3); pick.IsEnabled = false; cancel.IsEnabled = true; status.Text = "3 秒后取色，请将鼠标移到目标位置…"; timer.Start();
        }
        private void Tick()
        {
            double seconds = (sampleAt - DateTime.UtcNow).TotalSeconds;
            if (seconds > 0) { status.Text = Math.Ceiling(seconds).ToString(CultureInfo.InvariantCulture) + " 秒后取色，请保持鼠标位置…"; return; }
            // Do not unexpectedly sample after resume from sleep or a long UI stall.
            CancelPick(); if (seconds < -5) { status.Text = "取色已超时，请重新点击。"; return; }
            Color color; if (ColorTools.TrySampleCursor(out color)) { SetColor(color); status.Text = "已读取指针下的颜色"; } else status.Text = "暂时无法读取该位置，请换个位置重试。";
        }
        public void CancelPick() { timer.Stop(); if (pick != null) pick.IsEnabled = true; if (cancel != null) cancel.IsEnabled = false; status.Text = ""; }
        public void Dispose() { CancelPick(); }
    }
}
