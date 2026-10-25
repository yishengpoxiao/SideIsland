using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    internal static class ScreenPinTools
    {
        internal const long MaximumCapturePixels = 32000000;
        [StructLayout(LayoutKind.Sequential)] internal struct NativePoint { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);

        internal static Point CursorPosition()
        {
            NativePoint point; if (!GetCursorPos(out point)) throw new InvalidOperationException("暂时无法读取鼠标位置。"); return new Point(point.X, point.Y);
        }
        internal static Int32Rect Selection(Point first, Point second, Int32Rect desktop)
        {
            int x = (int)Math.Max(desktop.X, Math.Min(first.X, second.X)), y = (int)Math.Max(desktop.Y, Math.Min(first.Y, second.Y));
            int right = (int)Math.Min((long)desktop.X + desktop.Width, Math.Max(first.X, second.X)), bottom = (int)Math.Min((long)desktop.Y + desktop.Height, Math.Max(first.Y, second.Y));
            return right > x && bottom > y ? new Int32Rect(x, y, right - x, bottom - y) : Int32Rect.Empty;
        }
        internal static void ValidateCapture(Int32Rect bounds)
        {
            if (bounds.IsEmpty || bounds.Width < 2 || bounds.Height < 2 || bounds.Width > 32000 || bounds.Height > 32000 || (long)bounds.Width * bounds.Height > MaximumCapturePixels)
                throw new ArgumentException("请框选有效区域，截图最多支持 3200 万像素。");
        }
        internal static Rect ToOverlay(Int32Rect selected, Int32Rect desktop, double width, double height)
        {
            if (desktop.Width <= 0 || desktop.Height <= 0 || width <= 0 || height <= 0) return Rect.Empty;
            double x = width / desktop.Width, y = height / desktop.Height;
            return new Rect((selected.X - desktop.X) * x, (selected.Y - desktop.Y) * y, selected.Width * x, selected.Height * y);
        }
        internal static BitmapSource Capture(Int32Rect bounds, CancellationToken token)
        {
            ValidateCapture(bounds); token.ThrowIfCancellationRequested();
            using (var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    IntPtr source = GetDC(IntPtr.Zero); if (source == IntPtr.Zero) throw new InvalidOperationException("无法读取屏幕。");
                    IntPtr target = IntPtr.Zero;
                    try { target = graphics.GetHdc(); if (!BitBlt(target, 0, 0, bounds.Width, bounds.Height, source, bounds.X, bounds.Y, 0x40CC0020)) throw new InvalidOperationException("无法读取选中区域。"); }
                    finally { if (target != IntPtr.Zero) graphics.ReleaseHdc(target); ReleaseDC(IntPtr.Zero, source); }
                }
                token.ThrowIfCancellationRequested(); IntPtr handle = bitmap.GetHbitmap();
                try { var image = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); image.Freeze(); token.ThrowIfCancellationRequested(); return image; }
                finally { DeleteObject(handle); }
            }
        }
    }
}
