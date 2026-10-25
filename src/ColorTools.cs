using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace CodexIsland
{
    internal static class ColorTools
    {
        public static bool TryHex(string input, out Color color)
        {
            color = Colors.Black;
            string hex = (input ?? "").Trim(); if (hex.StartsWith("#")) hex = hex.Substring(1);
            if (hex.Length == 3) hex = new string(new[] { hex[0], hex[0], hex[1], hex[1], hex[2], hex[2] });
            uint value;
            if (hex.Length != 6 || !UInt32.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)) return false;
            color = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value); return true;
        }
        public static string Hex(Color color) { return String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B); }
        public static string Rgb(Color color) { return String.Format(CultureInfo.InvariantCulture, "rgb({0}, {1}, {2})", color.R, color.G, color.B); }
        public static string Hsl(Color color)
        {
            double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min, lightness = (max + min) / 2, hue = 0, saturation = 0;
            if (delta > 0)
            {
                saturation = delta / (1 - Math.Abs(2 * lightness - 1));
                hue = max == r ? ((g - b) / delta) % 6 : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
                hue *= 60; if (hue < 0) hue += 360;
            }
            return String.Format(CultureInfo.InvariantCulture, "hsl({0:0.#}, {1:0.#}%, {2:0.#}%)", hue, saturation * 100, lightness * 100);
        }
        private static double Linear(byte value) { double s = value / 255.0; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        private static double Luminance(Color value) { return .2126 * Linear(value.R) + .7152 * Linear(value.G) + .0722 * Linear(value.B); }
        public static double Contrast(Color first, Color second) { double a = Luminance(first), b = Luminance(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05); }
        public static Color Complement(Color color) { return Color.FromRgb((byte)(255 - color.R), (byte)(255 - color.G), (byte)(255 - color.B)); }

        [StructLayout(LayoutKind.Sequential)] private struct PixelPoint { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out PixelPoint point);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
        // Called only after the user explicitly starts the one-shot countdown.
        public static bool TrySampleCursor(out Color color)
        {
            color = Colors.Black; PixelPoint point;
            if (!GetCursorPos(out point)) return false;
            IntPtr dc = GetDC(IntPtr.Zero); if (dc == IntPtr.Zero) return false;
            try { uint pixel = GetPixel(dc, point.X, point.Y); if (pixel == UInt32.MaxValue) return false; color = Color.FromRgb((byte)pixel, (byte)(pixel >> 8), (byte)(pixel >> 16)); return true; }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }
    }
}
