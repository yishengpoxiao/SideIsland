using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // Only production controls in an isolated demo window. No pointer movement,
    // global input, clipboard use, desktop capture or real player operations.
    internal static class PickerAndChromeTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect bounds);
        private static int frames, headers, pickerTransitions, selectionCloses, dpiCases, geometryMessages;
        private static readonly List<double> Handlers = new List<double>();
        private static readonly List<double> PickerFrameGaps = new List<double>();

        internal static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), language = UiLocalization.Language, theme = UiTheme.Name;
            string folder = Path.Combine(Path.GetTempPath(), "CodexIsland.PickerChrome." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null; frames = headers = pickerTransitions = selectionCloses = dpiCases = geometryMessages = 0; Handlers.Clear(); PickerFrameGaps.Clear();
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false,
                    HoverExpandEnabled = false, AutoMusic = false, OnlineLyricsEnabled = false, ExpandedWidth = 391, ExpandedHeight = 617 });
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false };
                window.Loaded += (s, e) => StopTimers(window); window.Show(); Settle(window);
                Console.WriteLine("CHECK: actual tab-caption typography and picker/page-fade overlap.");
                TabCaptionSelection(window); NotificationTabCounterBaseline(window); PickerSelectionDuringPageFade(window);
                Console.WriteLine("CHECK: fixed header/page/scroll matrix; " + PopupDpiTests.Describe(window));
                HeaderMatrix(window); ScrollbarBoundary(window);
                Console.WriteLine("CHECK: all 38 tools and long-page scroll transitions."); AllTools(window);
                Console.WriteLine("CHECK: real picker height and selected-content motion."); PickerFrames(window); SelectionCollapse(window);
                Console.WriteLine("CHECK: rapid requests, search and interruption cleanup."); RapidAndDynamic(window); Interruptions(window);
                StopTimers(window); window.Close(); window = null;
                DpiHeaderMatrix();
                var sortedGaps = PickerFrameGaps.OrderBy(value => value).ToArray();
                Console.WriteLine("TIMING: steady picker render gap p95/max " + (sortedGaps.Length == 0 ? 0 : sortedGaps[(int)Math.Ceiling(sortedGaps.Length * .95) - 1]).ToString("0.0") + "/" + sortedGaps.DefaultIfEmpty(0).Max().ToString("0.0") + " ms; gaps are sampled only within individual height transitions, excluding setup/rapid-dispatch sequences.");
                Console.WriteLine("TIMING: picker request handler mean/max " + Handlers.DefaultIfEmpty(0).Average().ToString("0.0") + "/" + Handlers.DefaultIfEmpty(0).Max().ToString("0.0") + " ms; native move/size messages=" + geometryMessages + ".");
                Console.WriteLine("PASS: " + headers + " fixed-header native layout cases, all 38 tools, " + pickerTransitions + " real picker height transitions, " + selectionCloses + " smooth selection collapses, " + frames + " rendered geometry frames and " + dpiCases + " WPF DPI header layouts; physical/logical four-button bounds, scrolling, 80 rapid requests, dynamic search and interruption cleanup.");
            }
            catch (Exception error) { Console.Error.WriteLine("CHECK FAILURE: " + error.GetType().Name + ": " + error.Message); throw; }
            finally
            {
                if (window != null && !Read<bool>(window, "shuttingDown")) { StopTimers(window); window.Close(); }
                AppAppearance.Configure(language, theme); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        private sealed class TabCaptionPose
        {
            internal FontWeight Weight;
            internal Size Desired;
            internal Rect Logical, Physical;
            internal string Text;
            internal static TabCaptionPose Capture(Button tab, IslandWindow window)
            {
                TextBlock caption = TabCaption(tab);
                return new TabCaptionPose { Weight = caption.FontWeight, Desired = caption.DesiredSize,
                    Logical = Bounds(caption, window), Physical = ScreenBounds(caption), Text = caption.Text };
            }
            internal void Verify(Button tab, IslandWindow window, string context)
            {
                TextBlock caption = TabCaption(tab);
                Check(caption.Text == Text, context + ": changing selection rewrites the tab caption");
                Check(caption.FontWeight == Weight, context + ": selected state changes actual template TextBlock.FontWeight " + Weight + " -> " + caption.FontWeight);
                Check(Math.Abs(caption.DesiredSize.Width - Desired.Width) <= .1 && Math.Abs(caption.DesiredSize.Height - Desired.Height) <= .1,
                    context + ": selected state changes actual caption DesiredSize; actual=" + caption.DesiredSize + "; expected=" + Desired);
                Same(Bounds(caption, window), Logical, .1, context + ": template caption shifts inside its unchanged button");
                Same(ScreenBounds(caption), Physical, 1.01, context + ": template caption changes physical glyph location");
            }
        }

        private static TextBlock TabCaption(Button tab)
        {
            tab.ApplyTemplate();
            TextBlock[] captions = Descendants<TextBlock>(tab).Where(value => value.IsVisible && !String.IsNullOrEmpty(value.Text)).ToArray();
            Check(captions.Length == 1, "tab must expose one actual ContentTemplate TextBlock, found " + captions.Length);
            return captions[0];
        }

        private static void TabCaptionSelection(IslandWindow window)
        {
            foreach (string locale in new[] { "zh", "en" })
            {
                Configure(window, "right", 391, true, locale, "dark"); SelectPage(window, 0); window.SetExpanded(true, false); Settle(window); WaitFades(window);
                Button[] tabs = { Read<Button>(window, "notificationsTab"), Read<Button>(window, "statusTab"), Read<Button>(window, "toolsTab") };
                TabCaptionPose[] poses = tabs.Select(tab => TabCaptionPose.Capture(tab, window)).ToArray();
                var chrome = Chrome.Capture(window);
                foreach (int selected in new[] { 1, 2, 0 })
                {
                    string context = "tab template/" + locale + "/selected=" + selected;
                    Action verify = () => { chrome.Verify(window, context); for (int index = 0; index < tabs.Length; index++) poses[index].Verify(tabs[index], window, context + "/tab=" + index); };
                    EventHandler rendering = (sender, args) => { verify(); frames++; };
                    CompositionTarget.Rendering += rendering;
                    try
                    {
                        SelectPage(window, selected);
                        // Check inherited typography immediately, then the real
                        // arranged caption and every rendered fade frame. A fixed
                        // Button rectangle alone misses bold/normal glyph shifts.
                        for (int index = 0; index < tabs.Length; index++) Check(TabCaption(tabs[index]).FontWeight == poses[index].Weight,
                            context + ": request changes inherited caption FontWeight");
                        window.UpdateLayout(); verify(); WaitFades(window); verify();
                    }
                    finally { CompositionTarget.Rendering -= rendering; }
                }
            }
            Console.WriteLine("PASS: Chinese/English tab ContentTemplate text keeps FontWeight, desired size and logical/physical position when selection changes; selected colors may differ.");
        }

        private static void PickerSelectionDuringPageFade(IslandWindow window)
        {
            if (!SystemParameters.ClientAreaAnimation) { Console.WriteLine("CHECK: picker/page-fade overlap requires enabled animation; reduced-motion layout remains covered by the matrix."); return; }
            foreach (string locale in new[] { "zh", "en" })
            {
                string context = "picker inside incoming page/" + locale;
                Configure(window, "right", 391, true, locale, "dark"); SelectPage(window, 2); Select(window, "focus"); window.SetExpanded(true, false); Settle(window); WaitFades(window);
                var picker = Picker(window); picker.IsExpanded = true; WaitPicker(window);
                double open = EffectiveHeight(Viewport(picker)); Check(open > 70, context + ": fixture chooser is not genuinely open");
                SelectPage(window, 0); WaitFades(window);
                Check(picker.IsExpanded && !Motion(picker), context + ": leaving the tools page must retain its settled open chooser");
                SelectPage(window, 2); Pump(35); StopTimers(window);
                Check(Read<bool>(window, "pageTransitionActive"), context + ": selection must occur during the incoming page fade");
                object pageClock = Read<object>(window, "pageTransitionWatch"); int pageVersion = Read<int>(window, "pageTransitionVersion");
                Check(pageClock != null, context + ": incoming page has not rendered an actual fade frame");
                var chrome = Chrome.Capture(window); var heights = new List<double>();
                bool pageEndedWithMovingChooser = false, sawIntermediate = false;
                // Register after the existing page driver but BEFORE Select adds
                // the chooser driver. At natural page completion this observer
                // must still see the chooser's independent clock, even if a slow
                // frame would let both drivers finish later in the same event.
                EventHandler rendering = (sender, args) => {
                    chrome.Verify(window, context); frames++;
                    double height = EffectiveHeight(Viewport(picker)); heights.Add(height);
                    if (height > 1 && height < open - 1) sawIntermediate = true;
                    if (!Read<bool>(window, "pageTransitionActive") && Motion(picker)) pageEndedWithMovingChooser = true;
                };
                CompositionTarget.Rendering += rendering;
                try
                {
                    Select(window, "text");
                    Check(!picker.IsExpanded && Motion(picker), context + ": selecting a tool commits chooser height instead of starting its local motion");
                    Check(Read<bool>(window, "pageTransitionActive") && Read<int>(window, "pageTransitionVersion") == pageVersion &&
                        Object.ReferenceEquals(Read<object>(window, "pageTransitionWatch"), pageClock), context + ": chooser selection restarts or forces completion of its parent page fade");
                    window.UpdateLayout();
                    Check(Math.Abs(EffectiveHeight(Viewport(picker)) - open) <= 1.1, context + ": selection request loses the current chooser height");
                    WaitPicker(window); WaitFades(window); window.UpdateLayout();
                }
                finally { CompositionTarget.Rendering -= rendering; }
                Check(pageEndedWithMovingChooser, context + ": natural page completion prematurely commits the still-moving chooser");
                Check(sawIntermediate, context + ": local chooser has no actual intermediate arranged height");
                Monotonic(heights, false, context);
                Check(!picker.IsExpanded && !Motion(picker) && EffectiveHeight(Viewport(picker)) < .1 &&
                    Read<string>(window, "selectedTool") == "text" && Panels(window)["text"].IsVisible,
                    context + ": independent chooser/page clocks do not naturally reach the latest tool");
                chrome.Verify(window, context + "/final"); selectionCloses++;
            }
            Console.WriteLine("PASS: selecting a tool during the incoming Chinese/English page fade retains chooser height and naturally completes its independent height motion beyond parent completion.");
        }

        private static void NotificationTabCounterBaseline(IslandWindow window)
        {
            int previousUnread = Read<int>(window, "notificationCounterLastUnread");
            bool previousChanged = Read<bool>(window, "notificationCounterBadgeChanged");
            double previousDirection = Read<double>(window, "notificationCounterBadgeDirection");
            try
            {
                foreach (string locale in new[] { "zh", "en" })
                {
                    string context = "navigation count baseline/" + locale;
                    Configure(window, "right", 391, true, locale, "dark"); SelectPage(window, 0); window.SetExpanded(true, false); Settle(window); WaitFades(window);
                    Call(window, "StopNotificationCounterTransitions");
                    Button tab = Read<Button>(window, "notificationsTab"); TextBlock caption = TabCaption(tab);
                    var pose = TabCaptionPose.Capture(tab, window); var chrome = Chrome.Capture(window);
                    Matrix transform = caption.RenderTransform.Value;
                    // Exercise the real badge-update route's offset argument,
                    // rather than passing zero directly to the queue helper.
                    // Only internal demo bookkeeping changes; feed data and the
                    // visible caption text are deliberately left untouched.
                    Call(window, "BeginNotificationCounterUpdate", 40); Call(window, "EndNotificationCounterUpdate", 40);
                    Call(window, "StopNotificationCounterTransitions");
                    EventHandler rendering = (sender, args) => {
                        Check(TabCaption(tab).RenderTransform.Value == transform, context + ": counter fade translates a fixed navigation caption");
                        pose.Verify(tab, window, context); chrome.Verify(window, context); frames++;
                    };
                    CompositionTarget.Rendering += rendering;
                    try
                    {
                        Call(window, "BeginNotificationCounterUpdate", 41); Call(window, "EndNotificationCounterUpdate", 41);
                        Check(caption.RenderTransform.Value == transform, context + ": badge update changes the caption's baseline immediately");
                        if (SystemParameters.ClientAreaAnimation)
                            Check(Read<bool>(window, "notificationCounterTransitionActive") && caption.Opacity < 1,
                                context + ": fixture did not enter a real incoming counter fade");
                        Pump(30); StopTimers(window); pose.Verify(tab, window, context + "/before-page");
                        SelectPage(window, 1); window.UpdateLayout();
                        Check(caption.RenderTransform.Value == transform, context + ": page interruption jumps the counter caption");
                        WaitFades(window); pose.Verify(tab, window, context + "/after-page");
                    }
                    finally { CompositionTarget.Rendering -= rendering; Call(window, "StopNotificationCounterTransitions"); }
                }
            }
            finally
            {
                Call(window, "StopNotificationCounterTransitions");
                typeof(IslandWindow).GetField("notificationCounterLastUnread", Private).SetValue(window, previousUnread);
                typeof(IslandWindow).GetField("notificationCounterBadgeChanged", Private).SetValue(window, previousChanged);
                typeof(IslandWindow).GetField("notificationCounterBadgeDirection", Private).SetValue(window, previousDirection);
            }
            Console.WriteLine("PASS: Chinese/English navigation counter fades and page interruptions preserve the actual caption transform and baseline; no feed or clipboard mutation.");
        }

        private sealed class Chrome
        {
            internal Rect Native, TabsLogical, TabsPhysical, FooterLogical, FooterPhysical;
            internal Rect[] Logical, Physical;
            internal static Chrome Capture(IslandWindow window)
            {
                var buttons = Buttons(window);
                var tabs = Read<Border>(window, "expandedTabs");
                var hint = Read<TextBlock>(window, "expandedHint");
                return new Chrome { Native = NativeBounds(window), TabsLogical = Bounds(tabs, window), TabsPhysical = ScreenBounds(tabs), Logical = buttons.Select(button => Bounds(button, window)).ToArray(),
                    Physical = buttons.Select(button => new Rect(button.PointToScreen(new Point()), button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight)))).ToArray(),
                    FooterLogical = Bounds(hint, window), FooterPhysical = ScreenBounds(hint) };
            }
            internal void Verify(IslandWindow window, string context)
            {
                Check(NativeBounds(window) == Native, context + ": native window moved or resized");
                var tabs = Read<Border>(window, "expandedTabs");
                Same(Bounds(tabs, window), TabsLogical, .1, context + ": fixed page tabs moved logically");
                Same(ScreenBounds(tabs), TabsPhysical, 1.01, context + ": fixed page tabs moved physically");
                var hint = Read<TextBlock>(window, "expandedHint"); var host = Read<Grid>(window, "expandedHost");
                Check(Object.ReferenceEquals(hint.Parent, host) && Grid.GetRow(hint) == 3 && host.RowDefinitions.Count == 4 && host.RowDefinitions[3].Height.IsAuto,
                    context + ": window instruction must stay in its own fixed Auto footer row");
                Same(Bounds(hint, window), FooterLogical, .1, context + ": fixed instruction moves logically with page/tool scrolling");
                Same(ScreenBounds(hint), FooterPhysical, 1.01, context + ": fixed instruction moves physically with page/tool scrolling");
                Check(hint.IsVisible && hint.Opacity == 1, context + ": fixed footer fades or hides with page content");
                var buttons = Buttons(window);
                for (int index = 0; index < buttons.Length; index++)
                {
                    Same(Bounds(buttons[index], window), Logical[index], .1, context + ": logical header button moved " + index);
                    Same(new Rect(buttons[index].PointToScreen(new Point()), buttons[index].PointToScreen(new Point(buttons[index].ActualWidth, buttons[index].ActualHeight))), Physical[index], 1.01, context + ": physical header button moved " + index);
                    Check(buttons[index].Opacity == 1, context + ": fixed header button is faded");
                }
            }
        }

        private static void HeaderMatrix(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings");
            foreach (string side in new[] { "left", "right", "top", "bottom" })
            foreach (int width in new[] { 320, 391, 560 })
            foreach (string locale in new[] { "zh", "en" })
            foreach (string appearance in new[] { "light", "dark" })
            {
                Configure(window, side, width, width == 391, locale, appearance);
                Console.WriteLine("CASE: " + side + "/" + width + "/" + locale + "/" + appearance);
                Call(window, "SelectPage", true); window.SetExpanded(true, false); Settle(window);
                string context = side + "/" + width + "/" + locale + "/" + appearance;
                var expected = Chrome.Capture(window); CheckHeader(window, context);
                foreach (int page in new[] { 1, 2, 0 })
                {
                    ObserveChrome(window, expected, () => SelectPage(window, page), context + "/page=" + page);
                    var scroll = Read<ScrollViewer>(window, "detailsScroll");
                    scroll.ScrollToBottom(); Settle(window); expected.Verify(window, context + "/scroll bottom");
                    scroll.ScrollToTop(); Settle(window); expected.Verify(window, context + "/scroll top");
                }
                headers++;
            }
            settings.HiddenTools = "";
        }

        private static void AllTools(IslandWindow window)
        {
            Configure(window, "right", 391, true, "en", "dark"); Call(window, "SelectToolsPage"); window.SetExpanded(true, false); Settle(window);
            var expected = Chrome.Capture(window); var picker = Picker(window);
            Check(Panels(window).Count == 38, "fixture retains all 38 production tools");
            double headerHeight = -1;
            foreach (string key in Panels(window).Keys.ToArray())
            {
                picker.IsExpanded = false; SettlePicker(picker); Settle(window);
                ObserveChrome(window, expected, () => Select(window, key), "catalog transition/" + key);
                expected.Verify(window, "catalog/" + key); CheckHeader(window, "catalog/" + key);
                var toggle = picker.Template.FindName("PART_Header", picker) as FrameworkElement;
                if (toggle != null) { if (headerHeight < 0) headerHeight = toggle.ActualHeight; Check(Math.Abs(toggle.ActualHeight - headerHeight) <= .1, "tool-picker header changes height for " + key); }
                Read<ScrollViewer>(window, "detailsScroll").ScrollToBottom(); Settle(window); expected.Verify(window, "catalog bottom/" + key);
                if (key == "system" || key == "focus")
                {
                    ObserveChrome(window, expected, () => SelectPage(window, 0), "catalog bottom-to-messages/" + key);
                    ObserveChrome(window, expected, () => SelectPage(window, 1), "catalog messages-to-status/" + key);
                    ObserveChrome(window, expected, () => SelectPage(window, 2), "catalog status-to-tools/" + key);
                }
            }
        }

        private static void ScrollbarBoundary(IslandWindow window)
        {
            // 356 minus shell (18), host margins (48) and gutter (10) is 280:
            // the responsive title threshold previously crossed when Auto
            // scrollbars appeared on a long page and vanished on a short one.
            foreach (int width in new[] { 355, 356, 357 }) foreach (string locale in new[] { "zh", "en" })
            {
                Configure(window, "right", width, true, locale, "dark"); Call(window, "SelectToolsPage"); Select(window, "system"); window.SetExpanded(true, false); Settle(window);
                var expected = Chrome.Capture(window); var scroll = Read<ScrollViewer>(window, "detailsScroll");
                scroll.ScrollToBottom(); Settle(window); expected.Verify(window, "threshold bottom/" + width + "/" + locale);
                foreach (int page in new[] { 0, 1, 2 }) ObserveChrome(window, expected, () => SelectPage(window, page), "threshold/" + width + "/" + locale + "/" + page);
                headers++;
            }
        }

        private static void PickerFrames(IslandWindow window)
        {
            int index = 0;
            foreach (string side in new[] { "left", "right", "top", "bottom" })
            foreach (int width in new[] { 320, 391, 560 })
            {
                Configure(window, side, width, width != 560, index++ % 2 == 0 ? "zh" : "en", index % 2 == 0 ? "light" : "dark");
                Call(window, "SelectToolsPage"); Select(window, "focus"); window.SetExpanded(true, false); Settle(window);
                Picker(window).IsExpanded = false; SettlePicker(Picker(window)); Settle(window);
                ObservePicker(window, true, side + "/" + width + "/open"); ObservePicker(window, false, side + "/" + width + "/close");
            }
        }

        private static void ObservePicker(IslandWindow window, bool expand, string context)
        {
            var picker = Picker(window); var expected = Chrome.Capture(window); var heights = new List<double>();
            double before = EffectiveHeight(Viewport(picker)), lastFrame = -1; int messages = 0;
            var source = (HwndSource)PresentationSource.FromVisual(window);
            HwndSourceHook hook = (IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled) => { if (message == 3 || message == 5) messages++; return IntPtr.Zero; };
            EventHandler render = (s, e) => { RecordGap(e, ref lastFrame); expected.Verify(window, context); heights.Add(EffectiveHeight(Viewport(picker))); frames++; };
            source.AddHook(hook); CompositionTarget.Rendering += render;
            try
            {
                var timing = Stopwatch.StartNew(); picker.IsExpanded = expand; Handlers.Add(timing.Elapsed.TotalMilliseconds);
                window.UpdateLayout();
                Check(Math.Abs(EffectiveHeight(Viewport(picker)) - before) <= 1.1 || !SystemParameters.ClientAreaAnimation, context + ": picker jumps directly on the request");
                WaitPicker(window);
            }
            finally { CompositionTarget.Rendering -= render; source.RemoveHook(hook); }
            geometryMessages += messages; Check(messages == 0, context + ": picker changes native window geometry");
            double after = EffectiveHeight(Viewport(picker));
            Check(expand ? after > 70 : after < .1, context + ": picker final height incorrect " + after);
            if (SystemParameters.ClientAreaAnimation)
                Check(heights.Any(height => height > Math.Min(before, after) + 1 && height < Math.Max(before, after) - 1), context + ": no genuine intermediate layout height");
            Monotonic(heights, expand, context);
            Check(Double.IsNaN(Viewport(picker).Height), context + ": picker retains a temporary fixed height at rest");
            if (!expand) Check(Viewport(picker).Visibility == Visibility.Collapsed || !Viewport(picker).IsHitTestVisible, context + ": closed content can still intercept input");
            expected.Verify(window, context + "/final"); pickerTransitions++;
        }

        private static void SelectionCollapse(IslandWindow window)
        {
            foreach (string side in new[] { "left", "right", "top", "bottom" })
            {
                Configure(window, side, 391, true, "en", "dark"); Call(window, "SelectToolsPage"); Select(window, "focus"); window.SetExpanded(true, false); Settle(window);
                var picker = Picker(window); picker.IsExpanded = true; WaitPicker(window);
                var scroll = Read<ScrollViewer>(window, "detailsScroll"); scroll.ScrollToTop(); Settle(window);
                var expected = Chrome.Capture(window); double before = Bounds(Panels(window)["focus"], window).Top;
                var points = new List<double>(); var heights = new List<double>(); double lastFrame = -1;
                EventHandler render = (s, e) => { RecordGap(e, ref lastFrame); expected.Verify(window, "selection/" + side); points.Add(Bounds(Panels(window)["text"], window).Top); heights.Add(EffectiveHeight(Viewport(picker))); frames++; };
                CompositionTarget.Rendering += render;
                try
                {
                    var watch = Stopwatch.StartNew(); Select(window, "text"); Handlers.Add(watch.Elapsed.TotalMilliseconds); window.UpdateLayout();
                    if (SystemParameters.ClientAreaAnimation) Check(Math.Abs(Bounds(Panels(window)["text"], window).Top - before) <= 2, "selection starts with a large content jump on " + side);
                    WaitPicker(window); WaitFades(window); window.UpdateLayout();
                }
                finally { CompositionTarget.Rendering -= render; }
                double after = Bounds(Panels(window)["text"], window).Top;
                Check(after + 40 < before, "selection did not close its chooser on " + side);
                if (SystemParameters.ClientAreaAnimation) Check(points.Any(point => point < before - 1 && point > after + 1), "selected tool never moved through an intermediate layout position on " + side);
                Monotonic(points, false, "selection content/" + side); Monotonic(heights, false, "selection chooser/" + side);
                Check(!picker.IsExpanded && !Motion(picker) && Read<string>(window, "selectedTool") == "text", "selection latest target/chooser state wrong");
                expected.Verify(window, "selection final/" + side); selectionCloses++;
            }
        }

        private static void RapidAndDynamic(IslandWindow window)
        {
            Configure(window, "right", 391, true, "en", "light"); Call(window, "SelectToolsPage"); window.SetExpanded(true, false); Settle(window);
            var picker = Picker(window); var expected = Chrome.Capture(window); var keys = new[] { "focus", "text", "calculator", "qr", "system", "time" };
            for (int index = 0; index < 80; index++)
            {
                double before = EffectiveHeight(Viewport(picker));
                var watch = Stopwatch.StartNew();
                picker.IsExpanded = index % 3 != 0;
                if (index % 4 == 0) Select(window, keys[index % keys.Length]);
                Handlers.Add(watch.Elapsed.TotalMilliseconds);
                window.UpdateLayout();
                if (SystemParameters.ClientAreaAnimation) Check(Math.Abs(EffectiveHeight(Viewport(picker)) - before) <= 1.1, "rapid retarget jumps at request " + index);
                Pump(2); StopTimers(window); window.UpdateLayout(); expected.Verify(window, "rapid/" + index);
                Check(EffectiveHeight(Viewport(picker)) >= -.1, "rapid requests produce negative picker height");
            }
            picker.IsExpanded = true; WaitPicker(window); Check(picker.IsExpanded, "latest open request lost");
            var search = Read<TextBox>(window, "toolSearch");
            foreach (string query in new[] { "hash", "no-such-tool-fixture-7402", "", "calculator", "" })
            {
                search.Text = query; WaitPicker(window); expected.Verify(window, "dynamic search/" + query);
                Check(Double.IsNaN(Viewport(picker).Height), "search leaves stale constrained picker height");
            }
            Select(window, "text"); WaitPicker(window); WaitFades(window);
            Check(!picker.IsExpanded && Read<string>(window, "selectedTool") == "text", "last tool request did not win");
            foreach (var panel in Panels(window).Values) Check(Math.Abs(panel.Opacity - 1) < .001, "rapid picker/tool sequence leaves dim tool content");
        }

        private static void Interruptions(IslandWindow window)
        {
            foreach (string action in new[] { "collapse", "page", "appearance", "resize", "hide" })
            {
                Call(window, "SelectToolsPage"); window.SetExpanded(true, false); Settle(window); var picker = Picker(window);
                picker.IsExpanded = false; SettlePicker(picker); picker.IsExpanded = true; Pump(25); StopTimers(window);
                if (action == "collapse") window.SetExpanded(false, false);
                else if (action == "page") Call(window, "SelectPage", true);
                else if (action == "appearance") Call(window, "ApplyAppearance");
                else if (action == "resize") Call(window, "FreezeSizeAnimation");
                else window.Hide();
                Pump(200); StopTimers(window); Check(!Motion(picker), action + ": picker retained an animation after interruption");
                if (!window.IsVisible) { window.Show(); Settle(window); }
            }
        }

        private static void DpiHeaderMatrix()
        {
            string artifacts = Environment.GetEnvironmentVariable("CODEX_ISLAND_PICKER_ARTIFACTS");
            foreach (string locale in new[] { "zh", "en" }) foreach (string appearance in new[] { "light", "dark" })
            {
                var window = new IslandWindow(true);
                try
                {
                    var settings = Read<Settings>(window, "settings"); settings.Language = locale; settings.Theme = appearance; Call(window, "ApplyAppearance");
                    Call(window, "SelectToolsPage"); window.SetExpanded(true, false);
                    var content = (FrameworkElement)window.Content; window.Content = null;
                    // Detaching content loses Window's resource/style/font
                    // inheritance. Carry the real production context across;
                    // otherwise this would render default square WPF controls.
                    var root = new ContentControl { Content = content, Resources = window.Resources,
                        FontFamily = window.FontFamily, FontSize = window.FontSize, FontStyle = window.FontStyle,
                        FontWeight = window.FontWeight, FontStretch = window.FontStretch, Foreground = window.Foreground,
                        Background = window.Background, UseLayoutRounding = window.UseLayoutRounding,
                        SnapsToDevicePixels = window.SnapsToDevicePixels, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        VerticalContentAlignment = VerticalAlignment.Stretch };
                    TextOptions.SetTextFormattingMode(root, TextOptions.GetTextFormattingMode(window));
                    TextOptions.SetTextRenderingMode(root, TextOptions.GetTextRenderingMode(window));
                    TextOptions.SetTextHintingMode(root, TextOptions.GetTextHintingMode(window));
                    // This never-loaded tree lacks the real localization Entry
                    // descriptor. Model its dynamic badge subscription locally;
                    // static marked captions already refreshed via appearance.
                    var messageTab = Read<Button>(window, "notificationsTab");
                    var captionDescriptor = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(Button));
                    bool translatingCaption = false;
                    EventHandler translateCaption = (sender, args) => {
                        if (translatingCaption) return;
                        string current = messageTab.Content as string, translated = UiLocalization.Localize(current);
                        if (Object.Equals(current, translated)) return;
                        try { translatingCaption = true; messageTab.SetCurrentValue(ContentControl.ContentProperty, translated); }
                        finally { translatingCaption = false; }
                    };
                    captionDescriptor.AddValueChanged(messageTab, translateCaption); translateCaption(null, EventArgs.Empty);
                    try
                    {
                    foreach (int dpi in PopupDpiTests.Values) foreach (int width in new[] { 320, 391, 560 })
                    {
                        settings.CustomExpandedSize = true; settings.ExpandedWidth = width; settings.ExpandedHeight = 617; window.SetExpanded(true, false);
                        Check(Object.Equals(Read<Button>(window, "notificationsTab").Content, UiLocalization.Localize("消息")), "detached DPI message tab was not localized for " + locale);
                        PopupDpiTests.Apply(root, dpi); root.Measure(new Size(width, 617)); root.Arrange(new Rect(0, 0, width, 617)); root.UpdateLayout(); PopupDpiTests.Render(root, dpi);
                        var buttons = Buttons(window); var baseline = buttons.Select(button => Bounds(button, root)).ToArray();
                        Rect footerBaseline = Bounds(Read<TextBlock>(window, "expandedHint"), root);
                        foreach (int page in new[] { 0, 1, 2 })
                        {
                            SelectPage(window, page); root.UpdateLayout(); PopupDpiTests.Render(root, dpi);
                            Check(Object.Equals(Read<Button>(window, "notificationsTab").Content, UiLocalization.Localize("消息")), "detached DPI page switch left a raw message tab for " + locale + "/" + dpi + "/" + width + "/page=" + page + "; actual=" + Read<Button>(window, "notificationsTab").Content + "; expected=" + UiLocalization.Localize("消息") + "; language=" + UiLocalization.Language);
                            for (int index = 0; index < buttons.Length; index++) Same(Bounds(buttons[index], root), baseline[index], 1.01, "WPF DPI header position/" + dpi + "/" + width);
                            Same(Bounds(Read<TextBlock>(window, "expandedHint"), root), footerBaseline, 1.01, "WPF DPI fixed footer position/" + dpi + "/" + width + "/page=" + page);
                        }
                        CheckHeader(window, "WPF DPI/" + dpi + "/" + width); PopupDpiTests.VerifyGlyphDpi(Read<Grid>(window, "expandedHeader"), dpi, "picker/chrome header"); dpiCases++;
                        if (width == 320 || width == 391) PopupDpiTests.Save(root, artifacts, "picker-chrome-" + locale + "-" + appearance + "-" + width + "-" + dpi, dpi);
                    }
                    }
                    finally { captionDescriptor.RemoveValueChanged(messageTab, translateCaption); root.Content = null; }
                }
                finally { StopTimers(window); window.Close(); }
            }
        }

        private static void Configure(IslandWindow window, string side, int width, bool custom, string language, string theme)
        {
            window.SetExpanded(false, false); var settings = Read<Settings>(window, "settings"); settings.Side = side; settings.CustomExpandedSize = custom; settings.ExpandedWidth = width; settings.ExpandedHeight = 617;
            settings.Language = language; settings.Theme = theme; settings.HiddenTools = ""; settings.FavoriteTools = "focus,time";
            Call(window, "ApplyAppearance"); Call(window, "ChangeSide", side); Call(window, "ConfigureToolkit");
        }
        private static void ObserveChrome(IslandWindow window, Chrome expected, Action action, string context)
        {
            EventHandler frame = (s, e) => { expected.Verify(window, context); frames++; };
            CompositionTarget.Rendering += frame;
            try { action(); window.UpdateLayout(); expected.Verify(window, context + "/request"); WaitFades(window); expected.Verify(window, context + "/final"); }
            finally { CompositionTarget.Rendering -= frame; }
            CheckHeader(window, context);
        }
        private static void CheckHeader(IslandWindow window, string context)
        {
            var header = Read<Grid>(window, "expandedHeader"); var buttons = Buttons(window);
            Check(buttons.Length == 4, context + ": fixed header must retain four controls");
            var boxes = buttons.Select(button => Bounds(button, header)).ToArray();
            for (int index = 0; index < boxes.Length; index++)
            {
                Check(Math.Abs(boxes[index].Width - 32) < 1.1 && Math.Abs(boxes[index].Height - 32) < 1.1, context + ": header button dimensions changed");
                Check(Math.Abs(boxes[index].Top - boxes[0].Top) < .1, context + ": four controls do not share the same top edge");
                if (index > 0) Check(Math.Abs(boxes[index].Left - boxes[index - 1].Right - 6) < 1.1, context + ": header control gap changed");
            }
            foreach (var caption in Descendants<TextBlock>(header).Where(item => item.IsVisible && item.Text.Length > 0))
            {
                Check(caption.ActualHeight + 1.1 >= caption.DesiredSize.Height - caption.Margin.Top - caption.Margin.Bottom, context + ": cropped header caption " + caption.Text);
                Rect box = Bounds(caption, header); Check(box.Left >= -1.1 && box.Right <= header.ActualWidth + 1.1, context + ": header caption horizontal overflow");
            }
        }
        private static Button[] Buttons(IslandWindow window) { return ((Panel)Read<Button>(window, "pinButton").Parent).Children.OfType<Button>().ToArray(); }
        private static Expander Picker(IslandWindow window) { return Read<Expander>(window, "toolPicker"); }
        private static FrameworkElement Viewport(Expander picker) { picker.ApplyTemplate(); var part = picker.Template.FindName("PART_PickerViewport", picker) as FrameworkElement; Check(part != null, "picker animation viewport is missing"); return part; }
        private static bool Motion(Expander picker) { var property = picker.GetType().GetProperty("IsMotionActive", Private); return property != null && (bool)property.GetValue(picker, null); }
        private static void SettlePicker(Expander picker) { picker.GetType().GetMethod("SettleMotion", Private).Invoke(picker, null); }
        private static double EffectiveHeight(FrameworkElement element) { return element.Visibility == Visibility.Collapsed ? 0 : element.ActualHeight; }
        private static Dictionary<string, UIElement> Panels(IslandWindow window) { return Read<Dictionary<string, UIElement>>(window, "toolPanels"); }
        private static void Select(IslandWindow window, string key) { Call(window, "SelectTool", key); }
        private static void SelectPage(IslandWindow window, int page) { if (page == 2) Call(window, "SelectToolsPage"); else Call(window, "SelectPage", page == 0); }
        private static void WaitPicker(IslandWindow window) { var wait = Stopwatch.StartNew(); do { Pump(15); StopTimers(window); window.UpdateLayout(); } while (Motion(Picker(window)) && wait.ElapsedMilliseconds < 1800); Check(!Motion(Picker(window)), "picker motion did not complete within bounded wait"); window.UpdateLayout(); }
        private static void WaitFades(IslandWindow window) { var wait = Stopwatch.StartNew(); do { Pump(15); StopTimers(window); window.UpdateLayout(); } while ((Read<bool>(window, "pageTransitionActive") || Read<bool>(window, "toolTransitionActive")) && wait.ElapsedMilliseconds < 1800); Check(!Read<bool>(window, "pageTransitionActive") && !Read<bool>(window, "toolTransitionActive"), "content fade did not complete"); }
        private static void Settle(IslandWindow window) { StopTimers(window); window.UpdateLayout(); Pump(1); StopTimers(window); window.UpdateLayout(); }
        private static void StopTimers(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); var focus = Read<FocusPanel>(window, "focusPanel"); if (focus != null) focus.Stop(); }
        private static void RecordGap(EventArgs value, ref double previous) { var render = value as RenderingEventArgs; if (render == null) return; double current = render.RenderingTime.TotalMilliseconds; if (previous >= 0 && current > previous) PickerFrameGaps.Add(current - previous); previous = current; }
        private static void Monotonic(IList<double> values, bool increasing, string context) { for (int index = 1; index < values.Count; index++) Check(increasing ? values[index] + 1.1 >= values[index - 1] : values[index] <= values[index - 1] + 1.1, context + ": layout reverses or flashes between frames " + values[index - 1] + "→" + values[index]); }
        private static Rect Bounds(FrameworkElement element, Visual root) { return element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)); }
        private static Rect Bounds(UIElement element, Visual root) { return element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)); }
        private static Rect ScreenBounds(FrameworkElement element) { return new Rect(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight))); }
        private static Rect NativeBounds(Window window) { NativeRect bounds; Check(GetWindowRect(new WindowInteropHelper(window).Handle, out bounds), "own native window bounds unavailable"); return new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top); }
        private static void Same(Rect value, Rect expected, double tolerance, string context) { Check(Math.Abs(value.Left - expected.Left) <= tolerance && Math.Abs(value.Top - expected.Top) <= tolerance && Math.Abs(value.Width - expected.Width) <= tolerance && Math.Abs(value.Height - expected.Height) <= tolerance, context + "; actual=" + value + "; expected=" + expected); }
        private static T Read<T>(object value, string field) { return (T)value.GetType().GetField(field, Private).GetValue(value); }
        private static object Call(object value, string method, params object[] args) { try { return value.GetType().GetMethod(method, Private).Invoke(value, args); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) { var child = VisualTreeHelper.GetChild(root, index); var value = child as T; if (value != null) yield return value; foreach (var descendant in Descendants<T>(child)) yield return descendant; } }
        private static void Pump(int milliseconds) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Picker/chrome: " + message); }
    }
}
