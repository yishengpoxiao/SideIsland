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
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // Production controls in isolated own HWNDs, plus detached WPF DPI layouts.
    // No global input, desktop capture, live notifications, clipboard or saves.
    internal static class SettingsAndFilterMotionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
        private static int frames, settingTransitions, filterTransitions, dpiCases, geometryMessages;
        private static readonly List<double> SettingHandlers = new List<double>(), FilterHandlers = new List<double>(), FrameGaps = new List<double>();
        private static NativeWindowStateTrace settingsNativeTrace, islandNativeTrace;
        private static NativeFixtureInputIsolation settingsInputIsolation;

        internal static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), language = UiLocalization.Language, theme = UiTheme.Name;
            string folder = Path.Combine(Path.GetTempPath(), "CodexIsland.SettingsFilterMotion." + Guid.NewGuid().ToString("N"));
            IslandWindow island = null; SettingsWindow settingsWindow = null;
            frames = settingTransitions = filterTransitions = dpiCases = geometryMessages = 0;
            SettingHandlers.Clear(); FilterHandlers.Clear(); FrameGaps.Clear();
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false,
                    HoverExpandEnabled = false, AutoMusic = false, OnlineLyricsEnabled = false, CustomExpandedSize = true, ExpandedWidth = 391, ExpandedHeight = 638 });
                island = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false };
                islandNativeTrace = NativeWindowStateTrace.Attach(island, "settings-motion-owner");
                island.Loaded += (s, e) => StopTimers(island); island.Show(); Settle(island); island.SetExpanded(true, false); Settle(island);
                settingsWindow = new SettingsWindow(new Settings { AlwaysOnTop = false, NotificationsEnabled = false, CodexPath = "", CodexHome = "" })
                    { Owner = island, ShowActivated = false, IsHitTestVisible = false };
                settingsNativeTrace = NativeWindowStateTrace.Attach(settingsWindow, "settings-motion-dialog");
                settingsInputIsolation = NativeFixtureInputIsolation.Attach(settingsWindow);
                AppAppearance.Register(settingsWindow); settingsWindow.Show(); Settle(settingsWindow);
                Console.WriteLine("CHECK: settings real height transitions; " + PopupDpiTests.Describe(settingsWindow));
                SettingsMatrix(settingsWindow); SettingsRapidAndInterruptions(settingsWindow);
                settingsWindow = null;
                Console.WriteLine("CHECK: notification filter live fades, fixed chrome/actions and bounded viewport.");
                FilterMatrix(island); FilterRapidAndBackground(island); FilterInterruptions(island);
                island = null;
                DpiLayouts(); NotificationDpiLayouts();
                Timing("settings", SettingHandlers); Timing("notification filter", FilterHandlers);
                var gaps = FrameGaps.OrderBy(value => value).ToArray();
                Console.WriteLine("TIMING: local transition render gap p95/max " + (gaps.Length == 0 ? 0 : gaps[(int)Math.Ceiling(gaps.Length * .95) - 1]).ToString("0.0") + "/" + gaps.DefaultIfEmpty(0).Max().ToString("0.0") + " ms; setup and rapid-dispatch intervals excluded.");
                Console.WriteLine("PASS: " + settingTransitions + " real settings group height transitions, " + filterTransitions + " live filter fades, " + frames + " rendered frames and " + dpiCases + " WPF DPI layouts; 80 rapid requests per feature, fixed physical/logical chrome/footer/actions, scroll-end/reversal, current data, no stale notification pixels, bounded virtualization and interruption cleanup; native move/size messages=" + geometryMessages + ".");
            }
            catch (Exception error)
            {
                if (settingsNativeTrace != null) settingsNativeTrace.DumpFailure(error.Message);
                if (islandNativeTrace != null) islandNativeTrace.DumpFailure(error.Message);
                Console.Error.WriteLine("CHECK FAILURE: " + error.GetType().Name + ": " + error.Message); throw;
            }
            finally
            {
                if (settingsWindow != null) settingsWindow.Close();
                if (island != null && !Read<bool>(island, "shuttingDown")) { StopTimers(island); island.Close(); }
                if (settingsInputIsolation != null) { settingsInputIsolation.Dispose(); settingsInputIsolation = null; }
                if (settingsNativeTrace != null) { settingsNativeTrace.Dispose(); settingsNativeTrace = null; }
                if (islandNativeTrace != null) { islandNativeTrace.Dispose(); islandNativeTrace = null; }
                AppAppearance.Configure(language, theme); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        private sealed class FixedGeometry
        {
            internal Rect Native;
            internal FrameworkElement[] Elements;
            internal Rect[] Logical, Physical;
            internal static FixedGeometry Capture(Window window, IEnumerable<FrameworkElement> elements)
            {
                var array = elements.ToArray();
                return new FixedGeometry { Native = NativeBounds(window), Elements = array,
                    Logical = array.Select(element => Bounds(element, window)).ToArray(), Physical = array.Select(ScreenBounds).ToArray() };
            }
            internal void Verify(Window window, string context)
            {
                Same(NativeBounds(window), Native, .01, context + ": native window moved/resized");
                for (int index = 0; index < Elements.Length; index++)
                {
                    Same(Bounds(Elements[index], window), Logical[index], 1.1, context + ": fixed logical element moved " + index);
                    Same(ScreenBounds(Elements[index]), Physical[index], 1.1, context + ": fixed physical element moved " + index);
                    Check(Math.Abs(Elements[index].Opacity - 1) < .001, context + ": fixed element fades " + index);
                }
            }
        }

        private static void SettingsMatrix(SettingsWindow window)
        {
            var section = Section(window); var scroll = Read<ScrollViewer>(window, "settingsScroll");
            foreach (string language in new[] { "zh", "en" }) foreach (string theme in new[] { "light", "dark" })
            foreach (int width in new[] { 320, 590 }) foreach (int height in new[] { 360, 750 })
            {
                section.IsExpanded = false; SettleMotion(section); AppAppearance.Configure(language, theme);
                window.Width = width; window.Height = height; Settle(window);
                string context = language + "/" + theme + "/" + width + "x" + height;
                scroll.ScrollToTop(); Settle(window); ObserveSettings(window, true, context + "/open"); ObserveSettings(window, false, context + "/close");
                section.IsExpanded = true; WaitSettings(window); scroll.ScrollToBottom(); Settle(window);
                ObserveSettings(window, false, context + "/scroll-end-close");
                var modules = Read<Dictionary<string, CheckBox>>(window, "moduleChecks");
                Check(modules.Count == 38 && modules.Values.All(box => box.IsChecked == true), context + ": animation changed module selections");
                Check(window.Result == null, context + ": geometry test saved settings");
            }
        }

        private static void ObserveSettings(SettingsWindow window, bool open, string context, Action request = null)
        {
            if (settingsNativeTrace != null) settingsNativeTrace.Mark(context + "/before-request");
            if (islandNativeTrace != null) islandNativeTrace.Mark(context + "/before-request");
            var section = Section(window); var viewport = Viewport(section); var expected = FixedGeometry.Capture(window, SettingsChrome(window));
            var heights = new List<double>(); var opacities = new List<double>(); double before = EffectiveHeight(viewport), lastTime = -1;
            int messages = 0; var source = (HwndSource)PresentationSource.FromVisual(window);
            HwndSourceHook hook = (IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled) => { if (message == 3 || message == 5) messages++; return IntPtr.Zero; };
            EventHandler rendering = (s, e) => { expected.Verify(window, context); heights.Add(EffectiveHeight(viewport)); opacities.Add(viewport.Opacity); frames++; Gap(e, ref lastTime); };
            source.AddHook(hook); CompositionTarget.Rendering += rendering;
            try
            {
                var watch = Stopwatch.StartNew(); if (request == null) section.IsExpanded = open; else request(); SettingHandlers.Add(watch.Elapsed.TotalMilliseconds); window.UpdateLayout();
                Check(section.IsExpanded == open, context + ": header click does not update its bound expanded state");
                if (SystemParameters.ClientAreaAnimation) Check(Math.Abs(EffectiveHeight(viewport) - before) <= 1.1, context + ": request jumps straight to a different height");
                WaitSettings(window);
            }
            catch
            {
                if (settingsNativeTrace != null) settingsNativeTrace.DumpFailure(context);
                if (islandNativeTrace != null) islandNativeTrace.DumpFailure(context);
                throw;
            }
            finally { CompositionTarget.Rendering -= rendering; source.RemoveHook(hook); }
            geometryMessages += messages; Check(messages == 0, context + ": section changes native geometry");
            double after = EffectiveHeight(viewport);
            Check(open ? after > 100 : after < .1, context + ": final body height incorrect " + after);
            if (SystemParameters.ClientAreaAnimation)
                Check(heights.Any(value => value > Math.Min(before, after) + 1 && value < Math.Max(before, after) - 1), context + ": no genuine intermediate layout height");
            Monotonic(heights, open, context + "/height");
            Check(opacities.All(value => value >= -.001 && value <= 1.001), context + ": invalid body opacity");
            Check(Double.IsNaN(viewport.Height), context + ": temporary body height remains at rest");
            Check(viewport.Opacity == 1, context + ": completed section remains dim");
            if (!open) Check(viewport.Visibility == Visibility.Collapsed && !viewport.IsHitTestVisible, context + ": closed body retains hit region or blank space");
            expected.Verify(window, context + "/final"); CheckHeaderCaptions(section, context); settingTransitions++;
        }

        private static void SettingsRapidAndInterruptions(SettingsWindow window)
        {
            AppAppearance.Configure("en", "light"); window.Width = 391; window.Height = 560; Settle(window);
            var section = Section(window); section.IsExpanded = false; SettleMotion(section); Settle(window);
            var header = (ToggleButton)section.Template.FindName("PART_Header", section);
            header.BringIntoView(); Settle(window); Keyboard.Focus(header); Settle(window);
            Action clickHeader = () => typeof(ToggleButton).GetMethod("OnClick", Private).Invoke(header, null);
            ObserveSettings(window, true, "settings header ToggleButton/open", clickHeader);
            ObserveSettings(window, false, "settings header ToggleButton/close", clickHeader);
            var expected = FixedGeometry.Capture(window, SettingsChrome(window));
            for (int index = 0; index < 80; index++)
            {
                double before = EffectiveHeight(Viewport(section)); var watch = Stopwatch.StartNew(); section.IsExpanded = index % 3 != 0;
                SettingHandlers.Add(watch.Elapsed.TotalMilliseconds); window.UpdateLayout();
                if (SystemParameters.ClientAreaAnimation) Check(Math.Abs(before - EffectiveHeight(Viewport(section))) <= 1.1, "rapid settings reversal jumps on request " + index);
                Pump(3); window.UpdateLayout(); expected.Verify(window, "rapid settings/" + index);
            }
            section.IsExpanded = true; WaitSettings(window); Check(section.IsExpanded && !Motion(section), "latest settings open request lost");
            var boxes = Read<Dictionary<string, CheckBox>>(window, "moduleChecks");
            Check(boxes.Values.All(box => box.IsChecked == true), "rapid settings requests changed module values");
            object savedHeader = section.Header;
            section.Header = "Tools and modules / 工具模块：a longer section heading that must wrap safely in a compact settings window";
            Settle(window); CheckHeaderCaptions(section, "long bilingual settings header");
            Check(((FrameworkElement)section.Template.FindName("PART_Header", section)).ActualHeight > 32, "settings header did not wrap the long caption");
            section.Header = savedHeader; Settle(window);
            // A focused control inside the body must leave the closing hit tree.
            boxes.Values.First().BringIntoView(); Settle(window); Keyboard.Focus(boxes.Values.First());
            Check(((FrameworkElement)section.Content).IsKeyboardFocusWithin, "settings focus fixture did not focus a real body checkbox");
            section.IsExpanded = false; WaitSettings(window);
            Check(!((FrameworkElement)section.Content).IsKeyboardFocusWithin, "collapsed settings body retains keyboard focus"); Keyboard.ClearFocus();
            foreach (string action in new[] { "resize", "appearance", "hide" })
            {
                section.IsExpanded = false; SettleMotion(section); section.IsExpanded = true; Pump(25);
                if (action == "resize") window.Width += 1;
                else if (action == "appearance") AppAppearance.Configure("zh", "dark");
                else window.Hide();
                Settle(window); Check(!Motion(section), "settings " + action + " does not clean motion");
                if (!window.IsVisible) { window.Show(); Settle(window); }
            }
            section.IsExpanded = false; SettleMotion(section); section.IsExpanded = true; Pump(20); window.Close();
            Check(!Motion(section), "closing settings retains Rendering subscription");
        }

        private static void FilterMatrix(IslandWindow window)
        {
            Fill(window); var settings = Read<Settings>(window, "settings");
            foreach (string edge in new[] { "left", "right", "top", "bottom" })
            foreach (string language in new[] { "zh", "en" }) foreach (string theme in new[] { "light", "dark" })
            foreach (int width in new[] { 320, 560 })
            {
                window.SetExpanded(false, false); settings.Side = edge; settings.Language = language; settings.Theme = theme; settings.ExpandedWidth = width; settings.ExpandedHeight = 638;
                Call(window, "ApplyAppearance"); Call(window, "ChangeSide", edge); Call(window, "SelectPage", true); window.SetExpanded(true, false); Settle(window);
                ClickFilter(window, NotificationFilter.All); WaitFilter(window);
                string context = edge + "/" + language + "/" + theme + "/" + width;
                var expected = FixedGeometry.Capture(window, NotificationChrome(window));
                foreach (var filter in new[] { NotificationFilter.WeChat, NotificationFilter.QQ, NotificationFilter.Other, NotificationFilter.All }) ObserveFilter(window, filter, expected, context + "/" + filter);
                var feed = Read<NotificationFeed>(window, "notificationFeed"); feed.Clear(); Call(window, "UpdateNotificationList"); Settle(window);
                expected = FixedGeometry.Capture(window, NotificationChrome(window));
                ObserveFilter(window, NotificationFilter.QQ, expected, context + "/empty");
                Check(Read<ListBox>(window, "notificationList").Items.Count == 1, context + ": empty list has stale cards");
                Fill(window); ClickFilter(window, NotificationFilter.All); WaitFilter(window);
            }
        }

        private static void ObserveFilter(IslandWindow window, NotificationFilter filter, FixedGeometry expected, string context)
        {
            var list = Read<ListBox>(window, "notificationList"); var count = Read<TextBlock>(window, "notificationCount");
            var opacity = new List<double>(); double first = 1, lastTime = -1; int messages = 0;
            var source = (HwndSource)PresentationSource.FromVisual(window);
            HwndSourceHook hook = (IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled) => { if (message == 3 || message == 5) messages++; return IntPtr.Zero; };
            EventHandler rendering = (s, e) => {
                expected.Verify(window, context); opacity.Add(list.Opacity); frames++; Gap(e, ref lastTime);
                Check(Math.Abs(list.Opacity - count.Opacity) < .001, context + ": count/list opacity diverged");
                Check(list.Opacity >= .349 && list.Opacity <= 1.001, context + ": list flashes blank or invalid opacity");
            };
            string beforeLayout = DescribeFilterLayout(window);
            source.AddHook(hook); CompositionTarget.Rendering += rendering;
            try
            {
                var watch = Stopwatch.StartNew(); ClickFilter(window, filter); FilterHandlers.Add(watch.Elapsed.TotalMilliseconds); first = list.Opacity;
                Check(Read<NotificationFilter>(window, "notificationFilter") == filter, context + ": latest filter not selected synchronously");
                if (SystemParameters.ClientAreaAnimation) Check(first >= .349 && first < .999, context + ": new explicit filter does not start a soft live fade");
                window.UpdateLayout(); expected.Verify(window, context + "/request"); VerifyCards(window, filter, context); WaitFilter(window);
            }
            catch
            {
                Console.Error.WriteLine("FILTER LAYOUT BEFORE " + context + ": " + beforeLayout);
                Console.Error.WriteLine("FILTER LAYOUT FAILED AFTER REQUEST/LAYOUT " + context + ": " + DescribeFilterLayout(window));
                Call(window, "UpdateNotificationViewport"); window.UpdateLayout();
                Console.Error.WriteLine("FILTER LAYOUT AFTER EXPLICIT VIEWPORT REFRESH " + context + ": " + DescribeFilterLayout(window));
                throw;
            }
            finally { CompositionTarget.Rendering -= rendering; source.RemoveHook(hook); }
            geometryMessages += messages; Check(messages == 0, context + ": filter sends native move/size");
            if (SystemParameters.ClientAreaAnimation) Check(opacity.Any(value => value > first + .001 && value < .999), context + ": no intermediate rendered opacity");
            Monotonic(opacity, true, context + "/opacity", .001); Check(list.Opacity == 1 && count.Opacity == 1, context + ": completed filter remains dim");
            Check(list.ActualHeight >= 71 && list.ActualHeight <= list.MaxHeight + 1.1, context + ": list loses its bounded viewport");
            if (list.Items.Count > 12) Check(Realized(list) > 0 && Realized(list) < 12, context + ": filter realizes full notification history");
            Check(Read<System.Windows.Controls.Image>(window, "outgoingImage").Source == null, context + ": filter stores outgoing notification pixels");
            expected.Verify(window, context + "/final"); VerifyCards(window, filter, context); filterTransitions++;
        }

        private static void FilterRapidAndBackground(IslandWindow window)
        {
            Fill(window); ClickFilter(window, NotificationFilter.All); WaitFilter(window);
            var expected = FixedGeometry.Capture(window, NotificationChrome(window)); var list = Read<ListBox>(window, "notificationList");
            ClickFilter(window, NotificationFilter.WeChat); Pump(30); StopTimers(window);
            if (SystemParameters.ClientAreaAnimation)
            {
                Check(FilterActive(window), "background fixture has no active filter fade"); object clock = Read<object>(window, "notificationFilterTransitionWatch"); int version = Read<int>(window, "notificationFilterTransitionVersion"); double opacity = list.Opacity;
                ClickFilter(window, NotificationFilter.WeChat); Call(window, "UpdateNotificationListCore", false);
                Check(FilterActive(window) && Read<int>(window, "notificationFilterTransitionVersion") == version && ReferenceEquals(clock, Read<object>(window, "notificationFilterTransitionWatch")), "same filter/background sync resets its transition clock");
                Check(Math.Abs(opacity - list.Opacity) < .001, "same filter/background sync flashes opacity");
            }
            for (int index = 0; index < 80; index++)
            {
                var filter = (NotificationFilter)(index % 4); double before = list.Opacity;
                var watch = Stopwatch.StartNew(); ClickFilter(window, filter); FilterHandlers.Add(watch.Elapsed.TotalMilliseconds);
                if (SystemParameters.ClientAreaAnimation && before < .999) Check(list.Opacity + .001 >= before, "rapid filter deepens opacity on request " + index);
                window.UpdateLayout(); expected.Verify(window, "rapid filter/" + index); VerifyCards(window, filter, "rapid filter/" + index); Pump(3); StopTimers(window);
            }
            ClickFilter(window, NotificationFilter.Other); WaitFilter(window); Pump(200); StopTimers(window);
            VerifyCards(window, NotificationFilter.Other, "latest rapid filter"); Check(!FilterActive(window) && list.Opacity == 1, "rapid filter completion remains active/dim");
            var feed = Read<NotificationFeed>(window, "notificationFeed"); feed.Clear(); Call(window, "UpdateNotificationList"); Settle(window);
            expected.Verify(window, "cleared viewport/actions"); Fill(window); ClickFilter(window, NotificationFilter.All); WaitFilter(window); expected.Verify(window, "refilled viewport/actions");
        }

        private static void FilterInterruptions(IslandWindow window)
        {
            foreach (string action in new[] { "privacy", "clear", "block", "disabled", "appearance", "page", "collapse", "hide" })
            {
                var settings = Read<Settings>(window, "settings"); settings.HideNotificationBody = false;
                Call(window, "SelectPage", true); window.SetExpanded(true, false); Settle(window); Fill(window);
                ClickFilter(window, NotificationFilter.All); WaitFilter(window); ClickFilter(window, NotificationFilter.WeChat); Pump(25); StopTimers(window);
                if (action == "privacy") { settings.HideNotificationBody = true; Call(window, "UpdateNotificationList"); }
                else if (action == "clear") { Read<NotificationFeed>(window, "notificationFeed").Clear(); Call(window, "UpdateNotificationList"); }
                else if (action == "block") { Read<NotificationFeed>(window, "notificationFeed").SetSourceBlocked(WeChatKey, true); Call(window, "UpdateNotificationList"); }
                else if (action == "disabled") { settings.NotificationsEnabled = false; Call(window, "ConfigureNotifications"); }
                else if (action == "appearance") Call(window, "ApplyAppearance");
                else if (action == "page") Call(window, "SelectToolsPage");
                else if (action == "collapse") window.SetExpanded(false, false);
                else window.Hide();
                Check(!FilterActive(window), action + ": explicit interruption retains filter transition");
                Check(Read<ListBox>(window, "notificationList").Opacity == 1 && Read<TextBlock>(window, "notificationCount").Opacity == 1, action + ": interruption leaves dim notification controls");
                Check(Read<System.Windows.Controls.Image>(window, "outgoingImage").Source == null, action + ": interrupted filter retains notification bitmap");
                if (action == "privacy") Check(Read<ListBox>(window, "notificationList").Items.Cast<Border>().All(card => !CardText(card).Contains("FILTER_FIXTURE_")), "privacy retains raw message in live card");
                if (!window.IsVisible) { window.Show(); Settle(window); }
                Read<NotificationFeed>(window, "notificationFeed").SetSourceBlocked(WeChatKey, false);
            }
            Call(window, "SelectPage", true); window.SetExpanded(true, false); Settle(window); Fill(window); ClickFilter(window, NotificationFilter.All); WaitFilter(window);
            ClickFilter(window, NotificationFilter.WeChat); Pump(20); StopTimers(window); window.Close();
            Check(!FilterActive(window) && Read<ListBox>(window, "notificationList").Opacity == 1, "closing island retains filter Rendering subscription");
        }

        private static void DpiLayouts()
        {
            string artifacts = Environment.GetEnvironmentVariable("CODEX_ISLAND_SETTINGS_FILTER_ARTIFACTS");
            foreach (string language in new[] { "zh", "en" }) foreach (string theme in new[] { "light", "dark" }) foreach (int dpi in PopupDpiTests.Values)
            {
                AppAppearance.Configure(language, theme);
                var window = new SettingsWindow(new Settings()); AppAppearance.Register(window);
                try
                {
                    var root = Detached(window); var section = Section(window);
                    // A cold root per DPI models the application's system-DPI
                    // process. Hidden collapsed bodies keep cached old drawings
                    // when a detached tree is synthetically hot-switched.
                    foreach (int width in new[] { 320, 590 })
                    {
                        foreach (bool open in new[] { false, true })
                        {
                            section.IsExpanded = open; SettleMotion(section); PopupDpiTests.Apply(root, dpi); Layout(root, width, 560); PopupDpiTests.Render(root, dpi);
                            var footer = SettingsChrome(window).Last(); Rect footerBefore = Bounds(footer, root);
                            CheckHeaderCaptions(section, "settings DPI/" + dpi + "/" + width + "/" + language);
                            foreach (var button in Descendants<Button>(footer)) CheckCaption(button, "settings DPI footer");
                            try { PopupDpiTests.VerifyGlyphDpi(root, dpi, "settings section"); }
                            catch
                            {
                                Console.Error.WriteLine("SETTINGS DPI GLYPH CONTEXT: " + language + "/" + theme + "/" + dpi + "/" + width + "/open=" + open);
                                foreach (TextBlock label in PopupDpiTests.Descendants(root).OfType<TextBlock>())
                                    try { PopupDpiTests.VerifyGlyphDpi(label, dpi, "label"); } catch { Console.Error.WriteLine("STALE GLYPH: text='" + label.Text + "'; visible=" + label.IsVisible + "; visibility=" + label.Visibility + "; ancestors=" + VisibilityChain(label)); }
                                throw;
                            }
                            section.IsExpanded = !open; SettleMotion(section); Layout(root, width, 560); PopupDpiTests.Render(root, dpi);
                            Same(Bounds(footer, root), footerBefore, 1.1, "settings DPI footer moves on section toggle"); dpiCases++;
                        }
                        if (width == 320 && dpi == 144 && !String.IsNullOrEmpty(artifacts))
                        {
                            var scroll = Read<ScrollViewer>(window, "settingsScroll"); double oldOffset = scroll.VerticalOffset; bool oldOpen = section.IsExpanded;
                            try
                            {
                                section.IsExpanded = true; SettleMotion(section); Layout(root, width, 560);
                                scroll.ScrollToVerticalOffset(Math.Max(0, section.TranslatePoint(new Point(), scroll).Y + scroll.VerticalOffset - 12)); root.UpdateLayout();
                                PopupDpiTests.Save(root, artifacts, "settings-motion-" + language + "-" + theme + "-" + width + "-" + dpi, dpi);
                            }
                            finally { section.IsExpanded = oldOpen; SettleMotion(section); scroll.ScrollToVerticalOffset(oldOffset); Layout(root, width, 560); }
                        }
                    }
                    root.Content = null;
                }
                finally { window.Close(); }
            }
        }

        private static void NotificationDpiLayouts()
        {
            string artifacts = Environment.GetEnvironmentVariable("CODEX_ISLAND_SETTINGS_FILTER_ARTIFACTS");
            foreach (string language in new[] { "zh", "en" }) foreach (string theme in new[] { "light", "dark" }) foreach (int dpi in PopupDpiTests.Values)
            {
                var window = new IslandWindow(true); var subscriptions = new List<Action>(); ContentControl root = null;
                try
                {
                    var settings = Read<Settings>(window, "settings"); settings.Language = language; settings.Theme = theme;
                    Call(window, "ApplyAppearance"); Call(window, "SelectPage", true); window.SetExpanded(true, false); Fill(window);
                    root = Detached(window);
                    // Never-loaded trees lack the localization Entry subscriptions
                    // that live controls get from Loaded. Model only these known
                    // UI captions; original message text is never translated.
                    subscriptions.Add(TranslateDetached(Read<Button>(window, "notificationsTab"), ContentControl.ContentProperty));
                    subscriptions.Add(TranslateDetached(Read<TextBlock>(window, "notificationCount"), TextBlock.TextProperty));
                    foreach (int width in new[] { 320, 560 })
                    {
                        settings.ExpandedWidth = width; settings.ExpandedHeight = 638; settings.CustomExpandedSize = true; window.SetExpanded(true, false);
                        Fill(window); ClickFilter(window, NotificationFilter.All); PopupDpiTests.Apply(root, dpi); Layout(root, width, 638); PopupDpiTests.Render(root, dpi); Layout(root, width, 638);
                        var fixedElements = NotificationChrome(window).ToArray(); var baseline = fixedElements.Select(element => Bounds(element, root)).ToArray();
                        foreach (var filter in new[] { NotificationFilter.WeChat, NotificationFilter.QQ, NotificationFilter.Other, NotificationFilter.All })
                        {
                            ClickFilter(window, filter); Layout(root, width, 638); PopupDpiTests.Render(root, dpi);
                            for (int index = 0; index < fixedElements.Length; index++) Same(Bounds(fixedElements[index], root), baseline[index], 1.1, "filter DPI fixed geometry/" + language + "/" + theme + "/" + dpi + "/" + width + "/" + filter + "/element=" + index);
                            VerifyCards(window, filter, "filter DPI"); PopupDpiTests.VerifyGlyphDpi(root, dpi, "filter layout");
                            Check(Object.Equals(Read<Button>(window, "notificationsTab").Content, UiLocalization.Format("消息  {0}", 50)), "filter DPI message-tab locale differs from live controls");
                            dpiCases++;
                        }
                        Read<NotificationFeed>(window, "notificationFeed").Clear(); Call(window, "UpdateNotificationList"); Layout(root, width, 638); PopupDpiTests.Render(root, dpi);
                        for (int index = 0; index < fixedElements.Length; index++) Same(Bounds(fixedElements[index], root), baseline[index], 1.1, "empty filter DPI fixed geometry/" + language + "/" + dpi + "/" + width + "/element=" + index);
                        dpiCases++;
                        if (width == 320 && dpi == 144) PopupDpiTests.Save(root, artifacts, "notification-filter-" + language + "-" + theme + "-" + width + "-" + dpi, dpi);
                    }
                }
                finally { foreach (Action unsubscribe in subscriptions) unsubscribe(); if (root != null) root.Content = null; StopTimers(window); window.Close(); }
            }
        }

        private static Action TranslateDetached(DependencyObject target, DependencyProperty property)
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(property, target.GetType()); bool translating = false;
            EventHandler handler = (sender, args) =>
            {
                if (translating) return; string current = target.GetValue(property) as string, translated = UiLocalization.Localize(current);
                if (Object.Equals(current, translated)) return;
                try { translating = true; target.SetCurrentValue(property, translated); } finally { translating = false; }
            };
            descriptor.AddValueChanged(target, handler); handler(null, EventArgs.Empty);
            return () => descriptor.RemoveValueChanged(target, handler);
        }

        private static IEnumerable<FrameworkElement> SettingsChrome(SettingsWindow window)
        {
            var root = window.Content as Grid ?? (Grid)Read<ScrollViewer>(window, "settingsScroll").Parent;
            return root.Children.OfType<FrameworkElement>().Where(element => Grid.GetRow(element) == 0 || Grid.GetRow(element) == 2);
        }
        private static IEnumerable<FrameworkElement> NotificationChrome(IslandWindow window)
        {
            var buttons = ((Panel)Read<Button>(window, "pinButton").Parent).Children.OfType<Button>().Cast<FrameworkElement>();
            return buttons.Concat(new[] { Read<Border>(window, "expandedTabs") }).Concat(Read<Dictionary<NotificationFilter, Button>>(window, "filterButtons").Values.Cast<FrameworkElement>()).Concat(new[] { Read<StackPanel>(window, "notificationPage").Children.OfType<FrameworkElement>().Last() });
        }
        private static Expander Section(SettingsWindow window) { return ((System.Collections.IEnumerable)Read<object>(window, "animatedSections")).Cast<Expander>().Single(); }
        private static FrameworkElement Viewport(Expander section) { section.ApplyTemplate(); var value = section.Template.FindName("PART_PickerViewport", section) as FrameworkElement; Check(value != null, "smooth settings viewport missing"); return value; }
        private static bool Motion(Expander section) { return Property<bool>(section, "IsMotionActive"); }
        private static void SettleMotion(Expander section) { Call(section, "SettleMotion"); }
        private static bool FilterActive(IslandWindow window) { return Read<bool>(window, "notificationFilterTransitionActive"); }
        private static void WaitSettings(SettingsWindow window) { var wait = Stopwatch.StartNew(); do { Pump(15); window.UpdateLayout(); } while (Motion(Section(window)) && wait.ElapsedMilliseconds < 1600); Check(!Motion(Section(window)), "settings motion exceeds bounded completion wait"); window.UpdateLayout(); }
        private static void WaitFilter(IslandWindow window) { var wait = Stopwatch.StartNew(); do { Pump(15); StopTimers(window); window.UpdateLayout(); } while ((FilterActive(window) || Read<bool>(window, "pageTransitionActive")) && wait.ElapsedMilliseconds < 1600); Check(!FilterActive(window) && !Read<bool>(window, "pageTransitionActive"), "filter fade exceeds bounded completion wait"); window.UpdateLayout(); }
        private static void ClickFilter(IslandWindow window, NotificationFilter filter) { Read<Dictionary<NotificationFilter, Button>>(window, "filterButtons")[filter].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        private static void Fill(IslandWindow window)
        {
            var feed = Read<NotificationFeed>(window, "notificationFeed"); feed.Clear(); feed.SetSourceBlocked(WeChatKey, false);
            var messages = Enumerable.Range(0, 50).Select(index => new IslandNotification { Id = "filter-motion-" + index,
                AppId = index % 3 == 0 ? "fixture.wechat" : index % 3 == 1 ? "fixture.qq" : "fixture.other", AppName = index % 3 == 0 ? "微信" : index % 3 == 1 ? "QQ" : "Fixture Other",
                Title = "FILTER_FIXTURE_" + index + " " + new string('t', 160), Body = "FILTER_FIXTURE_BODY_" + index + " " + new string('b', 600), IsDemo = true, CreatedAt = DateTime.UtcNow.AddSeconds(-index) }).ToArray();
            Read<NotificationPreferences>(window, "notificationPreferences").Observe(messages); feed.Merge(messages, false, false); Call(window, "UpdateNotificationList"); Settle(window);
        }
        private static void VerifyCards(IslandWindow window, NotificationFilter filter, string context)
        {
            var list = Read<ListBox>(window, "notificationList"); var feed = Read<NotificationFeed>(window, "notificationFeed");
            int count = NotificationPresentation.Project(feed.GetItems(filter), Read<NotificationPreferences>(window, "notificationPreferences")).Count;
            Check(list.Items.Count == Math.Max(1, count), context + ": visible notification count is stale");
            if (count > 0) foreach (Border card in list.Items)
            {
                string text = CardText(card);
                Check(filter == NotificationFilter.All || text.Contains(filter == NotificationFilter.WeChat ? "微信" : filter == NotificationFilter.QQ ? "QQ" : "Fixture Other"), context + ": stale card from a different filter");
            }
        }
        private static int Realized(ListBox list) { return Enumerable.Range(0, list.Items.Count).Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) != null); }
        private static string DescribeFilterLayout(IslandWindow window)
        {
            var count = Read<TextBlock>(window, "notificationCount"); var list = Read<ListBox>(window, "notificationList"); var scroll = Read<ScrollViewer>(window, "detailsScroll");
            var page = Read<StackPanel>(window, "notificationPage"); var header = Read<Grid>(window, "expandedHeader"); var tabs = Read<Border>(window, "expandedTabs");
            return "language=" + UiLocalization.Language + "; count='" + count.Text + "' actual=" + count.ActualHeight + "; desired=" + count.DesiredSize + "; min=" + count.MinHeight +
                "; list height/max/actual/desired=" + list.Height + "/" + list.MaxHeight + "/" + list.ActualHeight + "/" + list.DesiredSize + "; viewport=" + scroll.ViewportHeight + "; offset=" + scroll.VerticalOffset +
                "; header desired/actual=" + header.DesiredSize + "/" + header.ActualHeight + "; tabs desired/actual=" + tabs.DesiredSize + "/" + tabs.ActualHeight + "; hint desired/actual=" + Read<TextBlock>(window, "expandedHint").DesiredSize + "/" + Read<TextBlock>(window, "expandedHint").ActualHeight + "; children=" +
                String.Join(" | ", page.Children.OfType<FrameworkElement>().Select((child, index) => index + ":" + child.GetType().Name + "; desired=" + child.DesiredSize + "; actual=" + child.ActualHeight + "; top=" + Bounds(child, window).Top));
        }
        private static string WeChatKey { get { return NotificationPreferences.SourceKey("fixture.wechat", "微信", true); } }
        private static string VisibilityChain(DependencyObject element) { var values = new List<string>(); for (DependencyObject current = element; current != null; current = VisualTreeHelper.GetParent(current)) { var ui = current as UIElement; values.Add(current.GetType().Name + (ui == null ? "" : ":" + ui.Visibility + "/" + ui.IsVisible)); } return String.Join(" > ", values); }
        private static string CardText(Border card) { return String.Join("|", Descendants<TextBlock>(card).Select(text => text.Text)); }
        private static void CheckHeaderCaptions(Expander section, string context) { var header = section.Template.FindName("PART_Header", section) as FrameworkElement; Check(header != null, context + ": missing header"); foreach (var text in Descendants<TextBlock>(header).Where(text => LocallyVisible(text) && text.Text.Length > 0)) { Check(text.ActualHeight + 1.1 >= text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom, context + ": header caption clipped vertically"); Check(Bounds(text, header).Right <= header.ActualWidth + 1.1, context + ": header caption overflows horizontally"); } }
        private static void CheckCaption(Button button, string context) { foreach (var text in Descendants<TextBlock>(button).Where(text => LocallyVisible(text))) Check(text.ActualHeight + 1.1 >= text.DesiredSize.Height, context + ": button caption clipped"); }
        private static bool LocallyVisible(DependencyObject element)
        {
            // A detached root has IsVisible=false without a presentation source,
            // although RenderTargetBitmap draws its visible subtree. Check the
            // actual Visibility chain so the DPI assertions do not skip labels.
            for (DependencyObject current = element; current != null; current = VisualTreeHelper.GetParent(current)) { var ui = current as UIElement; if (ui != null && ui.Visibility != Visibility.Visible) return false; }
            return true;
        }
        private static ContentControl Detached(Window window)
        {
            var content = (UIElement)window.Content; window.Content = null;
            // ContentControl's default presenter does not paint Background;
            // Window normally provides that layer outside its content tree.
            var root = new ContentControl { Content = new Border { Background = window.Background, Child = content }, Resources = window.Resources, FontFamily = window.FontFamily, FontSize = window.FontSize, FontWeight = window.FontWeight, FontStyle = window.FontStyle, FontStretch = window.FontStretch, Foreground = window.Foreground, Background = window.Background, UseLayoutRounding = window.UseLayoutRounding, SnapsToDevicePixels = window.SnapsToDevicePixels, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            TextOptions.SetTextFormattingMode(root, TextOptions.GetTextFormattingMode(window)); TextOptions.SetTextRenderingMode(root, TextOptions.GetTextRenderingMode(window)); TextOptions.SetTextHintingMode(root, TextOptions.GetTextHintingMode(window));
            return root;
        }
        private static void Gap(EventArgs args, ref double lastTime) { var time = (args as RenderingEventArgs).RenderingTime.TotalMilliseconds; if (lastTime >= 0 && time > lastTime) FrameGaps.Add(time - lastTime); lastTime = time; }
        private static void Timing(string label, List<double> values) { Console.WriteLine("TIMING: " + label + " request handler mean/max " + values.DefaultIfEmpty(0).Average().ToString("0.0") + "/" + values.DefaultIfEmpty(0).Max().ToString("0.0") + " ms."); }
        private static double EffectiveHeight(FrameworkElement value) { return value.Visibility == Visibility.Collapsed ? 0 : value.ActualHeight; }
        private static void Monotonic(IList<double> values, bool increasing, string context, double tolerance = 1.1) { for (int index = 1; index < values.Count; index++) Check(increasing ? values[index] + tolerance >= values[index - 1] : values[index] <= values[index - 1] + tolerance, context + ": layout/opacity reverses between frames " + values[index - 1] + "→" + values[index]); }
        private static Rect NativeBounds(Window window) { NativeRect rectangle; Check(GetWindowRect(new WindowInteropHelper(window).Handle, out rectangle), "own HWND bounds unavailable"); return new Rect(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top); }
        private static Rect ScreenBounds(FrameworkElement element) { return new Rect(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight))); }
        private static Rect Bounds(FrameworkElement element, Visual root) { return element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)); }
        private static void Same(Rect actual, Rect expected, double tolerance, string context) { Check(Math.Abs(actual.Left - expected.Left) <= tolerance && Math.Abs(actual.Top - expected.Top) <= tolerance && Math.Abs(actual.Width - expected.Width) <= tolerance && Math.Abs(actual.Height - expected.Height) <= tolerance, context + "; actual=" + actual + "; expected=" + expected); }
        private static void Settle(Window window) { window.UpdateLayout(); Pump(1); var island = window as IslandWindow; if (island != null) StopTimers(island); window.UpdateLayout(); }
        private static void Layout(FrameworkElement root, double width, double height) { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
        private static void StopTimers(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); var focus = Read<FocusPanel>(window, "focusPanel"); if (focus != null) focus.Stop(); }
        private static T Read<T>(object value, string field) { return (T)value.GetType().GetField(field, Private).GetValue(value); }
        private static T Property<T>(object value, string property) { return (T)value.GetType().GetProperty(property, Private).GetValue(value, null); }
        private static object Call(object value, string method, params object[] args) { try { return value.GetType().GetMethod(method, Private).Invoke(value, args); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) { var child = VisualTreeHelper.GetChild(root, index); var match = child as T; if (match != null) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; } }
        private static void Pump(int milliseconds) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
        private static void Check(bool value, string context) { if (!value) throw new Exception("Settings/filter motion: " + context); }
    }
}
