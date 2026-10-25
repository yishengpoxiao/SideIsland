using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Forms = System.Windows.Forms;

namespace CodexIsland
{
    // Synthetic geometry, fake hotkey registration and own demo windows only.
    internal static class DesktopExperienceTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        internal static void Run()
        {
            FullscreenGeometry(); FullscreenApplicationRules(); Preferences(); HotkeyValidation(); HotkeyRollback(); OwnWindowBehavior();
            Console.WriteLine("PASS: desktop fullscreen geometry/restoration, monitor anchors, gesture routing and hotkey conflict rollback.");
        }
        private static void Check(bool condition, string description) { if (!condition) throw new Exception("Desktop experience: " + description); }
        private static void FullscreenGeometry()
        {
            var sample = new FullscreenSample { Visible = true, Device = "display-A", Window = new Rect(-1920, 0, 1920, 1080), Monitor = new Rect(-1920, 0, 1920, 1080) };
            Check(FullscreenPolicy.IsFullscreen(sample, "display-A"), "negative-origin monitor fullscreen");
            Check(!FullscreenPolicy.IsFullscreen(sample, "display-B"), "other-monitor fullscreen ignored");
            sample.Window = new Rect(-1920, 0, 1920, 1040); Check(!FullscreenPolicy.IsFullscreen(sample, "display-A"), "maximized work area is not fullscreen"); sample.Window = sample.Monitor;
            sample.Maximized = sample.Captioned = true; Check(!FullscreenPolicy.IsFullscreen(sample, "display-A"), "captioned maximized app excluded with auto-hidden taskbar"); sample.Maximized = sample.Captioned = false;
            sample.Visible = false; Check(!FullscreenPolicy.IsFullscreen(sample, "display-A"), "hidden excluded"); sample.Visible = true;
            sample.Minimized = true; Check(!FullscreenPolicy.IsFullscreen(sample, "display-A"), "minimized excluded"); sample.Minimized = false;
            sample.Cloaked = true; Check(!FullscreenPolicy.IsFullscreen(sample, "display-A"), "cloaked excluded"); sample.Cloaked = false;
            sample.Desktop = true; Check(!FullscreenPolicy.IsFullscreen(sample, "display-A"), "desktop shell excluded"); sample.Desktop = false;
            sample.OwnProcess = true; Check(!FullscreenPolicy.IsFullscreen(sample, "display-A"), "own tool windows excluded");
        }
        private static void Preferences()
        {
            string directory = Path.Combine(Path.GetTempPath(), "IslandDesktopPreferences." + Guid.NewGuid().ToString("N"));
            try
            {
                string path = Path.Combine(directory, "desktop.json"); var store = new DesktopExperienceStore(path); var prefs = store.Load();
                Check(prefs.FullscreenMode == "off" && prefs.ClickAction == "default" && prefs.MiddleAction == "none" && prefs.WheelAction == "default", "defaults preserve interactions");
                var settings = new Settings { Side = "left", HorizontalPosition = .1, VerticalPosition = .7, FreePosition = true };
                prefs.Remember("A", settings); settings.Side = "bottom"; settings.HorizontalPosition = .8; settings.VerticalPosition = .9; settings.FreePosition = false; prefs.Remember("B", settings);
                Check(prefs.Restore("A", settings) && settings.Side == "left" && settings.FreePosition && settings.VerticalPosition == .7, "independent monitor A anchor");
                Check(prefs.Restore("B", settings) && settings.Side == "bottom" && !settings.FreePosition && settings.HorizontalPosition == .8, "independent monitor B anchor");
                prefs.RememberMonitors = false; Check(!prefs.Restore("A", settings), "remember toggle respected"); prefs.RememberMonitors = true;
                prefs.FullscreenMode = "slim"; prefs.MiddleAction = "screenpin"; prefs.SearchHotkey = "Ctrl+Shift+F9";
                prefs.FullscreenRules.Add(new FullscreenApplicationRule { ProcessName = "VLC.EXE", Mode = "hide" });
                Check(store.TrySave(prefs), "atomic preference save"); var loaded = new DesktopExperienceStore(path).Load(); Check(loaded.Monitors.Count == 2 && loaded.FullscreenMode == "slim" && loaded.SearchHotkey == prefs.SearchHotkey, "preference round trip");
                Check(loaded.FullscreenRules.Count == 1 && loaded.FullscreenRules[0].ProcessName == "vlc.exe" && loaded.FullscreenRules[0].Mode == "hide", "application fullscreen rules persist independently");
                loaded.Monitors.Add(new MonitorAnchor { Device = "bad", X = Double.NaN, Y = Double.PositiveInfinity }); loaded.Validate(); Check(loaded.Monitors.Last().X == .5 && loaded.Monitors.Last().Y == .4, "invalid anchors normalized");
                File.WriteAllText(path, "not json"); var corrupt = new DesktopExperienceStore(path); corrupt.Load(); Check(!corrupt.TrySave(prefs) && File.ReadAllText(path) == "not json", "unreadable settings preserved");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        private static void FullscreenApplicationRules()
        {
            var prefs = new DesktopExperiencePreferences { FullscreenMode = "slim" };
            prefs.FullscreenRules.Add(new FullscreenApplicationRule { ProcessName = "vlc.exe", Mode = "off" });
            prefs.FullscreenRules.Add(new FullscreenApplicationRule { ProcessName = "game", Mode = "hide" });
            prefs.FullscreenRules.Add(new FullscreenApplicationRule { ProcessName = "slides", Mode = "default" });
            Check(FullscreenPolicy.ModeFor(new FullscreenSample { ProcessName = "VLC" }, prefs) == "off", "keep-visible process rule overrides global avoidance");
            Check(FullscreenPolicy.ModeFor(new FullscreenSample { ProcessName = "GAME.EXE" }, prefs) == "hide", "process rule ignores exe suffix and case");
            Check(FullscreenPolicy.ModeFor(new FullscreenSample { ProcessName = "slides" }, prefs) == "slim", "default process rule follows global mode");
            Check(FullscreenPolicy.ModeFor(new FullscreenSample { ProcessName = "" }, prefs) == "slim" && FullscreenPolicy.ModeFor(new FullscreenSample { ProcessName = "unknown" }, prefs) == "slim", "unknown or protected process safely falls back to global rule");
            prefs.FullscreenMode = "off"; Check(FullscreenPolicy.ModeFor(new FullscreenSample { ProcessName = "game" }, prefs) == "hide", "specific avoidance works when global mode is off");
            prefs.FullscreenRules.Add(new FullscreenApplicationRule { ProcessName = "C:\\game.exe", Mode = "hide" });
            prefs.FullscreenRules.Add(new FullscreenApplicationRule { ProcessName = "VLC.EXE", Mode = "slim" }); prefs.Validate();
            Check(prefs.FullscreenRules.Count == 3 && prefs.FullscreenRules.Single(rule => rule.ProcessName == "vlc.exe").Mode == "slim", "invalid process paths rejected and duplicate rule keeps last edit");
            Check(FullscreenApplicationRule.NormalizeProcess("foo.exe.exe") == "foo.exe.exe" && FullscreenApplicationRule.NormalizeProcess("foo.exe") == "foo.exe", "canonical process names preserve distinct executable identities");
        }
        private static void HotkeyValidation()
        {
            HotkeyChord chord; Check(HotkeyChord.TryParse("Ctrl+Alt+Space", out chord) && chord.Modifiers == 3 && chord.Key == 0x20, "default hotkey parse");
            Check(HotkeyChord.TryParse("", out chord) && chord == null, "blank disables binding"); Check(HotkeyChord.TryParse("F9", out chord), "standalone function key");
            foreach (string invalid in new[] { "A", "Ctrl", "Ctrl+A+B", "Ctrl+", "Ctrl+Unknown", "Ctrl+LeftCtrl" }) Check(!HotkeyChord.TryParse(invalid, out chord), "invalid combination " + invalid);
            var prefs = new DesktopExperiencePreferences { SearchHotkey = "Alt+Ctrl+Space" }; Dictionary<string, HotkeyChord> bindings; string error;
            Check(!HotkeyChord.TryRead(prefs, out bindings, out error), "normalized duplicate chords rejected");
        }
        private static void HotkeyRollback()
        {
            var window = new Window(); var active = new Dictionary<int, uint>(); bool failNew = false; int unregisters = 0;
            var registration = new HotkeyRegistration(window, (id, modifiers, key) => { if (failNew && key == 0x42) return false; active.Add(id, key); return true; }, id => { active.Remove(id); unregisters++; });
            try
            {
                string error; var original = new Dictionary<string, HotkeyChord> { { "toggle", new HotkeyChord { Key = 0x20, Modifiers = 3 } } };
                Check(registration.Replace(original, key => { }, out error) && active.Count == 1, "initial fake binding registered"); failNew = true;
                var updated = new Dictionary<string, HotkeyChord> { { "toggle", new HotkeyChord { Key = 0x41, Modifiers = 3 } }, { "search", new HotkeyChord { Key = 0x42, Modifiers = 3 } } };
                Check(!registration.Replace(updated, key => { }, out error) && active.Count == 1 && active.Values.Single() == 0x20 && unregisters == 2, "partial conflict rolls back exact original set");
                failNew = false; Check(registration.Replace(updated, key => { }, out error) && active.Count == 2, "successful replacement after rollback");
            }
            finally { registration.Dispose(); window.Close(); Check(active.Count == 0, "dispose unregisters fake bindings"); }
        }
        private static T Read<T>(object instance, string field) { return (T)instance.GetType().GetField(field, Private).GetValue(instance); }
        private static void Invoke(object instance, string name, params object[] arguments) { instance.GetType().GetMethod(name, Private).Invoke(instance, arguments); }
        private static void OwnWindowBehavior()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"); string directory = Path.Combine(Path.GetTempPath(), "IslandDesktopWindow." + Guid.NewGuid().ToString("N")); IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", directory); new SettingsStore().Save(new Settings { AutoCollapse = false, NotificationsEnabled = false, AlwaysOnTop = false, AutoMusic = false });
                window = new IslandWindow(true); window.Show(); window.UpdateLayout(); window.SetExpanded(false, false);
                var prefs = Read<DesktopExperiencePreferences>(window, "desktopPreferences"); Check(prefs != null, "desktop module integrated");
                prefs.ToggleHotkey = "Ctrl+Shift+F9"; prefs.PrivacyHotkey = ""; Invoke(window, "ConfigureDesktopExperience");
                Check(Read<TextBlock>(window, "toolkitHint").Text.Contains("Ctrl+Shift+F9") && !Read<TextBlock>(window, "toolkitHint").Text.Contains("Ctrl+Alt+Space"), "toolkit hint reflects saved and disabled hotkeys");
                window.GetType().GetField("desktopMiddlePress", Private).SetValue(window, (Point?)new Point(10, 10));
                Invoke(window, "TrackExperienceMiddleGesture", new Point(10 + SystemParameters.MinimumHorizontalDragDistance + 1, 10));
                Check(!(bool)window.GetType().GetMethod("FinishExperienceMiddleGesture", Private).Invoke(window, new object[] { new Point(10, 10) }), "middle drag returning to origin stays canceled");
                window.GetType().GetField("desktopMiddlePress", Private).SetValue(window, (Point?)new Point(10, 10));
                Check((bool)window.GetType().GetMethod("FinishExperienceMiddleGesture", Private).Invoke(window, new object[] { new Point(10, 10) }), "stationary middle release remains a click");
                var focus = Read<FocusPanel>(window, "focusPanel"); prefs.ClickAction = "none"; Invoke(window, "RunExperienceCompactClick", "focus"); Check(!focus.IsRunning, "disabled click blocks component action");
                prefs.ClickAction = "focus"; Invoke(window, "RunExperienceCompactClick", "codex-primary"); Check(focus.IsRunning && !Read<bool>(window, "expanded"), "custom click runs focus without expansion");
                Invoke(window, "RunExperienceCompactClick", "codex-primary"); Check(!focus.IsRunning, "custom click pauses focus"); prefs.ClickAction = "default";
                Invoke(window, "RunExperienceCompactClick", "focus"); Check(focus.IsRunning && !Read<bool>(window, "expanded"), "default click preserves component quick action"); Invoke(window, "RunExperienceCompactClick", "focus");
                prefs.ClickAction = "focus"; window.SetExpanded(true, false); Invoke(window, "RunExperienceCompactClick", (object)null); Check(!focus.IsRunning && !Read<bool>(window, "expanded"), "expanded title click retains collapse behavior"); window.SetExpanded(false, false); prefs.ClickAction = "default";
                prefs.FullscreenMode = "hide"; string device = Forms.Screen.PrimaryScreen.DeviceName; var bounds = Forms.Screen.PrimaryScreen.Bounds;
                var sample = new FullscreenSample { Visible = true, Device = device, Window = new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height), Monitor = new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height) };
                window.DesktopFullscreenReader = () => sample; Keyboard.ClearFocus();
                Invoke(window, "UpdateFullscreenAvoidance"); Check(Read<bool>(window, "desktopAvoiding") && window.Opacity == 0 && !window.IsHitTestVisible && window.IsVisible, "fullscreen suppresses visuals without changing visibility");
                sample.Visible = false; Invoke(window, "UpdateFullscreenAvoidance"); Check(!Read<bool>(window, "desktopAvoiding") && window.Opacity == 1 && window.IsHitTestVisible, "exit restores opacity and hit testing");
                sample.Visible = true; Invoke(window, "UpdateFullscreenAvoidance"); window.Hide(); sample.Visible = false; Invoke(window, "UpdateFullscreenAvoidance"); Check(!window.IsVisible, "auto restoration preserves manual Hide");
                window.Show(); sample.Visible = true; prefs.FullscreenMode = "slim"; Invoke(window, "UpdateFullscreenAvoidance"); var strip = Read<Window>(window, "desktopSlimWindow"); Check(strip != null && strip.IsVisible && Math.Abs(Math.Min(strip.Width, strip.Height) - 9) <= 1, "slim edge strip appears within one device-rounded DIP");
                Invoke(window, "RestoreDesktopExperienceForReveal"); Check(!Read<bool>(window, "desktopAvoiding") && !strip.IsVisible, "explicit reveal bypasses fullscreen"); Invoke(window, "UpdateFullscreenAvoidance"); Check(!Read<bool>(window, "desktopAvoiding"), "fullscreen does not immediately rehide revealed island");
            }
            finally { if (window != null) window.Close(); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }
}
