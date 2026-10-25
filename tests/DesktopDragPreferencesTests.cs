using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexIsland
{
    // Only synthetic routed keys and this process's own demo window. No native
    // keyboard input, global capture, clipboard, screenshots or network calls.
    internal static class DesktopDragPreferencesTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        internal static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), directory = Path.Combine(Path.GetTempPath(), "IslandDragPreferences." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", directory);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, NotificationsEnabled = false, AutoCollapse = false, AutoMusic = false });
                window = new IslandWindow(true) { ShowActivated = false }; window.Loaded += (s, e) => StopTimers(window); window.Show(); window.UpdateLayout(); StopTimers(window);
                CancelRestoresOriginal(window); AltSkipsOneSnap(window);
                Console.WriteLine("PASS: Esc drag cancellation restores display/edge/anchor/pin without clicking; Alt skips one snap while preserving preference.");
            }
            finally { if (window != null) window.Close(); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        private static void CancelRestoresOriginal(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings"); settings.Side = "right"; settings.HorizontalPosition = .7; settings.VerticalPosition = .31; settings.FreePosition = true; settings.AutoSnapToEdge = true;
            window.SetExpanded(true, false); window.UpdateLayout(); StopTimers(window); Write(window, "pinned", true);
            string device = settings.ScreenName; Rect original = Bounds(window); var prefs = Read<DesktopExperiencePreferences>(window, "desktopPreferences"); int anchors = prefs.Monitors.Count;
            Write(window, "dragging", true); Call(window, "BeginExperienceDragSnapshot"); Write(window, "moved", true); Write(window, "pendingCompactAction", "focus");
            settings.ScreenName = "synthetic-other-monitor"; settings.Side = "bottom"; settings.HorizontalPosition = .9; settings.VerticalPosition = .8; settings.FreePosition = false; settings.AutoSnapToEdge = false; Write(window, "pinned", false);
            prefs.Monitors.Add(new MonitorAnchor { Device = "temporary-drag-monitor", X = .9, Y = .8 }); Call(window, "SetExpandedCore", false, false);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, (HwndSource)PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(key);
            Check(key.Handled && !Read<bool>(window, "dragging") && !Read<bool>(window, "moved") && Read<string>(window, "pendingCompactAction") == null, "Esc consumes drag and cancels pending component click");
            Check(settings.ScreenName == device && settings.Side == "right" && Math.Abs(settings.HorizontalPosition - .7) < .0001 && Math.Abs(settings.VerticalPosition - .31) < .0001 && settings.FreePosition && settings.AutoSnapToEdge, "original monitor, side, normalized coordinates and preferences restored");
            Check(Read<bool>(window, "expanded") && Read<bool>(window, "pinned") && Near(Bounds(window), original), "original expanded and pinned placement restored");
            Check(prefs.Monitors.Count == anchors && !Read<FocusPanel>(window, "focusPanel").IsRunning, "intermediate monitor anchors discarded without starting focus");
            Check(!(bool)Call(window, "TryCancelExperienceDrag"), "ordinary Esc is not consumed without a drag");
            Write(window, "dragging", true); Write(window, "resizing", true); Check(!(bool)Call(window, "TryCancelExperienceDrag"), "resize capture is not canceled by drag handler"); Write(window, "dragging", false); Write(window, "resizing", false); Write(window, "pinned", false); window.SetExpanded(false, false);
        }
        private static void AltSkipsOneSnap(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings"); settings.ScreenName = ""; settings.Side = "right"; settings.AutoSnapToEdge = true;
            Rect area = (Rect)Call(window, "PlacementArea");
            var free = new Rect(area.Left + (area.Width - 88) * .4, area.Top + (area.Height - 238) * .4, 88, 238);
            window.ExperienceDragModifiers = () => ModifierKeys.Alt; Call(window, "ApplyBounds", free); Write(window, "dragging", true); Write(window, "moved", true); Call(window, "FinishDrag", Read<Border>(window, "shell"), true);
            Check(settings.AutoSnapToEdge && settings.FreePosition && Near(Bounds(window), free), "Alt release keeps free coordinates but leaves autosnap preference enabled");
            window.ExperienceDragModifiers = () => ModifierKeys.None; Call(window, "ApplyBounds", free); Write(window, "dragging", true); Write(window, "moved", true); Call(window, "FinishDrag", Read<Border>(window, "shell"), true);
            Check(settings.AutoSnapToEdge && !settings.FreePosition && (bool)Call(window, "TouchesDockEdge", Bounds(window), area), "next ordinary drag snaps again");
            settings.AutoSnapToEdge = false; Call(window, "ApplyBounds", free); Write(window, "dragging", true); Write(window, "moved", true); Call(window, "FinishDrag", Read<Border>(window, "shell"), true);
            Check(!settings.AutoSnapToEdge && settings.FreePosition, "existing free-placement preference remains unchanged");
        }
        private static Rect Bounds(Window window) { return new Rect(window.Left, window.Top, window.Width, window.Height); }
        private static bool Near(Rect a, Rect b) { return Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1 && Math.Abs(a.Width - b.Width) <= 1 && Math.Abs(a.Height - b.Height) <= 1; }
        private static T Read<T>(object value, string field) { return (T)value.GetType().GetField(field, Private).GetValue(value); }
        private static void Write(object value, string field, object content) { value.GetType().GetField(field, Private).SetValue(value, content); }
        private static object Call(object value, string method, params object[] arguments) { try { return value.GetType().GetMethod(method, Private).Invoke(value, arguments); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static void StopTimers(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception("Drag preferences: " + message); }
    }
}
