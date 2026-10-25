using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexIsland
{
    internal static class ToolStyle
    {
        private sealed class HintVisibility : System.Windows.Data.IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture) { return String.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible; }
            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) { throw new NotSupportedException(); }
        }
        private static readonly HintVisibility EmptyHint = new HintVisibility();
        public static Border Card(UIElement content) { return new Border { Child = content, Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8), Background = Display.Brush("#1A2323"), BorderBrush = Display.Brush("#252F31"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16) }; }
        public static TextBox Input(string text = "") { return new TextBox { Text = text, MinHeight = 32, Padding = new Thickness(10, 6, 10, 6), Background = Display.Brush("#202D29"), Foreground = Display.Brush("#E3EFE7"), BorderBrush = Display.Brush("#3A5145"), BorderThickness = new Thickness(1), FontSize = 12, CaretBrush = Display.Brush("#E3EFE7") }; }
        public static TextBlock Hint(string text, bool localize = true) { var label = IslandWindow.Text(text, 11, "#8FA89A", false, localize); label.TextWrapping = TextWrapping.Wrap; label.LineHeight = 16; label.Margin = new Thickness(0, 4, 0, 6); label.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("Text") { Source = label, Converter = EmptyHint }); return label; }
        public static TextBlock Heading(string text) { var label = IslandWindow.Text(text, 16, "#E5F2E9", true); label.Margin = new Thickness(0, 0, 0, 6); label.TextWrapping = TextWrapping.Wrap; return label; }
    }

    public sealed class QuickShortcut { public string Name { get; set; } public string Target { get; set; } }
    public sealed class ShortcutStore
    {
        private const int MaximumFileBytes = 128 * 1024;
        private readonly string path;
        private bool unreadablePreserved;
        public bool LoadFailed { get; private set; }
        public ShortcutStore(string location = null) { path = Path.GetFullPath(location ?? Path.Combine(Path.GetDirectoryName(new SettingsStore().FilePath), "shortcuts.json")); }
        public List<QuickShortcut> Load()
        {
            LoadFailed = false; unreadablePreserved = false;
            try
            {
                if (!File.Exists(path)) return new List<QuickShortcut>();
                if (new FileInfo(path).Length > MaximumFileBytes) throw new IOException();
                var raw = new JavaScriptSerializer { MaxJsonLength = MaximumFileBytes, RecursionLimit = 8 }.Deserialize<List<QuickShortcut>>(File.ReadAllText(path, new UTF8Encoding(false, true)));
                if (raw == null) throw new ArgumentException();
                var result = Normalize(raw);
                if (result.Count != raw.Count || raw.Any(x => x != null && ((x.Name != null && x.Name.Length > 35) || (x.Target != null && x.Target.Length > 2048)))) LoadFailed = true;
                return result;
            }
            catch (IOException) { LoadFailed = true; }
            catch (UnauthorizedAccessException) { LoadFailed = true; }
            catch (ArgumentException) { LoadFailed = true; }
            catch (InvalidOperationException) { LoadFailed = true; }
            return new List<QuickShortcut>();
        }
        public void Save(IEnumerable<QuickShortcut> entries)
        {
            if (entries == null) throw new ArgumentNullException("entries");
            Directory.CreateDirectory(Path.GetDirectoryName(path)); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(Normalize(entries)));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (LoadFailed && !unreadablePreserved && File.Exists(path)) { File.Copy(path, path + ".unreadable-" + Guid.NewGuid().ToString("N") + ".bak", false); unreadablePreserved = true; }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                LoadFailed = false;
            }
            finally { if (File.Exists(temporary)) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        private static List<QuickShortcut> Normalize(IEnumerable<QuickShortcut> entries)
        {
            return entries.Where(x => x != null && !String.IsNullOrWhiteSpace(x.Name) && !String.IsNullOrWhiteSpace(x.Target)).Take(20)
                .Select(x => new QuickShortcut { Name = Bound(x.Name, 35), Target = Bound(x.Target, 2048) }).ToList();
        }
        private static string Bound(string text, int maximum) { if (text.Length <= maximum) return text; return text.Substring(0, Char.IsHighSurrogate(text[maximum - 1]) ? maximum - 1 : maximum); }
        public static bool IsAllowedTarget(string target)
        {
            if (String.IsNullOrWhiteSpace(target) || target.Length > 2048) return false;
            Uri uri;
            if (Uri.TryCreate(target, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https") && !String.IsNullOrEmpty(uri.Host)) return true;
            try { return Path.IsPathRooted(target) && (Directory.Exists(target) || (File.Exists(target) && new[] { ".exe", ".lnk" }.Contains(Path.GetExtension(target).ToLowerInvariant()))); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }
    }

    internal sealed class LauncherPanel : StackPanel
    {
        private readonly bool demo;
        private readonly ShortcutStore store = new ShortcutStore();
        private readonly List<QuickShortcut> shortcuts;
        private readonly StackPanel list = new StackPanel();
        private readonly TextBlock status = ToolStyle.Hint("");
        public LauncherPanel(bool demoMode)
        {
            demo = demoMode; shortcuts = demo ? new List<QuickShortcut>() : store.Load();
            Children.Add(ToolStyle.Heading("一步打开，少找一会儿"));
            Children.Add(ToolStyle.Hint(demo ? "演示模式 · 快捷操作不会执行" : "固定常用应用、文件夹和网址。点击后在系统中打开。"));
            var actions = new[] {
                CreateAction("文件管理", "explorer.exe"), CreateAction("桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                CreateAction("截图", "ms-screenclip:"), CreateAction("任务管理器", "taskmgr.exe"),
                CreateAction("Wi-Fi", "ms-settings:network-wifi"), CreateAction("蓝牙", "ms-settings:bluetooth"),
                CreateAction("显示设置", "ms-settings:display"), CreateAction("声音设置", "ms-settings:sound")
            };
            var launcherCard = ToolStyle.Card(PanelActionLayout.Create(4, 100, actions));
            Children.Add(launcherCard); Children.Add(IslandWindow.Text("我的快捷入口", 12, "#C1D8C9", true));
            list.Margin = new Thickness(0, 6, 0, 0); Children.Add(list);
            var add = PanelActionLayout.Caption(IslandWindow.Button("＋ 固定应用 / 文件夹 / 网址", AddShortcut)); Children.Add(add); Children.Add(status); Rebuild();
            if (!demo && store.LoadFailed) status.Text = "原入口文件部分或全部无法读取；保存修改前会先保留原文件备份。";
        }
        private Button CreateAction(string title, string target) { return IslandWindow.Button(title, () => Open(target)); }
        private void Open(string target)
        {
            if (demo) { status.Text = "演示模式不打开外部应用。"; return; }
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); status.Text = "已交给 Windows 打开"; }
            catch { status.Text = "暂时无法打开，请检查应用或路径是否可用。"; }
        }
        private void Rebuild()
        {
            list.Children.Clear();
            foreach (var entry in shortcuts.ToList())
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                var remove = IslandWindow.Button("×", () => { var candidate = shortcuts.Where(x => !Object.ReferenceEquals(x, entry)).ToList(); if (!Persist(candidate)) return; shortcuts.Remove(entry); Rebuild(); }); remove.ToolTip = "移除此入口"; remove.Width = 32; remove.MinHeight = 32; remove.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
                var open = UiLocalization.Protect(PanelActionLayout.Caption(IslandWindow.Button(entry.Name, () => { if (ShortcutStore.IsAllowedTarget(entry.Target)) Open(entry.Target); else status.Text = "入口已不可用，请重新添加。"; }, false))); open.ToolTip = entry.Target; row.Children.Add(open); list.Children.Add(row);
            }
            if (shortcuts.Count == 0) list.Children.Add(ToolStyle.Hint("还没有固定入口。"));
        }
        private bool Persist(IEnumerable<QuickShortcut> candidate)
        {
            if (demo) return true;
            try { store.Save(candidate); status.Text = "入口已保存"; return true; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            status.Text = "保存失败，原入口保持不变。请检查本地目录权限后重试。"; return false;
        }
        internal bool TryAddShortcut(QuickShortcut entry)
        {
            if (shortcuts.Count >= 20) { status.Text = "最多固定 20 个入口。"; return false; }
            var candidate = shortcuts.ToList(); candidate.Add(entry);
            if (!Persist(candidate)) return false;
            shortcuts.Add(entry); Rebuild(); return true;
        }
        private void AddShortcut()
        {
            if (shortcuts.Count >= 20) { status.Text = "最多固定 20 个入口。"; return; }
            CreateShortcutEditor().ShowDialog(); Rebuild();
        }
        internal CompactEditorWindow CreateShortcutEditor()
        {
            var window = new CompactEditorWindow("固定快捷入口", 380, Window.GetWindow(this));
            var content = CompactDialogLayout.Card(window);
            var name = CompactDialogLayout.Input("", 35); name.Name = "EntryName";
            var target = CompactDialogLayout.Input("", 2048); target.Name = "EntryTarget"; var error = window.Error;
            CompactDialogLayout.Field(content, "入口名称", name); CompactDialogLayout.Field(content, "完整网址、应用路径或文件夹路径", target);
            var browse = IslandWindow.Button("选应用", () => { var picker = new Microsoft.Win32.OpenFileDialog { Title = UiLocalization.Localize("选应用"), Filter = UiLocalization.FileFilter("应用与快捷方式|*.exe;*.lnk") }; if (picker.ShowDialog(window) == true) { target.Text = picker.FileName; if (String.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileNameWithoutExtension(picker.FileName); } });
            var folder = IslandWindow.Button("选文件夹", () => { using (var picker = new System.Windows.Forms.FolderBrowserDialog { Description = UiLocalization.Localize("选文件夹") }) { if (NativeDialogs.ShowFolder(picker, window) == System.Windows.Forms.DialogResult.OK) { target.Text = picker.SelectedPath; if (String.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileName(picker.SelectedPath); } } });
            var save = IslandWindow.Button("保存", () => { if (String.IsNullOrWhiteSpace(name.Text) || !ShortcutStore.IsAllowedTarget(target.Text.Trim())) { error.Text = "请输入名称和有效的应用、文件夹或 http(s) 网址。"; return; } if (!TryAddShortcut(new QuickShortcut { Name = name.Text.Trim(), Target = target.Text.Trim() })) { error.Text = status.Text; return; } window.DialogResult = true; });
            content.Children.Add(CompactDialogLayout.Browse(browse, folder)); window.SetActions(save); return window;
        }
    }

    internal sealed class ClipboardPanel : StackPanel
    {
        private readonly bool demo; private readonly List<string> clips = new List<string>(); private readonly StackPanel list = new StackPanel(); private readonly TextBlock status = ToolStyle.Hint("");
        public ClipboardPanel(bool demoMode)
        {
            demo = demoMode; Children.Add(ToolStyle.Heading("临时文字收纳"));
            Children.Add(ToolStyle.Hint("只在点击收纳时读取剪贴板；最多 15 条，退出即清空。"));
            Children.Add(PanelActionLayout.Create(2, IslandWindow.Button("收纳当前剪贴板", Collect), IslandWindow.Button("清空收纳", () => { clips.Clear(); Rebuild(); }))); Children.Add(status); Children.Add(list);
            if (demo) { clips.Add("这是一段示例文字，点击卡片可复制。"); status.Text = "演示模式 · 不读取或写入系统剪贴板"; } Rebuild();
        }
        private void Collect()
        {
            if (demo) return;
            try
            {
                if (!Clipboard.ContainsText()) { status.Text = "剪贴板中没有文本。"; return; }
                string value = Clipboard.GetText(); if (value.Length > 8000) { status.Text = "文字超过 8000 字，请先缩短后再收纳。"; return; }
                if (String.IsNullOrWhiteSpace(value)) { status.Text = "剪贴板文本为空。"; return; }
                clips.Remove(value); clips.Insert(0, value); if (clips.Count > 15) clips.RemoveAt(15); status.Text = "已收纳 · 仅保存在本次运行内存中"; Rebuild();
            }
            catch { status.Text = "剪贴板正被其他应用占用，请稍后再试。"; }
        }
        private void Rebuild()
        {
            list.Children.Clear();
            foreach (string clip in clips.ToList())
            {
                var stack = new StackPanel(); var text = IslandWindow.Text(clip.Length > 180 ? clip.Substring(0, 180) + "…" : clip, 12, "#C7DECF", false, false); text.TextWrapping = TextWrapping.Wrap; text.MaxHeight = 85; stack.Children.Add(text);
                var copy = IslandWindow.Button("复制", () => { if (demo) return; try { Clipboard.SetText(clip); status.Text = "已复制"; } catch { status.Text = "复制失败，请重试。"; } });
                var buttons = PanelActionLayout.Create(2, copy, IslandWindow.Button("移除", () => { clips.Remove(clip); Rebuild(); })); buttons.Margin = new Thickness(0, 6, 0, 0); stack.Children.Add(buttons); list.Children.Add(ToolStyle.Card(stack));
            }
            if (clips.Count == 0) list.Children.Add(ToolStyle.Hint("收纳区还是空的。"));
        }
    }

    internal sealed class CalculatorPanel : StackPanel
    {
        private readonly TextBox expression = ToolStyle.Input(); private readonly TextBlock result = IslandWindow.Text("—", 30, "#A1EACF", true); private readonly TextBlock status = ToolStyle.Hint("");
        private readonly Button copyResult;
        private readonly bool demo;
        public CalculatorPanel(bool demoMode)
        {
            demo = demoMode; Children.Add(ToolStyle.Heading("随手算一算")); Children.Add(ToolStyle.Hint("支持 + − * /、括号、^ 幂、% 百分数，及 sqrt / abs / round。"));
            expression.MaxLength = 256; expression.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Calculate(); e.Handled = true; } }; Children.Add(expression);
            result.TextWrapping = TextWrapping.Wrap; result.Margin = new Thickness(0, 8, 0, 8); Children.Add(result);
            var calculate = IslandWindow.Button("计算 ↵", Calculate);
            copyResult = IslandWindow.Button("复制结果", () => { if (!copyResult.IsEnabled) return; if (demo) { status.Text = "演示模式不写入剪贴板。"; return; } try { Clipboard.SetText(result.Text); status.Text = "已复制"; } catch { status.Text = "复制失败，请重试。"; } }); copyResult.IsEnabled = false; Children.Add(PanelActionLayout.Create(2, calculate, copyResult)); Children.Add(status);
            expression.TextChanged += (s, e) => { result.Text = "—"; copyResult.IsEnabled = false; status.Text = expression.Text.Length == 0 ? "输入表达式后按 Enter 计算。" : "输入已更改，按 Enter 重新计算。"; };
            var samples = new List<Button>(); foreach (string sample in new[] { "(128 + 64) * 1.2", "sqrt(144) + 2^3", "250 * 15%" }) { var value = sample; samples.Add(IslandWindow.Button(sample, () => { expression.Text = value; Calculate(); })); } Children.Add(ToolStyle.Card(PanelActionLayout.Create(3, samples.ToArray())));
            if (demo) { expression.Text = "(128 + 64) * 1.2"; Calculate(); }
        }
        internal void Calculate() { try { result.Text = Calculator.Evaluate(expression.Text).ToString("G12", CultureInfo.InvariantCulture); copyResult.IsEnabled = true; status.Text = "本地计算 · % 表示除以 100"; } catch (ArgumentException error) { result.Text = "—"; copyResult.IsEnabled = false; status.Text = error.Message; } }
    }
}
