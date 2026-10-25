using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    // Production form/content factories, measured and rasterized at real WPF
    // visual/text DPI. Detached trees never start screen selection or capture.
    internal static class PopupSurfaceDpiTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly List<string> Measurements = new List<string>();
        private static int scenarios, captions, glyphs, screenshots;

        internal static void Run(string directory = null)
        {
            directory = directory ?? Environment.GetEnvironmentVariable("CODEX_ISLAND_POPUP_ARTIFACTS");
            string previousLanguage = UiLocalization.Language, previousTheme = UiTheme.Name;
            scenarios = captions = glyphs = screenshots = 0; Measurements.Clear();
            try
            {
                foreach (string locale in new[] { "zh", "en" })
                foreach (string theme in new[] { "light", "dark" })
                {
                    AppAppearance.Configure(locale, theme);
                    string suffix = locale + "-" + theme;
                    Editors(suffix, directory); Notices(suffix, directory); Pins(suffix, directory); Selection(suffix, directory); ToolTips(suffix, directory);
                }
                if (!String.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllLines(Path.Combine(directory, "measurements.txt"), new[] { "Scenarios=" + scenarios + "; Captions=" + captions + "; GlyphRuns=" + glyphs + "; Screenshots=" + screenshots,
                        "Scope=WPF visual/text DPI 96/120/144/192 via public VisualTreeHelper.SetRootDpi; full production trees; actual GlyphRun.PixelsPerDip assertions; no ScaleTransform and no OS display setting changes. Native HWND DPI coverage is recorded separately by PopupDpiTestHost." }.Concat(Measurements));
                }
                Console.WriteLine("PASS: " + scenarios + " popup/floating WPF DPI layouts, " + captions + " captions and " + glyphs + " glyph runs; zh/en, light/dark, 100/125/150/200%, narrow/wide, long validation/messages, scroll access and original input data; " + screenshots + " screenshots.");
            }
            finally { AppAppearance.Configure(previousLanguage, previousTheme); }
        }

        private static void Editors(string suffix, string directory)
        {
            var launcher = new LauncherPanel(true);
            using (var scenes = new WorkspaceScenePanel(true))
            {
                string path = "C:\\Fixture\\保存设置\\a long folder\\" + new string('W', 130) + ".exe";
                var resource = new WorkspaceSceneResource { Name = "原文 / 保存设置 / " + new string('N', 35), Kind = "program", Target = path, Arguments = "--fixture=\"保存设置 / " + new string('A', 110) + "\"" };
                var windows = new[] { launcher.CreateShortcutEditor(), scenes.CreateResourceEditor(null), scenes.CreateResourceEditor(resource) };
                for (int index = 0; index < windows.Length; index++)
                {
                    var window = windows[index]; var root = Detach(window);
                    foreach (var input in PopupDpiTests.Descendants(window.Body).OfType<TextBox>()) if (input.Text.Length == 0) input.Text = input.Name.Contains("Target") ? path : "原文 / 保存设置 / fixture";
                    var original = PopupDpiTests.Descendants(window.Body).OfType<TextBox>().ToDictionary(input => input.Name, input => input.Text);
                    try
                    {
                        foreach (int dpi in PopupDpiTests.Values)
                        foreach (int width in new[] { 320, 430, 600 })
                        foreach (bool error in new[] { false, true })
                        {
                            window.Error.Text = error ? String.Join(" ", Enumerable.Repeat("Fixture validation / 中文路径无法访问，请重试。", 24)) : "";
                            string context = "editor-" + index + "/" + suffix + "/" + dpi + "/" + width + "/error=" + error;
                            Layout(root, width, 320, dpi, true); window.Scroll.ScrollToHome(); root.UpdateLayout();
                            VerifySurface(root, dpi, context);
                            var buttons = window.Footer.Children.OfType<Button>().ToArray();
                            Check(buttons.Length == 2 && Math.Abs(buttons[0].ActualWidth - buttons[1].ActualWidth) < 1.1 && Math.Abs(buttons[0].ActualHeight - buttons[1].ActualHeight) < 1.1, context + ": footer buttons differ");
                            Check(Bounds(window.Footer, root).Bottom <= root.ActualHeight + 1.1, context + ": footer cropped");
                            foreach (var input in PopupDpiTests.Descendants(window.Body).OfType<TextBox>()) Check(input.Text == original[input.Name] && Convert.ToString(input.ToolTip) == input.Text, context + ": original editor data changed");
                            if (width == 320 && !error && index != 1) Save(root, directory, "editor-" + index + "-" + suffix + "-" + dpi, dpi);
                            if (error)
                            {
                                window.Scroll.ScrollToEnd(); root.UpdateLayout(); PopupDpiTests.Pump();
                                Check(window.Scroll.ScrollableHeight < 1 || Math.Abs(window.Scroll.VerticalOffset - window.Scroll.ScrollableHeight) < 1.1, context + ": validation cannot be reached by scrolling");
                            }
                            scenarios++;
                        }
                    }
                    finally { root.Child = null; window.Close(); }
                }
                Check(resource.Target == path, "original resource modified by DPI checks");
            }
        }

        private static void Notices(string suffix, string directory)
        {
            foreach (MessageBoxImage severity in new[] { MessageBoxImage.Information, MessageBoxImage.Warning, MessageBoxImage.Error })
            foreach (bool longMessage in new[] { false, true })
            {
                string message = "原始异常 / 保存设置 / C:\\Fixture\\a long folder\\file.json\n" + (longMessage ? String.Join("\n", Enumerable.Repeat("Detail / 中文详情 with original path C:\\Fixture\\" + new string('P', 55) + ".json", 45)) : UiLocalization.Localize("所选 Codex 程序不存在。"));
                var window = new NoticeWindow(message, UiLocalization.Localize("修改尚未保存"), severity);
                var root = Detach(window);
                try
                {
                    foreach (int dpi in PopupDpiTests.Values)
                    foreach (int width in new[] { 280, 420, 560 })
                    {
                        string context = "notice/" + suffix + "/" + severity + "/long=" + longMessage + "/" + dpi + "/" + width;
                        Layout(root, width, 360, dpi, false); window.MessageScroll.ScrollToHome(); root.UpdateLayout(); VerifySurface(root, dpi, context);
                        Check(window.Message.Text == message && window.MessageScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled, context + ": original message changed");
                        Check(Bounds(window.Accept, root).Bottom <= root.ActualHeight + 1.1 && window.Accept.ActualHeight >= 32, context + ": fixed accept button cropped");
                        if (longMessage) { Check(window.MessageScroll.ScrollableHeight > 0, context + ": long error cannot scroll"); window.MessageScroll.ScrollToEnd(); root.UpdateLayout(); PopupDpiTests.Pump(); Check(Math.Abs(window.MessageScroll.VerticalOffset - window.MessageScroll.ScrollableHeight) < 1.1, context + ": long message end unreachable"); }
                        if (severity == MessageBoxImage.Warning && width == 280 && !longMessage) Save(root, directory, "notice-" + suffix + "-" + dpi, dpi);
                        scenarios++;
                    }
                }
                finally { root.Child = null; window.Close(); }
            }
        }

        private static void Pins(string suffix, string directory)
        {
            foreach (bool large in new[] { false, true })
            {
                BitmapSource image = Sample(large ? 900 : 24, large ? 520 : 18);
                var window = new ScreenPinWindow(image); var root = Detach(window);
                try
                {
                    foreach (int dpi in PopupDpiTests.Values)
                    foreach (int width in new[] { 240, 320, 600 })
                    foreach (double zoom in new[] { 100d, 300d })
                    {
                        window.ZoomPercent = zoom; window.OpacityPercent = 100;
                        string context = "pin/" + suffix + "/large=" + large + "/" + dpi + "/" + width + "/zoom=" + zoom;
                        Layout(root, width, 420, dpi, false); VerifySurface(root, dpi, context);
                        var toolbar = Read<Grid>(window, "toolbar"); var first = Read<Slider>(window, "zoom"); var second = Read<Slider>(window, "transparency");
                        Check(first.ActualWidth >= 70 && Math.Abs(first.ActualWidth - second.ActualWidth) <= 1.1, context + ": slider columns differ");
                        Check(Bounds(toolbar, root).Bottom <= root.ActualHeight + 1.1, context + ": footer cropped");
                        Check(Object.ReferenceEquals(window.Snapshot, image) && image.IsFrozen, context + ": generated image was recolored or replaced");
                        if (!large && width == 240 && zoom == 100) Save(root, directory, "pin-" + suffix + "-" + dpi, dpi);
                        scenarios++;
                    }
                }
                finally { root.Child = null; window.Close(); Check(window.Snapshot == null, "pin bitmap not released"); }
            }
        }

        private static void Selection(string suffix, string directory)
        {
            var desktop = new Int32Rect(-1920, -480, 3840, 2160);
            var window = (ScreenPinSelection)Activator.CreateInstance(typeof(ScreenPinSelection), Private, null, new object[] { desktop }, null);
            var root = Detach(window); var canvas = (Canvas)root.Child;
            try
            {
                foreach (int dpi in PopupDpiTests.Values)
                foreach (int width in new[] { 240, 320, 600 })
                foreach (bool error in new[] { false, true })
                {
                    window.Width = width; window.Height = 220;
                    Call(window, "UpdateSelection", new Int32Rect(1780, 1640, 140, 40));
                    var badge = Read<Border>(window, "sizeCard");
                    if (error) { UiLocalization.Text((TextBlock)badge.Child, "区域太小，请重新拖选。"); Call(window, "PositionBadge", new Rect(width - 2, 1, 0, 0), new Rect(0, 0, width, 220)); }
                    string context = "selection/" + suffix + "/" + dpi + "/" + width + "/error=" + error;
                    Layout(root, width, 220, dpi, true); VerifySurface(root, dpi, context);
                    foreach (var card in new[] { badge, Read<Border>(window, "instructionCard") }) { Rect bounds = Bounds(card, root); Check(bounds.Left >= -1.1 && bounds.Top >= -1.1 && bounds.Right <= root.ActualWidth + 1.1 && bounds.Bottom <= root.ActualHeight + 1.1, context + ": overlay caption outside monitor " + bounds + "/" + root.RenderSize + ";desired=" + card.DesiredSize + ";max=" + card.MaxWidth); }
                    if (width == 240 && !error) Save(root, directory, "selection-" + suffix + "-" + dpi, dpi);
                    Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, context + ": selection created a real capture overlay"); scenarios++;
                }
            }
            finally { root.Child = null; window.Close(); }
        }

        private static void ToolTips(string suffix, string directory)
        {
            foreach (string kind in new[] { "short", "long", "visual", "reused-visual" })
            {
                bool visual = kind == "visual" || kind == "reused-visual";
                string original = kind == "short" ? UiLocalization.Localize("拖动拉伸 · 朝屏幕内侧拖动可放大") : String.Join(" ", Enumerable.Repeat("原始路径 保存设置 C:\\Fixture\\" + new string('W', 35) + ".exe / Complete original tooltip", 30));
                var custom = new StackPanel(); custom.Children.Add(new TextBlock { Text = original, FontSize = 12, TextWrapping = TextWrapping.Wrap }); custom.Children.Add(new TextBlock { Text = UiLocalization.Localize("物理网卡链路状态；不测试互联网连接。"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
                var tip = new ToolTip { Content = kind == "visual" ? (object)custom : original };
                UiTheme.PrepareToolTip(tip);
                if (kind == "reused-visual") { tip.Content = custom; UiTheme.PrepareToolTip(tip); }
                try
                {
                    foreach (int dpi in PopupDpiTests.Values)
                    {
                        string context = "tooltip/" + suffix + "/" + kind + "/" + dpi;
                        Layout(tip, 240, 220, dpi, false); VerifySurface(tip, dpi, context);
                        Check(visual ? Object.ReferenceEquals(tip.Content, custom) && tip.ContentTemplate == null : (string)tip.Content == original && tip.ContentTemplate != null, context + ": tooltip lost original content or custom visual");
                        var scroll = PopupDpiTests.Descendants(tip).OfType<ScrollViewer>().First();
                        if (kind != "short") { Check(scroll.ScrollableHeight > 0, context + ": long tooltip does not scroll"); scroll.ScrollToEnd(); tip.UpdateLayout(); PopupDpiTests.Pump(); Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1.1, context + ": tooltip end unreachable"); }
                    Save(tip, directory, "tooltip-" + (kind == "short" ? "" : kind + "-") + suffix + "-" + dpi, dpi);
                        scenarios++;
                    }
                }
                finally { tip.IsOpen = false; }
            }
        }

        private static Border Detach(Window window)
        {
            var content = (FrameworkElement)window.Content; window.Content = null;
            var root = new Border { Child = content, Background = window.Background, UseLayoutRounding = window.UseLayoutRounding, SnapsToDevicePixels = true };
            TextElement.SetFontFamily(root, window.FontFamily); TextElement.SetFontSize(root, window.FontSize); TextElement.SetForeground(root, window.Foreground);
            UiTheme.Install(root.Resources); return root;
        }
        private static void Layout(FrameworkElement root, double width, double height, int dpi, bool fill)
        {
            PopupDpiTests.Apply(root, dpi); root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, fill ? height : Math.Min(height, root.DesiredSize.Height))); root.UpdateLayout(); PopupDpiTests.Pump();
        }
        private static void VerifySurface(FrameworkElement root, int dpi, string context)
        {
            var bitmap = PopupDpiTests.Render(root, dpi); PopupDpiTests.Verify(root, dpi, context);
            Check(bitmap.PixelWidth == (int)Math.Ceiling(root.RenderSize.Width * dpi / 96d) && Math.Abs(bitmap.DpiX - dpi) < .1, context + ": raster does not match requested DPI");
            int formatted = 0;
            foreach (var caption in PopupDpiTests.Descendants(root).OfType<TextBlock>().Where(text => text.Visibility == Visibility.Visible && text.ActualWidth > 0 && text.ActualHeight > 0 && !String.IsNullOrEmpty(text.Text)))
            {
                Check(caption.ActualHeight + 1.1 >= caption.DesiredSize.Height - caption.Margin.Top - caption.Margin.Bottom, context + ": cropped caption " + caption.Text.Substring(0, Math.Min(48, caption.Text.Length)) + " " + caption.ActualHeight + "/" + caption.DesiredSize.Height);
                // A root drawing forced at this DPI must contain at least one
                // actual text glyph; stale clipped scroll-body drawings need
                // not be rendered until scrolled into view.
                if (formatted == 0 && Bounds(caption, root).IntersectsWith(new Rect(root.RenderSize)))
                    formatted = PopupDpiTests.VerifyGlyphDpi(caption, dpi, context);
                captions++;
            }
            Check(formatted > 0, context + ": no text glyph was rendered"); glyphs += formatted;
            Measurements.Add(context + "; Size=" + root.RenderSize + "; Raster=" + bitmap.PixelWidth + "x" + bitmap.PixelHeight + "; " + PopupDpiTests.Describe(root));
        }
        private static void Save(FrameworkElement root, string directory, string name, int dpi) { if (!String.IsNullOrEmpty(directory)) { PopupDpiTests.Save(root, directory, name, dpi); screenshots++; } }
        private static BitmapSource Sample(int width, int height)
        {
            var pixels = new byte[width * height * 4]; for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) { int p = (y * width + x) * 4; pixels[p] = (byte)(90 + x * 90 / width); pixels[p + 1] = (byte)(90 + y * 120 / height); pixels[p + 2] = (byte)(35 + x * 80 / width); pixels[p + 3] = 255; }
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); image.Freeze(); return image;
        }
        private static Rect Bounds(FrameworkElement element, Visual root) { return element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)); }
        private static T Read<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        private static void Call(object owner, string name, params object[] args) { owner.GetType().GetMethod(name, Private).Invoke(owner, args); }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Popup surface DPI: " + message); }
    }

    internal static class PopupSurfaceDpiTestHost
    {
        [STAThread] private static int Main(string[] args)
        {
            try { PopupSurfaceDpiTests.Run(args.Length == 0 ? null : args[0]); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
