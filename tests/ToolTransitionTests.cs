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
    // Production tool panels in an isolated demo HWND. Inputs are local routed
    // actions only; no desktop capture, clipboard access or player commands.
    internal static class ToolTransitionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
        private static int transitions, frames, geometryMessages;
        private static readonly List<double> Handlers = new List<double>();

        internal static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string directory = Path.Combine(Path.GetTempPath(), "CodexIsland.ToolTransitions." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null; transitions = frames = geometryMessages = 0; Handlers.Clear();
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", directory);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false,
                    HoverExpandEnabled = false, AutoMusic = false, OnlineLyricsEnabled = false, ExpandedWidth = 440, ExpandedHeight = 600 });
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false };
                window.Loaded += (s, e) => StopTimers(window); window.Show(); Settle(window);
                Call(window, "SelectToolsPage"); window.SetExpanded(true, false); Settle(window);
                var panels = Panels(window);
                string[] expectedKeys = { "media", "focus", "notes", "reminders", "time", "launch", "files", "hash", "clipboard", "text", "calculator", "converter", "random", "colors", "system", "ports", "screenpin", "image", "diff", "watch", "awake", "qr", "scenes", "clipboardhistory", "logwatch", "desktop", "profiles", "diagnostics", "ocr", "templates", "ready", "backup", "autorules", "widgets", "audio-mixer", "lan-transfer", "batch", "activities" };
                Check(panels.Count == 38 && panels.Keys.OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(expectedKeys.OrderBy(key => key, StringComparer.Ordinal)) && ToolCatalog.All.Select(item => item.Key).SequenceEqual(expectedKeys), "fixture retains all thirty-eight specific production tools and the complete stable catalog");
                foreach (string key in panels.Keys.ToArray())
                {
                    Select(window, key == "focus" ? "time" : "focus"); WaitTool(window);
                    Observe(window, key, "catalog/" + key);
                }
                Matrix(window);
                RapidAndSame(window);
                BackgroundAndScroll(window);
                Routes(window);
                ParentAndMode(window);
                HiddenAndStoredKey(window);
                LongInput(window);
                Interruptions(window);
                Prepare(window); window.Close(); Clean(window, "closing"); Pump(220); Clean(window, "closed completion");
                double mean = Handlers.Count == 0 ? 0 : Handlers.Average(), maximum = Handlers.Count == 0 ? 0 : Handlers.Max();
                Console.WriteLine("TIMING: tool switch handler mean/max " + mean.ToString("0.0") + "/" + maximum.ToString("0.0") + " ms; native move/size messages during measured fades=" + geometryMessages + ".");
                Console.WriteLine("PASS: " + transitions + " real tool fades and " + frames + " rendering frames; all 38 tools, four edges, default/custom odd-size windows, Chinese/English and light/dark; 80 rapid requests, latest visible panel, stable HWND/header/picker, preserved data/scroll/refresh clocks, parent/mode coordination and interruption cleanup. No global input or desktop capture.");
            }
            finally
            {
                if (window != null && !Read<bool>(window, "shuttingDown")) { StopTimers(window); window.Close(); }
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static void Observe(IslandWindow window, string key, string context)
        {
            var panels = Panels(window); var target = panels[key]; Rect expected = NativeBounds(window);
            var values = new List<double>(); int moves = 0;
            var source = (HwndSource)PresentationSource.FromVisual(window);
            HwndSourceHook hook = (IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled) => {
                if (message == 3 || message == 5) { moves++; Check(NativeBounds(window) == expected, context + ": native geometry message moved/resized HWND"); }
                return IntPtr.Zero;
            };
            EventHandler onFrame = (s, e) => {
                values.Add(target.Opacity); frames++;
                Check(NativeBounds(window) == expected, context + ": native window jumps during fade");
                FixedChrome(window, context);
                Check(Read<Image>(window, "outgoingImage").Source == null, context + ": tool transition retains old bitmap pixels");
            };
            source.AddHook(hook); CompositionTarget.Rendering += onFrame;
            double first;
            try
            {
                var watch = Stopwatch.StartNew(); Select(window, key); Handlers.Add(watch.Elapsed.TotalMilliseconds);
                first = target.Opacity;
                Check(target.IsVisible && target.IsEnabled, context + ": selected panel unavailable at initial frame");
                if (SystemParameters.ClientAreaAnimation) Check(first >= .349 && first < 1, context + ": panel starts blank or lacks fade");
                window.UpdateLayout(); StopTimers(window); WaitTool(window);
            }
            finally { CompositionTarget.Rendering -= onFrame; source.RemoveHook(hook); }
            geometryMessages += moves;
            Check(Read<string>(window, "selectedTool") == key && target.IsVisible, context + ": final target lost");
            Check(panels.Count(pair => pair.Value.IsVisible) == 1, context + ": multiple tools visible");
            if (SystemParameters.ClientAreaAnimation) Check(values.Any(v => v > first + .01 && v < .99), context + ": no actual intermediate rendered frame");
            for (int i = 1; i < values.Count; i++) Check(values[i] + .001 >= values[i - 1], context + ": opacity reverses or flashes");
            Clean(window, context + "/completed"); transitions++;
        }

        private static void Matrix(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings");
            foreach (string side in new[] { "left", "right", "top", "bottom" })
            foreach (bool custom in new[] { false, true })
            foreach (string language in new[] { "zh", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                window.SetExpanded(false, false); settings.Language = language; settings.Theme = theme; Call(window, "ApplyAppearance");
                Call(window, "ChangeSide", side); settings.CustomExpandedSize = custom; settings.ExpandedWidth = custom ? 391 : 440; settings.ExpandedHeight = custom ? 617 : 600;
                Call(window, "SelectToolsPage"); Select(window, "focus"); window.SetExpanded(true, false); Settle(window);
                Observe(window, "text", side + "/" + custom + "/" + language + "/" + theme);
                Rect bounds = NativeBounds(window); Call(window, "RequestExpanded", true); Pump(20); StopTimers(window);
                Check(NativeBounds(window) == bounds, "stable expanded request after odd-DPI tool route changes native bounds");
            }
        }

        private static void RapidAndSame(IslandWindow window)
        {
            Select(window, "focus"); WaitTool(window); Select(window, "text"); Pump(30); StopTimers(window);
            if (SystemParameters.ClientAreaAnimation)
            {
                Check(Active(window), "same-tool fixture has no active fade");
                object clock = Read<object>(window, "toolTransitionWatch"); int version = Read<int>(window, "toolTransitionVersion"); double opacity = Panels(window)["text"].Opacity;
                Select(window, "text");
                Check(ReferenceEquals(clock, Read<object>(window, "toolTransitionWatch")) && version == Read<int>(window, "toolTransitionVersion") && Math.Abs(Panels(window)["text"].Opacity - opacity) < .001, "same-tool click restarts/brightens fade");
            }
            Rect expected = NativeBounds(window); string[] keys = Panels(window).Keys.ToArray();
            for (int request = 0; request < 80; request++)
            {
                string key = keys[(request * 7) % keys.Length];
                if (request % 4 == 0) Read<Dictionary<string, Button>>(window, "toolButtons")[key].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); else Select(window, key);
                Pump(3); StopTimers(window);
                Check(NativeBounds(window) == expected && Panels(window)[key].IsVisible, "rapid request changes bounds or loses current target");
                Check(Panels(window).Count(pair => pair.Value.IsVisible) == 1, "rapid request exposes previous tool");
                foreach (var pair in Panels(window).Where(pair => pair.Key != key)) Check(Math.Abs(pair.Value.Opacity - 1) < .001, "canceled panel keeps dim opacity");
            }
            Select(window, "calculator"); WaitTool(window); Pump(230); Clean(window, "rapid final");
            Check(Panels(window)["calculator"].IsVisible && Read<string>(window, "selectedTool") == "calculator", "old completion restores an obsolete tool");
        }

        private static void BackgroundAndScroll(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings"); settings.CustomExpandedSize = true; settings.ExpandedHeight = 360; window.SetExpanded(true, false); Settle(window);
            Select(window, "focus"); WaitTool(window); Select(window, "text"); window.UpdateLayout(); Pump(25); StopTimers(window);
            var scroll = Read<ScrollViewer>(window, "detailsScroll"); scroll.ScrollToVerticalOffset(Math.Min(40, scroll.ScrollableHeight)); window.UpdateLayout();
            double offset = scroll.VerticalOffset; Check(offset > 0, "scroll-preservation fixture has no scroll extent");
            object clock = Read<object>(window, "toolTransitionWatch"); int version = Read<int>(window, "toolTransitionVersion"); Rect bounds = NativeBounds(window);
            Select(window, "text"); Call(window, "ConfigureToolkit"); Call(window, "RebuildBuckets"); Call(window, "UpdateNotificationListCore", false);
            if (SystemParameters.ClientAreaAnimation) Check(Active(window) && ReferenceEquals(clock, Read<object>(window, "toolTransitionWatch")) && version == Read<int>(window, "toolTransitionVersion"), "same selection or background refresh replaces tool clock");
            window.UpdateLayout(); Check(Math.Abs(offset - scroll.VerticalOffset) < 1.1 && NativeBounds(window) == bounds, "background refresh resets scroll or geometry"); WaitTool(window);
        }

        private static void Routes(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings"); settings.FavoriteTools = "text,calculator"; Call(window, "RebuildFavorites");
            Select(window, "focus"); WaitTool(window);
            Read<WrapPanel>(window, "favoriteTools").Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Read<string>(window, "selectedTool") == "text", "favorite route does not select its tool");
            Rect bounds = NativeBounds(window); Call(window, "RequestExpanded", true);
            if (SystemParameters.ClientAreaAnimation) Check(Active(window), "same-expanded reveal cancels favorite tool fade");
            Check(NativeBounds(window) == bounds, "favorite plus reveal changes native bounds"); WaitTool(window);
            Read<TextBox>(window, "toolSearch").Text = "calculator"; Call(window, "ExecuteToolSearch");
            Check(Read<string>(window, "selectedTool") == "calculator", "search route loses latest tool"); WaitTool(window); Read<TextBox>(window, "toolSearch").Clear();
        }

        private static void ParentAndMode(IslandWindow window)
        {
            Call(window, "SelectPage", true); WaitPage(window); Call(window, "SelectToolsPage"); Pump(30); StopTimers(window);
            object pageClock = Read<object>(window, "pageTransitionWatch"); int pageVersion = Read<int>(window, "pageTransitionVersion"); double parent = Read<StackPanel>(window, "toolsPage").Opacity;
            string key = Read<string>(window, "selectedTool") == "text" ? "calculator" : "text"; Select(window, key);
            Check(!Active(window) && Panels(window)[key].Opacity == 1, "tool fade multiplies parent page opacity");
            if (SystemParameters.ClientAreaAnimation) Check(ReferenceEquals(pageClock, Read<object>(window, "pageTransitionWatch")) && pageVersion == Read<int>(window, "pageTransitionVersion") && Math.Abs(parent - Read<StackPanel>(window, "toolsPage").Opacity) < .001, "tool selection resets parent fade");
            WaitPage(window);
            window.SetExpanded(false, false); Select(window, "focus"); window.SetExpanded(true, true); Pump(40); StopTimers(window);
            object modeClock = Read<object>(window, "transitionWatch"); int modeVersion = Read<int>(window, "transitionVersion"); Rect visual = window.CurrentVisualBounds();
            Select(window, "text"); Call(window, "RequestExpanded", true);
            Check(!Active(window) && Panels(window)["text"].Opacity == 1, "tool fade competes with expansion");
            if (SystemParameters.ClientAreaAnimation) Check(ReferenceEquals(modeClock, Read<object>(window, "transitionWatch")) && modeVersion == Read<int>(window, "transitionVersion") && window.CurrentVisualBounds() == visual, "tool route restarts expansion clock or jumps visual geometry");
            Pump(300); StopTimers(window); window.SetExpanded(true, false); Settle(window);
        }

        private static void HiddenAndStoredKey(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings"); Select(window, "focus"); WaitTool(window); Select(window, "text");
            Write(window, "selectedTool", "calculator"); Call(window, "ConfigureToolkit");
            Check(Panels(window)["calculator"].IsVisible && !Panels(window)["text"].IsVisible && Panels(window)["text"].Opacity == 1, "preselected stored key fails to replace old visible panel"); WaitTool(window);
            Select(window, "text"); settings.HiddenTools = "text"; Call(window, "ConfigureToolkit");
            Check(!Panels(window)["text"].IsVisible && Panels(window)["text"].Opacity == 1, "hidden active tool retains dim/visible state"); WaitTool(window);
            Select(window, "text"); Clean(window, "hidden target"); Check(!Panels(window)["text"].IsVisible, "hidden target becomes visible");
            Select(window, "unknown-transition-fixture"); Clean(window, "unknown target");
            settings.HiddenTools = ""; Call(window, "ConfigureToolkit"); Select(window, "focus"); WaitTool(window);
        }

        private static void LongInput(IslandWindow window)
        {
            var panel = (TextToolsPanel)Panels(window)["text"]; var input = Read<TextBox>(panel, "input"); string before = input.Text;
            try
            {
                string original = String.Join("\n", Enumerable.Range(0, 400).Select(index => index + ": 保存设置 / Original text 😀 " + new string('W', 60)));
                input.Text = original; input.Select(17, 35); Select(window, "focus"); WaitTool(window); Observe(window, "text", "long-original-text");
                Select(window, "calculator"); WaitTool(window); Select(window, "text"); WaitTool(window);
                Check(input.Text == original && input.SelectionStart == 17 && input.SelectionLength == 35, "tool switching rebuilds or translates original editor data/selection");
            }
            finally { input.Text = before; }
        }

        private static void Interruptions(IslandWindow window)
        {
            Prepare(window); Call(window, "SelectPage", true); Clean(window, "leaving tools page"); WaitPage(window);
            Prepare(window); window.SetExpanded(false, true); Clean(window, "collapse"); Pump(260); StopTimers(window);
            Prepare(window); window.Hide(); Clean(window, "hide"); window.Show(); Settle(window);
            Prepare(window); Call(window, "ApplyAppearance"); Clean(window, "appearance");
            Prepare(window); Call(window, "ConfigureNotifications"); Clean(window, "privacy/notification reconfiguration");
            Prepare(window); Call(window, "DiscardTransitionSnapshot"); Clean(window, "message source/history invalidation");
            Prepare(window); Call(window, "FreezeSizeAnimation"); Clean(window, "resize preparation");
            Prepare(window); Call(window, "BeginToolTransition", Panels(window)["text"], false); Clean(window, "reduced-motion branch");
            Prepare(window); Call(window, "ShowPeek", new IslandNotification { Id = "tool-transition-peek", AppId = "fixture.tool.transition", AppName = "Fixture", Title = "Preview", Body = "Synthetic", IsDemo = true }); Clean(window, "peek");
            window.SetExpanded(false, false); Select(window, "calculator"); Clean(window, "compact selection");
            Prepare(window);
            var title = Read<Grid>(window, "expandedHeader").Children.OfType<StackPanel>().First();
            title.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = title });
            Clean(window, "drag preparation"); var shell = Read<Border>(window, "shell");
            if (Read<bool>(window, "dragging")) Call(window, "FinishDrag", shell, false); if (ReferenceEquals(Mouse.Captured, shell)) shell.ReleaseMouseCapture();
            Pump(220); Clean(window, "old callbacks after interruption");
        }

        private static void Prepare(IslandWindow window) { window.Show(); Call(window, "SelectToolsPage"); window.SetExpanded(true, false); Select(window, "focus"); WaitTool(window); Select(window, "text"); }
        private static void FixedChrome(IslandWindow window, string context)
        {
            foreach (string field in new[] { "shell", "detailsScroll", "toolsPage", "toolPicker", "favoriteTools", "notificationsTab", "statusTab", "toolsTab", "pinButton" })
                Check(Math.Abs(Read<UIElement>(window, field).Opacity - 1) < .001, context + ": fixed chrome or parent faded: " + field);
        }
        private static Dictionary<string, UIElement> Panels(IslandWindow window) { return Read<Dictionary<string, UIElement>>(window, "toolPanels"); }
        private static void Select(IslandWindow window, string key) { Call(window, "SelectTool", key); }
        private static bool Active(IslandWindow window) { return Read<bool>(window, "toolTransitionActive"); }
        private static void Clean(IslandWindow window, string context)
        {
            Check(!Active(window) && Read<object>(window, "toolTransitionWatch") == null && Read<object>(window, "toolTransitionPanel") == null, context + ": tool clock/target retained");
            foreach (var panel in Panels(window).Values) Check(Math.Abs(panel.Opacity - 1) < .001, context + ": old tool remains dimmed");
        }
        private static void WaitTool(IslandWindow window) { var elapsed = Stopwatch.StartNew(); do { Pump(20); StopTimers(window); } while (Active(window) && elapsed.ElapsedMilliseconds < 1500); window.UpdateLayout(); Check(!Active(window), "tool fade exceeded bounded wait"); }
        private static void WaitPage(IslandWindow window) { var elapsed = Stopwatch.StartNew(); do { Pump(20); StopTimers(window); } while (Read<bool>(window, "pageTransitionActive") && elapsed.ElapsedMilliseconds < 1500); window.UpdateLayout(); Check(!Read<bool>(window, "pageTransitionActive"), "parent fade exceeded bounded wait"); }
        private static void Settle(IslandWindow window) { StopTimers(window); window.UpdateLayout(); Pump(20); StopTimers(window); window.UpdateLayout(); }
        private static void StopTimers(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); var focus = Read<FocusPanel>(window, "focusPanel"); if (focus != null) focus.Stop(); }
        private static Rect NativeBounds(Window window) { NativeRect bounds; Check(GetWindowRect(new WindowInteropHelper(window).Handle, out bounds), "own HWND bounds unavailable"); return new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top); }
        private static T Read<T>(object target, string field) { return (T)target.GetType().GetField(field, Private).GetValue(target); }
        private static void Write(object target, string field, object value) { target.GetType().GetField(field, Private).SetValue(target, value); }
        private static object Call(object target, string method, params object[] values) { try { return target.GetType().GetMethod(method, Private).Invoke(target, values); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static void Pump(int milliseconds) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Tool transition: " + message); }
    }
}
