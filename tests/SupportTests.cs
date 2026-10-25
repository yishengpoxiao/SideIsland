using System;
using System.IO;
using System.Text;
using System.Threading;

namespace CodexIsland
{
    internal static class SupportTests
    {
        public static void Run()
        {
            string directory = Path.Combine(Path.GetTempPath(), "CodexIslandSupportTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string previousOverride = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", directory);
                SettingsStore store = new SettingsStore();
                Check(store.FilePath == Path.Combine(directory, "settings.json"), "Data directory override");
                Check(store.Load().Side == "right" && store.Load().AutoSnapToEdge && store.Load().CompactTextDirection == "auto" && !File.Exists(store.FilePath), "Missing file defaults");
                Settings expected = new Settings
                {
                    Side = "left", ScreenName = "显示器 2", VerticalPosition = 0.75,
                    FreePosition = true, HorizontalPosition = 0.625,
                    AutoSnapToEdge = false, CompactTextDirection = "rotate180",
                    CustomExpandedSize = true, ExpandedWidth = 744, ExpandedHeight = 987,
                    RefreshSeconds = 90, AutoCollapse = false, LaunchAtLogin = true,
                    CodexPath = @"D:\工具\codex.exe", CodexHome = @"D:\用户\.codex", AlertThreshold = 20,
                    NotificationsEnabled = false, NotificationPreview = false, HideNotificationBody = true,
                    QuietPreviews = true, AlwaysOnTop = false, OpacityPercent = 75,
                    CompactMode = "clock", HiddenTools = "calculator,clipboard"
                };
                store.Save(expected);
                Settings actual = store.Load();
                Check(actual.Side == expected.Side && actual.ScreenName == expected.ScreenName &&
                    actual.VerticalPosition == expected.VerticalPosition && actual.RefreshSeconds == expected.RefreshSeconds &&
                    actual.FreePosition == expected.FreePosition && actual.HorizontalPosition == expected.HorizontalPosition &&
                    actual.AutoSnapToEdge == expected.AutoSnapToEdge && actual.CompactTextDirection == expected.CompactTextDirection &&
                    actual.CustomExpandedSize == expected.CustomExpandedSize && actual.ExpandedWidth == expected.ExpandedWidth &&
                    actual.ExpandedHeight == expected.ExpandedHeight &&
                    actual.AutoCollapse == expected.AutoCollapse && actual.LaunchAtLogin == expected.LaunchAtLogin &&
                    actual.CodexPath == expected.CodexPath && actual.CodexHome == expected.CodexHome &&
                    actual.AlertThreshold == expected.AlertThreshold && actual.NotificationsEnabled == expected.NotificationsEnabled &&
                    actual.NotificationPreview == expected.NotificationPreview && actual.HideNotificationBody == expected.HideNotificationBody &&
                    actual.QuietPreviews == expected.QuietPreviews && actual.AlwaysOnTop == expected.AlwaysOnTop &&
                    actual.OpacityPercent == expected.OpacityPercent && actual.CompactMode == expected.CompactMode &&
                    actual.HiddenTools == expected.HiddenTools, "Complete settings round trip");
                store.Save(new Settings());
                Check(store.Load().Side == "right", "Atomic replacement of existing settings");
                Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Temporary files cleaned up");

                string[] malformed = { "{broken", "[]", "null", "42", "\"text\"" };
                foreach (string input in malformed)
                {
                    File.WriteAllText(store.FilePath, input);
                    actual = store.Load();
                    Check(actual.Side == "right" && actual.RefreshSeconds == 60, "Malformed settings defaults");
                }
                File.WriteAllBytes(store.FilePath, new byte[] { 0xff, 0xfe, 0x00 });
                Check(store.Load().RefreshSeconds == 60, "Invalid encoding defaults");
                File.WriteAllText(store.FilePath, "{\"Side\":\"left\",\"FutureField\":42}");
                Check(store.Load().Side == "left" && store.Load().RefreshSeconds == 60, "Partial settings and unknown fields");
                File.WriteAllText(store.FilePath, "{\"VerticalPosition\":-4,\"HorizontalPosition\":-0.1,\"ExpandedWidth\":-100,\"ExpandedHeight\":0,\"RefreshSeconds\":1,\"AlertThreshold\":-10}");
                actual = store.Load();
                Check(actual.VerticalPosition == 0 && actual.HorizontalPosition == 0 && actual.ExpandedWidth == 360 && actual.ExpandedHeight == 360 &&
                    actual.RefreshSeconds == 30 && actual.AlertThreshold == 1, "Lower bounds");
                File.WriteAllText(store.FilePath, "{\"VerticalPosition\":7,\"HorizontalPosition\":4,\"ExpandedWidth\":2147483648,\"ExpandedHeight\":9999,\"RefreshSeconds\":9999,\"AlertThreshold\":110}");
                actual = store.Load();
                Check(actual.VerticalPosition == 1 && actual.HorizontalPosition == 1 && actual.ExpandedWidth == 900 && actual.ExpandedHeight == 1100 &&
                    actual.RefreshSeconds == 600 && actual.AlertThreshold == 100, "Upper bounds");
                File.WriteAllText(store.FilePath, "{\"Side\":\"invalid\",\"ScreenName\":[],\"VerticalPosition\":true," +
                    "\"RefreshSeconds\":\"60\",\"AlertThreshold\":false,\"AutoCollapse\":\"false\",\"LaunchAtLogin\":1," +
                    "\"CodexPath\":null,\"CodexHome\":{}}");
                actual = store.Load();
                Check(actual.Side == "right" && actual.ScreenName == "" && actual.VerticalPosition == 0.4 &&
                    actual.RefreshSeconds == 60 && actual.AlertThreshold == 15 && actual.AutoCollapse &&
                    !actual.LaunchAtLogin && actual.CodexPath == "" && actual.CodexHome == "", "Strict field types");
                store.Save(new Settings { VerticalPosition = Double.NaN });
                Check(store.Load().VerticalPosition == 0.4, "Nonfinite position normalized before serialization");

                File.WriteAllText(store.FilePath, "{\"Side\":\"left\",\"ScreenName\":\"legacy monitor\",\"VerticalPosition\":0.7,\"AutoCollapse\":false}");
                actual = store.Load();
                Check(actual.Side == "left" && actual.ScreenName == "legacy monitor" && actual.VerticalPosition == 0.7 && !actual.AutoCollapse &&
                    !actual.FreePosition && actual.HorizontalPosition == 0.5 && !actual.CustomExpandedSize && actual.ExpandedWidth == 388 && actual.ExpandedHeight == 680,
                    "Older settings retain their docking and gain safe defaults for free movement and resizing");
                Check(actual.AutoSnapToEdge && actual.CompactTextDirection == "auto", "Older settings gain automatic edge snapping and upright compact text");
                foreach (string edge in new[] { "left", "right", "top", "bottom" })
                {
                    store.Save(new Settings { Side = edge });
                    Check(store.Load().Side == edge, "Every docking edge survives a round trip");
                }
                foreach (string direction in new[] { "auto", "rotate180", "mirror" })
                {
                    store.Save(new Settings { CompactTextDirection = direction, AutoSnapToEdge = false, FreePosition = true });
                    actual = store.Load();
                    Check(actual.CompactTextDirection == direction && !actual.AutoSnapToEdge && actual.FreePosition,
                        "Compact direction and disabled snapping survive a round trip");
                }
                foreach (string invalidFields in new[] {
                    "{\"AutoSnapToEdge\":\"false\",\"CompactTextDirection\":\"upside-down\",\"Side\":\"TOP\"}",
                    "{\"AutoSnapToEdge\":0,\"CompactTextDirection\":180,\"Side\":null}",
                    "{\"AutoSnapToEdge\":null,\"CompactTextDirection\":[],\"Side\":{}}" })
                {
                    File.WriteAllText(store.FilePath, invalidFields);
                    actual = store.Load();
                    Check(actual.AutoSnapToEdge && actual.CompactTextDirection == "auto" && actual.Side == "right",
                        "Snap, text direction and dock fields reject invalid values and wrong types");
                }
                store.Save(new Settings { Side = "invalid", CompactTextDirection = null });
                actual = store.Load();
                Check(actual.Side == "right" && actual.CompactTextDirection == "auto" && actual.AutoSnapToEdge,
                    "Dock and compact direction are normalized before saving");
                File.WriteAllText(store.FilePath, "{\"FreePosition\":\"true\",\"HorizontalPosition\":\"0.25\",\"CustomExpandedSize\":1,\"ExpandedWidth\":420.5,\"ExpandedHeight\":\"800\"}");
                actual = store.Load();
                Check(!actual.FreePosition && actual.HorizontalPosition == 0.5 && !actual.CustomExpandedSize && actual.ExpandedWidth == 388 && actual.ExpandedHeight == 680,
                    "Position and size fields reject wrong JSON value types");
                File.WriteAllText(store.FilePath, "{\"HorizontalPosition\":true,\"ExpandedWidth\":null,\"ExpandedHeight\":[]}");
                actual = store.Load();
                Check(actual.HorizontalPosition == 0.5 && actual.ExpandedWidth == 388 && actual.ExpandedHeight == 680, "Invalid geometry fields cannot enter restored window coordinates");
                foreach (double invalid in new[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity })
                {
                    store.Save(new Settings { FreePosition = true, HorizontalPosition = invalid });
                    actual = store.Load();
                    Check(actual.FreePosition && actual.HorizontalPosition == 0.5, "Nonfinite free position normalized before serialization");
                }
                store.Save(new Settings { FreePosition = true, HorizontalPosition = 3, CustomExpandedSize = true, ExpandedWidth = Int32.MaxValue, ExpandedHeight = Int32.MinValue });
                actual = store.Load();
                Check(actual.FreePosition && actual.HorizontalPosition == 1 && actual.CustomExpandedSize && actual.ExpandedWidth == 900 && actual.ExpandedHeight == 360,
                    "Saved geometry is clamped before persisting");
                store.Save(new Settings { FreePosition = false, HorizontalPosition = 0, CustomExpandedSize = false, ExpandedWidth = 360, ExpandedHeight = 1100 });
                actual = store.Load();
                Check(!actual.FreePosition && actual.HorizontalPosition == 0 && !actual.CustomExpandedSize && actual.ExpandedWidth == 360 && actual.ExpandedHeight == 1100,
                    "False switches and exact dimension boundaries survive a round trip");

                File.WriteAllText(store.FilePath, "{}");
                actual = store.Load();
                Check(!actual.QuietPreviews && actual.AlwaysOnTop && actual.OpacityPercent == 100 && actual.CompactMode == "quota" && actual.HiddenTools == "", "New feature defaults preserve older settings files");
                File.WriteAllText(store.FilePath, "{\"OpacityPercent\":-100,\"CompactMode\":\"focus\"}");
                actual = store.Load();
                Check(actual.OpacityPercent == 60 && actual.CompactMode == "focus", "Opacity lower bound and focus compact mode");
                File.WriteAllText(store.FilePath, "{\"OpacityPercent\":1000,\"CompactMode\":\"unknown\"}");
                actual = store.Load();
                Check(actual.OpacityPercent == 100 && actual.CompactMode == "quota", "Opacity upper bound and invalid compact mode fallback");
                File.WriteAllText(store.FilePath, "{\"QuietPreviews\":\"true\",\"AlwaysOnTop\":0,\"OpacityPercent\":\"80\",\"CompactMode\":[],\"HiddenTools\":null}");
                actual = store.Load();
                Check(!actual.QuietPreviews && actual.AlwaysOnTop && actual.OpacityPercent == 100 && actual.CompactMode == "quota" && actual.HiddenTools == "", "New fields enforce strict value types");
                File.WriteAllText(store.FilePath, "{\"OpacityPercent\":75.5}");
                Check(store.Load().OpacityPercent == 100, "Fractional opacity uses its default");
                store.Save(new Settings { OpacityPercent = 5, CompactMode = "invalid", HiddenTools = null });
                actual = store.Load();
                Check(actual.OpacityPercent == 60 && actual.CompactMode == "quota" && actual.HiddenTools == "", "New settings normalized before saving");

                SystemStats first = SystemStatsReader.Read();
                Thread.Sleep(120);
                SystemStats second = SystemStatsReader.Read();
                Check(InRange(first.MemoryPercent) && InRange(second.CpuPercent) && InRange(second.BatteryPercent), "System telemetry ranges");
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previousOverride);
                string full = Path.GetFullPath(directory);
                string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (String.Equals(Path.GetDirectoryName(full), expectedParent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("CodexIslandSupportTests-", StringComparison.Ordinal)) Directory.Delete(full, true);
            }
        }

        private static bool InRange(double? value)
        {
            return !value.HasValue || (value.Value >= 0 && value.Value <= 100);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Support test failed: " + message);
        }
    }
}
