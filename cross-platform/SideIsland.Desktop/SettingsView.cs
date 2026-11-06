using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SideIsland.Core;

namespace SideIsland.Desktop;
public sealed partial class IslandWindow
{
    private void RenderSettings()
    {
        var dataStatus = Button(T("数据状态与恢复", "Data status & recovery"), OpenDataStatus); dataStatus.Name = "settings-data-status"; body.Children.Add(dataStatus);
        var placement = new StackPanel { Spacing = 6 };
        var edge = new ComboBox { ItemsSource = new[] { T("自动", "Automatic"), T("顶部", "Top"), T("底部", "Bottom"), T("左侧", "Left"), T("右侧", "Right") }, SelectedIndex = prefs.Dock switch { "Top" => 1, "Bottom" => 2, "Left" => 3, "Right" => 4, _ => 0 } };
        placement.Children.Add(Hint(T("停靠边缘", "Dock edge"))); placement.Children.Add(edge);
        edge.SelectionChanged += (_, _) => { prefs.Dock = new[] { "Auto", "Top", "Bottom", "Left", "Right" }[Math.Max(0, edge.SelectedIndex)]; Save(); Place(); };
        var notch = Check(T("Mac 优先贴合刘海（无刘海回退顶边）", "Prefer the Mac notch; otherwise use the top edge"), prefs.PreferNotch, value => { prefs.PreferNotch = value; if (value && OperatingSystem.IsMacOS()) { prefs.Dock = "Auto"; prefs.AlwaysOnTop = true; Topmost = true; } Save(); Rebuild(); }); placement.Children.Add(notch);
        placement.Children.Add(Check(T("保持置顶（关闭时退出刘海模式）", "Keep on top; turning off leaves notch mode"), prefs.AlwaysOnTop, value => { prefs.AlwaysOnTop = value; if (!value && OperatingSystem.IsMacOS()) prefs.PreferNotch = false; Topmost = value; Save(); Rebuild(); }));
        placement.Children.Add(Check(T("在不同桌面显示（Mac Spaces）", "Show across Mac Spaces"), prefs.AcrossSpaces, value => { prefs.AcrossSpaces = value; Save(); Place(); }));
        placement.Children.Add(Check(T("允许在全屏空间显示（预览，需实机验证）", "Allow fullscreen Spaces (preview; verify on your Mac)"), prefs.OverFullscreen, value => { prefs.OverFullscreen = value; Save(); Place(); }));
        placement.Children.Add(Button(T("重新检测显示器与刘海", "Detect displays and notch again"), () => { Place(); Report(realNotch ? T("已贴合检测到的刘海。", "Aligned to the detected notch.") : T("没有可用的刘海区域，使用屏幕边缘。", "No usable notch found; using edge placement.")); })); body.Children.Add(Card(placement));

        var behavior = new StackPanel { Spacing = 6 };
        behavior.Children.Add(Check(T("悬停后展开", "Expand on hover"), prefs.HoverExpand, value => { prefs.HoverExpand = value; hover.Stop(); Save(); }));
        behavior.Children.Add(Hint(T("悬停等待时间（毫秒）", "Hover delay (milliseconds)")));
        var delay = new NumericUpDown { Minimum = 100, Maximum = 5000, Increment = 50, Value = prefs.HoverDelayMs, FormatString = "0" };
        delay.ValueChanged += (_, _) => { prefs.HoverDelayMs = (int)(delay.Value ?? 650); QueueSave(); }; behavior.Children.Add(delay);
        behavior.Children.Add(Hint(T("关闭悬停后，只点击才展开。拖动阈值触发前不会展开。", "When hover is off, click to expand. Pressing a widget cancels hover before dragging.")));
        behavior.Children.Add(Check(T("固定展开", "Pin expanded"), pinned, value => { pinned = value; if (value) Expand(); })); body.Children.Add(Card(behavior));

        var widgets = new StackPanel { Spacing = 6 };
        widgets.Children.Add(Hint(T("第一个收起组件", "First collapsed widget"))); widgets.Children.Add(WidgetPicker(prefs.Slot1, value => prefs.Slot1 = value));
        widgets.Children.Add(Hint(T("第二个收起组件", "Second collapsed widget"))); widgets.Children.Add(WidgetPicker(prefs.Slot2, value => prefs.Slot2 = value));
        widgets.Children.Add(Hint(T("Mac 刘海物理缺口不显示内容；组件在左右两翼，展开内容位于缺口下方。", "The physical camera cutout has no display pixels. Widgets use the two wings; expanded content starts below it."))); body.Children.Add(Card(widgets));

        var size = new StackPanel { Spacing = 6 }; size.Children.Add(Hint(T("展开宽度 / 高度", "Expanded width / height")));
        var width = new NumericUpDown { Minimum = 320, Maximum = 900, Value = (decimal)prefs.ExpandedWidth, FormatString = "0" };
        var height = new NumericUpDown { Minimum = 360, Maximum = 900, Value = (decimal)prefs.ExpandedHeight, FormatString = "0" };
        width.ValueChanged += (_, _) => { prefs.ExpandedWidth = (double)(width.Value ?? 480); QueueSave(); Place(); };
        height.ValueChanged += (_, _) => { prefs.ExpandedHeight = (double)(height.Value ?? 640); QueueSave(); Place(); };
        size.Children.Add(Actions(width, height)); body.Children.Add(Card(size));

        body.Children.Add(Card(new StackPanel { Spacing = 5, Children = {
            Text(T($"跨平台预览版 · {ApplicationVersion}", $"Cross-platform preview · {ApplicationVersion}"), 15),
            Hint(T($"已迁移 {Tools.Length} 个入口和共享核心，不等于 Windows 38 项完整适配。", $"{Tools.Length} entries and a shared core; this preview is not full parity with the 38-tool Windows edition.")),
            Hint(T("通知读取、截图和文件处理使用各系统接口，权限与桌面环境会影响可用范围。分应用音量、完整场景和媒体歌词仍需独立适配。", "Notifications, capture and file tools use platform interfaces; permissions and desktop support affect coverage. Per-app audio, full scenes and media lyrics still need separate adapters.")),
            Hint(T("Ubuntu 使用 X11／XWayland 浮窗；原生 Wayland 的位置和置顶受合成器控制。", "Ubuntu uses X11/XWayland floating windows; native Wayland placement and stacking depend on the compositor.")),
            Hint(T("Mac 安装包需要目标设备运行验证；当前构建不代表已签名或通过 Apple 公证。", "Mac packages require target-device validation. A cross-build is not Apple signing or notarization."))
        } }));
    }
    private CheckBox Check(string label, bool initial, Action<bool> changed)
    {
        var check = new CheckBox { Content = Text(label, 12), IsChecked = initial, Margin = new(0, 2), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        check.IsCheckedChanged += (_, _) => changed(check.IsChecked == true); return check;
    }
    private ComboBox WidgetPicker(string current, Action<string> set)
    {
        var names = new[] { T("Codex 短期额度", "Codex short-term"), T("Codex 每周额度", "Codex weekly"), T("专注计时", "Focus"), T("时钟", "Clock"), T("音乐", "Music"), T("任务动态", "Tasks"), T("保持唤醒", "Keep awake"), T("未读通知", "Unread notifications") };
        var picker = new ComboBox { ItemsSource = names, SelectedIndex = Math.Max(0, Array.IndexOf(Preferences.Widgets, current)) };
        picker.SelectionChanged += (_, _) => { if (picker.SelectedIndex < 0) return; set(Preferences.Widgets[picker.SelectedIndex]); Save(); RefreshCompact(); }; return picker;
    }
}
