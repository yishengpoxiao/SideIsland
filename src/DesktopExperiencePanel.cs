using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace CodexIsland
{
    internal sealed class DesktopExperiencePanel : StackPanel
    {
        private readonly ComboBox fullscreen, click, middle, wheel, monitors;
        private readonly CheckBox remember;
        private readonly TextBox toggleKey, privacyKey, searchKey, captureKey;
        private readonly TextBox ruleProcess = ToolStyle.Input();
        private readonly ComboBox ruleMode;
        private readonly StackPanel ruleRows = new StackPanel();
        private readonly TextBlock status;
        private DesktopExperiencePreferences current;
        private string editingRuleProcess;
        internal Func<DesktopExperiencePreferences, string> ApplyRequested;
        internal Action<string> MoveMonitorRequested;
        internal DesktopExperiencePanel(DesktopExperiencePreferences preferences)
        {
            current = preferences.Copy();
            Children.Add(ToolStyle.Heading("桌面交互与快捷键"));
            Children.Add(ToolStyle.Hint("全屏避让只影响灵动岛所在屏幕；退出全屏后恢复。截图、拖动和编辑期间不会自动隐藏。"));
            Children.Add(ToolStyle.Hint("遇到全屏程序时")); fullscreen = Choice(new[] { "保持显示", "自动隐藏", "收为贴边细条" }, new[] { "off", "hide", "slim" }.ToList().IndexOf(current.FullscreenMode)); Children.Add(fullscreen);
            Children.Add(ToolStyle.Hint("按应用设置全屏规则"));
            Children.Add(ToolStyle.Hint("填写进程名称，例如 vlc.exe、chrome.exe。规则仅在该应用前台全屏时生效；未知或受保护进程使用上方全局规则。最多 64 项，修改后点击底部保存。"));
            ruleProcess.MaxLength = 132; ruleProcess.ToolTip = "只填写进程名称，不需要完整路径"; Children.Add(ruleProcess);
            ruleMode = Choice(new[] { "跟随全局规则", "保持显示", "自动隐藏", "收为贴边细条" }, 0); Children.Add(ruleMode);
            Children.Add(PanelActionLayout.Create(1, IslandWindow.Button("添加／更新应用规则", AddRule)));
            Children.Add(new ScrollViewer { Content = ruleRows, MaxHeight = 210, Margin = new Thickness(0, 6, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); RebuildRules();
            Children.Add(ToolStyle.Hint("收起时左键单击")); click = ActionChoice(current.ClickAction); Children.Add(click);
            Children.Add(ToolStyle.Hint("收起时鼠标中键")); middle = ActionChoice(current.MiddleAction); Children.Add(middle);
            Children.Add(ToolStyle.Hint("收起时鼠标滚轮")); wheel = ActionChoice(current.WheelAction); Children.Add(wheel);
            Children.Add(ToolStyle.Hint("组件默认操作保留封面和组件快捷操作；自定义只在收起状态生效。拖动不会触发点击。滚轮音量每格调整 2%。"));
            Children.Add(ToolStyle.Hint("拖动中按 Esc 撤销到原位置；按住 Alt 松手可临时取消吸附，不改变自动吸附偏好。"));
            remember = UiLocalization.Content(new CheckBox { IsChecked = current.RememberMonitors, Margin = new Thickness(0, 10, 0, 4) }, "分别记住每个显示器的位置与停靠方向"); Children.Add(remember);
            monitors = new ComboBox { Margin = new Thickness(0, 6, 0, 5), Padding = new Thickness(7) }; UiLocalization.Protect(monitors); Children.Add(monitors); RefreshMonitors();
            Children.Add(PanelActionLayout.Create(1, IslandWindow.Button("移动到所选显示器", () => { string target = monitors.SelectedValue as string; if (!String.IsNullOrEmpty(target) && MoveMonitorRequested != null) MoveMonitorRequested(target); })));
            Children.Add(ToolStyle.Hint("全局快捷键（留空禁用；例如 Ctrl+Alt+Space、Ctrl+Shift+F9）"));
            toggleKey = KeyInput("展开／收起快捷键", current.ToggleHotkey); privacyKey = KeyInput("隐私模式快捷键", current.PrivacyHotkey);
            searchKey = KeyInput("搜索功能快捷键", current.SearchHotkey); captureKey = KeyInput("截图钉屏快捷键", current.CaptureHotkey);
            var save = PanelActionLayout.Create(1, IslandWindow.Button("保存桌面交互设置", Apply)); save.Margin = new Thickness(0, 8, 0, 0); Children.Add(save);
            status = ToolStyle.Hint(""); Children.Add(status);
        }
        private TextBox KeyInput(string label, string value) { Children.Add(ToolStyle.Hint(label)); var input = ToolStyle.Input(value); input.MaxLength = 80; Children.Add(input); return input; }
        private static ComboBox Choice(string[] values, int selected) { var combo = UiLocalization.Combo(new ComboBox { Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 5), ItemsSource = values }); combo.SelectedIndex = Math.Max(0, selected); return combo; }
        private static ComboBox ActionChoice(string key) { return Choice(GestureActionCatalog.Labels, Array.IndexOf(GestureActionCatalog.Keys, key)); }
        private void AddRule()
        {
            string process = FullscreenApplicationRule.NormalizeProcess(ruleProcess.Text);
            if (process.Length == 0) { status.Text = "请输入有效进程名称，例如 vlc.exe。"; return; }
            string mode = new[] { "default", "off", "hide", "slim" }[Math.Max(0, ruleMode.SelectedIndex)];
            var existing = current.FullscreenRules.FirstOrDefault(rule => String.Equals(rule.ProcessName, process, StringComparison.OrdinalIgnoreCase));
            var editing = current.FullscreenRules.FirstOrDefault(rule => rule.ProcessName == editingRuleProcess);
            if (editing != null && existing != null && !Object.ReferenceEquals(editing, existing)) { status.Text = "该进程已有规则，请编辑对应规则。"; return; }
            if (editing != null) { editing.ProcessName = process; existing = editing; }
            if (existing == null)
            {
                if (current.FullscreenRules.Count >= 64) { status.Text = "最多保留 64 项应用全屏规则。"; return; }
                current.FullscreenRules.Add(new FullscreenApplicationRule { ProcessName = process, Mode = mode });
            }
            else existing.Mode = mode;
            ruleProcess.Clear(); editingRuleProcess = null; ruleMode.SelectedIndex = 0; RebuildRules(); status.Text = "规则已修改，请点击底部保存桌面交互设置。";
        }
        private void RebuildRules()
        {
            ruleRows.Children.Clear();
            foreach (var item in current.FullscreenRules.OrderBy(rule => rule.ProcessName, StringComparer.OrdinalIgnoreCase))
            {
                var rule = item; var body = new StackPanel();
                body.Children.Add(IslandWindow.Text(rule.ProcessName, 12, "#E3EFE7", true, false));
                int index = Array.IndexOf(new[] { "default", "off", "hide", "slim" }, rule.Mode);
                body.Children.Add(ToolStyle.Hint(new[] { "跟随全局规则", "保持显示", "自动隐藏", "收为贴边细条" }[Math.Max(0, index)]));
                var edit = IslandWindow.Button("编辑", () => { editingRuleProcess = rule.ProcessName; ruleProcess.Text = rule.ProcessName; ruleMode.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "default", "off", "hide", "slim" }, rule.Mode)); ruleProcess.Focus(); });
                var remove = IslandWindow.Button("删除", () => { current.FullscreenRules.Remove(rule); if (editingRuleProcess == rule.ProcessName) { editingRuleProcess = null; ruleProcess.Clear(); } RebuildRules(); status.Text = "规则已修改，请点击底部保存桌面交互设置。"; }); body.Children.Add(PanelActionLayout.Create(2, edit, remove)); ruleRows.Children.Add(ToolStyle.Card(body));
            }
            if (ruleRows.Children.Count == 0) ruleRows.Children.Add(ToolStyle.Hint("尚无应用规则，所有应用使用全局规则。"));
        }
        private void Apply()
        {
            var next = current.Copy(); next.FullscreenMode = new[] { "off", "hide", "slim" }[Math.Max(0, fullscreen.SelectedIndex)];
            next.ClickAction = GestureActionCatalog.Keys[Math.Max(0, click.SelectedIndex)]; next.MiddleAction = GestureActionCatalog.Keys[Math.Max(0, middle.SelectedIndex)]; next.WheelAction = GestureActionCatalog.Keys[Math.Max(0, wheel.SelectedIndex)];
            next.RememberMonitors = remember.IsChecked == true; next.ToggleHotkey = toggleKey.Text; next.PrivacyHotkey = privacyKey.Text; next.SearchHotkey = searchKey.Text; next.CaptureHotkey = captureKey.Text;
            next.Validate(); string error = ApplyRequested == null ? null : ApplyRequested(next);
            if (String.IsNullOrEmpty(error)) { current = next.Copy(); RebuildRules(); status.Text = "桌面交互设置已保存并生效。"; } else status.Text = error;
        }
        internal void SetStatus(string message) { status.Text = message; }
        internal void RefreshMonitors()
        {
            string selected = monitors.SelectedValue as string;
            monitors.DisplayMemberPath = "Label"; monitors.SelectedValuePath = "Device";
            monitors.ItemsSource = Forms.Screen.AllScreens.Select((screen, index) => new MonitorChoice { Device = screen.DeviceName, Label = (index + 1).ToString(CultureInfo.InvariantCulture) + " · " + screen.DeviceName + " · " + screen.Bounds.Width + " × " + screen.Bounds.Height }).ToList();
            monitors.SelectedValue = selected; if (monitors.SelectedIndex < 0 && monitors.Items.Count > 0) monitors.SelectedIndex = 0;
        }
        private sealed class MonitorChoice { public string Device { get; set; } public string Label { get; set; } }
    }
}
