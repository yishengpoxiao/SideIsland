using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CodexIsland
{
    // Accelerated repetition on our own demo window, not a claim of 24-hour use.
    // No global input, real clipboard/player commands, captures, HTTP/TCP probes,
    // physical display changes or actual power-state transitions are performed.
    internal static class LongRunningTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const long MiB = 1024L * 1024;
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetGuiResources(IntPtr process, uint flags);
        private sealed class ResourceSample
        {
            internal long PrivateBytes, ManagedBytes;
            internal int Handles;
            internal uint UserObjects, GdiObjects;
        }
        private sealed class FakeClock : IFocusClock
        {
            internal DateTime Now = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
            internal TimeSpan Monotonic;
            public DateTime UtcNow { get { return Now; } }
            public TimeSpan Elapsed { get { return Monotonic; } }
            internal void Advance(TimeSpan value) { Now += value; Monotonic += value; }
        }
        internal static void Run()
        {
            FocusCycles(); WindowCycles();
        }
        private static void FocusCycles()
        {
            const int iterations = 10000;
            var watch = Stopwatch.StartNew(); var clock = new FakeClock(); var timer = new FocusTimer(clock); int events = 0;
            timer.Completed += () => events++;
            for (int index = 0; index < iterations; index++)
            {
                var duration = TimeSpan.FromSeconds(30 + index % 60);
                timer.SetCountdown(duration, "fixture focus", true); timer.Start();
                clock.Advance(TimeSpan.FromTicks(duration.Ticks / 3)); timer.Pause(); TimeSpan paused = timer.Value;
                Check(paused > TimeSpan.Zero && paused < duration, "Pause preserves the exact remaining deadline interval");
                clock.Advance(TimeSpan.FromDays(2));
                Check(timer.Value == paused && !timer.IsCompleted, "Paused countdown does not consume a simulated sleep interval");
                timer.Start(); clock.Advance(paused + TimeSpan.FromSeconds(7)); timer.Poll(); timer.Poll();
                Check(timer.IsCompleted && !timer.IsRunning && timer.Value == TimeSpan.Zero && events == index + 1 && timer.CompletedFocusSessions == index + 1,
                    "Crossing a deadline completes once without notification/session-count drift");
                timer.SetStopwatch(); timer.Start(); clock.Advance(TimeSpan.FromMilliseconds(37));
                clock.Now = clock.Now.AddHours(-5);
                Check(timer.Value == TimeSpan.FromMilliseconds(37), "Stopwatch follows the fake monotonic clock across wall-clock changes");
                timer.Pause(); clock.Advance(TimeSpan.FromSeconds(100));
                Check(timer.Value == TimeSpan.FromMilliseconds(37), "Paused stopwatch remains stable");
                timer.Start(); clock.Advance(TimeSpan.FromMilliseconds(63)); timer.Pause();
                Check(timer.Value == TimeSpan.FromMilliseconds(100) && events == index + 1, "Repeated stopwatch pause/resume accumulates exact elapsed time without countdown events");
            }
            watch.Stop();
            Console.WriteLine("Focus cycle stress: " + iterations.ToString(CultureInfo.InvariantCulture) + " deadline/pause/resume/stopwatch scenarios in " + watch.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms (fake clock only).");
        }
        private static void WindowCycles()
        {
            string previousDirectory = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), language = UiLocalization.Language, theme = UiTheme.Name;
            var previousContext = SynchronizationContext.Current;
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string folder = Path.GetFullPath(Path.Combine(temporaryRoot, "SideIsland.CycleStress." + Guid.NewGuid().ToString("N")));
            Check(folder.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase), "Fixture storage remains under the temporary root");
            IslandWindow window = null;
            var entireWatch = Stopwatch.StartNew(); int rounds = 0, switches = 0, expansions = 0, powerPairs = 0, displaySignals = 0;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { AutoMusic = false, NotificationsEnabled = false, AlwaysOnTop = false, AutoCollapse = false,
                    HoverExpandEnabled = false, Language = language, Theme = theme, HiddenTools = "", CompactMode = "quota", CompactSlot1 = "clock", CompactSlot2 = "focus" });
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false, Topmost = false };
                window.Show(); PumpToIdle(); window.SetExpanded(true, false); Call(window, "SelectToolsPage");
                var panels = Read<Dictionary<string, UIElement>>(window, "toolPanels");
                string[] keys = ToolCatalog.All.Select(item => item.Key).ToArray();
                string[] expectedKeys = { "media", "focus", "notes", "reminders", "time", "launch", "files", "hash", "clipboard", "text", "calculator", "converter", "random", "colors", "system", "ports", "screenpin", "image", "diff", "watch", "awake", "qr", "scenes", "clipboardhistory", "logwatch", "desktop", "profiles", "diagnostics", "ocr", "templates", "ready", "backup", "autorules", "widgets", "audio-mixer", "lan-transfer", "batch", "activities" };
                Check(keys.Length == 38 && keys.SequenceEqual(expectedKeys) && panels.Keys.OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(expectedKeys.OrderBy(key => key, StringComparer.Ordinal)), "All thirty-eight specific catalog tools, with no missing or extra panels, are present in the demo stress window");
                var templates = Read<TextTemplatePanel>(window, "textTemplatePanel"); Await(templates.PendingLoad, 5000);

                // WPF creates styles, font caches and generated containers lazily.
                // Warm every panel twice before taking a resource baseline.
                for (int pass = 0; pass < 2; pass++)
                {
                    foreach (string key in keys) Visit(window, key, false);
                    Call(window, "ApplyRuntimePowerMode", PowerModes.Suspend); Call(window, "ApplyRuntimePowerMode", PowerModes.Resume);
                    Call(window, "OnDesktopDisplaySettingsChanged"); PumpToIdle();
                }
                Call(window, "SelectTool", "focus"); window.SetExpanded(true, false); Quiesce();
                ResourceSample before = Sample(); var workWatch = Stopwatch.StartNew();
                // Time-bounded repetition makes this useful on both fast and slow
                // desktops while still exercising several complete catalog passes.
                while ((rounds < 6 || workWatch.Elapsed < TimeSpan.FromSeconds(10)) && rounds < 24)
                {
                    foreach (string key in keys)
                    {
                        Visit(window, key, true); switches++; expansions += 3;
                    }
                    rounds++;
                    Call(window, "ApplyRuntimePowerMode", PowerModes.Suspend);
                    Check(Read<bool>(window, "runtimeSuspended") && !Read<DispatcherTimer>(window, "displayTimer").IsEnabled && !Read<NowPlayingMonitor>(window, "nowPlaying").Running,
                        "Simulated suspend stops runtime/media sampling");
                    // Navigation during suspension must not restart the monitor.
                    Call(window, "SelectTool", "media");
                    Check(!Read<NowPlayingMonitor>(window, "nowPlaying").Running && !Read<bool>(Read<MediaPanel>(window, "mediaPanel"), "running"), "Selecting media during suspension leaves both media samplers stopped");
                    Call(window, "ApplyRuntimePowerMode", PowerModes.Resume); powerPairs++;
                    Check(!Read<bool>(window, "runtimeSuspended"), "Simulated resume clears suspended state");
                    if (rounds % 2 == 0) { Call(window, "OnDesktopDisplaySettingsChanged"); displaySignals++; }
                    if (rounds % 3 == 0) { window.Hide(); PumpToIdle(); window.Show(); window.SetExpanded(true, false); Call(window, "SelectToolsPage"); }
                    PumpToIdle();
                    if (workWatch.Elapsed > TimeSpan.FromSeconds(22)) break;
                }
                workWatch.Stop();
                Call(window, "SelectTool", "media"); window.SetExpanded(true, false); Call(window, "SelectToolsPage"); PumpToIdle();
                Check(Read<bool>(Read<MediaPanel>(window, "mediaPanel"), "running") && Read<NowPlayingMonitor>(window, "nowPlaying").Running,
                    "Active media presentation recovers after all simulated resume/navigation cycles");
                Call(window, "SelectTool", "focus"); window.SetExpanded(false, false); window.SetExpanded(true, false); PumpToIdle();
                Check(Read<bool>(window, "expanded") && Read<string>(window, "selectedTool") == "focus" && panels["focus"].Visibility == Visibility.Visible && !Read<bool>(window, "visualTransitionActive"),
                    "The fixture remains switchable with a settled expanded presentation");
                Check(!templates.HasUnsavedChanges, "Stress navigation never creates or edits user templates");
                Quiesce(); ResourceSample after = Sample();
                long privateDelta = after.PrivateBytes - before.PrivateBytes, managedDelta = after.ManagedBytes - before.ManagedBytes;
                int handleDelta = after.Handles - before.Handles;
                long userDelta = (long)after.UserObjects - before.UserObjects, gdiDelta = (long)after.GdiObjects - before.GdiObjects;
                Console.WriteLine("Window cycle stress: " + keys.Length + " tools, " + rounds + " full passes, " + switches + " selections, " + expansions + " non-animated expand/collapse calls, " + powerPairs + " simulated power pairs, " + displaySignals + " simulated display signals; workload " + workWatch.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + " s, total " + entireWatch.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + " s.");
                Console.WriteLine("Post-GC resource delta: private " + Megabytes(privateDelta) + " MiB; managed " + Megabytes(managedDelta) + " MiB; handles " + handleDelta + "; USER " + userDelta + "; GDI " + gdiDelta + ".");
                // Reserve room for bounded WPF/render-driver caches. These limits
                // catch substantial retained growth, not ordinary cache noise.
                Check(privateDelta <= 160 * MiB && managedDelta <= 48 * MiB, "Repeated tool/power/display cycles retain bounded private and managed memory");
                Check(handleDelta <= 256 && userDelta <= 128 && gdiDelta <= 128, "Repeated cycles retain bounded kernel, USER and GDI resources");
                Console.WriteLine("PASS: isolated demo window cycle stress and 10000 fake-clock scenarios; no real 24-hour durability claim.");
            }
            finally
            {
                bool closed = window == null;
                try
                {
                    if (window != null) { StopWindowTimers(window); window.Close(); PumpToIdle(); closed = Read<bool>(window, "shuttingDown"); }
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext); AppAppearance.Configure(language, theme);
                    Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previousDirectory);
                    if (Directory.Exists(folder))
                    {
                        Check(Path.GetFullPath(folder).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0, "Cleanup remains inside the owned fixture directory");
                        Directory.Delete(folder, true);
                    }
                }
                Check(closed, "The demo stress window closes and disposes its toolkit");
            }
        }
        private static void Visit(IslandWindow window, string key, bool cycle)
        {
            window.SetExpanded(true, false); Call(window, "SelectToolsPage"); Call(window, "SelectTool", key); window.UpdateLayout(); PumpToIdle();
            Check(Read<string>(window, "selectedTool") == key, "Tool navigation accepts " + key);
            if (cycle) { window.SetExpanded(false, false); window.SetExpanded(true, false); window.UpdateLayout(); PumpToIdle(); }
        }
        private static ResourceSample Sample()
        {
            using (var process = Process.GetCurrentProcess())
            {
                process.Refresh(); return new ResourceSample { PrivateBytes = process.PrivateMemorySize64, ManagedBytes = GC.GetTotalMemory(false), Handles = process.HandleCount,
                    GdiObjects = GetGuiResources(process.Handle, 0), UserObjects = GetGuiResources(process.Handle, 1) };
            }
        }
        private static string Megabytes(long bytes) { return (bytes / (double)MiB).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture); }
        private static void Quiesce()
        {
            PumpToIdle(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < 80) { PumpToIdle(); Thread.Sleep(2); }
            GC.Collect(); GC.WaitForPendingFinalizers(); PumpToIdle();
        }
        private static void Await(Task task, int timeout)
        {
            var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.ElapsedMilliseconds < timeout) { PumpToIdle(); Thread.Sleep(1); }
            Check(task.IsCompleted, "The fixture's asynchronous template load settles before resource measurement"); task.GetAwaiter().GetResult();
        }
        private static void PumpToIdle()
        {
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
        }
        private static void StopWindowTimers(IslandWindow window)
        { foreach (var field in typeof(IslandWindow).GetFields(Fields)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); }
        private static T Read<T>(object instance, string name) { return (T)instance.GetType().GetField(name, Fields).GetValue(instance); }
        private static object Call(object instance, string name, params object[] args)
        { try { return instance.GetType().GetMethod(name, Fields).Invoke(instance, args); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception("Cycle stress: " + message); }
    }
}
