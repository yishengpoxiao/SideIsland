using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodexIsland
{
    internal static class StartupWindowTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        public static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string folder = Path.Combine(Path.GetTempPath(), "CodexIsland.StartupWindow." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false });
                window = new IslandWindow(true);
                window.Show(); Pump(); StopTimers(window);
                var tray = (Forms.NotifyIcon)typeof(IslandWindow).GetField("tray", Private).GetValue(window);
                window.WindowState = WindowState.Minimized; Pump(); StopTimers(window);
                Check(window.WindowState == WindowState.Minimized, "fixture really minimizes its own window");
                tray.ContextMenuStrip.Items[0].PerformClick(); Pump(); StopTimers(window);
                Check(window.WindowState == WindowState.Normal && window.IsVisible, "tray show restores a minimized island");
                Check((bool)typeof(IslandWindow).GetField("expanded", Private).GetValue(window), "restored island is expanded");
                Check(!window.Topmost, "revealing an island preserves the user's always-on-top preference");
                window.Hide(); tray.ContextMenuStrip.Items[0].PerformClick(); Pump(); StopTimers(window);
                Check(window.IsVisible && window.WindowState == WindowState.Normal, "tray show restores a hidden island");
                window.Left = -50000; window.Top = -50000;
                tray.ContextMenuStrip.Items[0].PerformClick(); window.UpdateLayout(); StopTimers(window);
                var area = (Rect)typeof(IslandWindow).GetMethod("PlacementArea", Private).Invoke(window, null);
                Check(window.Left >= area.Left - 1 && window.Top >= area.Top - 1 && window.Left + window.Width <= area.Right + 1 && window.Top + window.Height <= area.Bottom + 1,
                    "revealing an island brings stale off-screen coordinates back inside its display");
                Console.WriteLine("PASS: startup reveal restores hidden/minimized windows, corrects placement and preserves topmost preference.");
            }
            finally
            {
                if (window != null) { StopTimers(window); window.Close(); }
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
        private static void StopTimers(IslandWindow window)
        {
            foreach (FieldInfo field in typeof(IslandWindow).GetFields(Private))
                if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop();
        }
        private static void Pump()
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Startup window: " + message); }
    }
}
