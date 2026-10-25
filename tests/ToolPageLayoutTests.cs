using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    // Measures the real 38 tool pages in an isolated demo HWND. No global input,
    // clipboard writes, notification access, capture or player commands.
    internal static class ToolPageLayoutTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static void Run(string artifacts = null)
        {
            if (artifacts == null) artifacts = Environment.GetEnvironmentVariable("CODEX_ISLAND_LAYOUT_ARTIFACTS");
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), language = UiLocalization.Language, theme = UiTheme.Name;
            string directory = Path.Combine(Path.GetTempPath(), "CodexIsland.ToolPageLayout." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            var issues = new List<string>(); int scenarios = 0, buttons = 0, editors = 0;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", directory);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false,
                    CustomExpandedSize = true, ExpandedWidth = 440, ExpandedHeight = 720, FavoriteTools = "", HiddenTools = "" });
                window = new IslandWindow(true) { ShowActivated = false };
                window.Loaded += (s, e) => StopTimers(window);
                window.Show(); Pump(); StopTimers(window);
                Call(window, "SelectToolsPage"); window.SetExpanded(true, false);
                var settings = Read<Settings>(window, "settings");
                var panels = Read<Dictionary<string, UIElement>>(window, "toolPanels");
                if (artifacts != null) Directory.CreateDirectory(artifacts);
                foreach (string testLanguage in new[] { "zh", "en" })
                foreach (string testTheme in new[] { "light", "dark" })
                foreach (int width in new[] { 320, 388, 560, 900 })
                {
                    AppAppearance.Configure(testLanguage, testTheme); settings.ExpandedWidth = width; window.SetExpanded(true, false);
                    foreach (var tool in ToolCatalog.All)
                    {
                        Call(window, "SelectTool", tool.Key); Settle(window);
                        var panel = (FrameworkElement)panels[tool.Key]; scenarios++;
                        string context = tool.Key + "/" + testLanguage + "/" + testTheme + "/" + width;
                        if (panel.ActualWidth < 150 || !panel.IsMeasureValid || !panel.IsArrangeValid) issues.Add(context + ": page did not settle");
                        foreach (var control in Descendants<Control>(panel).Where(c => c.IsVisible && (c is Button || c is TextBox || c is ComboBox || c is CheckBox)))
                        {
                            Point origin = control.TranslatePoint(new Point(), panel);
                            if (!InHorizontalScroller(control, panel) && (origin.X < -1 || origin.X + control.ActualWidth > panel.ActualWidth + 1))
                                issues.Add(context + ": control outside page " + control.GetType().Name + " " + Caption(control) + " x=" + origin.X + " w=" + control.ActualWidth + " page=" + panel.ActualWidth);
                            var button = control as Button;
                            if (button != null)
                            {
                                buttons++;
                                foreach (var caption in Descendants<TextBlock>(button).Where(t => t.IsVisible))
                                    if (caption.DesiredSize.Width > caption.ActualWidth + 1 || caption.DesiredSize.Height > caption.ActualHeight + 1)
                                        issues.Add(context + ": cropped button caption " + Caption(button));
                            }
                            var input = control as TextBox;
                            if (input != null && input.Text.Length > 0 && input.VerticalOffset < .1)
                            {
                                editors++; Rect glyph = input.GetRectFromCharacterIndex(0);
                                if (!glyph.IsEmpty && (glyph.Top < -1 || glyph.Bottom > input.ActualHeight + 1 || glyph.Height < input.FontSize))
                                    issues.Add(context + ": cropped first input glyph " + glyph + " height=" + input.ActualHeight);
                            }
                        }
                        foreach (var grid in Descendants<Grid>(panel).Where(g => g.GetType().Name == "ActionGrid" && g.IsVisible)) CheckGroup(grid, context, issues);
                        if (artifacts != null && width == 388) SavePanel(panel, Path.Combine(artifacts, tool.Key + "-" + testLanguage + "-" + testTheme + ".png"));
                    }
                }
                WindowHeights(window, panels, issues);
                AlternateModes(window, panels, issues);
                CompactContentStateTests.Run(window, panels);
                CompactMediaStateTests.Run(window, panels);
                EditorAndReflow(issues);
                if (artifacts != null) File.WriteAllLines(Path.Combine(artifacts, "layout-measurements.txt"), new[] {
                    "Scenarios=" + scenarios + "; buttons=" + buttons + "; editors=" + editors + "; issues=" + issues.Count,
                    "Real WPF demo pages; widths320/388/560/900; languages zh/en; themes light/dark." }.Concat(issues));
                if (issues.Count > 0) throw new Exception("Tool page layout (" + issues.Count + "): " + String.Join("\n", issues.Take(35)));
                Console.WriteLine("PASS: " + scenarios + " actual tool-page layouts, " + buttons + " captions and " + editors + " input glyphs across 4 widths, Chinese/English and light/dark; responsive action groups, hidden-action reflow and editor content/selection/scrolling.");
            }
            finally
            {
                if (window != null) { StopTimers(window); window.Close(); }
                AppAppearance.Configure(language, theme);
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
        private static void WindowHeights(IslandWindow window, Dictionary<string, UIElement> panels, List<string> issues)
        {
            var settings = Read<Settings>(window, "settings"); int oldHeight = settings.ExpandedHeight, scenarios = 0;
            var scroll = Read<ScrollViewer>(window, "detailsScroll");
            try
            {
                foreach (string language in new[] { "zh", "en" })
                foreach (string theme in new[] { "light", "dark" })
                foreach (int width in new[] { 320, 900 })
                foreach (int height in new[] { 360, 1100 })
                {
                    AppAppearance.Configure(language, theme); settings.ExpandedWidth = width; settings.ExpandedHeight = height; window.SetExpanded(true, false);
                    foreach (var tool in ToolCatalog.All)
                    {
                        Call(window, "SelectTool", tool.Key); Settle(window); scenarios++;
                        string context = tool.Key + "/" + language + "/" + theme + "/" + width + "x" + height;
                        if (Math.Abs(window.ActualWidth - width) > 1) issues.Add(context + ": requested window width was not applied");
                        if (scroll.ViewportHeight <= 100) issues.Add(context + ": usable page viewport is too short");
                        scroll.ScrollToBottom(); Settle(window);
                        if (Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) > 1) issues.Add(context + ": cannot scroll to the end of the tool page");
                        var panel = (FrameworkElement)panels[tool.Key];
                        foreach (var control in Descendants<Control>(panel).Where(c => c.IsVisible && (c is Button || c is TextBox || c is ComboBox || c is CheckBox)))
                        {
                            Point origin = control.TranslatePoint(new Point(), panel);
                            if (!InHorizontalScroller(control, panel) && (origin.X < -1 || origin.X + control.ActualWidth > panel.ActualWidth + 1)) issues.Add(context + ": resized page control overflows");
                        }
                    }
                }
            }
            finally { settings.ExpandedHeight = oldHeight; window.SetExpanded(true, false); scroll.ScrollToTop(); Settle(window); }
            Console.WriteLine("Measured: " + scenarios + " short/tall tool-page layouts and end-of-page scrolling at heights360/1100.");
        }
        private static void AlternateModes(IslandWindow window, Dictionary<string, UIElement> panels, List<string> issues)
        {
            var settings = Read<Settings>(window, "settings"); int scenarios = 0;
            string[] keys = { "time", "random", "converter", "text", "image", "qr" };
            string[] fields = { "mode", "mode", "category", "operation", "format", "correction" };
            foreach (string language in new[] { "zh", "en" })
            foreach (string theme in new[] { "light", "dark" })
            foreach (int width in new[] { 320, 900 })
            {
                AppAppearance.Configure(language, theme); settings.ExpandedWidth = width; window.SetExpanded(true, false);
                for (int index = 0; index < keys.Length; index++)
                {
                    var panel = (FrameworkElement)panels[keys[index]];
                    var choice = Read<ComboBox>(panel, fields[index]); int selected = choice.SelectedIndex;
                    try
                    {
                        Call(window, "SelectTool", keys[index]);
                        for (int mode = 0; mode < choice.Items.Count; mode++)
                        {
                            choice.SelectedIndex = mode; Settle(window); scenarios++;
                            string context = keys[index] + "/mode=" + mode + "/" + language + "/" + theme + "/" + width;
                            foreach (var control in Descendants<Control>(panel).Where(c => c.IsVisible && (c is Button || c is TextBox || c is CheckBox || c is ComboBox)))
                            {
                                Point origin = control.TranslatePoint(new Point(), panel);
                                if (!InHorizontalScroller(control, panel) && (origin.X < -1 || origin.X + control.ActualWidth > panel.ActualWidth + 1)) issues.Add(context + ": mode control overflows page");
                                if (control is Button || control is CheckBox || control is ComboBox)
                                    foreach (var caption in Descendants<TextBlock>(control).Where(t => t.IsVisible))
                                        if (caption.DesiredSize.Width > caption.ActualWidth + 1 || caption.DesiredSize.Height > caption.ActualHeight + 1) issues.Add(context + ": mode caption is cropped " + caption.Text);
                            }
                            foreach (var group in Descendants<Grid>(panel).Where(g => g.IsVisible && g.GetType().Name == "ActionGrid")) CheckGroup(group, context, issues);
                        }
                    }
                    finally { choice.SelectedIndex = selected; }
                }
            }
            Console.WriteLine("Measured: " + scenarios + " alternate tool modes across narrow/wide windows, languages and themes.");
        }
        private static void EditorAndReflow(List<string> issues)
        {
            var window = new Window { Width = 320, Height = 480, ShowActivated = false, ShowInTaskbar = false, FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI") };
            AppAppearance.Register(window);
            var host = new StackPanel { Margin = new Thickness(12) }; window.Content = host;
            var first = IslandWindow.Button("保存并继续", () => { }); var hidden = IslandWindow.Button("Cancel", () => { }); var third = IslandWindow.Button("Copy processed result", () => { });
            var actions = PanelActionLayout.Create(3, first, hidden, third); host.Children.Add(actions);
            var text = ToolStyle.Input("中文 AbgQ 09"); text.Height = 32; host.Children.Add(text);
            var rich = new RichTextBox { Height = 96, Padding = new Thickness(10, 6, 10, 6), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var option = new CheckBox { Content = "Keep aspect ratio / 保持比例" }; host.Children.Add(option);
            var emptyHint = ToolStyle.Hint(""); host.Children.Add(emptyHint);
            rich.Document.Blocks.Add(new Paragraph(new Run("中文 config=value\nsecond\nthird\nfourth\nfifth\nsixth")) { Margin = new Thickness(0) }); host.Children.Add(rich);
            string original = new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text;
            try
            {
                window.Show(); window.UpdateLayout(); Pump();
                var optionCaption = Descendants<TextBlock>(option).First(value => value.Text.Contains("Keep aspect"));
                if (optionCaption.TextAlignment != TextAlignment.Left) issues.Add("checkbox caption drifts away from its tick");
                if (emptyHint.Visibility != Visibility.Collapsed) issues.Add("empty status hint reserves blank space");
                emptyHint.Text = "Visible status"; window.UpdateLayout(); Pump();
                if (emptyHint.Visibility != Visibility.Visible) issues.Add("nonempty status hint does not reappear");
                foreach (int width in new[] { 180, 260, 520, 180 })
                {
                    actions.Width = width; window.UpdateLayout(); Pump(); window.UpdateLayout();
                    CheckGroup(actions, "fixture/" + width, issues);
                }
                hidden.Visibility = Visibility.Collapsed; actions.Width = 520; window.UpdateLayout(); Pump(); window.UpdateLayout();
                if (Grid.GetColumn(third) != 2 || actions.RowDefinitions.Count != 1) issues.Add("hidden action leaves a hole or unused row");
                hidden.Visibility = Visibility.Visible; window.UpdateLayout(); Pump(); window.UpdateLayout();
                if (Grid.GetColumn(third) != 4) issues.Add("restored action did not reflow");
                text.Select(3, 4); AppAppearance.Configure("en", "light"); window.UpdateLayout(); Pump();
                Rect glyph = text.GetRectFromCharacterIndex(0);
                text.Padding = new Thickness(4, 6, 4, 6); window.UpdateLayout(); Pump();
                Rect reducedPadding = text.GetRectFromCharacterIndex(0);
                text.Padding = new Thickness(10, 6, 10, 6); window.UpdateLayout();
                if (glyph.IsEmpty || glyph.Top < 0 || glyph.Bottom > text.ActualHeight || Math.Abs(glyph.Left - reducedPadding.Left - 6) > .75)
                    issues.Add("editor applies padding more than once or clips a 32px input: " + glyph);
                if (text.Text != "中文 AbgQ 09" || text.SelectionStart != 3 || text.SelectionLength != 4 || new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text != original)
                    issues.Add("appearance change modified input text, selection or document");
                rich.ScrollToEnd(); window.UpdateLayout(); Pump();
                if (rich.VerticalOffset <= 0) issues.Add("rich editor lost vertical scrolling");
                rich.ScrollToHome(); window.UpdateLayout();
                Rect richGlyph = rich.Document.ContentStart.GetNextInsertionPosition(LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward);
                if (richGlyph.IsEmpty || richGlyph.Top < 0 || richGlyph.Bottom > rich.ActualHeight) issues.Add("rich editor first line is cropped");
            }
            finally { window.Close(); }
        }
        private static void CheckGroup(Grid grid, string context, List<string> issues)
        {
            var actions = grid.Children.OfType<Button>().Where(b => b.Visibility != Visibility.Collapsed).ToArray();
            if (actions.Length == 0) return;
            foreach (var button in actions)
                if (Math.Abs(button.ActualWidth - actions[0].ActualWidth) > 1 || Math.Abs(button.ActualHeight - actions[0].ActualHeight) > 1)
                    issues.Add(context + ": unequal action dimensions " + Caption(button));
            foreach (var row in actions.GroupBy(Grid.GetRow))
            {
                var ordered = row.OrderBy(Grid.GetColumn).ToArray();
                for (int i = 1; i < ordered.Length; i++)
                {
                    Point a = ordered[i - 1].TranslatePoint(new Point(), grid), b = ordered[i].TranslatePoint(new Point(), grid);
                    if (Math.Abs(b.X - a.X - ordered[i - 1].ActualWidth - 6) > 1 || Math.Abs(a.Y - b.Y) > 1)
                        issues.Add(context + ": action gap or baseline differs from 6px");
                }
            }
        }
        private static string Caption(Control c) { var content = c as ContentControl; return content == null ? "" : Convert.ToString(content.Content); }
        private static bool InHorizontalScroller(DependencyObject item, DependencyObject page)
        {
            for (var ancestor = VisualTreeHelper.GetParent(item); ancestor != null && ancestor != page; ancestor = VisualTreeHelper.GetParent(ancestor))
            { var scroll = ancestor as ScrollViewer; if (scroll != null && scroll.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled && scroll.ExtentWidth > scroll.ViewportWidth + 1) return true; }
            return false;
        }
        private static void SavePanel(FrameworkElement panel, string path)
        {
            int width = Math.Max(1, (int)Math.Ceiling(panel.ActualWidth)), height = Math.Max(1, (int)Math.Ceiling(panel.ActualHeight));
            // Render in local coordinates: Render(panel) includes its parent
            // offset and can crop the bottom of otherwise correctly laid-out UI.
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(Display.Brush("#111518"), null, new Rect(0, 0, width, height));
                context.DrawRectangle(new VisualBrush(panel) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
            }
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var file = File.Create(path)) encoder.Save(file);
        }
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            { var child = VisualTreeHelper.GetChild(root, i); var value = child as T; if (value != null) yield return value; foreach (var descendant in Descendants<T>(child)) yield return descendant; }
        }
        private static object Call(object value, string method, params object[] args) { try { return value.GetType().GetMethod(method, Private).Invoke(value, args); } catch (TargetInvocationException e) { throw e.InnerException ?? e; } }
        private static T Read<T>(object value, string field) { return (T)value.GetType().GetField(field, Private).GetValue(value); }
        private static void StopTimers(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); }
        private static void Settle(IslandWindow window) { window.UpdateLayout(); Pump(); StopTimers(window); window.UpdateLayout(); Pump(); StopTimers(window); }
        private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    }
}
