using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;

namespace CodexIsland
{
    internal static partial class UiTheme
    {
        private sealed class ThemeBrush
        {
            internal readonly SolidColorBrush Dark, Light;
            internal ThemeBrush(Color source)
            {
                Dark = Snapshot(Map(source, false)); Light = Snapshot(Map(source, true));
            }
            private static SolidColorBrush Snapshot(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
        }
        private static readonly Dictionary<string, ThemeBrush> Palette = new Dictionary<string, ThemeBrush>(StringComparer.OrdinalIgnoreCase);
        // Keep the original semantic role: equal dark colors may have different
        // light colors. Exactly two immutable instances are retained per role.
        private static readonly Dictionary<Brush, string> BrushRoles = new Dictionary<Brush, string>();
        private static readonly List<WeakReference> InstalledResources = new List<WeakReference>();
        private static readonly DependencyProperty ProtectedColorsProperty = DependencyProperty.RegisterAttached("ProtectedColors", typeof(bool), typeof(UiTheme), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
        private static readonly object InstalledKey = new object();
        private static string name = "dark";
        internal static string Name { get { return name; } }
        internal static bool IsLight { get { return name == "light"; } }
        internal static bool IsColorProtected(DependencyObject target) { return target != null && (bool)target.GetValue(ProtectedColorsProperty); }
        internal static T ProtectColor<T>(T target) where T : DependencyObject { target.SetValue(ProtectedColorsProperty, true); return target; }
        internal static T Protect<T>(T target) where T : DependencyObject { return ProtectColor(target); }
        internal static Brush Brush(string hex)
        {
            ThemeBrush value;
            if (!Palette.TryGetValue(hex, out value))
            {
                value = new ThemeBrush((Color)ColorConverter.ConvertFromString(hex)); Palette[hex] = value;
                BrushRoles.Add(value.Dark, hex); BrushRoles.Add(value.Light, hex);
            }
            return IsLight ? value.Light : value.Dark;
        }
        internal static void Configure(string theme)
        {
            string next = String.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
            if (name == next) return;
            name = next;
            var seen = new HashSet<ResourceDictionary>();
            for (int index = InstalledResources.Count - 1; index >= 0; index--)
            {
                var resources = InstalledResources[index].Target as ResourceDictionary;
                if (resources == null) InstalledResources.RemoveAt(index); else RefreshResourceBrushes(resources, seen);
            }
        }
        internal static void RefreshLiteralBrushes(DependencyObject target)
        {
            if (target == null || IsColorProtected(target) || (target is Freezable && ((Freezable)target).IsFrozen)) return;
            // Snapshot before SetCurrentValue: changing a local DP invalidates
            // the LocalValueEnumerator. Expressions are intentionally left to
            // their binding/resource source rather than replaced with literals.
            var values = new List<LocalValueEntry>();
            var local = target.GetLocalValueEnumerator(); while (local.MoveNext()) values.Add(local.Current);
            foreach (var entry in values)
            {
                if (entry.Property.ReadOnly) continue;
                var previous = entry.Value as Brush; string role;
                if (previous == null || !BrushRoles.TryGetValue(previous, out role)) continue;
                Brush replacement = Brush(role);
                if (!Object.ReferenceEquals(previous, replacement)) target.SetCurrentValue(entry.Property, replacement);
            }
        }
        internal static void RefreshResourceBrushes(ResourceDictionary resources) { RefreshResourceBrushes(resources, new HashSet<ResourceDictionary>()); }
        private static void RefreshResourceBrushes(ResourceDictionary resources, HashSet<ResourceDictionary> seen)
        {
            if (resources == null || !seen.Add(resources)) return;
            var keys = new List<object>(); foreach (object key in resources.Keys) keys.Add(key);
            foreach (object key in keys)
            {
                var previous = resources[key] as Brush; string role;
                if (previous == null || !BrushRoles.TryGetValue(previous, out role)) continue;
                Brush replacement = Brush(role);
                if (!Object.ReferenceEquals(previous, replacement)) resources[key] = replacement;
            }
            foreach (var merged in resources.MergedDictionaries) RefreshResourceBrushes(merged, seen);
        }
        private static Color Map(Color source, bool light)
        {
            Color chrome;
            if (TryChrome(source, light, out chrome)) return chrome;
            if (!light || source.A == 0) return source;
            double luminance = .2126 * source.R + .7152 * source.G + .0722 * source.B;
            int spread = Math.Max(source.R, Math.Max(source.G, source.B)) - Math.Min(source.R, Math.Min(source.G, source.B));
            // This palette contains application chrome only. Image/QR pixels and
            // user-selected color swatches are not passed through this service.
            if (luminance < 28) return Color.FromArgb(source.A, 247, 248, 250);
            if (luminance < 43) return Color.FromArgb(source.A, 240, 243, 246);
            if (luminance < 64 && spread < 35) return Color.FromArgb(source.A, 215, 222, 229);
            if (luminance < 95)
            {
                // Tinted difference backgrounds and selected buttons retain
                // their meaning with lighter surfaces in the light theme.
                if (source.R > source.G * 1.35) return Color.FromArgb(source.A, 250, 222, 224);
                if (source.R > source.G && source.G > source.B * 1.2) return Color.FromArgb(source.A, 253, 236, 205);
                return Color.FromArgb(source.A, 197, 226, 211);
            }
            if (spread < 35) return luminance >= 190 ? Color.FromArgb(source.A, 37, 49, 64) : Color.FromArgb(source.A, 84, 101, 118);
            // Saturated accents become dark enough for text over light cards.
            double factor = luminance > 150 ? .42 : .56;
            return Color.FromArgb(source.A, (byte)Math.Min(125, source.R * factor), (byte)Math.Min(125, source.G * factor), (byte)Math.Min(125, source.B * factor));
        }
        private static bool TryChrome(Color source, bool isLight, out Color color)
        {
            // Only known application chrome is aliased here. Saturated status,
            // diff and accent colors retain their meaning; image pixels and
            // user-selected swatches never pass through this service.
            int value = (source.R << 16) | (source.G << 8) | source.B;
            int dark, light;
            switch (value)
            {
                case 0x111518: dark = 0x111113; light = 0xF2F2F7; break;
                case 0x111A16: dark = 0x171719; light = 0xF7F7FA; break;
                case 0x1A2323: case 0x1A2023: case 0x1A2425: case 0x19211D: case 0x17201D: case 0x192220: dark = 0x1C1C1E; light = 0xFFFFFF; break;
                case 0x202D29: case 0x1D2827: case 0x202C27: dark = 0x2C2C2E; light = 0xF2F2F7; break;
                case 0x1E2725: dark = 0x242426; light = 0xE9E9EF; break;
                case 0x27312F: dark = 0x323236; light = 0xEDEDF3; break;
                case 0x1B2824: dark = 0x1B2636; light = 0xEEF5FF; break;
                case 0x3A5145: case 0x384843: case 0x293A32: case 0x34473D: dark = 0x3A3A3C; light = 0xDDDEE5; break;
                case 0x252F31: case 0x2B3336: dark = 0x2D2D30; light = 0xE9E9EF; break;
                case 0x30383B: case 0x2A3033: case 0x2E3C34: dark = 0x3A3A3C; light = 0xE4E4EA; break;
                case 0x31594B: case 0x355448: case 0x3B6755: case 0x365649: dark = 0x24456B; light = 0xDFEDFF; break;
                case 0xA1EACF: case 0x9BE9CA: dark = 0x78BFFF; light = 0x007AFF; break;
                case 0x007AFF: dark = 0x0A84FF; light = 0x007AFF; break;
                case 0x2E3844: dark = 0x3A3A40; light = 0xE2E2E9; break;
                case 0x3A4654: dark = 0x48484F; light = 0xD4D4DF; break;
                case 0x72C9A8: dark = 0x78BFFF; light = 0x007AFF; break;
                case 0xE3EFE7: case 0xE1ECE5: case 0xE0ECE5: case 0xDDE9E2: case 0xDCEAE2:
                case 0xE5F2E9: case 0xE3EDE7: case 0xE2EFE6: dark = 0xEBEBF0; light = 0x1C1C1E; break;
                case 0xF0F5F2: case 0xEFF8F3: case 0xEDF3F0: dark = 0xF5F5F7; light = 0x111113; break;
                case 0xB9D5C9: case 0xB1CBBB: case 0xC7E9D7: dark = 0xCECED6; light = 0x414149; break;
                case 0xD6E5DB: case 0xD4E6DC: dark = 0xE1E1E6; light = 0x2C2C2E; break;
                case 0x879690: case 0x879D90: case 0x8FA89A: case 0x8EA799: case 0x91A29B:
                case 0x85928E: case 0x84998D: case 0x8C9994: case 0x95A69E: dark = 0x98989F; light = 0x6B6B73; break;
                case 0x748A7E: case 0x799184: case 0x717B7E: dark = 0x85858F; light = 0x74747D; break;
                default: color = source; return false;
            }
            int selected = isLight ? light : dark;
            color = Color.FromArgb(source.A, (byte)(selected >> 16), (byte)(selected >> 8), (byte)selected); return true;
        }
        internal static void Prepare(FrameworkElement element)
        {
            if (element == null || (bool)element.GetValue(ProtectedColorsProperty)) return;
            var option = element as System.Windows.Controls.Primitives.ToggleButton;
            if ((option is CheckBox || option is RadioButton) && option.Content is string && option.ContentTemplate == null)
                PanelActionLayout.Caption(option);
            var control = element as Control;
            if (control != null)
            {
                if (Object.ReferenceEquals(control.Foreground, Brushes.White) || Object.ReferenceEquals(control.Foreground, Brushes.Black)) control.SetCurrentValue(Control.ForegroundProperty, Brush("#E3EFE7"));
                var box = control as TextBox; if (box != null && (Object.ReferenceEquals(box.CaretBrush, Brushes.White) || Object.ReferenceEquals(box.CaretBrush, Brushes.Black))) box.SetCurrentValue(TextBox.CaretBrushProperty, Brush("#E3EFE7"));
            }
            var label = element as TextBlock;
            if (label != null && (Object.ReferenceEquals(label.Foreground, Brushes.White) || Object.ReferenceEquals(label.Foreground, Brushes.Black))) label.SetCurrentValue(TextBlock.ForegroundProperty, Brush("#E3EFE7"));
        }
        internal static void Install(ResourceDictionary resources)
        {
            InstallMenuSupport();
            InstallToolTipSupport();
            if (resources.Contains(InstalledKey)) { RefreshResourceBrushes(resources); return; }
            resources[InstalledKey] = true; InstalledResources.Add(new WeakReference(resources));
            resources["Island.Surface"] = Brush("#111518"); resources["Island.Card"] = Brush("#1A2323"); resources["Island.Input"] = Brush("#202D29"); resources["Island.Border"] = Brush("#3A5145");
            resources["Island.Text"] = Brush("#E3EFE7"); resources["Island.Muted"] = Brush("#8FA89A"); resources["Island.Accent"] = Brush("#A1EACF"); resources["Island.Selection"] = Brush("#31594B");
            resources["Island.Hover"] = Brush("#2E3844"); resources["Island.Pressed"] = Brush("#3A4654"); resources["Island.Focus"] = Brush("#72C9A8"); resources["Island.Track"] = Brush("#30383B");
            resources["Island.ControlAccent"] = Brush("#007AFF");
            resources[SystemColors.WindowBrushKey] = Brush("#202D29"); resources[SystemColors.WindowTextBrushKey] = Brush("#E3EFE7"); resources[SystemColors.ControlBrushKey] = Brush("#1A2323"); resources[SystemColors.ControlTextBrushKey] = Brush("#E3EFE7");
            resources[SystemColors.HighlightBrushKey] = Brush("#31594B"); resources[SystemColors.HighlightTextBrushKey] = Brush("#E3EFE7"); resources[SystemColors.MenuBrushKey] = Brush("#1A2323"); resources[SystemColors.MenuTextBrushKey] = Brush("#E3EFE7"); resources[SystemColors.GrayTextBrushKey] = Brush("#8FA89A");
            var styles = (ResourceDictionary)XamlReader.Parse(Styles);
            resources.MergedDictionaries.Add(styles);
        }
        // These forms provide wrapped item captions. Keep other selectors' raw
        // process, device and project labels horizontally accessible as before.
        internal static ComboBox CompactChoice(ComboBox control)
        {
            control.DropDownOpened += (s, e) => {
                var popup = control.Template.FindName("PART_Popup", control) as System.Windows.Controls.Primitives.Popup;
                var frame = popup == null ? null : popup.Child as Border;
                if (frame == null) return;
                frame.SetBinding(FrameworkElement.WidthProperty, new Binding("ActualWidth") { Source = control });
                var scroll = frame.Child as ScrollViewer;
                if (scroll != null) scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            };
            return control;
        }
    }
}
