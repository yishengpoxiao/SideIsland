using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class AppearanceTests
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception("Appearance: " + message); }
        internal static void Run()
        {
            string oldLanguage = UiLocalization.Language, oldTheme = UiTheme.Name;
            try
            {
                UiLocalization.Configure("zh"); UiTheme.Configure("dark");
                var label = UiLocalization.Text(new TextBlock(), "保存设置");
                var source = UiLocalization.Protect(new TextBlock { Text = "保存设置" });
                var input = new TextBox { Text = "保存设置\nC:\\中文\\项目.txt" };
                UiLocalization.Text(source, "保存设置");
                var selection = new ComboBox { ItemsSource = new[] { "开始", "停止保持唤醒" }, SelectedIndex = 1 };
                int selectionChanges = 0; selection.SelectionChanged += (s, e) => selectionChanges++;
                UiLocalization.Combo(selection);
                UiLocalization.Configure("en");
                Check(label.Text == "Save settings", "registered UI label changes to English");
                Check(source.Text == "保存设置" && input.Text == "保存设置\nC:\\中文\\项目.txt", "user content and paths remain byte-for-byte unchanged");
                Check(selection.SelectedIndex == 1 && (string)selection.SelectedItem == "停止保持唤醒" && selectionChanges == 0, "locale leaves ComboBox values and business selection unchanged");
                Check(UiLocalization.Localize("新增 3 行 · 删除 2 行 · 修改 1 行") == "3 added lines · 2 removed · 1 changed", "anchored diff template translates numeric fields");
                Check(UiLocalization.Localize("已另存 D:\\项目\\保存设置.png") == "Saved as D:\\项目\\保存设置.png", "localized status preserves a Chinese file path");
                Check(UiLocalization.Localize("这是用户的中文内容") == "这是用户的中文内容", "unknown content is never transliterated or replaced");
                foreach (var definition in ToolCatalog.All) { Check(UiLocalization.HasTranslation(definition.Label), "catalog contains tool label " + definition.Key); Check(UiLocalization.HasTranslation(definition.SettingsLabel), "catalog contains settings label " + definition.Key); }
                foreach (string key in CompactWidgetCatalog.Keys) Check(UiLocalization.Localize(CompactWidgetCatalog.Label(key, "zh")) == CompactWidgetCatalog.Label(key, "en"), "compact widget follows its canonical label " + key);
                Check(UiLocalization.FileFilter("Codex 程序|*.exe;*.cmd;*.ps1|所有文件|*.*") == "Codex program|*.exe;*.cmd;*.ps1|All files|*.*", "native file filter translates descriptions only");
                Check(UiLocalization.FileFilter("所有文件|所有文件") == "All files|所有文件", "native filter matching patterns are never translated");
                Check(UiLocalization.Localize("与黑色对比 15.19:1 · 与白色对比 1.38:1\n搭配黑色文字更清晰。") == "Contrast with black 15.19:1 · with white 1.38:1\nBlack text is easier to read.", "color result localizes only the built-in black/white recommendation");
                Check(UiLocalization.Localize("0.001\n千米 km") == "0.001\nKilometers km", "unit result preserves the number while translating its built-in unit label");
                Check(UiLocalization.Localize("待提醒 · 每天 18:00\n下次：2026-10-24 18:00") == "Pending · Daily 18:00\nNext: 2026-10-24 18:00", "daily reminder structure localizes completely");
                UiLocalization.Configure("zh"); Check(label.Text == "保存设置", "switching back restores source text");
                Check(UiLocalization.FileFilter("Codex 程序|*.exe;*.cmd;*.ps1|所有文件|*.*") == "Codex 程序|*.exe;*.cmd;*.ps1|所有文件|*.*", "Chinese native filter remains unchanged");
                var brush = (SolidColorBrush)Display.Brush("#111518"); Color dark = brush.Color;
                var preview = new Border { Background = new SolidColorBrush(Color.FromRgb(12, 34, 56)) }; UiTheme.ProtectColor(preview);
                var swatch = new TextBlock { Text = "#FFFFFF", Foreground = Brushes.White }; preview.Child = swatch;
                UiTheme.Configure("light"); UiTheme.Prepare(swatch);
                var lightBrush = (SolidColorBrush)Display.Brush("#111518");
                Check(!Object.ReferenceEquals(brush, lightBrush) && lightBrush.Color.R > 230 && brush.Color == dark && brush.IsFrozen && lightBrush.IsFrozen, "theme changes select a frozen snapshot without changing a retired brush");
                Check(Object.ReferenceEquals(swatch.Foreground, Brushes.White) && ((SolidColorBrush)preview.Background).Color == Color.FromRgb(12, 34, 56), "protected image/color subtree preserves original colors");
                var resources = new ResourceDictionary(); UiTheme.Install(resources); UiTheme.Install(resources);
                Check(resources.MergedDictionaries.Count == 1 && resources.Contains(typeof(ComboBox)), "theme installs complete control resources once");
                Check(brush.IsFrozen && !System.Windows.Data.BindingOperations.IsDataBound(brush, SolidColorBrush.ColorProperty), "semantic snapshots carry no mutable Color binding into native resources");
                var implicitStyle = new Style(typeof(TextBox)); implicitStyle.Setters.Add(new Setter(Control.BackgroundProperty, Display.Brush("#202D29"))); implicitStyle.Seal();
                Check(((SolidColorBrush)Display.Brush("#202D29")).IsFrozen, "sealed styles retain immutable theme snapshots");
                UiTheme.Configure("dark"); Check(brush.Color == dark && Object.ReferenceEquals(brush, Display.Brush("#111518")), "switching theme back reuses the exact original frozen palette");
                ShownWindow();
                ThemeSnapshotTests.Run();
                Console.WriteLine("PASS: appearance localization, 38 tool labels, dynamic templates, untouched user data, unchanged selections, immutable live themes and control resources.");
            }
            finally { UiTheme.Configure(oldTheme); UiLocalization.Configure(oldLanguage); }
        }
        private static void ShownWindow()
        {
            AppAppearance.Configure("en", "light");
            var window = new Window { Width = 360, Height = 380, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen, Title = "Appearance test" };
            var body = new StackPanel { Background = Display.Brush("#111518") }; window.Content = body;
            var status = UiLocalization.Text(new TextBlock(), "保存设置"); body.Children.Add(status);
            var input = new TextBox { Text = "保存设置\nD:\\项目\\原文.txt" }; body.Children.Add(input);
            var choice = new ComboBox { ItemsSource = new[] { "开始", "停止保持唤醒" }, SelectedIndex = 1 }; body.Children.Add(choice);
            var button = UiLocalization.Content(new Button(), "保存设置"); body.Children.Add(button);
            body.Children.Add(new RichTextBox { Height = 60 });
            int changes = 0; choice.SelectionChanged += (s, e) => changes++;
            AppAppearance.Register(window);
            try
            {
                window.Show(); window.UpdateLayout(); Pump();
                Check(status.Text == "Save settings" && (string)button.Content == "Save settings", "startup in saved English survives Loaded and control template creation");
                for (int index = 0; index < 4; index++) { AppAppearance.Configure("en", "light"); window.UpdateLayout(); Pump(); Check((string)button.Content == "Save settings", "shown buttons update without being rebuilt"); AppAppearance.Configure("zh", "dark"); window.UpdateLayout(); Pump(); }
                AppAppearance.Configure("en", "light");
                status.Text = "已保存：D:\\项目\\原文.txt";
                Check(status.Text == "Saved: D:\\项目\\原文.txt", "shown dynamic status updates use registered templates");
                Check(input.Text == "保存设置\nD:\\项目\\原文.txt" && choice.SelectedIndex == 1 && changes == 0, "live language/theme switches preserve editor and current selection");
                Check(choice.Template != null && choice.Template.FindName("PART_Popup", choice) != null, "shown dropdown uses themed popup template");
                Check(((SolidColorBrush)Display.Brush("#202D29")).IsFrozen, "actual controls use immutable semantic brushes after template creation");
                AppAppearance.Configure("zh", "dark"); Check(status.Text == "已保存：D:\\项目\\原文.txt", "live dynamic state restores source language");
                AppAppearance.Configure("en", "light"); status.Text = "新增 3 行 · 删除 2 行 · 修改 1 行"; status.Text += " · 完整差异已统计，预览仅显示前 600 行。";
                Check(status.Text.IndexOf("完整", StringComparison.Ordinal) < 0, "appended status suffixes stay localized");
                AppAppearance.Configure("zh", "dark"); Check(status.Text == "新增 3 行 · 删除 2 行 · 修改 1 行 · 完整差异已统计，预览仅显示前 600 行。", "appended localized status restores every source segment");
            }
            finally { window.Close(); Pump(); }
        }
        private static void Pump()
        {
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
        }
    }
}
