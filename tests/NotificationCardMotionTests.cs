using System;
using System.Collections;
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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // Own isolated demo HWNDs and synthetic feed clocks. No real notification
    // APIs, desktop capture, global input, user account data or clipboard access.
    internal static class NotificationCardMotionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
        private static int frames, addCases, readCases, removeCases, counterCases, clearCases, dpiCases, nativeMessages, sequence;
        private static readonly List<double> Handlers = new List<double>(), Gaps = new List<double>();
        private sealed class Clock { internal DateTime Now = DateTime.UtcNow; }

        internal static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), language = UiLocalization.Language, theme = UiTheme.Name;
            string folder = Path.Combine(Path.GetTempPath(), "CodexIsland.CardMotion." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            frames = addCases = readCases = removeCases = counterCases = clearCases = dpiCases = nativeMessages = sequence = 0;
            Handlers.Clear(); Gaps.Clear();
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false,
                    HoverExpandEnabled = false, AutoMusic = false, OnlineLyricsEnabled = false, CustomExpandedSize = true,
                    ExpandedWidth = 391, ExpandedHeight = 680, CompactSlot1 = "notifications", CompactSlot2 = "codex-secondary" });
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false };
                window.Loaded += (s, e) => StopTimers(window); window.Show(); Settle(window);
                Console.WriteLine("CHECK: notification card live add/read/expiry geometry; " + PopupDpiTests.Describe(window));
                Matrix(window);
                Console.WriteLine("CHECK: bottom anchor, unchanged refresh, rapid latest-wins and privacy cleanup.");
                ScrollAnchor(window); BackgroundAndRapid(window); CounterBoundaries(window); ClearButton(window); Interruptions(window);
                window = null;
                DpiLayouts();
                var gaps = Gaps.OrderBy(value => value).ToArray();
                Console.WriteLine("TIMING: notification mutation handler mean/max " + Handlers.DefaultIfEmpty(0).Average().ToString("0.0") + "/" + Handlers.DefaultIfEmpty(0).Max().ToString("0.0") + " ms.");
                Console.WriteLine("TIMING: notification motion render gap p95/max " + (gaps.Length == 0 ? 0 : gaps[(int)Math.Ceiling(gaps.Length * .95) - 1]).ToString("0.0") + "/" + gaps.DefaultIfEmpty(0).Max().ToString("0.0") + " ms; setup and rapid dispatch excluded.");
                Console.WriteLine("PASS: " + addCases + " live card additions, " + readCases + " read-state fades, " + removeCases + " ordinary removal transitions, " + counterCases + " unread boundary transitions, " + clearCases + " safe clear-button transitions, " + frames + " rendered geometry frames and " + dpiCases + " WPF DPI layouts; 80 mixed rapid requests, latest logical data, retained card identity/scroll anchors, bounded virtualization, fixed chrome/actions and privacy/interruption cleanup; native move/size messages=" + nativeMessages + ".");
            }
            catch (Exception error) { Console.Error.WriteLine("CHECK FAILURE: " + error.GetType().Name + ": " + error.Message); throw; }
            finally
            {
                if (window != null && !Read<bool>(window, "shuttingDown")) { StopTimers(window); window.Close(); }
                AppAppearance.Configure(language, theme); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        private sealed class FixedGeometry
        {
            private Rect native;
            private FrameworkElement[] elements;
            private Rect[] logical, physical;
            internal static FixedGeometry Capture(IslandWindow window)
            {
                var controls = Chrome(window).ToArray();
                return new FixedGeometry { native = NativeBounds(window), elements = controls,
                    logical = controls.Select(control => Bounds(control, window)).ToArray(), physical = controls.Select(ScreenBounds).ToArray() };
            }
            internal void Verify(IslandWindow window, string context)
            {
                Same(NativeBounds(window), native, .01, context + ": native HWND moved/resized");
                for (int index = 0; index < elements.Length; index++)
                {
                    Same(Bounds(elements[index], window), logical[index], 1.1, context + ": fixed logical chrome moved " + index);
                    Same(ScreenBounds(elements[index]), physical[index], 1.1, context + ": fixed physical chrome moved " + index);
                    Check(Math.Abs(elements[index].Opacity - 1) < .001, context + ": fixed surface fades " + index);
                }
            }
        }

        private static void Matrix(IslandWindow window)
        {
            foreach (string edge in new[] { "left", "right", "top", "bottom" })
            foreach (string language in new[] { "zh", "en" }) foreach (string theme in new[] { "light", "dark" })
            foreach (int width in new[] { 320, 560 })
            {
                Configure(window, edge, width, language, theme);
                string context = edge + "/" + language + "/" + theme + "/" + width;
                var clock = Reset(window, 3);
                ObserveAdd(window, clock, context);
                ObserveRead(window, context);
                ObserveRemoval(window, context);
            }
        }

        private static void ObserveAdd(IslandWindow window, Clock clock, string context)
        {
            var list = List(window); var survivor = (Border)list.Items[0]; double before = Bounds(survivor, window).Top;
            var expected = FixedGeometry.Capture(window); var positions = new List<double>(); var opacity = new List<double>();
            Border added = null;
            Observe(window, expected, () => {
                var message = Message(clock.Now.AddSeconds(1), "incoming-" + ++sequence, 0);
                Feed(window).Merge(new[] { message }, false, false); Refresh(window, false);
                Check(list.Items.Count == 4 && ReferenceEquals(survivor, list.Items[1]), context + ": add replaces unrelated live card");
                Check(CardText((Border)list.Items[0]).Contains(message.Title), context + ": incoming logical card not current");
                added = (Border)list.Items[0]; VerifyCounts(window, 4, 4);
            }, () => { positions.Add(Bounds(survivor, window).Top); if (added != null) opacity.Add(added.Opacity); }, context + "/add");
            double after = Bounds(survivor, window).Top;
            Check(after > before + 25, context + ": incoming card does not shift existing top card");
            if (SystemParameters.ClientAreaAnimation)
            {
                Check(positions.Any(value => value > before + 1 && value < after - 1), context + ": no genuine survivor intermediate position");
                Check(opacity.Any(value => value > .001 && value < .999), context + ": incoming card never softly fades");
            }
            Monotonic(positions, true, 1.1, context + "/add positions"); Monotonic(opacity, true, .001, context + "/add opacity"); CheckRest(window, context); addCases++;
        }

        private static void ObserveRead(IslandWindow window, string context)
        {
            var list = List(window); var titles = list.Items.Cast<Border>().Select(card => CardTitle(card)).ToArray();
            var expected = FixedGeometry.Capture(window); var opacity = new List<double>();
            Observe(window, expected, () => {
                ClickRead(window);
                VerifyCounts(window, 4, 0);
                Check(list.Items.Count == 4 && list.Items.Cast<Border>().All(card => !CardText(card).Contains("●")), context + ": read state not immediate");
                Check(titles.SequenceEqual(list.Items.Cast<Border>().Select(CardTitle)), context + ": reading changes notification content/order");
            }, () => opacity.Add(((Border)list.Items[0]).Opacity), context + "/read");
            if (SystemParameters.ClientAreaAnimation) Check(opacity.Any(value => value > .001 && value < .999), context + ": read state has no live intermediate fade");
            Monotonic(opacity, true, .001, context + "/read opacity"); CheckRest(window, context); readCases++;
        }

        private static void ObserveRemoval(IslandWindow window, string context)
        {
            var clock = Reset(window, 0); var old = Message(clock.Now - NotificationFeed.Retention + TimeSpan.FromSeconds(2), "expired-" + ++sequence, 0);
            old.AppId = "fixture.wechat.priority." + sequence; old.AppName = "微信";
            var fresh = Message(clock.Now, "fresh-" + ++sequence, 1);
            var preferences = Read<NotificationPreferences>(window, "notificationPreferences"); preferences.Observe(new[] { old, fresh }); preferences.SetPriority(NotificationPreferences.SourceKey(old), 1);
            Feed(window).Merge(new[] { old, fresh }, false, false); Refresh(window, true); Wait(window);
            var list = List(window); Check(list.Items.Count == 2 && CardText((Border)list.Items[0]).Contains(old.Title), context + ": expiry fixture top priority missing");
            var removed = (Border)list.Items[0]; var survivor = (Border)list.Items[1]; double before = Bounds(survivor, window).Top;
            var expected = FixedGeometry.Capture(window); var positions = new List<double>(); var removedOpacity = new List<double>();
            bool sawOverlay = false;
            string removalBefore = DescribeCard(window, removed), removalAfter = "";
            Observe(window, expected, () => {
                clock.Now = clock.Now.AddSeconds(3); Refresh(window, false);
                removalAfter = DescribeCard(window, removed);
                Check(list.Items.Count == 1 && ReferenceEquals(list.Items[0], survivor), context + ": expired logical item was retained/deferred");
                VerifyCounts(window, 1, 1);
            }, () => {
                positions.Add(Bounds(survivor, window).Top);
                var overlay = Overlay(window); sawOverlay |= overlay.Children.Count > 0;
                var outgoing = overlay.Children.OfType<Border>().FirstOrDefault(card => Object.Equals(card.Tag, removed.Tag));
                if (outgoing != null)
                {
                    removedOpacity.Add(outgoing.Opacity);
                    Check(CardText(outgoing).Contains(old.Title), context + ": removed live visual shows a different notification");
                    Check(!outgoing.IsHitTestVisible && !overlay.IsHitTestVisible, context + ": removed live visual intercepts input");
                }
            }, context + "/expiry");
            double after = Bounds(survivor, window).Top;
            Check(after < before - 25, context + ": expired row leaves a blank gap");
            if (SystemParameters.ClientAreaAnimation)
            {
                Check(positions.Any(value => value < before - 1 && value > after + 1), context + ": removed-row survivor jumps directly");
                Check(sawOverlay && removedOpacity.Any(value => value > .001 && value < .999), context + ": ordinary removed card lacks a real outgoing fade; before=" + removalBefore + "; request=" + removalAfter + "; final=" + DescribeCard(window, removed) + "; samples=" + String.Join(",", removedOpacity.Select(value => value.ToString("0.000"))));
            }
            Monotonic(positions, false, 1.1, context + "/remove positions");
            Monotonic(removedOpacity, false, .001, context + "/remove opacity"); CheckRest(window, context); removeCases++;
        }

        private static void Observe(IslandWindow window, FixedGeometry expected, Action request, Action sample, string context)
        {
            double lastTime = -1; int messages = 0;
            var source = (HwndSource)PresentationSource.FromVisual(window);
            HwndSourceHook hook = (IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled) => { if (message == 3 || message == 5) messages++; return IntPtr.Zero; };
            EventHandler rendering = (sender, args) => {
                expected.Verify(window, context); sample(); frames++;
                double now = ((RenderingEventArgs)args).RenderingTime.TotalMilliseconds;
                if (lastTime >= 0 && now > lastTime) Gaps.Add(now - lastTime); lastTime = now;
                Check(Overlay(window).Children.Count <= 12, context + ": outgoing visuals are not viewport bounded");
                foreach (UIElement card in List(window).Items) Check(card.Opacity >= -.001 && card.Opacity <= 1.001, context + ": invalid card opacity");
            };
            source.AddHook(hook);
            try
            {
                var watch = Stopwatch.StartNew(); request(); Handlers.Add(watch.Elapsed.TotalMilliseconds); window.UpdateLayout();
                expected.Verify(window, context + "/request");
                CompositionTarget.Rendering += rendering;
                try { Wait(window); } finally { CompositionTarget.Rendering -= rendering; }
            }
            finally { source.RemoveHook(hook); }
            Check(messages == 0, context + ": native geometry changes during notification mutation"); nativeMessages += messages;
            expected.Verify(window, context + "/final");
        }

        private static void ScrollAnchor(IslandWindow window)
        {
            Configure(window, "right", 391, "en", "light"); var clock = Reset(window, 50);
            var list = List(window); var scroll = Viewer(window);
            for (int attempt = 0; attempt < 3; attempt++) { list.ScrollIntoView(list.Items[49]); scroll.ScrollToEnd(); Settle(window); }
            Check(scroll.VerticalOffset > 0 && Realized(list) > 0 && Realized(list) < 12, "scroll anchor fixture is not a bounded bottom viewport");
            Border anchor = VisibleCards(window).First(); double y = Bounds(anchor, window).Top;
            string beforeDescription = DescribeScroll(window, anchor);
            double oldOffset = scroll.VerticalOffset, rowSpan = anchor.ActualHeight + anchor.Margin.Top + anchor.Margin.Bottom;
            int oldIndex = list.Items.IndexOf(anchor);
            var positions = new List<double>();
            var expected = FixedGeometry.Capture(window);
            Observe(window, expected, () => {
                Feed(window).Merge(new[] { Message(clock.Now.AddSeconds(1), "offscreen-arrival-" + ++sequence, 2) }, false, false); Refresh(window, false);
                Check(list.Items.Count == 50 && list.Items.Contains(anchor), "capacity trim loses the surviving visible scroll anchor");
            }, () => { positions.Add(Bounds(anchor, window).Top); Check(Realized(list) < 12, "arrival at bottom realizes full history"); }, "bottom scroll anchor");
            double afterY = Bounds(anchor, window).Top;
            double requestedOffset = oldOffset + (list.Items.IndexOf(anchor) - oldIndex) * rowSpan;
            double availableOffset = Math.Max(0, Math.Min(scroll.ScrollableHeight, requestedOffset));
            double shortfall = requestedOffset - availableOffset;
            if (shortfall > 1.5)
            {
                // This capped-history fixture removes the old last row while
                // inserting at the top. Its first visible survivor becomes the
                // final item: the old Y needs an offset beyond the real extent.
                // Verify only that unavoidable clamp, without an empty spacer.
                SameVertical(scroll.VerticalOffset, scroll.ScrollableHeight, 1.5, "bottom reader does not clamp to its real maximum");
                SameVertical(shortfall, rowSpan, 1.5, "capacity-trim fixture has an unexpected unavoidable offset shortfall");
                SameVertical(afterY - y, shortfall, 1.5, "bottom clamp exceeds its unavoidable displacement; before=" + beforeDescription + "; after=" + DescribeScroll(window, anchor));
                SameVertical(scroll.ExtentHeight, 50 * rowSpan, 1.5, "bottom clamp adds artificial empty scroll extent");
                if (SystemParameters.ClientAreaAnimation)
                {
                    Check(positions.Count > 1 && Math.Abs(positions[0] - y) <= 1.5, "bottom clamp flashes directly on its first frame");
                    Check(positions.Any(value => value > y + 1 && value < afterY - 1), "bottom clamp lacks genuine intermediate visual movement");
                }
                Monotonic(positions, true, 1.5, "bottom capacity clamp");
            }
            else SameVertical(afterY, y, 1.5, "new offscreen arrival jumps a feasible bottom anchor; before=" + beforeDescription + "; after=" + DescribeScroll(window, anchor));
            Check(scroll.VerticalOffset > 0, "new arrival scrolls bottom reader to top"); CheckRest(window, "bottom scroll anchor");
            // A middle reader has enough extent for exact anchoring. Three
            // simultaneous arrivals move its identity outside the old realized
            // range and exercise the targeted ScrollIntoView fallback.
            clock = Reset(window, 50); scroll = Viewer(window); scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * .45); Settle(window);
            anchor = VisibleCards(window).First(); y = Bounds(anchor, window).Top; oldOffset = scroll.VerticalOffset; oldIndex = list.Items.IndexOf(anchor);
            expected = FixedGeometry.Capture(window);
            Observe(window, expected, () => {
                Feed(window).Merge(Enumerable.Range(1, 3).Select(index => Message(clock.Now.AddSeconds(index), "burst-" + ++sequence, index % 3)), false, false); Refresh(window, false);
                Check(list.Items.Count == 50 && list.Items.IndexOf(anchor) == oldIndex + 3, "burst projection does not preserve the reader's existing identity");
            }, () => { Check(Realized(list) < 12, "burst anchor realizes all history"); }, "middle reader three-arrival anchor");
            SameVertical(Bounds(anchor, window).Top, y, 1.5, "feasible middle anchor jumps after a three-arrival burst");
            Check(scroll.VerticalOffset > oldOffset && scroll.VerticalOffset < scroll.ScrollableHeight - 1, "middle anchor did not compensate within available extent");
            CheckRest(window, "middle burst anchor");
        }

        private static void BackgroundAndRapid(IslandWindow window)
        {
            var clock = Reset(window, 3);
            Feed(window).Merge(new[] { Message(clock.Now.AddSeconds(1), "background-" + ++sequence, 0) }, false, false); Refresh(window, false); Pump(30); StopTimers(window);
            if (SystemParameters.ClientAreaAnimation)
            {
                Check(CardActive(window), "unchanged-refresh fixture has no active card motion");
                object watch = Read<object>(window, "notificationCardTransitionWatch"); int version = Read<int>(window, "notificationCardTransitionVersion");
                object counterWatch = Read<object>(window, "notificationCounterTransitionWatch"); int counterVersion = Read<int>(window, "notificationCounterTransitionVersion");
                Refresh(window, false); window.UpdateLayout();
                Check(CardActive(window) && ReferenceEquals(watch, Read<object>(window, "notificationCardTransitionWatch")) && version == Read<int>(window, "notificationCardTransitionVersion"), "unchanged background refresh restarts card motion");
                Check(ReferenceEquals(counterWatch, Read<object>(window, "notificationCounterTransitionWatch")) && counterVersion == Read<int>(window, "notificationCounterTransitionVersion"), "unchanged background refresh restarts numeric motion");
            }
            Wait(window); var expected = FixedGeometry.Capture(window);
            for (int index = 0; index < 80; index++)
            {
                var timing = Stopwatch.StartNew();
                if (index % 11 == 0) ClickFilter(window, (NotificationFilter)((index / 11) % 4));
                else if (index % 13 == 0) { Feed(window).Clear(); Refresh(window, true); Check(Overlay(window).Children.Count == 0, "rapid clear retains old text overlay"); }
                else if (index % 3 == 1) ClickRead(window);
                else { clock.Now = clock.Now.AddSeconds(1); Feed(window).Merge(new[] { Message(clock.Now, "rapid-" + ++sequence, index % 3) }, false, false); Refresh(window, false); }
                Handlers.Add(timing.Elapsed.TotalMilliseconds); window.UpdateLayout(); expected.Verify(window, "rapid mutation/" + index);
                VerifyProjection(window, "rapid mutation/" + index); Check(Realized(List(window)) < 12, "rapid changes realize full history");
                Pump(3); StopTimers(window);
            }
            ClickFilter(window, NotificationFilter.All); Wait(window); VerifyProjection(window, "latest rapid state"); CheckRest(window, "rapid completion");
            // A parent filter fade controls the list/count and must not multiply
            // with child row/counter opacity in the same content subtree.
            ClickFilter(window, NotificationFilter.WeChat);
            Check(!CardActive(window), "filter starts while child card motions still own transforms");
            Check(!CounterTargets(window).Contains(Read<TextBlock>(window, "notificationCount")), "filter count receives a second child opacity animation");
            Wait(window);
        }

        private static void CounterBoundaries(IslandWindow window)
        {
            Configure(window, "right", 320, "en", "light"); var clock = Reset(window, 9);
            ObserveCounter(window, () => { Feed(window).Merge(new[] { Message(clock.Now.AddSeconds(1), "ten-" + ++sequence, 0) }, false, false); Refresh(window, false); }, 10, "expanded 9-to-10");
            Reset(window, 50); ObserveCounter(window, () => ClickRead(window), 0, "expanded 50-to-0");
            foreach (string edge in new[] { "left", "right", "top", "bottom" })
            {
                Configure(window, edge, 391, "zh", "dark"); var settings = Read<Settings>(window, "settings"); settings.CompactMode = "quota"; settings.CompactSlot1 = "notifications";
                clock = Reset(window, 9); window.SetExpanded(false, false); Settle(window);
                ObserveCounter(window, () => { Feed(window).Merge(new[] { Message(clock.Now.AddSeconds(1), "compact-ten-" + ++sequence, 0) }, false, false); Refresh(window, false); }, 10, "compact 9-to-10/" + edge);
                window.SetExpanded(true, false); Reset(window, 50); window.SetExpanded(false, false); Settle(window);
                ObserveCounter(window, () => ClickRead(window), 0, "compact 50-to-0/" + edge);
            }
            window.SetExpanded(true, false); Settle(window);
        }

        private static void ObserveCounter(IslandWindow window, Action request, int unread, string context)
        {
            bool expanded = Read<bool>(window, "expanded");
            Rect native = NativeBounds(window), firstSlot = Bounds(Read<Grid>(window, "compactSlot1"), window), secondSlot = Bounds(Read<Grid>(window, "compactSlot2"), window);
            var badge = Read<TextBlock>(window, "compactNotificationIcon");
            double badgeCenter = Bounds(badge, window).Left + badge.ActualWidth / 2;
            var opacity = new List<double>(); var translations = new List<double>();
            var targetSamples = new Dictionary<UIElement, List<double>>();
            EventHandler rendering = (sender, args) => {
                Same(NativeBounds(window), native, .01, context + ": numeric update changes HWND");
                if (!expanded)
                {
                    Same(Bounds(Read<Grid>(window, "compactSlot1"), window), firstSlot, 1.1, context + ": compact primary moves");
                    Same(Bounds(Read<Grid>(window, "compactSlot2"), window), secondSlot, 1.1, context + ": compact secondary moves");
                    SameVertical(Bounds(badge, window).Left + badge.ActualWidth / 2, badgeCenter, 1.1, context + ": compact badge loses its center");
                }
                foreach (UIElement target in CounterTargets(window).Keys)
                {
                    opacity.Add(target.Opacity); translations.Add(target.RenderTransform.Value.OffsetY);
                    List<double> values; if (!targetSamples.TryGetValue(target, out values)) targetSamples[target] = values = new List<double>(); values.Add(target.Opacity);
                    Check(target.Opacity >= .349 && target.Opacity <= 1.001, context + ": numeric fade flashes blank");
                    if (expanded) Check(Math.Abs(target.RenderTransform.Value.OffsetY) <= 2.1, context + ": numeric offset escapes its compact movement");
                }
                frames++;
            };
            request(); window.UpdateLayout();
            Check(Feed(window).UnreadCount == unread, context + ": canonical unread state deferred");
            string expected = unread > 0 ? unread.ToString() : "✦"; Check(badge.Text == expected, context + ": badge text is stale during animation");
            if (expanded) VerifyCounts(window, List(window).Items.Count, unread);
            CompositionTarget.Rendering += rendering;
            try { Wait(window); } finally { CompositionTarget.Rendering -= rendering; }
            if (SystemParameters.ClientAreaAnimation) Check(opacity.Any(value => value > .001 && value < .999), context + ": no genuine numeric intermediate opacity");
            foreach (var values in targetSamples.Values) Monotonic(values, true, .001, context + "/numeric opacity");
            Check(!CounterActive(window), context + ": numeric driver retained at rest");
            if (!expanded) Check(Read<CompactWidgetVisual>(window, "compactWidget1").State.Value == unread.ToString(), context + ": compact unread widget is not current");
            counterCases++;
        }

        private static void ClearButton(IslandWindow window)
        {
            Configure(window, "right", 320, "en", "light"); Reset(window, 5);
            var list = List(window); var count = Read<TextBlock>(window, "notificationCount");
            var expected = FixedGeometry.Capture(window); var rowOpacity = new List<double>(); var countOpacity = new List<double>();
            Observe(window, expected, () => {
                var clear = Descendants<Button>(Read<StackPanel>(window, "notificationPage")).First(button => Object.Equals(button.Content, UiLocalization.Localize("清空岛内记录")));
                clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Feed(window).Items.Count == 0 && list.Items.Count == 1, "clear button defers logical deletion"); VerifyCounts(window, 0, 0);
                Check(Overlay(window).Children.Count == 0 && !CardText((Border)list.Items[0]).Contains("CARD_FIXTURE"), "clear button keeps removed original content");
            }, () => {
                rowOpacity.Add(((Border)list.Items[0]).Opacity); countOpacity.Add(count.Opacity);
                Check(Overlay(window).Children.Count == 0 && !CardText((Border)list.Items[0]).Contains("CARD_FIXTURE"), "safe clear fade exposes old notification text");
            }, "real clear button");
            if (SystemParameters.ClientAreaAnimation)
            {
                Check(rowOpacity.Any(value => value > .001 && value < .999), "real clear button lacks a new safe empty-state fade");
                Check(countOpacity.Any(value => value > .001 && value < .999), "real clear button lacks a new safe zero-count fade");
            }
            Monotonic(rowOpacity, true, .001, "safe empty-state fade"); Monotonic(countOpacity, true, .001, "safe zero-count fade");
            CheckRest(window, "real clear button"); clearCases++;
        }

        private static void Interruptions(IslandWindow window)
        {
            foreach (string action in new[] { "privacy", "clear", "block", "disabled", "appearance", "page", "collapse", "resize", "hide", "wheel" })
            {
                Configure(window, "right", 391, "zh", "dark"); var clock = Reset(window, 4);
                Feed(window).Merge(new[] { Message(clock.Now.AddSeconds(1), "interrupt-" + ++sequence, 0) }, false, false); Refresh(window, false); Pump(25); StopTimers(window);
                var settings = Read<Settings>(window, "settings");
                if (action == "privacy") { settings.HideNotificationBody = true; Refresh(window, true); }
                else if (action == "clear") { Feed(window).Clear(); Refresh(window, true); }
                else if (action == "block") Call(window, "SetNotificationSourceBlocked", NotificationPreferences.SourceKey(Feed(window).Items.First()), true);
                else if (action == "disabled") { settings.NotificationsEnabled = false; Call(window, "ConfigureNotifications"); }
                else if (action == "appearance") Call(window, "ApplyAppearance");
                else if (action == "page") Call(window, "SelectToolsPage");
                else if (action == "collapse") window.SetExpanded(false, false);
                else if (action == "resize") Call(window, "FreezeSizeAnimation");
                else if (action == "wheel")
                {
                    var expiring = Message(clock.Now - NotificationFeed.Retention + TimeSpan.FromSeconds(2), "wheel-expiry-" + ++sequence, 0);
                    expiring.AppId = "fixture.wheel.priority." + sequence;
                    var preferences = Read<NotificationPreferences>(window, "notificationPreferences"); preferences.Observe(new[] { expiring }); preferences.SetPriority(NotificationPreferences.SourceKey(expiring), 1);
                    Feed(window).Merge(new[] { expiring }, false, false); Refresh(window, true); Wait(window);
                    clock.Now = clock.Now.AddSeconds(3); Refresh(window, false); Pump(25); StopTimers(window);
                    if (SystemParameters.ClientAreaAnimation) Check(CardActive(window) && Overlay(window).Children.Count > 0, "wheel fixture has no real removed live visual");
                    var titles = List(window).Items.Cast<Border>().Select(CardTitle).ToArray(); int unread = Feed(window).UnreadCount;
                    var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
                    List(window).RaiseEvent(wheel);
                    Check(!wheel.Handled, "card cleanup consumes the user's scroll wheel event");
                    Check(!CardActive(window) && Overlay(window).Children.Count == 0, "wheel leaves absolute outgoing visuals attached to a scrolling viewport");
                    Check(unread == Feed(window).UnreadCount && titles.SequenceEqual(List(window).Items.Cast<Border>().Select(CardTitle)), "wheel cleanup changes current notifications");
                }
                else window.Hide();
                Check(Overlay(window).Children.Count == 0, action + ": retains an outgoing notification content overlay");
                Check(Read<Image>(window, "outgoingImage").Source == null, action + ": retains notification bitmap pixels");
                if (action == "privacy") Check(List(window).Items.Cast<Border>().All(card => !CardText(card).Contains("CARD_FIXTURE")), "privacy exposes original card text");
                if (action == "clear" || action == "disabled") Check(List(window).Items.Count == 1 && !CardText((Border)List(window).Items[0]).Contains("CARD_FIXTURE"), action + ": retains old logical content");
                if (action == "page" || action == "collapse" || action == "resize" || action == "hide") Check(!CardActive(window) && !CounterActive(window), action + ": old drivers retain ownership");
                Wait(window); if (!window.IsVisible) { window.Show(); Settle(window); }
                CheckRest(window, "interrupt/" + action);
            }
            Configure(window, "right", 391, "en", "light"); var finalClock = Reset(window, 3);
            Feed(window).Merge(new[] { Message(finalClock.Now.AddSeconds(1), "close-" + ++sequence, 0) }, false, false); Refresh(window, false); Pump(20);
            StopTimers(window); window.Close(); Check(!CardActive(window) && !CounterActive(window) && Overlay(window).Children.Count == 0, "closed window retains notification animation references");
        }

        private static void DpiLayouts()
        {
            string artifacts = Environment.GetEnvironmentVariable("CODEX_ISLAND_CARD_MOTION_ARTIFACTS");
            foreach (string language in new[] { "zh", "en" }) foreach (string theme in new[] { "light", "dark" }) foreach (int dpi in PopupDpiTests.Values)
            {
                var window = new IslandWindow(true); ContentControl root = null; var subscriptions = new List<Action>();
                try
                {
                    var settings = Read<Settings>(window, "settings"); settings.Language = language; settings.Theme = theme; settings.CustomExpandedSize = true; settings.ExpandedHeight = 680;
                    Call(window, "ApplyAppearance"); Call(window, "SelectPage", true); window.SetExpanded(true, false);
                    root = Detached(window);
                    subscriptions.Add(TranslateDetached(Read<Button>(window, "notificationsTab"), ContentControl.ContentProperty));
                    subscriptions.Add(TranslateDetached(Read<TextBlock>(window, "notificationCount"), TextBlock.TextProperty));
                    foreach (int width in new[] { 320, 560 })
                    {
                        settings.ExpandedWidth = width; window.SetExpanded(true, false); var clock = ResetDetached(window, 9);
                        PopupDpiTests.Apply(root, dpi); Layout(root, width, 680); PopupDpiTests.Render(root, dpi); Layout(root, width, 680);
                        var controls = Chrome(window).ToArray(); var baseline = controls.Select(control => Bounds(control, root)).ToArray();
                        foreach (string action in new[] { "add", "read", "clear" })
                        {
                            if (action == "add") { Feed(window).Merge(new[] { Message(clock.Now.AddSeconds(1), "dpi-add-" + ++sequence, 0) }, false, false); Refresh(window, false); }
                            else if (action == "read") ClickRead(window);
                            else { Feed(window).Clear(); Refresh(window, true); }
                            Layout(root, width, 680); PopupDpiTests.Render(root, dpi); Layout(root, width, 680);
                            for (int index = 0; index < controls.Length; index++) Same(Bounds(controls[index], root), baseline[index], 1.1, "notification DPI stable chrome/" + language + "/" + dpi + "/" + width + "/" + action + "/" + index);
                            Check(!CardActive(window) && !CounterActive(window), "detached DPI tree starts rendering drivers");
                            PopupDpiTests.VerifyGlyphDpi(root, dpi, "notification card/counters"); CheckCaptions(root, "notification DPI/" + dpi + "/" + action); dpiCases++;
                        }
                        if (width == 320 && dpi == 144) PopupDpiTests.Save(root, artifacts, "notification-card-" + language + "-" + theme + "-" + width + "-" + dpi, dpi);
                    }
                }
                finally { foreach (Action unsubscribe in subscriptions) unsubscribe(); if (root != null) root.Content = null; StopTimers(window); window.Close(); }
            }
        }

        private static void Configure(IslandWindow window, string edge, int width, string language, string theme)
        {
            window.SetExpanded(false, false); var settings = Read<Settings>(window, "settings"); settings.Side = edge; settings.Language = language; settings.Theme = theme;
            settings.ExpandedWidth = width; settings.ExpandedHeight = 680; settings.CustomExpandedSize = true; settings.HideNotificationBody = false; settings.NotificationsEnabled = false;
            Call(window, "ApplyAppearance"); Call(window, "ChangeSide", edge); Call(window, "SelectPage", true); window.SetExpanded(true, false); Settle(window);
        }
        private static Clock Reset(IslandWindow window, int count) { var clock = ResetDetached(window, count); Settle(window); Wait(window); Viewer(window).ScrollToTop(); Settle(window); return clock; }
        private static Clock ResetDetached(IslandWindow window, int count)
        {
            Call(window, "StopNotificationCardTransition"); Call(window, "StopNotificationCounterTransitions");
            var clock = new Clock(); Set(window, "notificationFeed", new NotificationFeed(() => clock.Now));
            Set(window, "notificationFilter", NotificationFilter.All);
            var messages = Enumerable.Range(0, count).Select(index => Message(clock.Now.AddSeconds(-index), "fixture-" + ++sequence, index % 3)).ToArray();
            Read<NotificationPreferences>(window, "notificationPreferences").Observe(messages); Feed(window).Merge(messages, false, false); Refresh(window, true); return clock;
        }
        private static IslandNotification Message(DateTime created, string id, int source) { return new IslandNotification { Id = id, AppId = source == 0 ? "fixture.wechat" : source == 1 ? "fixture.qq" : "fixture.other", AppName = source == 0 ? "微信" : source == 1 ? "QQ" : "Fixture Other", Title = "CARD_FIXTURE_" + id, Body = "CARD_FIXTURE_BODY_" + id + " " + new string('b', 180), CreatedAt = created, IsDemo = true }; }
        private static void Refresh(IslandWindow window, bool explicitChange) { if (explicitChange) Call(window, "UpdateNotificationList"); else Call(window, "UpdateNotificationListCore", false); }
        private static void ClickRead(IslandWindow window) { var button = Descendants<Button>(Read<StackPanel>(window, "notificationPage")).First(item => Object.Equals(item.Content, UiLocalization.Localize("岛内已读"))); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        private static void ClickFilter(IslandWindow window, NotificationFilter filter) { Read<Dictionary<NotificationFilter, Button>>(window, "filterButtons")[filter].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        private static NotificationFeed Feed(IslandWindow window) { return Read<NotificationFeed>(window, "notificationFeed"); }
        private static ListBox List(IslandWindow window) { return Read<ListBox>(window, "notificationList"); }
        private static Canvas Overlay(IslandWindow window) { return Read<Canvas>(window, "notificationCardOverlay"); }
        private static bool CardActive(IslandWindow window) { return Read<bool>(window, "notificationCardTransitionActive"); }
        private static bool CounterActive(IslandWindow window) { return Read<bool>(window, "notificationCounterTransitionActive"); }
        private static IDictionary CounterTargets(IslandWindow window) { return Read<IDictionary>(window, "notificationCounterTargets"); }
        private static ScrollViewer Viewer(IslandWindow window) { return Descendants<ScrollViewer>(List(window)).First(); }
        private static int Realized(ListBox list) { return Enumerable.Range(0, list.Items.Count).Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) != null); }
        private static IEnumerable<Border> VisibleCards(IslandWindow window) { var list = List(window); var viewport = new Rect(new Point(), list.RenderSize); return list.Items.Cast<Border>().Where(card => list.ItemContainerGenerator.ContainerFromItem(card) != null && !Rect.Intersect(Bounds(card, list), viewport).IsEmpty); }
        private static IEnumerable<FrameworkElement> Chrome(IslandWindow window) { return ((Panel)Read<Button>(window, "pinButton").Parent).Children.OfType<Button>().Cast<FrameworkElement>().Concat(new[] { Read<Border>(window, "expandedTabs") }).Concat(Read<Dictionary<NotificationFilter, Button>>(window, "filterButtons").Values.Cast<FrameworkElement>()).Concat(new[] { Read<StackPanel>(window, "notificationPage").Children.OfType<FrameworkElement>().Last() }); }
        private static void VerifyCounts(IslandWindow window, int visible, int unread) { Check(Feed(window).UnreadCount == unread, "canonical unread count incorrect"); Check(Read<TextBlock>(window, "notificationCount").Text == UiLocalization.Format("最近通知  ·  {0} 条  ·  岛内未读 {1}", visible, unread), "count caption is stale"); Check(Object.Equals(Read<Button>(window, "notificationsTab").Content, unread > 0 ? UiLocalization.Format("消息  {0}", unread) : UiLocalization.Localize("消息")), "message tab count is stale"); }
        private static void VerifyProjection(IslandWindow window, string context) { var expected = NotificationPresentation.Project(Feed(window).GetItems(Read<NotificationFilter>(window, "notificationFilter")), Read<NotificationPreferences>(window, "notificationPreferences")); var list = List(window); Check(list.Items.Count == Math.Max(1, expected.Count), context + ": projection count is stale"); Check(list.Items.Cast<Border>().Select(card => Convert.ToString(card.Tag)).Distinct().Count() == list.Items.Count, context + ": duplicate logical card identity"); foreach (var message in expected) Check(list.Items.Cast<Border>().Any(card => CardText(card).Contains(message.Title)), context + ": projected notification missing"); }
        private static void CheckRest(IslandWindow window, string context) { Check(!CardActive(window) && !CounterActive(window) && Overlay(window).Children.Count == 0, context + ": animation retains resources at rest"); foreach (UIElement card in List(window).Items) { Check(Math.Abs(card.Opacity - 1) < .001, context + ": card remains faded"); Check(Math.Abs(card.RenderTransform.Value.OffsetX) < .01 && Math.Abs(card.RenderTransform.Value.OffsetY) < .01, context + ": card retains a translation"); } }
        private static void Wait(IslandWindow window) { var watch = Stopwatch.StartNew(); do { Pump(15); StopTimers(window); window.UpdateLayout(); } while ((CardActive(window) || CounterActive(window) || Read<bool>(window, "notificationFilterTransitionActive") || Read<bool>(window, "pageTransitionActive")) && watch.ElapsedMilliseconds < 1800); Check(!CardActive(window) && !CounterActive(window) && !Read<bool>(window, "notificationFilterTransitionActive") && !Read<bool>(window, "pageTransitionActive"), "notification motions exceed bounded completion wait"); window.UpdateLayout(); }
        private static string CardTitle(Border card) { return LogicalText(card).FirstOrDefault(text => text.StartsWith("CARD_FIXTURE_", StringComparison.Ordinal) && !text.StartsWith("CARD_FIXTURE_BODY_", StringComparison.Ordinal)) ?? ""; }
        private static string CardText(Border card) { return String.Join("|", LogicalText(card)); }
        private static string DescribeCard(IslandWindow window, Border card)
        {
            DependencyObject visual = VisualTreeHelper.GetParent(card); var presenter = visual as ContentPresenter;
            return "logical=" + (card.Parent == null ? "null" : card.Parent.GetType().Name) + "; visual=" + (visual == null ? "null" : visual.GetType().Name) +
                "; generated=" + (List(window).ItemContainerGenerator.ContainerFromItem(card) == null ? "null" : List(window).ItemContainerGenerator.ContainerFromItem(card).GetType().Name) +
                "; presenterContentOwn=" + (presenter != null && Object.ReferenceEquals(presenter.Content, card)) + "; inItems=" + List(window).Items.Contains(card) +
                "; loaded=" + card.IsLoaded + "; visible=" + card.IsVisible + "; overlay=" + Overlay(window).Children.Count + "; alpha=" + card.Opacity;
        }
        private static string DescribeScroll(IslandWindow window, Border anchor)
        {
            var list = List(window); var scroll = Viewer(window);
            return "offset=" + scroll.VerticalOffset + "; max=" + scroll.ScrollableHeight + "; extent=" + scroll.ExtentHeight + "; viewport=" + scroll.ViewportHeight +
                "; anchorIndex=" + list.Items.IndexOf(anchor) + "; anchorY=" + Bounds(anchor, window).Top + "; cardHeight=" + anchor.ActualHeight + "; realized=" + Realized(list);
        }
        private static IEnumerable<string> LogicalText(DependencyObject root) { var label = root as TextBlock; if (label != null) { yield return label.Text; yield break; } foreach (object value in LogicalTreeHelper.GetChildren(root)) { var dependency = value as DependencyObject; if (dependency != null) foreach (string text in LogicalText(dependency)) yield return text; } }
        private static void CheckCaptions(DependencyObject root, string context) { foreach (var text in Descendants<TextBlock>(root).Where(EffectivelyVisible).Where(item => item.Text.Length > 0 && item.TextTrimming == TextTrimming.None)) Check(text.ActualHeight + 1.1 >= text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom, context + ": cropped caption " + text.Text); }
        private static bool EffectivelyVisible(FrameworkElement element) { for (DependencyObject current = element; current != null; current = VisualTreeHelper.GetParent(current)) { var ui = current as UIElement; if (ui != null && ui.Visibility != Visibility.Visible) return false; } return true; }
        private static Action TranslateDetached(DependencyObject target, DependencyProperty property) { var descriptor = DependencyPropertyDescriptor.FromProperty(property, target.GetType()); bool updating = false; EventHandler handler = (sender, args) => { if (updating) return; string raw = target.GetValue(property) as string, value = UiLocalization.Localize(raw); if (Object.Equals(raw, value)) return; try { updating = true; target.SetCurrentValue(property, value); } finally { updating = false; } }; descriptor.AddValueChanged(target, handler); handler(null, EventArgs.Empty); return () => descriptor.RemoveValueChanged(target, handler); }
        private static ContentControl Detached(Window window) { var content = (UIElement)window.Content; window.Content = null; var root = new ContentControl { Content = new Border { Background = window.Background, Child = content }, Resources = window.Resources, FontFamily = window.FontFamily, FontSize = window.FontSize, FontStyle = window.FontStyle, FontWeight = window.FontWeight, FontStretch = window.FontStretch, Foreground = window.Foreground, Background = window.Background, UseLayoutRounding = window.UseLayoutRounding, SnapsToDevicePixels = window.SnapsToDevicePixels, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch }; TextOptions.SetTextFormattingMode(root, TextOptions.GetTextFormattingMode(window)); TextOptions.SetTextRenderingMode(root, TextOptions.GetTextRenderingMode(window)); TextOptions.SetTextHintingMode(root, TextOptions.GetTextHintingMode(window)); return root; }
        private static void Layout(FrameworkElement root, double width, double height) { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
        private static void Settle(IslandWindow window) { StopTimers(window); window.UpdateLayout(); Pump(1); StopTimers(window); window.UpdateLayout(); }
        private static void StopTimers(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); var focus = Read<FocusPanel>(window, "focusPanel"); if (focus != null) focus.Stop(); }
        private static Rect NativeBounds(Window window) { NativeRect rect; Check(GetWindowRect(new WindowInteropHelper(window).Handle, out rect), "own HWND bounds unavailable"); return new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top); }
        private static Rect ScreenBounds(FrameworkElement element) { return new Rect(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight))); }
        private static Rect Bounds(FrameworkElement element, Visual root) { return element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)); }
        private static void Same(Rect value, Rect expected, double tolerance, string context) { Check(Math.Abs(value.Left - expected.Left) <= tolerance && Math.Abs(value.Top - expected.Top) <= tolerance && Math.Abs(value.Width - expected.Width) <= tolerance && Math.Abs(value.Height - expected.Height) <= tolerance, context + "; actual=" + value + "; expected=" + expected); }
        private static void SameVertical(double value, double expected, double tolerance, string context) { Check(Math.Abs(value - expected) <= tolerance, context + "; actual=" + value + "; expected=" + expected); }
        private static void Monotonic(IList<double> values, bool increasing, double tolerance, string context) { for (int index = 1; index < values.Count; index++) Check(increasing ? values[index] + tolerance >= values[index - 1] : values[index] <= values[index - 1] + tolerance, context + ": motion reverses/flashes " + values[index - 1] + "→" + values[index]); }
        private static T Read<T>(object value, string field) { return (T)value.GetType().GetField(field, Private).GetValue(value); }
        private static void Set(object value, string field, object data) { value.GetType().GetField(field, Private).SetValue(value, data); }
        private static object Call(object value, string method, params object[] args) { try { return value.GetType().GetMethod(method, Private).Invoke(value, args); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) { var child = VisualTreeHelper.GetChild(root, index); var item = child as T; if (item != null) yield return item; foreach (var descendant in Descendants<T>(child)) yield return descendant; } }
        private static void Pump(int milliseconds) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
        private static void Check(bool value, string context) { if (!value) throw new Exception("Notification card motion: " + context); }
    }
}
