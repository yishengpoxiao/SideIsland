using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    internal static class SettingsBackupSchema
    {
        internal static readonly HashSet<string> MainKeys = Keys("Side ScreenName VerticalPosition HorizontalPosition FreePosition AutoSnapToEdge CompactTextDirection CustomExpandedSize ExpandedWidth ExpandedHeight RefreshSeconds AutoCollapse HoverExpandEnabled HoverDelayMilliseconds AlertThreshold NotificationsEnabled NotificationPreview HideNotificationBody QuietPreviews AlwaysOnTop OpacityPercent CompactMode CompactSlot1 CompactSlot2 Language Theme FocusMinutes AutoMusic OnlineLyricsEnabled HiddenTools FavoriteTools LastTool");
        private static readonly HashSet<string> DesktopKeys = Keys("FullscreenMode ClickAction MiddleAction WheelAction RememberMonitors ToggleHotkey PrivacyHotkey SearchHotkey CaptureHotkey Monitors FullscreenRules");
        private static readonly HashSet<string> ProfileKeys = Keys("Id Name Mode Slot1 Slot2 Tool Favorites Quiet AutoMusic");
        private static readonly HashSet<string> LayoutKeys = Keys("Profiles ActiveId Previous RestoreAfterFocus FocusRestoreArmed");
        private static readonly HashSet<string> NotificationKeys = Keys("Version MergeDuplicates Sources");
        private static readonly HashSet<string> SourceKeys = Keys("AppId AppName Blocked Priority");
        private static readonly HashSet<string> MusicKeys = Keys("LyricFontSize LyricOffsetMilliseconds PreferredSourceId");
        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = SettingsBackupService.MaximumBytes, RecursionLimit = 32 }; }
        internal static Dictionary<string, object> Object(string json)
        {
            var value = Json().DeserializeObject(json) as Dictionary<string, object>;
            if (value == null) Invalid(); return value;
        }
        internal static string Clean(string name, string json, bool exporting)
        {
            var value = Object(json);
            if (name == "settings.json") MainPreferences(value, exporting);
            else if (name == "desktop-experience.json") Desktop(value, exporting);
            else if (name == "layout-profiles.json") Layout(value, exporting);
            else if (name == "notification-sources.json") Notifications(value, exporting);
            else if (name == "music-preferences.json") Music(value, exporting);
            else if (name == "auto-scene-rules.json") AutoRules(value, exporting);
            else if (name == "widget-layouts.json") Widgets(value, exporting);
            else if (name == "task-activity-preferences.json") { Filter(value, Keys("CompactEnabled"), exporting); OptionalBool(value, "CompactEnabled"); }
            else Invalid();
            return Json().Serialize(value);
        }
        private static void MainPreferences(Dictionary<string, object> value, bool exporting)
        {
            Filter(value, MainKeys, exporting); if (value.Count == 0) Invalid();
            foreach (var pair in value)
            {
                Type expected = typeof(Settings).GetField(pair.Key).FieldType;
                if (expected == typeof(bool)) Boolean(pair.Value);
                else if (expected == typeof(int))
                {
                    int minimum = 0, maximum = 0;
                    switch (pair.Key)
                    {
                        case "ExpandedWidth": minimum = 360; maximum = 900; break;
                        case "ExpandedHeight": minimum = 360; maximum = 1100; break;
                        case "RefreshSeconds": minimum = 30; maximum = 600; break;
                        case "HoverDelayMilliseconds": minimum = 100; maximum = 5000; break;
                        case "AlertThreshold": minimum = 1; maximum = 100; break;
                        case "OpacityPercent": minimum = 60; maximum = 100; break;
                        case "FocusMinutes": minimum = 1; maximum = 1440; break;
                        default: Invalid(); break;
                    }
                    Integer(pair.Value, minimum, maximum);
                }
                else if (expected == typeof(double)) Number(pair.Value, 0, 1);
                else Text(pair.Value, pair.Key == "ScreenName" ? 200 : 2048);
            }
            Choice(value, "Side", "left", "right", "top", "bottom"); Choice(value, "CompactTextDirection", "auto", "rotate180", "mirror");
            Choice(value, "CompactMode", "quota", "clock", "focus", "music"); Choice(value, "Language", "zh", "en"); Choice(value, "Theme", "light", "dark");
            Choice(value, "CompactSlot1", CompactWidgetCatalog.Keys); Choice(value, "CompactSlot2", CompactWidgetCatalog.Keys);
            Tool(value, "LastTool"); ToolList(value, "HiddenTools", ToolCatalog.All.Length); ToolList(value, "FavoriteTools", ToolCatalog.MaximumFavorites);
        }
        private static void Desktop(Dictionary<string, object> value, bool exporting)
        {
            Filter(value, DesktopKeys, exporting); if (value.Count == 0) Invalid();
            Choice(value, "FullscreenMode", "off", "hide", "slim");
            foreach (string key in new[] { "ClickAction", "MiddleAction", "WheelAction" }) Choice(value, key, GestureActionCatalog.Keys);
            OptionalBool(value, "RememberMonitors");
            foreach (string key in new[] { "ToggleHotkey", "PrivacyHotkey", "SearchHotkey", "CaptureHotkey" }) if (value.ContainsKey(key)) Text(value[key], 80);
            var monitors = List(value, "Monitors", 32, false); var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (monitors != null) foreach (object raw in monitors)
            {
                var monitor = Object(raw); Filter(monitor, Keys("Device Side X Y Free"), exporting);
                string device = RequiredText(monitor, "Device", 200); if (String.IsNullOrWhiteSpace(device) || !devices.Add(device)) Invalid();
                Choice(monitor, "Side", "left", "right", "top", "bottom"); OptionalBool(monitor, "Free");
                foreach (string key in new[] { "X", "Y" }) if (monitor.ContainsKey(key)) Number(monitor[key], 0, 1);
            }
            var rules = List(value, "FullscreenRules", 64, false); var processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rules != null) foreach (object raw in rules)
            {
                var rule = Object(raw); Filter(rule, Keys("ProcessName Mode"), exporting);
                string process = RequiredText(rule, "ProcessName", 132); string normalized = FullscreenApplicationRule.NormalizeProcess(process);
                if (normalized.Length == 0 || !processes.Add(normalized) || !rule.ContainsKey("Mode")) Invalid();
                Choice(rule, "Mode", "default", "off", "hide", "slim"); rule["ProcessName"] = normalized;
            }
            var preferences = Json().Deserialize<DesktopExperiencePreferences>(Json().Serialize(value));
            Dictionary<string, HotkeyChord> chords; string error;
            if (!HotkeyChord.TryRead(preferences, out chords, out error)) throw new ArgumentException(error);
        }
        private static void Layout(Dictionary<string, object> value, bool exporting)
        {
            Filter(value, LayoutKeys, exporting); var profiles = List(value, "Profiles", 20, true); var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object raw in profiles)
            {
                var profile = Object(raw); Filter(profile, ProfileKeys, exporting); ValidateProfile(profile);
                string id = RequiredText(profile, "Id", 32); Guid guid;
                if (!Guid.TryParseExact(id, "N", out guid) || !ids.Add(id)) Invalid();
            }
            if (value.ContainsKey("ActiveId"))
            {
                string id = Text(value["ActiveId"], 32);
                if (id.Length != 0 && !LayoutProfileStore.IsBuiltin(id) && !ids.Contains(id)) Invalid();
            }
            OptionalBool(value, "RestoreAfterFocus"); OptionalBool(value, "FocusRestoreArmed");
            // Recovery state belongs to the running instance, never to another machine.
            value["Previous"] = null; value["FocusRestoreArmed"] = false;
        }
        private static void ValidateProfile(Dictionary<string, object> profile)
        {
            string name = RequiredText(profile, "Name", 60); if (String.IsNullOrWhiteSpace(name)) Invalid();
            Choice(profile, "Mode", "quota", "clock", "focus", "music"); Choice(profile, "Slot1", CompactWidgetCatalog.Keys); Choice(profile, "Slot2", CompactWidgetCatalog.Keys);
            Tool(profile, "Tool"); ToolList(profile, "Favorites", ToolCatalog.MaximumFavorites); OptionalBool(profile, "Quiet"); OptionalBool(profile, "AutoMusic");
        }
        private static void Notifications(Dictionary<string, object> value, bool exporting)
        {
            Filter(value, NotificationKeys, exporting); if (!value.ContainsKey("Version")) Invalid(); Integer(value["Version"], 1, 2); OptionalBool(value, "MergeDuplicates");
            var sources = List(value, "Sources", 500, true); var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (object raw in sources)
            {
                var source = Object(raw); Filter(source, SourceKeys, exporting);
                string app = RequiredText(source, "AppId", 4096), name = RequiredText(source, "AppName", 80);
                if (String.IsNullOrWhiteSpace(app) && (String.IsNullOrWhiteSpace(name) || name == "未知应用")) Invalid();
                string key = String.IsNullOrWhiteSpace(app) ? "name:" + name : "id:" + app; if (!identities.Add(key)) Invalid();
                OptionalBool(source, "Blocked"); if (source.ContainsKey("Priority")) Integer(source["Priority"], 0, 1);
            }
        }
        private static void Music(Dictionary<string, object> value, bool exporting)
        {
            Filter(value, MusicKeys, exporting); if (value.Count == 0) Invalid();
            if (value.ContainsKey("LyricFontSize")) Integer(value["LyricFontSize"], 9, 16);
            if (value.ContainsKey("LyricOffsetMilliseconds")) Integer(value["LyricOffsetMilliseconds"], -10000, 10000);
            if (value.ContainsKey("PreferredSourceId")) Text(value["PreferredSourceId"], 512);
        }
        private static void AutoRules(Dictionary<string, object> value, bool exporting)
        {
            Filter(value, Keys("Enabled Rules"), exporting); OptionalBool(value, "Enabled");
            var rules = List(value, "Rules", 40, true); var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in rules)
            {
                var rule = Object(raw); Filter(rule, Keys("Id Name Executable Start End Monitor Power ProfileId Enabled Priority CooldownSeconds"), exporting);
                RequiredText(rule, "Id", 32); RequiredText(rule, "Name", 60); OptionalBool(rule, "Enabled");
                foreach (string field in new[] { "Executable", "Start", "End", "Monitor", "Power", "ProfileId" }) if (rule.ContainsKey(field)) Text(rule[field], field == "Monitor" || field == "Executable" ? 128 : 64);
                if (rule.ContainsKey("Priority")) Integer(rule["Priority"], 0, 100); if (rule.ContainsKey("CooldownSeconds")) Integer(rule["CooldownSeconds"], 5, 3600);
                var typed = Json().Deserialize<AutoSceneRule>(Json().Serialize(rule)); string error;
                if (!AutoScenePolicy.Validate(typed, out error) || !identities.Add(typed.Id)) Invalid();
            }
        }
        private static void Widgets(Dictionary<string, object> value, bool exporting)
        {
            Filter(value, Keys("Layouts"), exporting); var layouts = List(value, "Layouts", 24, true); var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in layouts)
            {
                var layout = Object(raw); Filter(layout, Keys("ProfileId Enabled RotateSeconds NotificationOverride FocusOverride ActivityOverride NotificationPriority FocusPriority ActivityPriority Cards"), exporting);
                foreach (string field in new[] { "Enabled", "NotificationOverride", "FocusOverride", "ActivityOverride" }) OptionalBool(layout, field);
                RequiredText(layout, "ProfileId", 64); if (layout.ContainsKey("RotateSeconds")) Integer(layout["RotateSeconds"], 0, 60);
                foreach (string field in new[] { "NotificationPriority", "FocusPriority", "ActivityPriority" }) if (layout.ContainsKey(field)) Integer(layout[field], 0, 100);
                foreach (var cardRaw in List(layout, "Cards", 6, true)) Filter(Object(cardRaw), Keys("Id Key Size"), exporting);
                var typed = Json().Deserialize<WidgetLayoutDefinition>(Json().Serialize(layout));
                if (!WidgetLayoutPolicy.Valid(typed) || !identities.Add(typed.ProfileId)) Invalid();
            }
        }
        private static HashSet<string> Keys(string keys) { return new HashSet<string>(keys.Split(' '), StringComparer.Ordinal); }
        private static void Filter(Dictionary<string, object> value, HashSet<string> keys, bool exporting)
        {
            foreach (string key in value.Keys.ToArray()) if (!keys.Contains(key)) { if (!exporting) Invalid(); value.Remove(key); }
        }
        private static Dictionary<string, object> Object(object raw) { var value = raw as Dictionary<string, object>; if (value == null) Invalid(); return value; }
        private static IList List(Dictionary<string, object> value, string key, int maximum, bool required)
        {
            if (!value.ContainsKey(key)) { if (required) Invalid(); return null; }
            var list = value[key] as IList; if (list == null || list.Count > maximum) Invalid(); return list;
        }
        private static bool Boolean(object raw) { if (!(raw is bool)) Invalid(); return (bool)raw; }
        private static void OptionalBool(Dictionary<string, object> value, string key) { if (value.ContainsKey(key)) Boolean(value[key]); }
        private static string RequiredText(Dictionary<string, object> value, string key, int maximum) { if (!value.ContainsKey(key)) Invalid(); return Text(value[key], maximum); }
        private static string Text(object raw, int maximum) { string text = raw as string; if (text == null || text.Length > maximum || text.Any(Char.IsControl)) Invalid(); return text; }
        private static void Integer(object raw, int minimum, int maximum) { if (!(raw is int || raw is long)) Invalid(); long value = Convert.ToInt64(raw, CultureInfo.InvariantCulture); if (value < minimum || value > maximum) Invalid(); }
        private static void Number(object raw, double minimum, double maximum)
        { if (!(raw is int || raw is long || raw is double || raw is decimal || raw is float)) Invalid(); double value = Convert.ToDouble(raw, CultureInfo.InvariantCulture); if (Double.IsNaN(value) || Double.IsInfinity(value) || value < minimum || value > maximum) Invalid(); }
        private static void Choice(Dictionary<string, object> value, string key, params string[] choices) { if (value.ContainsKey(key) && !choices.Contains(Text(value[key], 2048), StringComparer.Ordinal)) Invalid(); }
        private static void Tool(Dictionary<string, object> value, string key) { if (value.ContainsKey(key) && !ToolCatalog.Contains(Text(value[key], 64))) Invalid(); }
        private static void ToolList(Dictionary<string, object> value, string key, int maximum)
        {
            if (!value.ContainsKey(key)) return; string text = Text(value[key], 2048); if (text.Length == 0) return;
            string[] keys = text.Split(','); if (keys.Length > maximum || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length || keys.Any(item => !ToolCatalog.Contains(item))) Invalid();
        }
        private static void Invalid() { throw new ArgumentException("偏好设置字段、类型或范围无效。"); }
    }
}
