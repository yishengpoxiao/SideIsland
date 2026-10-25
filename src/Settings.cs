using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace CodexIsland
{
    public class Settings
    {
        public const int MinimumHoverDelayMilliseconds = 100;
        public const int MaximumHoverDelayMilliseconds = 5000;
        public string Side = "right";
        public string ScreenName = "";
        public double VerticalPosition = 0.4;
        public double HorizontalPosition = 0.5;
        public bool FreePosition = false;
        public bool AutoSnapToEdge = true;
        public string CompactTextDirection = "auto";
        public bool CustomExpandedSize = false;
        public int ExpandedWidth = 388;
        public int ExpandedHeight = 680;
        public int RefreshSeconds = 60;
        public bool AutoCollapse = true;
        public bool HoverExpandEnabled = true;
        public int HoverDelayMilliseconds = 650;
        public bool LaunchAtLogin = false;
        public string CodexPath = "";
        public string CodexHome = "";
        public int AlertThreshold = 15;
        public bool NotificationsEnabled = true;
        public bool NotificationPreview = true;
        public bool HideNotificationBody = false;
        public bool QuietPreviews = false;
        public bool AlwaysOnTop = true;
        public int OpacityPercent = 100;
        public string CompactMode = "quota";
        public string CompactSlot1 = "codex-primary";
        public string CompactSlot2 = "codex-secondary";
        public string Language = "zh";
        public string Theme = "dark";
        public int FocusMinutes = 25;
        public bool AutoMusic = true;
        public bool OnlineLyricsEnabled = false;
        public string HiddenTools = "";
        public string FavoriteTools = "focus,notes,reminders";
        public string LastTool = "focus";
    }

    public sealed class SettingsStore
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder name);
        private readonly object sync = new object();
        public string FilePath { get; private set; }

        public SettingsStore(string path = null)
        {
            if (path == null)
            {
                string directory = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
                if (String.IsNullOrWhiteSpace(directory))
                    directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexIsland");
                path = Path.Combine(directory, "settings.json");
            }
            FilePath = Path.GetFullPath(path);
        }

        public Settings Load()
        {
            lock (sync)
            {
                try
                {
                    string json = File.ReadAllText(FilePath, new UTF8Encoding(false, true));
                    object parsed = new JavaScriptSerializer().DeserializeObject(json);
                    return Validate(parsed as IDictionary<string, object>);
                }
                catch (IOException) { return new Settings(); }
                catch (UnauthorizedAccessException) { return new Settings(); }
                catch (ArgumentException) { return new Settings(); }
                catch (InvalidOperationException) { return new Settings(); }
            }
        }

        public void Save(Settings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            lock (sync)
            {
                Settings safe = Validate(new Dictionary<string, object>
                {
                    { "Side", settings.Side }, { "ScreenName", settings.ScreenName },
                    { "VerticalPosition", settings.VerticalPosition }, { "RefreshSeconds", settings.RefreshSeconds },
                    { "HorizontalPosition", settings.HorizontalPosition }, { "FreePosition", settings.FreePosition },
                    { "AutoSnapToEdge", settings.AutoSnapToEdge }, { "CompactTextDirection", settings.CompactTextDirection },
                    { "CustomExpandedSize", settings.CustomExpandedSize }, { "ExpandedWidth", settings.ExpandedWidth }, { "ExpandedHeight", settings.ExpandedHeight },
                    { "AutoCollapse", settings.AutoCollapse }, { "LaunchAtLogin", settings.LaunchAtLogin },
                    { "HoverExpandEnabled", settings.HoverExpandEnabled }, { "HoverDelayMilliseconds", settings.HoverDelayMilliseconds },
                    { "CodexPath", settings.CodexPath }, { "CodexHome", settings.CodexHome },
                    { "AlertThreshold", settings.AlertThreshold },
                    { "NotificationsEnabled", settings.NotificationsEnabled },
                    { "NotificationPreview", settings.NotificationPreview },
                    { "HideNotificationBody", settings.HideNotificationBody },
                    { "QuietPreviews", settings.QuietPreviews }, { "AlwaysOnTop", settings.AlwaysOnTop },
                    { "OpacityPercent", settings.OpacityPercent }, { "CompactMode", settings.CompactMode }, { "HiddenTools", settings.HiddenTools },
                    { "FavoriteTools", settings.FavoriteTools }, { "LastTool", settings.LastTool },
                    { "CompactSlot1", settings.CompactSlot1 }, { "CompactSlot2", settings.CompactSlot2 },
                    { "Language", settings.Language }, { "Theme", settings.Theme },
                    { "FocusMinutes", settings.FocusMinutes }, { "AutoMusic", settings.AutoMusic }, { "OnlineLyricsEnabled", settings.OnlineLyricsEnabled }
                });
                string json = new JavaScriptSerializer().Serialize(safe);
                string directory = Path.GetDirectoryName(FilePath);
                Directory.CreateDirectory(directory);
                string temporaryPath = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(json + Environment.NewLine);
                    using (FileStream file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        file.Write(bytes, 0, bytes.Length);
                        file.Flush(true);
                    }
                    if (File.Exists(FilePath))
                        File.Replace(temporaryPath, FilePath, null);
                    else
                        File.Move(temporaryPath, FilePath);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        try { File.Delete(temporaryPath); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
        }

        public static void SetLaunchAtLogin(bool enabled)
        {
            const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string valueName = "CodexIsland";
            if (enabled)
            {
                string executable = Assembly.GetExecutingAssembly().Location;
                string command = "\"" + executable + "\"";
                try
                {
                    uint length = 0;
                    if (GetCurrentPackageFamilyName(ref length, null) == 122)
                    {
                        var family = new StringBuilder((int)length);
                        if (GetCurrentPackageFamilyName(ref length, family) == 0)
                            command = "\"" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") + "\" \"shell:AppsFolder\\" + family + "!Island\"";
                    }
                }
                catch (EntryPointNotFoundException) { }
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    if (key == null) throw new IOException("无法打开当前用户的启动设置。");
                    key.SetValue(valueName, command, RegistryValueKind.String);
                }
            }
            else
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, true))
                {
                    if (key != null) key.DeleteValue(valueName, false);
                }
            }
        }

        private static Settings Validate(IDictionary<string, object> raw)
        {
            Settings result = new Settings();
            if (raw == null) return result;
            result.FocusMinutes = ReadInteger(raw, "FocusMinutes", 25, 1, 1440);
            result.AutoMusic = ReadBool(raw, "AutoMusic", result.AutoMusic);
            result.OnlineLyricsEnabled = ReadBool(raw, "OnlineLyricsEnabled", result.OnlineLyricsEnabled);
            string side = ReadString(raw, "Side", result.Side);
            if (side == "left" || side == "right" || side == "top" || side == "bottom") result.Side = side;
            result.ScreenName = ReadString(raw, "ScreenName", result.ScreenName);
            result.FreePosition = ReadBool(raw, "FreePosition", result.FreePosition);
            result.AutoSnapToEdge = ReadBool(raw, "AutoSnapToEdge", result.AutoSnapToEdge);
            string compactTextDirection = ReadString(raw, "CompactTextDirection", result.CompactTextDirection);
            if (compactTextDirection == "auto" || compactTextDirection == "rotate180" || compactTextDirection == "mirror")
                result.CompactTextDirection = compactTextDirection;
            result.CustomExpandedSize = ReadBool(raw, "CustomExpandedSize", result.CustomExpandedSize);
            result.ExpandedWidth = ReadInteger(raw, "ExpandedWidth", result.ExpandedWidth, 360, 900);
            result.ExpandedHeight = ReadInteger(raw, "ExpandedHeight", result.ExpandedHeight, 360, 1100);
            result.CodexPath = ReadString(raw, "CodexPath", result.CodexPath);
            result.CodexHome = ReadString(raw, "CodexHome", result.CodexHome);
            result.AutoCollapse = ReadBool(raw, "AutoCollapse", result.AutoCollapse);
            result.HoverExpandEnabled = ReadBool(raw, "HoverExpandEnabled", result.HoverExpandEnabled);
            result.HoverDelayMilliseconds = ReadInteger(raw, "HoverDelayMilliseconds", result.HoverDelayMilliseconds,
                Settings.MinimumHoverDelayMilliseconds, Settings.MaximumHoverDelayMilliseconds);
            result.LaunchAtLogin = ReadBool(raw, "LaunchAtLogin", result.LaunchAtLogin);
            result.NotificationsEnabled = ReadBool(raw, "NotificationsEnabled", result.NotificationsEnabled);
            result.NotificationPreview = ReadBool(raw, "NotificationPreview", result.NotificationPreview);
            result.HideNotificationBody = ReadBool(raw, "HideNotificationBody", result.HideNotificationBody);
            result.QuietPreviews = ReadBool(raw, "QuietPreviews", result.QuietPreviews);
            result.AlwaysOnTop = ReadBool(raw, "AlwaysOnTop", result.AlwaysOnTop);
            result.OpacityPercent = ReadInteger(raw, "OpacityPercent", 100, 60, 100);
            string compactMode = ReadString(raw, "CompactMode", "quota"); if (compactMode == "quota" || compactMode == "clock" || compactMode == "focus" || compactMode == "music") result.CompactMode = compactMode;
            string[] widgetKeys = CompactWidgetCatalog.Keys;
            string first = ReadString(raw, "CompactSlot1", result.CompactSlot1), second = ReadString(raw, "CompactSlot2", result.CompactSlot2);
            if (Array.IndexOf(widgetKeys, first) >= 0) result.CompactSlot1 = first;
            if (Array.IndexOf(widgetKeys, second) >= 0) result.CompactSlot2 = second;
            if (ReadString(raw, "Language", "zh") == "en") result.Language = "en";
            if (ReadString(raw, "Theme", "dark") == "light") result.Theme = "light";
            result.HiddenTools = ReadString(raw, "HiddenTools", "");
            result.FavoriteTools = ToolCatalog.NormalizeFavorites(ReadString(raw, "FavoriteTools", result.FavoriteTools));
            string lastTool = ReadString(raw, "LastTool", "focus"); result.LastTool = ToolCatalog.Contains(lastTool) ? lastTool : "focus";
            result.RefreshSeconds = ReadInteger(raw, "RefreshSeconds", result.RefreshSeconds, 30, 600);
            result.AlertThreshold = ReadInteger(raw, "AlertThreshold", result.AlertThreshold, 1, 100);
            object value;
            if (raw.TryGetValue("HorizontalPosition", out value) && IsNumber(value))
            {
                double position = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (!Double.IsNaN(position) && !Double.IsInfinity(position)) result.HorizontalPosition = Math.Max(0.0, Math.Min(1.0, position));
            }
            if (raw.TryGetValue("VerticalPosition", out value) && IsNumber(value))
            {
                double position = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (!Double.IsNaN(position) && !Double.IsInfinity(position))
                    result.VerticalPosition = Math.Max(0.0, Math.Min(1.0, position));
            }
            return result;
        }

        private static string ReadString(IDictionary<string, object> raw, string key, string fallback)
        {
            object value;
            return raw.TryGetValue(key, out value) && value is string ? (string)value : fallback;
        }

        private static bool ReadBool(IDictionary<string, object> raw, string key, bool fallback)
        {
            object value;
            return raw.TryGetValue(key, out value) && value is bool ? (bool)value : fallback;
        }

        private static int ReadInteger(IDictionary<string, object> raw, string key, int fallback, int minimum, int maximum)
        {
            object value;
            if (!raw.TryGetValue(key, out value) || !(value is int || value is long)) return fallback;
            long number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return (int)Math.Max(minimum, Math.Min(maximum, number));
        }

        private static bool IsNumber(object value)
        {
            return value is int || value is long || value is double || value is decimal || value is float;
        }
    }
}
