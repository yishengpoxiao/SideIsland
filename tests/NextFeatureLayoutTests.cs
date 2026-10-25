using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    // Detached WPF metadata/glyph DPI matrix plus a separate own-HWND focus
    // check. Never changes monitor DPI, network access, clipboard or user files.
    internal static class NextFeatureLayoutTests
    {
        private sealed class Fixture : IDisposable
        {
            internal FrameworkElement Panel;
            internal readonly List<IDisposable> Resources = new List<IDisposable>();
            public void Dispose() { foreach (var resource in Resources) resource.Dispose(); }
        }
        private static readonly string[] Keys = { "autorules", "widgets", "audio-mixer", "lan-transfer", "batch", "activities", "notificationtasks", "annotation" };
        private static int cases, captions, groups, glyphs;
        private static readonly HashSet<string> captionEvidenceKeys = new HashSet<string>(StringComparer.Ordinal);
        private static readonly PropertyInfo previousMeasureConstraint = typeof(UIElement).GetProperty("PreviousConstraint", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly string[] StaticCaptions = {
            "自动场景规则", "保存全部规则", "组件布局编辑器", "保存组件布局",
            "分应用音量", "设为默认输出", "局域网传文件", "开始分享",
            "批量文件处理", "任务都在这里继续", "脚本本地接入", "通知待办",
            "复制标注图片", "撤销标注"
        };
        internal static void Run()
        {
            string language = UiLocalization.Language, theme = UiTheme.Name;
            cases = captions = groups = glyphs = 0;
            captionEvidenceKeys.Clear();
            var issues = new List<string>();
            try
            {
                foreach (string caption in StaticCaptions)
                {
                    Check(UiLocalization.HasTranslation(caption), "Missing registered static translation: " + caption);
                    UiLocalization.Configure("en"); Check(UiLocalization.Localize(caption) != caption, "Static caption did not translate: " + caption);
                }
                CaptionMeasurementCalibration();
                foreach (string lang in new[] { "zh", "en" })
                foreach (string color in new[] { "light", "dark" })
                foreach (int width in new[] { 320, 391, 590, 900 })
                foreach (int height in new[] { 360, 750 })
                foreach (int dpi in new[] { 96, 120, 144, 192 })
                {
                    AppAppearance.Configure(lang, color);
                    foreach (string key in Keys)
                    {
                        using (var fixture = Create(key))
                        {
                            double clientWidth = width - 32, clientHeight = height - 112;
                            var viewport = new ScrollViewer { Content = fixture.Panel, Width = clientWidth, Height = clientHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, UseLayoutRounding = true, SnapsToDevicePixels = true };
                            UiTheme.Install(viewport.Resources); AppAppearance.RegisterTree(viewport);
                            string context = key + "/" + lang + "/" + color + "/" + width + "x" + height + "/" + dpi;
                            PopupDpiTests.Apply(viewport, dpi);
                            Layout(viewport, clientWidth, clientHeight);
                            // Verify actual glyph formatter DPI, not only root metadata.
                            PopupDpiTests.Render(viewport, dpi);
                            glyphs += PopupDpiTests.VerifyGlyphDpi(viewport, dpi, context);
                            PopupDpiTests.Verify(viewport, dpi, context);
                            Validate(fixture.Panel, context, issues);
                            viewport.ScrollToBottom(); Layout(viewport, clientWidth, clientHeight);
                            if (Math.Abs(viewport.VerticalOffset - viewport.ScrollableHeight) > 1.1) issues.Add(context + ": page end is unreachable");
                            if (viewport.ViewportHeight < clientHeight - 2) issues.Add(context + ": excessive reserved viewport space");
                            Validate(fixture.Panel, context + "/end", issues);
                            viewport.Content = null; cases++;
                        }
                    }
                }
                FocusAndDraftFixture(issues);
                if (issues.Count != 0) throw new InvalidOperationException("New feature layout issues " + issues.Count + ":\n" + String.Join("\n", issues));
                Console.WriteLine("PASS: " + cases + " new feature detached layouts; " + captions + " caption checks, " + groups + " action groups and " + glyphs + " glyph DPI checks; zh/en, light/dark, widths320/391/590/900, heights360/750, DPI96/120/144/192; own native text focus/selection and scroll-end access.");
            }
            finally { AppAppearance.Configure(language, theme); }
        }
        private static Fixture Create(string key)
        {
            var fixture = new Fixture();
            if (key == "autorules")
            {
                var store = new AutoSceneRuleStore(true);
                store.Data.Rules.Add(new AutoSceneRule { Name = "User label 中文 / English", Executable = "Code.exe", ProfileId = "code" });
                fixture.Panel = new AutoSceneRulesPanel(store, () => LayoutProfileStore.Builtins(), () => null, () => { }, () => { }, () => { });
            }
            else if (key == "widgets")
            {
                var store = new WidgetLayoutStore(true); var layout = WidgetLayoutPolicy.Default("", new Settings()); layout.Enabled = true;
                layout.Cards.Add(new WidgetCardDefinition { Key = "focus", Size = "large" }); layout.Cards.Add(new WidgetCardDefinition { Key = "notifications", Size = "small" }); store.Save(layout);
                fixture.Panel = new WidgetLayoutEditorPanel(store, () => LayoutProfileStore.Builtins(), () => new Settings(), () => "", () => { });
            }
            else if (key == "audio-mixer") { var panel = new AudioMixerPanel(true); fixture.Panel = panel; fixture.Resources.Add(panel); }
            else if (key == "lan-transfer") { var panel = new LanTransferPanel(true); fixture.Panel = panel; fixture.Resources.Add(panel); }
            else if (key == "batch") { var panel = new BatchFilesPanel(true); fixture.Panel = panel; fixture.Resources.Add(panel); }
            else if (key == "activities")
            {
                var hub = new TaskActivityHub(); hub.Publish(new TaskActivityUpdate { Id = "layout1", Title = "User build 中文 with a long project name", Message = "User content is preserved", Source = "User source", Progress = 37 });
                hub.Publish(new TaskActivityUpdate { Id = "layout2", Title = "Unknown progress", Message = "No estimated progress", Source = "script" });
                var panel = new TaskActivityPanel(true, hub); fixture.Panel = panel; fixture.Resources.Add(panel);
            }
            else if (key == "notificationtasks")
            {
                var panel = new NotificationTasksPanel(true);
                var item = panel.Add(new IslandNotification { Id = "layout", AppId = "fixture", AppName = "User application", Title = "用户消息 / message text must stay unchanged", Body = "User body", CreatedAt = DateTime.UtcNow }, 15);
                panel.EditProject(item.Id); fixture.Panel = panel; fixture.Resources.Add(panel);
            }
            else
            {
                var bitmap = BitmapSource.Create(240, 120, 96, 96, PixelFormats.Bgra32, null, new byte[240 * 120 * 4], 240 * 4); bitmap.Freeze();
                var document = new ScreenshotAnnotationDocument(bitmap); var surface = new ScreenshotAnnotationSurface(document) { Height = 120, MinWidth = 0 };
                document.Add(ScreenshotAnnotationKind.Rectangle, new Point(5, 5), new Point(80, 60), "");
                var panel = new StackPanel(); panel.Children.Add(surface); panel.Children.Add(new ScreenshotAnnotationControls(document, surface)); fixture.Panel = panel; fixture.Resources.Add(document);
            }
            fixture.Panel.UseLayoutRounding = true; fixture.Panel.SnapsToDevicePixels = true;
            return fixture;
        }
        private static void Layout(FrameworkElement panel, double width, double height)
        { panel.Measure(new Size(width, height)); panel.Arrange(new Rect(0, 0, width, height)); panel.UpdateLayout(); PopupDpiTests.Pump(); panel.UpdateLayout(); }
        private static void Validate(FrameworkElement panel, string context, IList<string> issues)
        {
            if (!panel.IsMeasureValid || !panel.IsArrangeValid || panel.ActualWidth <= 0) { issues.Add(context + ": unsettled page"); return; }
            foreach (var control in PopupDpiTests.Descendants(panel).OfType<Control>().Where(c => Effective(c, panel) && (c is Button || c is TextBox || c is ComboBox || c is CheckBox || c is PasswordBox)))
            {
                if (control.ActualWidth < .1) continue;
                var origin = control.TranslatePoint(new Point(), panel);
                if (!InsideHorizontalScroller(control, panel) && (origin.X < -1.1 || origin.X + control.ActualWidth > panel.ActualWidth + 1.1)) issues.Add(context + ": control exceeds page " + control.GetType().Name);
                var button = control as Button;
                if (button != null)
                {
                    string sourceCaption = button.Content as string;
                    if (UiLocalization.IsEnglish && sourceCaption != null && !UiLocalization.IsProtected(button) && UiLocalization.Localize(sourceCaption) != sourceCaption) issues.Add(context + ": registered action was not translated " + sourceCaption);
                    if (!button.Focusable || !KeyboardNavigation.GetIsTabStop(button)) issues.Add(context + ": action lost keyboard access " + button.Content);
                    foreach (var text in PopupDpiTests.Descendants(button).OfType<TextBlock>().Where(t => Effective(t, button)))
                    {
                        captions++;
                        if (text.TextTrimming == TextTrimming.None) RecordCaptionIssue(text, true, context, "action", "cropped action caption " + button.Content, panel, issues);
                    }
                }
            }
            foreach (var text in PopupDpiTests.Descendants(panel).OfType<TextBlock>().Where(t => Effective(t, panel) && !UiLocalization.IsProtected(t) && t.TextTrimming == TextTrimming.None && t.ActualWidth > 0))
            {
                captions++;
                if (text.TextWrapping != TextWrapping.NoWrap) RecordCaptionIssue(text, false, context, "wrapped", "cropped wrapped UI caption " + text.Text, panel, issues);
            }
            foreach (var group in PopupDpiTests.Descendants(panel).OfType<Grid>().Where(g => g.GetType().Name == "ActionGrid" && Effective(g, panel)))
            {
                groups++;
                var buttons = group.Children.OfType<Button>().Where(b => b.Visibility != Visibility.Collapsed).ToArray();
                foreach (var row in buttons.GroupBy(Grid.GetRow))
                {
                    double firstWidth = row.First().ActualWidth, firstHeight = row.First().ActualHeight;
                    if (row.Any(b => Math.Abs(b.ActualWidth - firstWidth) > 1.1 || Math.Abs(b.ActualHeight - firstHeight) > 1.1)) issues.Add(context + ": unequal grouped actions");
                    var ordered = row.OrderBy(Grid.GetColumn).ToArray();
                    for (int i = 1; i < ordered.Length; i++)
                    {
                        double gap = ordered[i].TranslatePoint(new Point(), group).X - ordered[i - 1].TranslatePoint(new Point(ordered[i - 1].ActualWidth, 0), group).X;
                        if (Math.Abs(gap - 6) > 1.1) issues.Add(context + ": action gap differs from compact spacing");
                    }
                }
            }
        }
        private static void RecordCaptionIssue(TextBlock text, bool checkWidth, string context, string kind, string issue, FrameworkElement panel, IList<string> issues)
        {
            var measured = MeasureCaption(text, checkWidth);
            if (!measured.Cropped) return;
            issues.Add(context + ": " + issue);
            string key = kind + "|" + UiLocalization.Language + "|" + VisualTreeHelper.GetDpi(text).PixelsPerInchY.ToString(CultureInfo.InvariantCulture) + "|" + text.Text;
            if (!captionEvidenceKeys.Add(key)) return;
            Console.WriteLine("CAPTION ISSUE " + context + "; Kind=" + kind + "; CheckWidth=" + checkWidth + "; Text=" + text.Text.Replace("\r", "\\r").Replace("\n", "\\n") + "; " + CaptionEvidence(text, measured));
            Console.WriteLine("CAPTION REASONS DesiredWidth=" + (checkWidth && measured.DesiredContent.Width > measured.Visible.Width + 1.1) +
                "; DesiredHeight=" + (measured.DesiredContent.Height > measured.Visible.Height + 1.1) + "; NaturalWidth=" + (checkWidth && measured.NaturalDesired.Width > measured.Visible.Width + 1.1) +
                "; NaturalHeight=" + (measured.NaturalDesired.Height > measured.Visible.Height + 1.1) + "; EmptyVisible=" + (!String.IsNullOrEmpty(text.Text) && (measured.Visible.Width <= 0 || measured.Visible.Height <= 0)));
            int depth = 0;
            for (DependencyObject current = text; current != null; current = VisualTreeHelper.GetParent(current))
            {
                var element = current as FrameworkElement;
                if (element != null)
                {
                    var clip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(element);
                    var visualClip = VisualTreeHelper.GetClip(element);
                    var control = element as Control; var border = element as Border;
                    string padding = control != null ? control.Padding.ToString() : border != null ? border.Padding.ToString() : "n/a";
                    string origin;
                    try { origin = element.TranslatePoint(new Point(), panel).ToString(CultureInfo.InvariantCulture); } catch { origin = "unavailable"; }
                    Console.WriteLine(String.Format(CultureInfo.InvariantCulture,
                        "CAPTION CHAIN {0}: Type={1}; Name={2}; TemplatedParent={3}; Desired={4}; Actual={5},{6}; Margin={7}; Padding={8}; LayoutSlot={9}; LayoutClip={10}; VisualClip={11}; ClipToBounds={12}; Height={13}; MinHeight={14}; MaxHeight={15}; Align={16}/{17}; OriginInPanel={18}; RenderTransform={19}; LayoutTransform={20}; MeasureValid={21}; ArrangeValid={22}",
                        depth, element.GetType().Name, element.Name, element.TemplatedParent == null ? "none" : element.TemplatedParent.GetType().Name,
                        element.DesiredSize, element.ActualWidth, element.ActualHeight, element.Margin, padding,
                        System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(element), clip == null ? "none" : clip.Bounds.ToString(CultureInfo.InvariantCulture), visualClip == null ? "none" : visualClip.Bounds.ToString(CultureInfo.InvariantCulture),
                        element.ClipToBounds, element.Height, element.MinHeight, element.MaxHeight, element.HorizontalAlignment, element.VerticalAlignment,
                        origin, element.RenderTransform, element.LayoutTransform, element.IsMeasureValid, element.IsArrangeValid));
                }
                depth++;
            }
            Console.Out.Flush();
        }
        private sealed class CaptionMeasurement
        {
            internal Size Desired, DesiredContent, Actual, OriginalConstraint, NaturalConstraint, NaturalDesired, Visible;
            internal Thickness Margin;
            internal Rect LayoutSlot;
            internal Geometry LayoutClip, VisualClip;
            internal bool Cropped;
        }
        private static CaptionMeasurement MeasureCaption(TextBlock text, bool checkWidth)
        {
            // FrameworkElement.DesiredSize includes its layout margin, whereas
            // ActualWidth/ActualHeight describe only the rendered element. Do
            // not treat a heading's bottom spacing as hidden text.
            Thickness margin = text.Margin;
            var measured = new CaptionMeasurement {
                Desired = text.DesiredSize, Margin = margin, Actual = new Size(text.ActualWidth, text.ActualHeight),
                DesiredContent = new Size(Math.Max(0, text.DesiredSize.Width - margin.Left - margin.Right), Math.Max(0, text.DesiredSize.Height - margin.Top - margin.Bottom)),
                LayoutSlot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(text),
                LayoutClip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(text), VisualClip = VisualTreeHelper.GetClip(text)
            };
            Rect visible = new Rect(new Point(), measured.Actual);
            if (measured.LayoutClip != null) visible.Intersect(measured.LayoutClip.Bounds);
            if (measured.VisualClip != null) visible.Intersect(measured.VisualClip.Bounds);
            measured.Visible = visible.IsEmpty ? new Size() : visible.Size;
            Check(previousMeasureConstraint != null && previousMeasureConstraint.PropertyType == typeof(Size), "This WPF runtime does not expose its original measure constraint; caption validation cannot substitute arranged width.");
            measured.OriginalConstraint = (Size)previousMeasureConstraint.GetValue(text, null);

            // UIElement stores the actual last Measure argument. A left-aligned
            // ContentPresenter can subsequently arrange this TextBlock at its
            // rounded DesiredWidth (112 instead of its 246-wide measure input).
            // Reflowing a clone at that rounded render width invents an extra
            // line which the original correctly formatted text does not have.
            // Restore FrameworkElement's margin and explicit/min/max-width
            // processing, while lifting only the vertical restriction.
            double width = Math.Max(0, measured.OriginalConstraint.Width - margin.Left - margin.Right);
            width = Math.Max(text.MinWidth, Math.Min(width, text.MaxWidth));
            if (!Double.IsNaN(text.Width)) width = Math.Max(text.MinWidth, Math.Min(text.Width, text.MaxWidth));
            var natural = CreateNaturalCaption(text);
            measured.NaturalConstraint = new Size(text.TextWrapping == TextWrapping.NoWrap ? Double.PositiveInfinity : width, Double.PositiveInfinity);
            natural.Measure(measured.NaturalConstraint); measured.NaturalDesired = natural.DesiredSize;
            measured.Cropped = (checkWidth && measured.DesiredContent.Width > measured.Visible.Width + 1.1) ||
                measured.DesiredContent.Height > measured.Visible.Height + 1.1 || measured.NaturalDesired.Height > measured.Visible.Height + 1.1 ||
                (checkWidth && measured.NaturalDesired.Width > measured.Visible.Width + 1.1) ||
                (!String.IsNullOrEmpty(text.Text) && (measured.Visible.Width <= 0 || measured.Visible.Height <= 0));
            return measured;
        }
        private static TextBlock CreateNaturalCaption(TextBlock text)
        {
            var natural = new TextBlock {
                Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize,
                FontWeight = text.FontWeight, FontStyle = text.FontStyle, FontStretch = text.FontStretch,
                TextWrapping = text.TextWrapping, TextTrimming = TextTrimming.None,
                Padding = text.Padding, LineHeight = text.LineHeight, LineStackingStrategy = text.LineStackingStrategy,
                Language = text.Language, FlowDirection = text.FlowDirection,
                UseLayoutRounding = text.UseLayoutRounding, SnapsToDevicePixels = text.SnapsToDevicePixels
            };
            TextOptions.SetTextFormattingMode(natural, TextOptions.GetTextFormattingMode(text));
            VisualTreeHelper.SetRootDpi(natural, VisualTreeHelper.GetDpi(text));
            return natural;
        }
        private static string CaptionEvidence(TextBlock text, CaptionMeasurement measured)
        {
            return String.Format(CultureInfo.InvariantCulture,
                "DesiredSize={0}; ActualSize={1}; Margin={2}; DesiredContent={3}; NaturalConstraint={4}; NaturalDesired={5}; Visible={6}; LayoutSlot={7}; LayoutClip={8}; VisualClip={9}; Font={10}/{11}; Wrap={12}; Height={13}; MaxHeight={14}; LineHeight={15}; LineStacking={16}; DPI={17}; TextLength={18}; OriginalConstraint={19}; Formatting={20}",
                measured.Desired, measured.Actual, measured.Margin, measured.DesiredContent, measured.NaturalConstraint, measured.NaturalDesired, measured.Visible,
                measured.LayoutSlot, measured.LayoutClip == null ? "none" : measured.LayoutClip.Bounds.ToString(CultureInfo.InvariantCulture),
                measured.VisualClip == null ? "none" : measured.VisualClip.Bounds.ToString(CultureInfo.InvariantCulture), text.FontFamily, text.FontSize, text.TextWrapping,
                text.Height, text.MaxHeight, text.LineHeight, text.LineStackingStrategy, VisualTreeHelper.GetDpi(text).PixelsPerInchY, text.Text.Length, measured.OriginalConstraint, TextOptions.GetTextFormattingMode(text));
        }
        private static void CaptionMeasurementCalibration()
        {
            foreach (int dpi in new[] { 96, 120, 144, 192 })
            {
                var normal = new TextBlock { Text = "正常标题 / A wrapped caption with layout spacing", FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(7, 4, 9, 8) };
                VisualTreeHelper.SetRootDpi(normal, new DpiScale(dpi / 96d, dpi / 96d));
                normal.Measure(new Size(140, Double.PositiveInfinity)); normal.Arrange(new Rect(0, 0, 140, normal.DesiredSize.Height));
                var normalMeasurement = MeasureCaption(normal, true);
                Console.WriteLine("CALIBRATION caption/normal/" + dpi + "; ExpectedCropped=false; ActualCropped=" + normalMeasurement.Cropped + "; " + CaptionEvidence(normal, normalMeasurement)); Console.Out.Flush();
                Check(normal.DesiredSize.Height > normal.ActualHeight + 1.1, "Caption calibration must exercise a real outer margin at " + dpi);
                Check(!normalMeasurement.Cropped, "An intact caption with outer margin was reported cropped at " + dpi);
                var clipped = new TextBlock { Text = normal.Text, FontSize = normal.FontSize, TextWrapping = TextWrapping.Wrap, Margin = normal.Margin, Height = 10 };
                VisualTreeHelper.SetRootDpi(clipped, new DpiScale(dpi / 96d, dpi / 96d));
                // Both samples receive the same parent width. Arranging the
                // negative sample at its own clipped DesiredSize.Width can
                // collapse the text width as well as its height, which is not
                // the fixed-width page constraint this calibration represents.
                clipped.Measure(new Size(140, Double.PositiveInfinity)); clipped.Arrange(new Rect(0, 0, 140, clipped.DesiredSize.Height));
                var clippedMeasurement = MeasureCaption(clipped, true);
                Console.WriteLine("CALIBRATION caption/height-clipped/" + dpi + "; ExpectedCropped=true; ActualCropped=" + clippedMeasurement.Cropped + "; " + CaptionEvidence(clipped, clippedMeasurement)); Console.Out.Flush();
                Check(clippedMeasurement.Cropped, "A deliberately height-clipped caption escaped detection at " + dpi);

                var aligned = new TextBlock { Text = "Wi-Fi · 192.168.1.20", FontFamily = normal.FontFamily, FontSize = 12, TextWrapping = TextWrapping.Wrap, UseLayoutRounding = true };
                var presenter = new ContentPresenter { Content = aligned, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                var wideParent = new Grid { Width = 246, Height = 40, UseLayoutRounding = true }; wideParent.Children.Add(presenter);
                VisualTreeHelper.SetRootDpi(wideParent, new DpiScale(dpi / 96d, dpi / 96d));
                wideParent.Measure(new Size(246, 40)); wideParent.Arrange(new Rect(0, 0, 246, 40)); wideParent.UpdateLayout();
                var alignedMeasurement = MeasureCaption(aligned, true);
                var tightClone = CreateNaturalCaption(aligned); tightClone.Measure(new Size(aligned.ActualWidth, Double.PositiveInfinity));
                Console.WriteLine("CALIBRATION caption/wide-parent-left-aligned/" + dpi + "; ExpectedCropped=false; ActualCropped=" + alignedMeasurement.Cropped +
                    "; TightArrangedWidthClone=" + tightClone.DesiredSize + "; RoundedActualWouldReflow=" + (tightClone.DesiredSize.Height > aligned.ActualHeight + 1.1) + "; " + CaptionEvidence(aligned, alignedMeasurement)); Console.Out.Flush();
                Check(alignedMeasurement.OriginalConstraint.Width > aligned.ActualWidth + 1.1, "Aligned caption calibration must retain a wider original measure constraint at " + dpi);
                Check(!alignedMeasurement.Cropped, "An intact left-aligned caption in a wide parent was reported cropped at " + dpi);
            }
            Console.WriteLine("PASS: caption measurement calibration at DPI96/120/144/192; actual outer margins and left-aligned captions accepted, deliberately height-clipped captions rejected.");
        }
        private static bool Effective(DependencyObject node, DependencyObject root)
        {
            for (DependencyObject current = node; current != null; current = VisualTreeHelper.GetParent(current))
            { var element = current as UIElement; if (element != null && element.Visibility != Visibility.Visible) return false; if (ReferenceEquals(current, root)) return true; }
            return false;
        }
        private static bool InsideHorizontalScroller(DependencyObject node, DependencyObject root)
        {
            for (DependencyObject current = VisualTreeHelper.GetParent(node); current != null && !ReferenceEquals(current, root); current = VisualTreeHelper.GetParent(current))
            { var scroll = current as ScrollViewer; if (scroll != null && scroll.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled) return true; }
            return false;
        }
        private static void FocusAndDraftFixture(IList<string> issues)
        {
            var window = new Window { Width = 320, Height = 360, ShowActivated = false, ShowInTaskbar = false, IsHitTestVisible = false };
            var panel = new AutoSceneRulesPanel(new AutoSceneRuleStore(true), () => LayoutProfileStore.Builtins(), () => null, () => { }, () => { }, () => { });
            var viewport = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            window.Content = viewport; AppAppearance.Register(window); var guard = NativeFixtureInputIsolation.Attach(window);
            try
            {
                window.Show(); PopupDpiTests.Pump(); window.UpdateLayout();
                var input = (TextBox)typeof(AutoSceneRulesPanel).GetField("name", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
                input.BringIntoView(); PopupDpiTests.Pump();
                input.Text = "User text 中文 AbgQ"; input.Focus(); Keyboard.Focus(input); input.SelectAll(); PopupDpiTests.Pump();
                if (!input.IsKeyboardFocusWithin || input.SelectionLength != input.Text.Length || input.Text != "User text 中文 AbgQ") issues.Add("own-focus: input focus, selection or protected user text failed");
                var actions = PopupDpiTests.Descendants(panel).OfType<Button>().Last(); actions.BringIntoView(); PopupDpiTests.Pump();
                Point point = actions.TranslatePoint(new Point(), viewport);
                if (point.Y < -1 || point.Y + actions.ActualHeight > viewport.ActualHeight + 1) issues.Add("own-focus: last action cannot be scrolled into view");
            }
            finally { window.Close(); guard.Dispose(); viewport.Content = null; }
        }
        private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    }
}
