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
    // Uses isolated settings and this process's own demo HWND/input tree only.
    // It never moves the real pointer or sends input to another application.
    internal static class HoverPreferencesTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly MethodInfo ChangeMouseOver = typeof(MouseDevice).GetMethod("ChangeMouseOver", Private, null, new[] { typeof(IInputElement), typeof(int) }, null);
        [DllImport("user32.dll")] private static extern IntPtr GetCapture();
        [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        internal static void Run()
        {
            string previousData = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string previousLanguage = UiLocalization.Language, previousTheme = UiTheme.Name;
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.HoverPreferences." + Guid.NewGuid().ToString("N"));
            IInputElement prior = Mouse.DirectlyOver;
            IslandWindow window = null;
            try
            {
                Check(ChangeMouseOver != null, "own-process mouse-over fixture is available");
                Directory.CreateDirectory(folder);
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                SettingsPersistence(folder);
                new SettingsStore().Save(new Settings {
                    AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false,
                    AutoMusic = false, AutoSnapToEdge = false, CompactSlot1 = "focus", CompactSlot2 = "clock",
                    HoverExpandEnabled = true, HoverDelayMilliseconds = 1100
                });
                window = new IslandWindow(true) { ShowActivated = false };
                window.Loaded += (s, e) => StopTimers(window);
                window.Show(); Pump(1); window.UpdateLayout(); StopTimers(window);
                RuntimeCustomDelay(window);
                DisabledHoverGuards(window);
                DisabledManualGestures(window);
                SettingsDialog(window);
                Console.WriteLine("PASS: hover preference defaults/migration, strict input and clamped persistence; real 1100 ms dwell; disabling cancels pending/stale hover and collapse reentry; click/focus/drag remain available; settings validation, cancel, English/light appearance and round trips.");
            }
            finally
            {
                Over(null);
                if (window != null)
                {
                    var captured = Mouse.Captured as DependencyObject;
                    if (captured != null && Window.GetWindow(captured) == window) Mouse.Capture(null);
                    StopTimers(window); window.Close();
                }
                Over(prior);
                AppAppearance.Configure(previousLanguage, previousTheme);
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previousData);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        private static void SettingsPersistence(string folder)
        {
            var defaults = new Settings();
            Check(defaults.HoverExpandEnabled && defaults.HoverDelayMilliseconds == 650, "new installs retain the existing enabled 650 ms behavior");
            string path = Path.Combine(folder, "hover-settings.json");
            var store = new SettingsStore(path);
            File.WriteAllText(path, "{\"AutoCollapse\":false,\"CompactSlot1\":\"focus\"}");
            var old = store.Load();
            Check(old.HoverExpandEnabled && old.HoverDelayMilliseconds == 650 && !old.AutoCollapse && old.CompactSlot1 == "focus", "old settings acquire defaults without losing established choices");
            store.Save(new Settings { HoverExpandEnabled = false, HoverDelayMilliseconds = 1375, FocusMinutes = 37, AutoMusic = false });
            var loaded = store.Load();
            Check(!loaded.HoverExpandEnabled && loaded.HoverDelayMilliseconds == 1375 && loaded.FocusMinutes == 37 && !loaded.AutoMusic, "disabled state and custom delay round-trip with unrelated preferences");
            foreach (int input in new[] { -10, 0, 99, 100, 650, 5000, 5001, Int32.MaxValue })
            {
                int expected = Math.Max(100, Math.Min(5000, input));
                File.WriteAllText(path, "{\"HoverDelayMilliseconds\":" + input + "}");
                Check(store.Load().HoverDelayMilliseconds == expected, "loading clamps an integer delay " + input);
                store.Save(new Settings { HoverDelayMilliseconds = input });
                Check(store.Load().HoverDelayMilliseconds == expected, "saving clamps an integer delay " + input);
            }
            foreach (string invalid in new[] { "null", "\"1300\"", "true", "1200.5", "{}", "[]" })
            {
                File.WriteAllText(path, "{\"HoverExpandEnabled\":\"false\",\"HoverDelayMilliseconds\":" + invalid + "}");
                loaded = store.Load();
                Check(loaded.HoverExpandEnabled && loaded.HoverDelayMilliseconds == 650, "wrong JSON types fall back safely: " + invalid);
            }
        }

        private static void RuntimeCustomDelay(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings");
            var timer = Read<DispatcherTimer>(window, "hoverTimer");
            Check(settings.HoverDelayMilliseconds == 1100 && timer.Interval.TotalMilliseconds == 1100, "startup applies the persisted custom delay to the live timer");
            ResetCompact(window);
            Enter(window);
            Check(timer.IsEnabled, "enabled blank-shell hover arms its real timer");
            Pump(700);
            Compact(window, "custom 1100 ms delay remains closed past the former 650 ms default");
            WaitFor(() => Read<bool>(window, "expanded"), 1000, "sustained custom hover eventually expands");
            Pump(IslandWindow.TransitionMilliseconds + 70); StopTimers(window);

            ResetCompact(window); Enter(window);
            settings.HoverDelayMilliseconds = 1600;
            Call(window, "ConfigureToolkit");
            Check(!timer.IsEnabled && timer.Interval.TotalMilliseconds == 1600, "applying a changed delay cancels the prearmed old deadline");
            Over(null); StopTimers(window);
            settings.HoverDelayMilliseconds = -1; Call(window, "ConfigureHoverExpansion");
            Check(timer.Interval.TotalMilliseconds == 100, "live configuration protects against a below-range in-memory delay");
            settings.HoverDelayMilliseconds = 9000; Call(window, "ConfigureHoverExpansion");
            Check(timer.Interval.TotalMilliseconds == 5000, "live configuration protects against an above-range in-memory delay");
            settings.HoverDelayMilliseconds = 1100; Call(window, "ConfigureHoverExpansion");
        }

        private static void DisabledHoverGuards(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings");
            ResetCompact(window); Enter(window);
            var timer = Read<DispatcherTimer>(window, "hoverTimer");
            Check(timer.IsEnabled, "a genuine hover is pending before disable");
            settings.HoverExpandEnabled = false; Call(window, "ConfigureToolkit");
            Check(!timer.IsEnabled, "disabling through live settings stops the pending hover timer");
            Call(window, "OnHoverElapsed"); Compact(window, "stale elapsed callback after disable");
            Call(window, "OnIslandMouseEnter"); Call(window, "RecheckCompactActionHover");
            Check(!timer.IsEnabled, "disabled enter and component recheck cannot rearm hover");
            Pump(1200); Compact(window, "disabled sustained hover beyond its prior deadline");

            Over(null); StopTimers(window); window.SetExpanded(true, false); window.UpdateLayout();
            window.SetExpanded(false, true);
            if (SystemParameters.ClientAreaAnimation)
                Check(Read<bool>(window, "visualTransitionActive"), "fixture enters an actual automatic-collapse style transition");
            Over(Read<Border>(window, "shell")); Call(window, "OnIslandMouseEnter"); Call(window, "RecheckCompactActionHover"); Call(window, "OnHoverElapsed");
            Check(!Read<bool>(window, "expanded") && !timer.IsEnabled, "disabled reentry cannot reverse an in-progress collapse");
            Pump(IslandWindow.TransitionMilliseconds + 80); StopTimers(window);
            Compact(window, "disabled collapse reentry settles compact");
        }

        private static void DisabledManualGestures(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings");
            Check(!settings.HoverExpandEnabled, "manual fixture keeps hover disabled");
            ResetCompact(window); InitializeMouseSource(window); Pump(60); StopTimers(window);
            var shell = Read<Border>(window, "shell");
            MouseEventHandler ignorePhysicalButton = (s, e) => e.Handled = true;
            shell.PreviewMouseMove += ignorePhysicalButton;
            try
            {
                Press(window, shell); Release(window, shell);
                Check(Read<bool>(window, "expanded"), "blank-shell click still expands with hover disabled");
                ResetCompact(window);
                var focus = Read<FocusPanel>(window, "focusPanel");
                Press(window, Read<Grid>(window, "compactSlot1")); Release(window, shell);
                Check(focus.IsRunning && !Read<bool>(window, "expanded"), "focus widget still starts directly without opening the disabled-hover island");
                focus.ToggleQuickFocus();
                Point origin = Press(window, shell);
                Vector delta = PresentationSource.FromVisual(window).CompositionTarget.TransformToDevice.Transform(new Vector(40, 35));
                Check((bool)Call(window, "MoveDrag", origin + delta), "disabled-hover compact island still accepts a real drag threshold");
                Release(window, shell); StopTimers(window);
                Compact(window, "disabled-hover drag release");
            }
            finally { shell.PreviewMouseMove -= ignorePhysicalButton; }
        }

        private static void SettingsDialog(IslandWindow owner)
        {
            Over(null); StopTimers(owner);
            AppAppearance.Configure("en", "light");
            var source = new Settings { HoverExpandEnabled = false, HoverDelayMilliseconds = 1375, Language = "en", Theme = "light", AutoMusic = false, FocusMinutes = 37 };
            var edited = SaveDialog(owner, source, dialog => {
                var check = Read<CheckBox>(dialog, "hoverExpand"); var input = Read<TextBox>(dialog, "hoverDelay");
                Check(check.IsChecked == false && input.Text == "1375" && !input.IsEnabled, "settings load a disabled hover option and retain its delay");
                Check((string)check.Content == UiLocalization.Localize("悬停时自动展开") && (string)check.Content != "悬停时自动展开", "new hover option participates in English localization");
                Check(UiTheme.Name == "light" && input.Background != null, "hover controls participate in the existing light appearance");
                check.IsChecked = true;
                Check(input.IsEnabled && input.Text == "1375", "re-enabling restores the previous editable delay");
                foreach (string invalid in new[] { "", "0", "99", "5001", "1000.5", "abc", "99999999" })
                {
                    input.Text = invalid; Call(dialog, "Save");
                    Check(dialog.IsVisible && dialog.Result == null && Read<TextBlock>(dialog, "hoverValidation").Visibility == Visibility.Visible,
                        "enabled invalid delay stays in the dialog with inline feedback: " + invalid);
                }
                input.Text = "2100";
                Check(Read<TextBlock>(dialog, "hoverValidation").Visibility == Visibility.Collapsed, "editing clears the prior inline validation");
            });
            Check(edited.HoverExpandEnabled && edited.HoverDelayMilliseconds == 2100 && edited.FocusMinutes == 37 && !edited.AutoMusic,
                "settings Save commits enabled custom dwell without changing unrelated features");
            var preserved = SaveDialog(owner, edited, dialog => {
                Read<TextBox>(dialog, "hoverDelay").Text = "invalid";
                Read<CheckBox>(dialog, "hoverExpand").IsChecked = false;
                Check(!Read<TextBox>(dialog, "hoverDelay").IsEnabled, "turning hover off disables the input immediately");
            });
            Check(!preserved.HoverExpandEnabled && preserved.HoverDelayMilliseconds == 2100, "disabled invalid text cannot overwrite the last valid delay");
            var retainedEdit = SaveDialog(owner, edited, dialog => {
                Read<TextBox>(dialog, "hoverDelay").Text = "3200";
                Read<CheckBox>(dialog, "hoverExpand").IsChecked = false;
            });
            Check(!retainedEdit.HoverExpandEnabled && retainedEdit.HoverDelayMilliseconds == 3200, "a valid edited delay remains available after disabling hover");
            var store = new SettingsStore(); store.Save(retainedEdit); var loaded = store.Load();
            Check(!loaded.HoverExpandEnabled && loaded.HoverDelayMilliseconds == 3200, "real UI results survive settings persistence");

            var canceled = new SettingsWindow(edited) { Owner = owner, ShowActivated = false, IsHitTestVisible = false };
            Exception failure = null;
            canceled.Loaded += (s, e) => canceled.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                try
                {
                    Read<CheckBox>(canceled, "hoverExpand").IsChecked = false; Read<TextBox>(canceled, "hoverDelay").Text = "4900";
                    Descendants(canceled).OfType<Button>().Single(button => (string)button.Content == UiLocalization.Localize("取消")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception error) { failure = error; canceled.Close(); }
            }));
            bool? result = canceled.ShowDialog();
            if (failure != null) throw failure;
            Check(result == false && canceled.Result == null && edited.HoverExpandEnabled && edited.HoverDelayMilliseconds == 2100,
                "Cancel discards edits and leaves the original settings object unchanged");
        }

        private static Settings SaveDialog(IslandWindow owner, Settings source, Action<SettingsWindow> inspect)
        {
            var dialog = new SettingsWindow(source) { Owner = owner, ShowActivated = false, IsHitTestVisible = false };
            Exception failure = null;
            dialog.Loaded += (s, e) => dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                try { inspect(dialog); Call(dialog, "Save"); }
                catch (Exception error) { failure = error; dialog.Close(); }
            }));
            bool? result = dialog.ShowDialog();
            if (failure != null) throw failure;
            Check(result == true && dialog.Result != null, "real settings Save closes its modal dialog successfully");
            return dialog.Result;
        }
        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i); yield return child;
                foreach (DependencyObject item in Descendants(child)) yield return item;
            }
        }
        private static void ResetCompact(IslandWindow window)
        {
            Over(null); StopTimers(window); Write(window, "hoverSuppressed", false); Write(window, "pinned", false);
            window.SetExpanded(false, false); window.UpdateLayout(); StopTimers(window);
        }
        private static void Enter(IslandWindow window)
        {
            Over(null); StopTimers(window); Write(window, "hoverSuppressed", false);
            Over(Read<Border>(window, "shell")); Call(window, "RecheckCompactActionHover");
            Check(window.IsMouseOver, "synthetic hover reaches only this process's own window");
        }
        private static void Over(IInputElement target) { if (ChangeMouseOver != null) ChangeMouseOver.Invoke(Mouse.PrimaryDevice, new object[] { target, Environment.TickCount }); }
        private static Point Press(IslandWindow window, UIElement target)
        {
            InitializeMouseSource(window);
            target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent, Source = target });
            Check(Read<bool>(window, "dragging") && ReferenceEquals(Mouse.Captured, Read<Border>(window, "shell")), "manual press captures the persistent own shell");
            Point origin = target.PointToScreen(new Point(Math.Max(1, target.RenderSize.Width / 2), Math.Max(1, target.RenderSize.Height / 2)));
            Write(window, "motionStart", origin); return origin;
        }
        private static void Release(IslandWindow window, UIElement target) { target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent, Source = target }); }
        private static void InitializeMouseSource(IslandWindow window)
        {
            var source = PresentationSource.FromVisual(window) as HwndSource;
            Check(source != null && GetCapture() == IntPtr.Zero && Mouse.Captured == null, "input fixture starts with an own HWND and no capture");
            SetCapture(source.Handle);
            try { SendMessage(source.Handle, 0x0200, IntPtr.Zero, new IntPtr(40 | (40 << 16))); }
            finally { if (GetCapture() == source.Handle) ReleaseCapture(); }
            Check(ReferenceEquals(Mouse.PrimaryDevice.ActiveSource, source), "native mouse source is assigned only to the own fixture HWND");
        }
        private static void StopTimers(IslandWindow window)
        {
            foreach (FieldInfo field in typeof(IslandWindow).GetFields(Private))
                if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop();
        }
        private static void Compact(IslandWindow window, string label) { Check(!Read<bool>(window, "expanded") && !Read<bool>(window, "peeking") && !Read<bool>(window, "visualTransitionActive"), label + " remains fully compact"); }
        private static void WaitFor(Func<bool> condition, int timeout, string label)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.ElapsedMilliseconds < timeout) Pump(20);
            Check(condition(), label);
        }
        private static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        private static T Read<T>(object value, string name) { return (T)value.GetType().GetField(name, Private).GetValue(value); }
        private static void Write(object value, string name, object data) { value.GetType().GetField(name, Private).SetValue(value, data); }
        private static object Call(object value, string name, params object[] args)
        {
            try { return value.GetType().GetMethod(name, Private).Invoke(value, args); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Hover preferences: " + message); }
    }
}
