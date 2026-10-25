using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexIsland
{
    // Measure the actual editor trees without creating native windows, showing
    // pickers, saving user data or launching resources. Native/DPI presentation
    // checks can use the same editor factories in their own visible fixtures.
    internal static class EditorDialogLayoutTests
    {
        internal static void Run()
        {
            string language = UiLocalization.Language, theme = UiTheme.Name;
            var launcher = new LauncherPanel(true); var scenes = new WorkspaceScenePanel(true);
            string path = "C:\\Fixture\\保存设置\\a long project folder\\" + new string('W', 180) + ".exe";
            string resourceName = "中文 / 保存设置 / " + new string('W', 35);
            var resource = new WorkspaceSceneResource { Name = resourceName, Kind = "program", Target = path, Arguments = "--fixture=\"保存设置 / " + new string('W', 100) + "\"" };
            var windows = new[] { launcher.CreateShortcutEditor(), scenes.CreateResourceEditor(null), scenes.CreateResourceEditor(resource) };
            int scenarios = 0;
            try
            {
                var shortcutTarget = Descendants<TextBox>(windows[0].Body).First(x => x.Name == "EntryTarget"); shortcutTarget.Text = path;
                foreach (string testLanguage in new[] { "zh", "en" })
                foreach (string testTheme in new[] { "light", "dark" })
                {
                    AppAppearance.Configure(testLanguage, testTheme);
                    foreach (var window in windows)
                    foreach (int width in new[] { 320, 430, 600 })
                    foreach (int height in new[] { 320, 520 })
                    foreach (bool longError in new[] { false, true })
                    {
                        window.Error.Text = longError ? String.Join(" ", Enumerable.Repeat("Fixture validation / 中文路径无法访问，请重试。", 20)) : "";
                        // Width and height are client-space DIP constraints; the
                        // native minimum remains 320 by 360 including its chrome.
                        window.Layout.Measure(new Size(width, height)); window.Layout.Arrange(new Rect(0, 0, width, height)); window.Layout.UpdateLayout();
                        string context = testLanguage + "/" + testTheme + "/" + window.Title + "/" + width + "x" + height + "/error=" + longError;
                        Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, context + ": layout checks created an HWND");
                        Check(window.MinWidth == 320 && window.MinHeight == 360 && window.ResizeMode == ResizeMode.CanResize, context + ": editor lost narrow-window resizing");
                        Check(window.Scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && window.Scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled, context + ": editor no longer scrolls vertically");
                        var actions = window.Footer.Children.OfType<Button>().ToArray();
                        Check(actions.Length == 2 && actions[0].IsCancel, context + ": fixed Cancel/Save actions missing");
                        Check(Math.Abs(actions[0].ActualWidth - actions[1].ActualWidth) <= 1 && Math.Abs(actions[0].ActualHeight - actions[1].ActualHeight) <= 1, context + ": footer actions differ in size (" + actions[0].ActualWidth + "x" + actions[0].ActualHeight + "/" + actions[1].ActualWidth + "x" + actions[1].ActualHeight + ")");
                        Check(actions.All(action => action.ActualWidth >= 90 && action.ActualHeight >= 32), context + ": footer action too small");
                        Check(Grid.GetRow(window.Footer) == 2 && !IsBelow(window.Footer, window.Scroll), context + ": footer moved into scrolling body");
                        Point bottom = window.Footer.TranslatePoint(new Point(0, window.Footer.ActualHeight), window.Layout);
                        Check(bottom.Y <= window.Layout.ActualHeight + 1, context + ": footer extends outside client area");
                        foreach (var frame in window.Body.Children.OfType<Border>()) Check(frame.Padding == new Thickness(12) && frame.Margin.Bottom == 8 && frame.CornerRadius.TopLeft == 16, context + ": editor card spacing differs");
                        foreach (var text in Descendants<TextBlock>(window.Layout).Where(item => item.ActualWidth > 0 && item.ActualHeight > 0 && item.Visibility == Visibility.Visible && !String.IsNullOrEmpty(item.Text)))
                        {
                            Check(text.ActualHeight + 1 >= text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom, context + ": cropped caption " + text.Text);
                            if (!HasAncestor<TextBox>(text)) Check(text.TextWrapping == TextWrapping.Wrap, context + ": non-wrapping editor caption " + text.Text);
                        }
                        foreach (var input in Descendants<TextBox>(window.Body)) Check(UiLocalization.IsProtected(input) && Equals(input.ToolTip, input.Text), context + ": input tooltip does not preserve complete original text");
                        scenarios++;
                    }
                    Check(shortcutTarget.Text == path, "Appearance translated shortcut input");
                    var savedTarget = Descendants<TextBox>(windows[2].Body).First(x => x.Name == "ResourceTarget");
                    var savedName = Descendants<TextBox>(windows[2].Body).First(x => x.Name == "ResourceName");
                    Check(savedTarget.Text == path && savedName.Text == resourceName && resource.Target == path && resource.Name == resourceName, "Editor inspection mutated an original resource");
                }
                var type = Descendants<ComboBox>(windows[1].Body).Single(); var arguments = Descendants<TextBox>(windows[1].Body).First(x => x.Name == "ResourceArguments");
                type.SelectedIndex = 2; Check(arguments.IsEnabled, "Program resource lost optional arguments"); arguments.Text = "--test";
                type.SelectedIndex = 0; Check(!arguments.IsEnabled && arguments.Text == "", "Non-program resource retained program arguments");
                Check(windows.All(window => new WindowInteropHelper(window).Handle == IntPtr.Zero), "Editor fixture created a native window");
                Console.WriteLine("PASS: " + scenarios + " compact editor layouts without HWNDs; Chinese/English, light/dark, narrow/large client constraints, long errors, equal fixed actions, wrapped captions and protected path tooltips.");
            }
            finally { foreach (var window in windows) window.Close(); scenes.Dispose(); AppAppearance.Configure(language, theme); }
        }
        private static bool IsBelow(DependencyObject item, DependencyObject ancestor) { for (DependencyObject parent = VisualTreeHelper.GetParent(item); parent != null; parent = VisualTreeHelper.GetParent(parent)) if (Object.ReferenceEquals(parent, ancestor)) return true; return false; }
        private static bool HasAncestor<T>(DependencyObject item) where T : DependencyObject { for (DependencyObject parent = VisualTreeHelper.GetParent(item); parent != null; parent = VisualTreeHelper.GetParent(parent)) if (parent is T) return true; return false; }
        private static IEnumerable<T> Descendants<T>(DependencyObject item) where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++) { var child = VisualTreeHelper.GetChild(item, index); var typed = child as T; if (typed != null) yield return typed; foreach (var descendant in Descendants<T>(child)) yield return descendant; }
            // Unmeasured forms have no template visuals yet; their form inputs
            // are still reachable through their logical tree.
            if (VisualTreeHelper.GetChildrenCount(item) == 0) foreach (object child in LogicalTreeHelper.GetChildren(item)) { var dependency = child as DependencyObject; if (dependency == null) continue; var typed = dependency as T; if (typed != null) yield return typed; foreach (var descendant in Descendants<T>(dependency)) yield return descendant; }
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
