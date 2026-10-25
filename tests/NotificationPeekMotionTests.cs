using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // Actual own demo HWNDs and cold detached DPI roots. No system notification
    // reads, messages sent, desktop capture, pointer movement or global input.
    internal static class NotificationPeekMotionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
        private static int transitions, frames, rapidRequests, modeCases, interruptCases, routingCases, dpiCases, nativeMessages, sequence;
        private static readonly List<double> Handlers = new List<double>(), Gaps = new List<double>();

        internal static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), language = UiLocalization.Language, theme = UiTheme.Name;
            string folder = Path.Combine(Path.GetTempPath(), "CodexIsland.PeekMotion." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            transitions = frames = rapidRequests = modeCases = interruptCases = routingCases = dpiCases = nativeMessages = sequence = 0;
            Handlers.Clear(); Gaps.Clear();
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false,
                    HoverExpandEnabled = false, AutoMusic = false, OnlineLyricsEnabled = false, CustomExpandedSize = true,
                    ExpandedWidth = 391, ExpandedHeight = 680 });
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false };
                window.Loaded += (s, e) => StopTimers(window); window.Show(); Settle(window);
                Console.WriteLine("CHECK: consecutive live notification previews; " + PopupDpiTests.Describe(window));
                Matrix(window); ConstrainedFootprint(window);
                Console.WriteLine("CHECK: initial mode-clock priority, repeat requests, 80 mixed operations and cleanup.");
                DuringFirstMode(window); Console.WriteLine("PASS STAGE: parent mode priority.");
                RepeatsAndRapid(window); Console.WriteLine("PASS STAGE: repeated and rapid preview requests.");
                Interruptions(window); window = null; Console.WriteLine("PASS STAGE: interruption cleanup.");
                RoutingAndTimeout();
                Console.WriteLine("PASS STAGE: latest-target routing and timeout cleanup.");
                DpiLayouts();
                var gaps = Gaps.OrderBy(value => value).ToArray();
                Console.WriteLine("TIMING: consecutive preview handler mean/max " + Handlers.DefaultIfEmpty(0).Average().ToString("0.0") + "/" + Handlers.DefaultIfEmpty(0).Max().ToString("0.0") + " ms.");
                Console.WriteLine("TIMING: preview render gap p95/max " + (gaps.Length == 0 ? 0 : gaps[(int)Math.Ceiling(gaps.Length * .95) - 1]).ToString("0.0") + "/" + gaps.DefaultIfEmpty(0).Max().ToString("0.0") + " ms; setup/rapid dispatch intervals excluded.");
                Console.WriteLine("PASS: " + transitions + " real consecutive preview transitions, " + modeCases + " initial-mode interruptions, " + rapidRequests + " mixed rapid requests, " + interruptCases + " cleanup paths, " + routingCases + " latest-target routes, " + frames + " rendered geometry frames and " + dpiCases + " WPF DPI layouts; latest original/masked content and tooltips, fixed shell, parent-clock preservation and released old references; native move/size messages=" + nativeMessages + ".");
            }
            catch (Exception error) { Console.Error.WriteLine("CHECK FAILURE: " + error.GetType().Name + ": " + error.Message); throw; }
            finally
            {
                if (window != null && !Read<bool>(window, "shuttingDown")) { StopTimers(window); window.Close(); }
                AppAppearance.Configure(language, theme); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        private sealed class FixedPreview
        {
            private Rect native, panel, physicalPanel, surface, heading;
            internal static FixedPreview Capture(IslandWindow window)
            {
                var target = Read<Border>(window, "peekPanel");
                var content = Read<StackPanel>(window, "peekContent");
                var header = (FrameworkElement)VisualTreeHelper.GetParent(Read<TextBlock>(window, "peekSource"));
                return new FixedPreview { native = NativeBounds(window), panel = Bounds(target, window), physicalPanel = ScreenBounds(target), surface = window.CurrentVisualBounds(), heading = Bounds(header, content) };
            }
            internal void Verify(IslandWindow window, string context)
            {
                Same(NativeBounds(window), native, .01, context + ": native window moved/resized");
                Same(Bounds(Read<Border>(window, "peekPanel"), window), panel, 1.1, context + ": preview panel jumps");
                Same(ScreenBounds(Read<Border>(window, "peekPanel")), physicalPanel, 1.1, context + ": physical preview panel jumps");
                Same(window.CurrentVisualBounds(), surface, .01, context + ": preview outline jumps");
                var header = (FrameworkElement)VisualTreeHelper.GetParent(Read<TextBlock>(window, "peekSource"));
                Same(Bounds(header, Read<StackPanel>(window, "peekContent")), heading, 1.1, context + ": heading layout shifts when the title/body changes");
                Check(Read<Border>(window, "shell").Opacity == 1, context + ": body fade changes the whole shell");
            }
        }

        private static void Matrix(IslandWindow window)
        {
            foreach (string edge in new[] { "left", "right", "top", "bottom" })
            foreach (string language in new[] { "zh", "en" }) foreach (string theme in new[] { "light", "dark" })
            foreach (bool privacy in new[] { false, true })
            {
                Configure(window, edge, language, theme, privacy);
                Show(window, Message(0)); Wait(window);
                string context = edge + "/" + language + "/" + theme + "/privacy=" + privacy;
                foreach (int kind in new[] { 1, 2, 3 }) ObservePreview(window, Message(kind), context + "/kind=" + kind);
                window.SetExpanded(false, false); Settle(window); CheckEnded(window, context + "/end");
            }
        }

        private static void ConstrainedFootprint(IslandWindow window)
        {
            // Simulate a narrow available preview footprint without modifying
            // monitor configuration or global DPI settings.
            foreach (string language in new[] { "zh", "en" })
            {
                Configure(window, "right", language, "light", false); Show(window, Message(0)); Wait(window);
                Call(window, "TransitionSize", 260d, 142d, false); Settle(window);
                ObservePreview(window, Message(1), "constrained260/" + language);
                CheckPreviewCaptions(Read<Border>(window, "peekPanel"), "constrained260/" + language);
            }
            foreach (Rect work in new[] { new Rect(-640, -200, 260, 160), new Rect(0, 0, 332, 142) })
            foreach (string edge in new[] { "left", "right", "top", "bottom" })
            {
                Rect compact = WindowGeometry.DockCompact(new Rect(work.Left + 20, work.Top + 20, 88, 238), work, edge);
                Rect preview = WindowGeometry.PlaceFromCompact(compact, new Size(Math.Min(332, work.Width), Math.Min(142, work.Height)), edge, work);
                Check(preview.Left >= work.Left - .01 && preview.Top >= work.Top - .01 && preview.Right <= work.Right + .01 && preview.Bottom <= work.Bottom + .01, "constrained work area preview escapes on " + edge);
            }
        }

        private static void ObservePreview(IslandWindow window, IslandNotification message, string context)
        {
            var content = Read<StackPanel>(window, "peekContent"); var expected = FixedPreview.Capture(window);
            var opacity = new List<double>(); var offset = new List<double>(); double last = -1;
            var source = (HwndSource)PresentationSource.FromVisual(window); int messages = 0;
            HwndSourceHook hook = (IntPtr hwnd, int code, IntPtr w, IntPtr l, ref bool handled) => { if (code == 3 || code == 5) messages++; return IntPtr.Zero; };
            EventHandler render = (s, args) => {
                expected.Verify(window, context); VerifyLatest(window, message, context);
                opacity.Add(content.Opacity); offset.Add(content.RenderTransform.Value.OffsetY); frames++;
                Check(content.Opacity >= .349 && content.Opacity <= 1.001, context + ": content flashes blank or invalid alpha");
                Check(Math.Abs(content.RenderTransform.Value.OffsetY) <= 4.1, context + ": preview text moves outside its subtle offset");
                Gap(args, ref last);
            };
            source.AddHook(hook);
            try
            {
                var watch = Stopwatch.StartNew(); Show(window, message); Handlers.Add(watch.Elapsed.TotalMilliseconds);
                window.UpdateLayout(); VerifyLatest(window, message, context + "/immediate"); expected.Verify(window, context + "/immediate");
                if (SystemParameters.ClientAreaAnimation) Check(Active(window) && content.Opacity < .999, context + ": no live content transition begins");
                CompositionTarget.Rendering += render;
                try { Wait(window); } finally { CompositionTarget.Rendering -= render; }
            }
            finally { source.RemoveHook(hook); }
            Check(messages == 0, context + ": consecutive preview sent native move/size messages"); nativeMessages += messages;
            if (SystemParameters.ClientAreaAnimation) Check(opacity.Any(value => value > .351 && value < .999), context + ": no genuine intermediate preview alpha");
            Monotonic(opacity, true, .001, context + "/opacity");
            for (int index = 1; index < offset.Count; index++) Check(Math.Abs(offset[index]) <= Math.Abs(offset[index - 1]) + .01, context + ": text offset reverses");
            CheckRest(window, context); CheckPreviewCaptions(Read<Border>(window, "peekPanel"), context); transitions++;
        }

        private static void DuringFirstMode(IslandWindow window)
        {
            foreach (string edge in new[] { "left", "right", "top", "bottom" }) foreach (bool fromExpanded in new[] { false, true })
            {
                Configure(window, edge, "en", "dark", false);
                if (fromExpanded) { Call(window, "SelectPage", true); window.SetExpanded(true, false); Settle(window); }
                var first = Message(0); Show(window, first); Pump(30); StopTimers(window);
                if (SystemParameters.ClientAreaAnimation) Check(Read<bool>(window, "visualTransitionActive"), "initial-mode fixture has already finished");
                object clock = Read<object>(window, "transitionWatch"); int version = Read<int>(window, "transitionVersion");
                double alpha = Read<Border>(window, "shell").Opacity * Read<StackPanel>(window, "peekContent").Opacity;
                Rect native = NativeBounds(window), visual = window.CurrentVisualBounds();
                var next = Message(1); Show(window, next);
                VerifyLatest(window, next, "initial-mode/latest");
                Check(ReferenceEquals(clock, Read<object>(window, "transitionWatch")) && version == Read<int>(window, "transitionVersion"), "consecutive arrival restarts parent mode clock");
                Same(NativeBounds(window), native, .01, "consecutive arrival resizes the parent union HWND");
                Same(window.CurrentVisualBounds(), visual, .01, "consecutive arrival jumps parent visual bounds");
                Check(!Active(window) && Read<StackPanel>(window, "peekContent").Opacity == 1, "parent mode priority double-fades preview content");
                Check(Math.Abs(Read<Border>(window, "shell").Opacity * Read<StackPanel>(window, "peekContent").Opacity - alpha) <= .001, "consecutive arrival flashes the effective incoming alpha");
                Check(Read<System.Windows.Controls.Image>(window, "outgoingImage").Source == null && Read<object>(window, "pendingTransitionSnapshot") == null, "new preview retains old message pixels in a mode bitmap");
                var incoming = new List<double>();
                EventHandler render = (s, e) => { incoming.Add(Read<Border>(window, "shell").Opacity * Read<StackPanel>(window, "peekContent").Opacity); frames++; Check(!(Read<Border>(window, "shell").Opacity < .999 && Read<StackPanel>(window, "peekContent").Opacity < .999), "mode replacement double-fades its incoming layer"); };
                CompositionTarget.Rendering += render;
                try { Wait(window); } finally { CompositionTarget.Rendering -= render; }
                Monotonic(incoming, true, .001, "mode replacement effective opacity");
                VerifyLatest(window, next, "initial-mode/final"); CheckRest(window, "initial-mode/final"); modeCases++;
            }
        }

        private static void RepeatsAndRapid(IslandWindow window)
        {
            Configure(window, "right", "en", "light", false); Show(window, Message(0)); Wait(window);
            var current = Message(1); Show(window, current); Pump(25); StopTimers(window);
            if (SystemParameters.ClientAreaAnimation)
            {
                Check(Active(window), "repeat fixture has no active preview transition");
                object clock = Read<object>(window, "peekTransitionWatch"); int version = Read<int>(window, "peekTransitionVersion");
                double opacity = Read<StackPanel>(window, "peekContent").Opacity;
                Show(window, current); Call(window, "RefreshPeekText"); window.UpdateLayout();
                Check(ReferenceEquals(clock, Read<object>(window, "peekTransitionWatch")) && version == Read<int>(window, "peekTransitionVersion"), "same preview/background refresh restarts its clock");
                Check(Math.Abs(opacity - Read<StackPanel>(window, "peekContent").Opacity) < .001, "same preview/background refresh changes alpha");
            }
            Wait(window);
            var sameTextNewIdentity = new IslandNotification { Id = current.Id, AppId = current.AppId, AppName = current.AppName,
                Title = current.Title, Body = current.Body, IsDemo = current.IsDemo, CreatedAt = current.CreatedAt.AddSeconds(1) };
            int identityVersion = Read<int>(window, "peekTransitionVersion"); Show(window, sameTextNewIdentity);
            if (SystemParameters.ClientAreaAnimation) Check(Active(window) && Read<int>(window, "peekTransitionVersion") != identityVersion, "new timestamp with the same text is mistaken for a repeated toast");
            Wait(window); current = sameTextNewIdentity; VerifyLatest(window, current, "new timestamp identity");
            for (int index = 0; index < 80; index++)
            {
                if (index % 8 == 0)
                {
                    if (index % 16 == 0) window.SetExpanded(true, true); else window.SetExpanded(false, true);
                    Pump(3); StopTimers(window);
                }
                if (index % 8 == 1 && Read<bool>(window, "expanded")) Call(window, "SelectPage", true);
                if (index % 8 == 2) ClickFilter(window, (NotificationFilter)(index % 4));
                if (index % 8 == 3) ClickClear(window);
                if (index % 8 == 4) { window.SetExpanded(false, false); Settle(window); }
                current = Message(index % 4);
                double before = Read<StackPanel>(window, "peekContent").Opacity; bool wasActive = Active(window), wasPeek = Read<bool>(window, "peeking");
                Show(window, current); window.UpdateLayout(); VerifyLatest(window, current, "rapid/" + index);
                if (wasPeek && wasActive && SystemParameters.ClientAreaAnimation) Check(Read<StackPanel>(window, "peekContent").Opacity + .001 >= before, "rapid latest preview deepens its fade on request " + index);
                Pump(3); StopTimers(window); rapidRequests++;
            }
            Wait(window); VerifyLatest(window, current, "rapid/latest"); CheckRest(window, "rapid/latest");
            // Timer starts for each new message; an old scheduled timeout must
            // not terminate a fresh preview while its own dwell is still active.
            var timer = Read<DispatcherTimer>(window, "peekTimer"); TimeSpan interval = timer.Interval;
            try
            {
                timer.Interval = TimeSpan.FromMilliseconds(180); Show(window, Message(0)); Pump(110);
                current = Message(1); Show(window, current); Pump(110);
                Check(Read<bool>(window, "peeking"), "earlier timeout dismisses a newer preview before its dwell"); VerifyLatest(window, current, "renewed dwell");
            }
            finally { timer.Stop(); timer.Interval = interval; StopTimers(window); }
        }

        private static void Interruptions(IslandWindow window)
        {
            foreach (string action in new[] { "expand", "collapse", "filter", "clear", "privacy", "block", "disabled", "appearance", "resize", "hide" })
            {
                Configure(window, "right", "zh", "dark", false); var first = Message(0); var next = Message(1);
                Read<NotificationPreferences>(window, "notificationPreferences").Observe(new[] { first, next });
                Read<NotificationFeed>(window, "notificationFeed").Merge(new[] { first, next }, false, false);
                Show(window, first); Wait(window); Show(window, next); Pump(20); StopTimers(window);
                if (SystemParameters.ClientAreaAnimation) Check(Active(window), action + ": interruption fixture has no active preview fade");
                var settings = Read<Settings>(window, "settings");
                object peekClock = Read<object>(window, "peekTransitionWatch"); int peekVersion = Read<int>(window, "peekTransitionVersion");
                double peekAlpha = Read<StackPanel>(window, "peekContent").Opacity;
                if (action == "expand") window.SetExpanded(true, true);
                else if (action == "collapse") window.SetExpanded(false, true);
                else if (action == "filter") ClickFilter(window, NotificationFilter.QQ);
                else if (action == "clear") ClickClear(window);
                else if (action == "privacy") { settings.HideNotificationBody = true; Call(window, "ConfigureNotifications"); }
                else if (action == "block") Call(window, "SetNotificationSourceBlocked", NotificationPreferences.SourceKey(next), true);
                else if (action == "disabled") { settings.NotificationsEnabled = false; Call(window, "ConfigureNotifications"); }
                else if (action == "appearance") { settings.Language = "en"; settings.Theme = "light"; settings.HideNotificationBody = true; Call(window, "ApplyAppearance"); }
                else if (action == "resize") Call(window, "FreezeSizeAnimation");
                else window.Hide();
                if (action == "filter")
                {
                    Check(ReferenceEquals(peekClock, Read<object>(window, "peekTransitionWatch")) && peekVersion == Read<int>(window, "peekTransitionVersion"), "filter interrupts an unrelated preview clock");
                    Check(Math.Abs(peekAlpha - Read<StackPanel>(window, "peekContent").Opacity) <= .001, "filter flashes the current preview");
                    VerifyLatest(window, next, "filter retains latest preview");
                }
                else Check(!Active(window), action + ": preview keeps its own rendering driver after interruption");
                if (action == "expand") Check(Read<bool>(window, "expanded"), "expand is overridden by ending peek");
                if (action == "collapse") Check(!Read<bool>(window, "expanded"), "collapse is overridden by ending peek");
                if (new[] { "clear", "privacy", "block", "disabled", "hide" }.Contains(action)) CheckEnded(window, action + "/immediate");
                if (action == "appearance") VerifyLatest(window, next, "appearance keeps refreshed masked preview");
                Wait(window);
                if (new[] { "clear", "privacy", "block", "disabled", "hide", "expand", "collapse" }.Contains(action)) CheckEnded(window, action + "/final");
                else if (action == "filter" || action == "appearance" || action == "resize") VerifyLatest(window, next, action + "/final");
                if (!window.IsVisible) { window.Show(); Settle(window); }
                Read<NotificationPreferences>(window, "notificationPreferences").SetBlocked(NotificationPreferences.SourceKey(next), false);
                Read<NotificationFeed>(window, "notificationFeed").SetSourceBlocked(NotificationPreferences.SourceKey(next), false);
                interruptCases++;
            }
            Configure(window, "right", "zh", "dark", false); Show(window, Message(0)); Wait(window); Show(window, Message(1)); Pump(20); StopTimers(window); window.Close();
            Check(!Active(window), "close retains preview rendering driver"); CheckEnded(window, "close"); interruptCases++;
        }

        private static void RoutingAndTimeout()
        {
            // The interruption test closes its window. Each routing fixture owns
            // its own fresh window so no closed HWND is reused.
            foreach (string appId in new[] { "fixture.qq", "sideisland.timer", "sideisland.reminders", "sideisland.watch", "sideisland.logwatch", "sideisland.ready" })
            {
                Console.WriteLine("CHECK: latest-target route " + appId);
                var window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false };
                try
                {
                    window.Loaded += (s, e) => StopTimers(window); window.Show(); Settle(window);
                    Configure(window, "right", "en", "light", false); Show(window, Message(0)); Wait(window);
                    var latest = Message(1); latest.AppId = appId; Show(window, latest);
                    Call(window, "OpenPeekTarget");
                    Check(Read<bool>(window, "expanded") && !Read<bool>(window, "peeking"), "latest preview click fails to expand");
                    if (appId.StartsWith("sideisland.", StringComparison.Ordinal))
                    {
                        string tool = appId == "sideisland.timer" ? "focus" : appId == "sideisland.reminders" ? "reminders" : appId.Substring("sideisland.".Length);
                        Check(Read<bool>(window, "toolsPageSelected") && Read<string>(window, "selectedTool") == tool, "latest preview click routes an old tool: " + appId);
                    }
                    else Check(Read<bool>(window, "notificationPageSelected") && !Read<bool>(window, "toolsPageSelected"), "normal preview click fails to select messages");
                    Wait(window); CheckEnded(window, "latest route/" + appId); routingCases++;
                    window.SetExpanded(false, false); Show(window, Message(0)); Wait(window);
                    var timer = Read<DispatcherTimer>(window, "peekTimer"); timer.Interval = TimeSpan.FromMilliseconds(35); timer.Start(); Pump(55);
                    Wait(window); CheckEnded(window, "timer expiry"); Check(!Read<bool>(window, "expanded"), "peek timer expiry unexpectedly expands the island");
                }
                finally { if (!Read<bool>(window, "shuttingDown")) { StopTimers(window); window.Close(); } }
            }
        }

        private static void DpiLayouts()
        {
            string artifacts = Environment.GetEnvironmentVariable("CODEX_ISLAND_PEEK_MOTION_ARTIFACTS");
            foreach (string language in new[] { "zh", "en" }) foreach (string theme in new[] { "light", "dark" })
            foreach (int dpi in PopupDpiTests.Values)
            {
                Console.WriteLine("CHECK: cold preview DPI root " + language + "/" + theme + "/" + dpi);
                var window = new IslandWindow(true); ContentControl root = null;
                try
                {
                    Configure(window, "right", language, theme, false); Show(window, Message(0)); StopTimers(window);
                    root = Detached(window);
                    foreach (int width in new[] { 260, 332 })
                    foreach (int kind in new[] { 1, 2, 3 })
                    {
                        // Dwell expiry belongs to the own-HWND cases. Keep the
                        // cold layout root free of native timer side effects.
                        var message = Message(kind); Show(window, message); StopTimers(window); Call(window, "TransitionSize", (double)width, 142d, false);
                        PopupDpiTests.Apply(root, dpi); Layout(root, width, 142); PopupDpiTests.Render(root, dpi); Layout(root, width, 142);
                        VerifyLatest(window, message, "DPI/" + dpi + "/" + width);
                        Check(!Active(window), "never-loaded DPI preview starts a rendering driver");
                        CheckPreviewCaptions(Read<Border>(window, "peekPanel"), "DPI/" + dpi + "/" + width + "/kind=" + kind);
                        Check(PopupDpiTests.VerifyGlyphDpi(Read<Border>(window, "peekPanel"), dpi, "notification preview") > 0, "preview DPI test drew no glyphs");
                        dpiCases++;
                        if (dpi == 144 && width == 332 && kind == 1) PopupDpiTests.Save(root, artifacts, "notification-peek-" + language + "-" + theme + "-" + width + "-" + dpi, dpi);
                    }
                    Call(window, "EndPeek"); CheckEnded(window, "DPI end");
                }
                finally { if (root != null) root.Content = null; StopTimers(window); window.Close(); }
            }
        }

        private static void VerifyLatest(IslandWindow window, IslandNotification message, string context)
        {
            Check(Read<bool>(window, "peeking") && !Read<bool>(window, "expanded"), context + ": latest preview mode is not current");
            var settings = Read<Settings>(window, "settings");
            Check(Read<string>(window, "peekAppId") == message.AppId, context + ": latest click target is stale");
            var retained = Read<IslandNotification>(window, "peekMessage");
            Check(retained != null && retained.Id == message.Id && retained.Title == message.Title && retained.Body == message.Body && retained.CreatedAt == message.CreatedAt, context + ": latest data reference is stale");
            string source = String.IsNullOrWhiteSpace(message.AppName) ? UiLocalization.Localize("其他") : message.AppName;
            string title = settings.HideNotificationBody ? UiLocalization.Localize("收到一条新通知") : message.Title;
            string body = settings.HideNotificationBody ? UiLocalization.Localize("消息内容已隐藏") : message.Body;
            foreach (var pair in new[] { new KeyValuePair<string, string>("peekSource", source), new KeyValuePair<string, string>("peekTitle", title), new KeyValuePair<string, string>("peekBody", body) })
            {
                var label = Read<TextBlock>(window, pair.Key);
                Check(label.Text == pair.Value && Convert.ToString(label.ToolTip) == pair.Value, context + ": stale original/masked caption or tooltip in " + pair.Key);
            }
            Check(Read<TextBlock>(window, "peekKind").Text == UiLocalization.Localize(message.IsDemo ? "示例" : "新通知"), context + ": stale notification type");
            Check(Read<TextBlock>(window, "peekHint").Text == UiLocalization.Localize("点击查看"), context + ": preview click hint was not restored after cleanup");
            Check(Read<TextBlock>(window, "peekTitle").Visibility == (title.Length == 0 ? Visibility.Collapsed : Visibility.Visible), context + ": empty title reserves a row");
            Check(Read<TextBlock>(window, "peekBody").Visibility == (body.Length == 0 ? Visibility.Collapsed : Visibility.Visible), context + ": empty body reserves a row");
            if (!Read<bool>(window, "visualTransitionActive")) Check(Read<System.Windows.Controls.Image>(window, "outgoingImage").Source == null && Read<object>(window, "pendingTransitionSnapshot") == null, context + ": consecutive preview keeps old pixels");
            var actual = Descendants<TextBlock>(Read<Border>(window, "peekPanel")).Where(label => EffectiveVisible(label)).Select(label => label.Text).ToArray();
            foreach (string old in actual.Where(text => text.StartsWith("PRIVATE_PEEK_", StringComparison.Ordinal))) Check(old == source || old == title || old == body, context + ": another preview text layer survives");
        }

        private static void CheckEnded(IslandWindow window, string context)
        {
            Check(!Read<bool>(window, "peeking") && !Active(window), context + ": peek remains active");
            Check(Read<Border>(window, "peekPanel").Visibility == Visibility.Collapsed, context + ": hidden preview remains visible");
            Check(Read<IslandNotification>(window, "peekMessage") == null && String.IsNullOrEmpty(Read<string>(window, "peekAppId")), context + ": old preview data/target reference retained");
            Check(Read<object>(window, "peekTransitionWatch") == null && String.IsNullOrEmpty(Read<string>(window, "peekRenderedSignature")), context + ": old preview clock/signature retained");
            foreach (string field in new[] { "peekSource", "peekTitle", "peekBody", "peekKind", "peekHint" })
            {
                var text = Read<TextBlock>(window, field); Check(text.Text.Length == 0 && String.IsNullOrEmpty(Convert.ToString(text.ToolTip)), context + ": old text/tooltip retained in " + field);
            }
        }
        private static void CheckRest(IslandWindow window, string context)
        {
            var content = Read<StackPanel>(window, "peekContent");
            Check(!Active(window) && content.Opacity == 1 && Math.Abs(content.RenderTransform.Value.OffsetX) < .01 && Math.Abs(content.RenderTransform.Value.OffsetY) < .01, context + ": preview retains temporary opacity/transform/clock");
        }
        private static void CheckPreviewCaptions(Border panel, string context)
        {
            foreach (var text in Descendants<TextBlock>(panel).Where(item => EffectiveVisible(item) && item.Text.Length > 0))
            {
                Rect bounds = Bounds(text, panel);
                Check(bounds.Left >= -1.1 && bounds.Top >= -1.1 && bounds.Right <= panel.ActualWidth + 1.1 && bounds.Bottom <= panel.ActualHeight + 1.1, context + ": preview caption outside its frame " + text.Text);
                Check(text.ActualHeight + 1.1 >= text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom, context + ": preview caption loses a partial line");
            }
        }
        private static bool EffectiveVisible(DependencyObject value) { while (value != null) { var element = value as UIElement; if (element != null && element.Visibility != Visibility.Visible) return false; value = VisualTreeHelper.GetParent(value); } return true; }
        private static IslandNotification Message(int kind)
        {
            int id = ++sequence;
            return new IslandNotification { Id = "peek-" + id, AppId = kind == 2 ? "fixture.empty" : kind % 2 == 0 ? "fixture.wechat" : "fixture.qq",
                AppName = kind == 2 ? "" : "PRIVATE_PEEK_SOURCE_" + id + (kind == 1 ? " / 应用 source " + new string('s', 80) : ""),
                Title = kind == 2 || kind == 3 ? "" : "PRIVATE_PEEK_TITLE_" + id + (kind == 1 ? " / 中文 😀 " + new string('t', 180) : ""),
                Body = kind == 3 ? "" : "PRIVATE_PEEK_BODY_" + id + (kind == 1 ? " / original 原文 😀 " + new string('b', 300) : ""),
                CreatedAt = DateTime.UtcNow, IsDemo = kind % 2 == 0 };
        }
        private static void Configure(IslandWindow window, string edge, string language, string theme, bool privacy)
        {
            window.SetExpanded(false, false); var settings = Read<Settings>(window, "settings"); settings.Side = edge; settings.Language = language; settings.Theme = theme;
            settings.HideNotificationBody = privacy; settings.NotificationsEnabled = false; settings.AutoCollapse = false; settings.HoverExpandEnabled = false;
            Call(window, "ApplyAppearance"); Call(window, "ChangeSide", edge); Settle(window);
        }
        private static ContentControl Detached(Window window)
        {
            var content = window.Content; window.Content = null;
            var root = new ContentControl { Content = new Border { Background = window.Background, Child = (UIElement)content }, Resources = window.Resources,
                FontFamily = window.FontFamily, FontSize = window.FontSize, FontWeight = window.FontWeight, FontStyle = window.FontStyle, FontStretch = window.FontStretch,
                Foreground = window.Foreground, Background = window.Background, UseLayoutRounding = window.UseLayoutRounding, SnapsToDevicePixels = window.SnapsToDevicePixels,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            TextOptions.SetTextFormattingMode(root, TextOptions.GetTextFormattingMode(window)); TextOptions.SetTextRenderingMode(root, TextOptions.GetTextRenderingMode(window)); TextOptions.SetTextHintingMode(root, TextOptions.GetTextHintingMode(window)); return root;
        }
        private static void ClickFilter(IslandWindow window, NotificationFilter value) { Read<Dictionary<NotificationFilter, Button>>(window, "filterButtons")[value].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        private static void ClickClear(IslandWindow window) { Descendants<Button>(Read<StackPanel>(window, "notificationPage")).First(button => Object.Equals(button.Content, UiLocalization.Localize("清空岛内记录"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        private static void Show(IslandWindow window, IslandNotification message) { Call(window, "ShowPeek", message); }
        private static bool Active(IslandWindow window) { return Read<bool>(window, "peekTransitionActive"); }
        private static void Wait(IslandWindow window) { var watch = Stopwatch.StartNew(); do { Pump(15); StopTimers(window); window.UpdateLayout(); } while ((Active(window) || Read<bool>(window, "visualTransitionActive") || Read<bool>(window, "pageTransitionActive") || Read<bool>(window, "notificationFilterTransitionActive")) && watch.ElapsedMilliseconds < 1800); Check(!Active(window) && !Read<bool>(window, "visualTransitionActive"), "preview/mode motion exceeds bounded completion wait"); window.UpdateLayout(); }
        private static void Gap(EventArgs args, ref double last) { double now = ((RenderingEventArgs)args).RenderingTime.TotalMilliseconds; if (last >= 0 && now > last) Gaps.Add(now - last); last = now; }
        private static Rect NativeBounds(Window window) { NativeRect value; Check(GetWindowRect(new WindowInteropHelper(window).Handle, out value), "own native bounds missing"); return new Rect(value.Left, value.Top, value.Right - value.Left, value.Bottom - value.Top); }
        private static Rect Bounds(FrameworkElement value, Visual root) { return value.TransformToAncestor(root).TransformBounds(new Rect(value.RenderSize)); }
        private static Rect ScreenBounds(FrameworkElement value) { return new Rect(value.PointToScreen(new Point()), value.PointToScreen(new Point(value.ActualWidth, value.ActualHeight))); }
        private static void Same(Rect value, Rect expected, double tolerance, string context) { Check(Math.Abs(value.Left - expected.Left) <= tolerance && Math.Abs(value.Top - expected.Top) <= tolerance && Math.Abs(value.Width - expected.Width) <= tolerance && Math.Abs(value.Height - expected.Height) <= tolerance, context + "; actual=" + value + "; expected=" + expected); }
        private static void Monotonic(IList<double> values, bool increasing, double tolerance, string context) { for (int index = 1; index < values.Count; index++) Check(increasing ? values[index] + tolerance >= values[index - 1] : values[index] <= values[index - 1] + tolerance, context + ": opacity reverses between frames"); }
        private static void Settle(Window window) { window.UpdateLayout(); Pump(1); var island = window as IslandWindow; if (island != null) StopTimers(island); window.UpdateLayout(); }
        private static void StopTimers(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); var focus = Read<FocusPanel>(window, "focusPanel"); if (focus != null) focus.Stop(); }
        private static void Layout(FrameworkElement root, double width, double height) { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
        private static T Read<T>(object value, string field) { return (T)value.GetType().GetField(field, Private).GetValue(value); }
        private static object Call(object value, string method, params object[] args) { try { return value.GetType().GetMethod(method, Private).Invoke(value, args); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) { var child = VisualTreeHelper.GetChild(root, index); var typed = child as T; if (typed != null) yield return typed; foreach (var nested in Descendants<T>(child)) yield return nested; } }
        private static void Pump(int milliseconds) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
        private static void Check(bool value, string context) { if (!value) throw new Exception("Notification peek motion: " + context); }
    }
}
