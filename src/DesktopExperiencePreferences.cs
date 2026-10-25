using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Input;
using InputKey = System.Windows.Input.Key;

namespace CodexIsland
{
    internal sealed class FullscreenApplicationRule
    {
        public string ProcessName = "", Mode = "default";
        internal static string NormalizeProcess(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            string name = value.Trim();
            if (name.Length == 0 || name.Length > 132 || name.Any(c => Char.IsControl(c) || "\\/:*?\"<>|".IndexOf(c) >= 0)) return "";
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".com", StringComparison.OrdinalIgnoreCase)) name += ".exe";
            if (name.Length > 132) return "";
            return name.ToLowerInvariant();
        }
    }
    internal sealed class MonitorAnchor
    {
        public string Device = "", Side = "right";
        public double X = .5, Y = .4;
        public bool Free;
    }

    internal sealed class DesktopExperiencePreferences
    {
        public string FullscreenMode = "off", ClickAction = "default", MiddleAction = "none", WheelAction = "default";
        public bool RememberMonitors = true;
        public string ToggleHotkey = "Ctrl+Alt+Space", PrivacyHotkey = "Ctrl+Alt+P", SearchHotkey = "", CaptureHotkey = "";
        public List<MonitorAnchor> Monitors = new List<MonitorAnchor>();
        public List<FullscreenApplicationRule> FullscreenRules = new List<FullscreenApplicationRule>();
        internal DesktopExperiencePreferences Copy() { return new JavaScriptSerializer().Deserialize<DesktopExperiencePreferences>(new JavaScriptSerializer().Serialize(this)); }
        internal void Validate()
        {
            if (FullscreenMode != "hide" && FullscreenMode != "slim") FullscreenMode = "off";
            ClickAction = GestureActionCatalog.Normalize(ClickAction, "default"); MiddleAction = GestureActionCatalog.Normalize(MiddleAction, "none"); WheelAction = GestureActionCatalog.Normalize(WheelAction, "default");
            Monitors = (Monitors ?? new List<MonitorAnchor>()).Where(x => x != null && !String.IsNullOrWhiteSpace(x.Device) && x.Device.Length <= 200).GroupBy(x => x.Device, StringComparer.OrdinalIgnoreCase).Select(x => x.Last()).Take(32).ToList();
            foreach (var anchor in Monitors)
            {
                if (anchor.Side != "top" && anchor.Side != "bottom" && anchor.Side != "left") anchor.Side = "right";
                anchor.X = SafeFraction(anchor.X, .5); anchor.Y = SafeFraction(anchor.Y, .4);
            }
            FullscreenRules = (FullscreenRules ?? new List<FullscreenApplicationRule>()).Where(rule => rule != null && FullscreenApplicationRule.NormalizeProcess(rule.ProcessName).Length > 0 && new[] { "default", "off", "hide", "slim" }.Contains(rule.Mode)).GroupBy(rule => FullscreenApplicationRule.NormalizeProcess(rule.ProcessName), StringComparer.OrdinalIgnoreCase)
                .Select(group => new FullscreenApplicationRule { ProcessName = FullscreenApplicationRule.NormalizeProcess(group.Last().ProcessName), Mode = group.Last().Mode }).Take(64).ToList();
            ToggleHotkey = Limit(ToggleHotkey); PrivacyHotkey = Limit(PrivacyHotkey); SearchHotkey = Limit(SearchHotkey); CaptureHotkey = Limit(CaptureHotkey);
        }
        private static string Limit(string text) { return text == null ? "" : text.Trim().Substring(0, Math.Min(80, text.Trim().Length)); }
        private static double SafeFraction(double value, double fallback) { return Double.IsNaN(value) || Double.IsInfinity(value) ? fallback : Math.Max(0, Math.Min(1, value)); }
        internal void Remember(string device, Settings settings)
        {
            if (!RememberMonitors || String.IsNullOrWhiteSpace(device)) return;
            var anchor = Monitors.FirstOrDefault(x => String.Equals(x.Device, device, StringComparison.OrdinalIgnoreCase));
            if (anchor == null) { if (Monitors.Count >= 32) Monitors.RemoveAt(0); anchor = new MonitorAnchor { Device = device }; Monitors.Add(anchor); }
            anchor.Side = settings.Side; anchor.X = settings.HorizontalPosition; anchor.Y = settings.VerticalPosition; anchor.Free = settings.FreePosition;
        }
        internal bool Restore(string device, Settings settings)
        {
            if (!RememberMonitors) return false;
            var anchor = Monitors.FirstOrDefault(x => String.Equals(x.Device, device, StringComparison.OrdinalIgnoreCase));
            if (anchor == null) return false;
            settings.Side = anchor.Side; settings.HorizontalPosition = anchor.X; settings.VerticalPosition = anchor.Y; settings.FreePosition = anchor.Free; return true;
        }
    }

    internal sealed class DesktopExperienceStore
    {
        private readonly string path;
        private bool unreadable;
        internal DesktopExperienceStore(string location = null) { path = Path.GetFullPath(location ?? Path.Combine(Path.GetDirectoryName(new SettingsStore().FilePath), "desktop-experience.json")); }
        internal DesktopExperiencePreferences Load()
        {
            try
            {
                if (!File.Exists(path)) return new DesktopExperiencePreferences();
                if (new FileInfo(path).Length > 128 * 1024) throw new IOException();
                var result = new JavaScriptSerializer().Deserialize<DesktopExperiencePreferences>(File.ReadAllText(path, new UTF8Encoding(false, true)));
                if (result == null) throw new IOException(); result.Validate(); return result;
            }
            catch { unreadable = File.Exists(path); return new DesktopExperiencePreferences(); }
        }
        internal bool TrySave(DesktopExperiencePreferences value)
        {
            if (unreadable) return false;
            string temporary = null;
            try
            {
                var safe = value.Copy(); safe.Validate(); Directory.CreateDirectory(Path.GetDirectoryName(path));
                temporary = Path.Combine(Path.GetDirectoryName(path), ".desktop-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(safe) + Environment.NewLine); file.Write(bytes, 0, bytes.Length); file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); return true;
            }
            catch { return false; }
            finally { if (temporary != null && File.Exists(temporary)) try { File.Delete(temporary); } catch { } }
        }
    }

    internal static class GestureActionCatalog
    {
        internal static readonly string[] Keys = { "default", "none", "expand", "focus", "screenpin", "mute-output", "mute-mic", "search", "media-toggle", "media-previous", "media-next", "volume", "launcher" };
        internal static readonly string[] Labels = { "组件默认操作", "不执行操作", "展开／收起", "开始／暂停专注", "截图钉屏", "输出静音切换", "麦克风静音切换", "搜索功能", "音乐播放／暂停", "上一首", "下一首", "调整输出音量", "打开快捷启动" };
        internal static string Normalize(string key, string fallback) { return Keys.Contains(key) ? key : fallback; }
    }

    internal sealed class HotkeyChord
    {
        internal uint Modifiers, Key;
        internal string Text;
        internal static bool TryParse(string value, out HotkeyChord chord)
        {
            chord = null; if (String.IsNullOrWhiteSpace(value)) return true;
            string[] parts = value.Split('+'); uint modifiers = 0; InputKey key = InputKey.None;
            foreach (string raw in parts)
            {
                string part = raw.Trim();
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) modifiers |= HotkeyRegistration.Control;
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= HotkeyRegistration.Alt;
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= HotkeyRegistration.Shift;
                else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)) modifiers |= 8;
                else
                {
                    if (key != InputKey.None || part.Length == 0) return false;
                    try { key = (InputKey)new KeyConverter().ConvertFromInvariantString(part); } catch { return false; }
                }
            }
            int native = KeyInterop.VirtualKeyFromKey(key);
            if (key == InputKey.None || native <= 0 || native > 255 || key == InputKey.LeftCtrl || key == InputKey.RightCtrl || key == InputKey.LeftAlt || key == InputKey.RightAlt || key == InputKey.LeftShift || key == InputKey.RightShift || key == InputKey.LWin || key == InputKey.RWin || (modifiers == 0 && (key < InputKey.F1 || key > InputKey.F24))) return false;
            chord = new HotkeyChord { Modifiers = modifiers, Key = (uint)native, Text = value.Trim() }; return true;
        }
        internal static bool TryRead(DesktopExperiencePreferences value, out Dictionary<string, HotkeyChord> bindings, out string error)
        {
            bindings = new Dictionary<string, HotkeyChord>(); error = "";
            var texts = new Dictionary<string, string> { { "toggle", value.ToggleHotkey }, { "privacy", value.PrivacyHotkey }, { "search", value.SearchHotkey }, { "screenpin", value.CaptureHotkey } };
            var used = new HashSet<string>();
            foreach (var item in texts)
            {
                HotkeyChord chord; if (!TryParse(item.Value, out chord)) { error = "快捷键格式无效，请使用 Ctrl+Alt+Space 等组合。"; return false; }
                if (chord == null) continue;
                if (!used.Add(chord.Modifiers.ToString(CultureInfo.InvariantCulture) + ":" + chord.Key.ToString(CultureInfo.InvariantCulture))) { error = "多个操作不能使用相同快捷键。"; return false; }
                bindings.Add(item.Key, chord);
            }
            return true;
        }
    }
}
