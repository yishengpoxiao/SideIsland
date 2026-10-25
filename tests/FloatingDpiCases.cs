using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    // Extract the actual full-screen avoidance strip through its production
    // builder, then render that same Border offline at four WPF DPI values.
    // The short-lived demo owner has isolated data and is hidden immediately.
    internal static class FloatingDpiCases
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect bounds);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
        private static T Read<T>(object instance, string field) { return (T)instance.GetType().GetField(field, Private).GetValue(instance); }
        private static object Call(object instance, string method, params object[] arguments) { return instance.GetType().GetMethod(method, Private).Invoke(instance, arguments); }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Slim floating DPI: " + message); }

        internal static void Run(string directory = null)
        {
            string previousData = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), language = UiLocalization.Language, theme = UiTheme.Name;
            string folder = Path.Combine(Path.GetTempPath(), "IslandSlimDpi." + Guid.NewGuid().ToString("N"));
            var evidence = new List<string>(); int nativeCases = 0, renders = 0;
            IslandWindow owner = null; Window slim = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                foreach (string locale in new[] { "zh", "en" }) foreach (string appearance in new[] { "light", "dark" })
                {
                    new SettingsStore().Save(new Settings { Language = locale, Theme = appearance, NotificationsEnabled = false, AlwaysOnTop = false, AutoCollapse = false, HoverExpandEnabled = false, AutoMusic = false });
                    AppAppearance.Configure(locale, appearance);
                    owner = new IslandWindow(true) { ShowActivated = false, Opacity = 0, IsHitTestVisible = false };
                    owner.Show(); owner.UpdateLayout(); PopupDpiTests.Pump(); Stop(owner); owner.Hide(); PopupDpiTests.Pump();
                    Check(PresentationSource.FromVisual(owner) is HwndSource, "hidden demo owner retains its native DPI source for physical/DIP monitor conversion");
                    var settings = Read<Settings>(owner, "settings"); settings.HorizontalPosition = .5; settings.VerticalPosition = .4;
                    Border strip = null;
                    foreach (string side in new[] { "left", "right", "top", "bottom" })
                    {
                        settings.Side = side; Call(owner, "ShowDesktopSlim"); slim = Read<Window>(owner, "desktopSlimWindow");
                        slim.UpdateLayout(); PopupDpiTests.PumpRendering();
                        strip = (Border)slim.Content; bool horizontal = side == "top" || side == "bottom";
                        double width = horizontal ? 76 : 9, height = horizontal ? 9 : 76;
                        Check(Math.Abs(slim.Width - width) <= 1.01 && Math.Abs(slim.Height - height) <= 1.01, "actual production strip retains the 76 by 9 DIP target after native pixel rounding for " + side + " (" + slim.Width + "x" + slim.Height + ")");
                        Check(slim.ShowActivated == false && slim.ShowInTaskbar == false && slim.Topmost, "native strip keeps its nonactivating floating behavior");
                        Check(Convert.ToString(strip.ToolTip) == UiLocalization.Localize("点击恢复灵动岛"), "actual strip tooltip follows " + locale);
                        Rect area = (Rect)Call(owner, "WorkArea");
                        Check(slim.Left >= area.Left && slim.Top >= area.Top && slim.Left + width <= area.Right + .1 && slim.Top + height <= area.Bottom + .1, "strip stays inside its monitor work area for " + side);
                        Check(Math.Abs((side == "left" ? slim.Left - area.Left : side == "right" ? area.Right - slim.Left - width : side == "top" ? slim.Top - area.Top : area.Bottom - slim.Top - height) - 1) <= 1.01, "strip keeps its one-DIP target inset after native pixel rounding for " + side);
                        IntPtr handle = new WindowInteropHelper(slim).Handle; NativeRect bounds = new NativeRect(); Check(handle != IntPtr.Zero && GetWindowRect(handle, out bounds), "actual strip exposes a native rectangle");
                        var source = (HwndSource)PresentationSource.FromVisual(slim); Matrix transform = source.CompositionTarget.TransformToDevice;
                        Check(Math.Abs(bounds.Right - bounds.Left - width * transform.M11) < 2 && Math.Abs(bounds.Bottom - bounds.Top - height * transform.M22) < 2, "actual native dimensions match the strip's WPF DPI transform");
                        evidence.Add(locale + "/" + appearance + "/" + side + "; Native=" + (bounds.Right - bounds.Left) + "x" + (bounds.Bottom - bounds.Top) + "; " + PopupDpiTests.Describe(strip)); nativeCases++;
                        slim.Hide();
                    }
                    IntPtr closedHandle = new WindowInteropHelper(slim).Handle; slim.Content = null; slim.Close(); slim = null; PopupDpiTests.Pump(); Check(!IsWindow(closedHandle), "actual strip HWND is released before offline DPI rendering");
                    owner.GetType().GetField("desktopSlimWindow", Private).SetValue(owner, null);
                    Stop(owner); owner.Close(); owner = null; PopupDpiTests.Pump();
                    foreach (string side in new[] { "left", "right", "top", "bottom" }) foreach (int dpi in PopupDpiTests.Values)
                    {
                        bool horizontal = side == "top" || side == "bottom"; double width = horizontal ? 76 : 9, height = horizontal ? 9 : 76;
                        PopupDpiTests.Apply(strip, dpi); strip.Measure(new Size(width, height)); strip.Arrange(new Rect(0, 0, width, height)); strip.UpdateLayout(); PopupDpiTests.Pump();
                        RenderTargetBitmap bitmap = PopupDpiTests.Render(strip, dpi); PopupDpiTests.Verify(strip, dpi, "actual slim " + side);
                        Check(strip.CornerRadius.TopLeft == 5 && strip.BorderThickness.Left == 1, "actual rounded strip styling remains intact at " + dpi);
                        Check(Alpha(bitmap, 0, 0) < 255 && Alpha(bitmap, bitmap.PixelWidth / 2, bitmap.PixelHeight / 2) > 240, "rounded strip has transparent corners and a solid center at " + dpi);
                        if (!String.IsNullOrEmpty(directory)) PopupDpiTests.Save(strip, directory, "slim-" + locale + "-" + appearance + "-" + side + "-" + dpi, dpi);
                        evidence.Add(locale + "/" + appearance + "/" + side + "/" + dpi + "; Raster=" + bitmap.PixelWidth + "x" + bitmap.PixelHeight + "; " + PopupDpiTests.Describe(strip)); renders++;
                    }
                }
                if (!String.IsNullOrEmpty(directory)) File.WriteAllLines(Path.Combine(directory, "measurements.txt"), evidence);
                Console.WriteLine("PASS: " + nativeCases + " actual full-screen strip native positions and " + renders + " WPF DPI renders; four edges, zh/en, light/dark; actual production Border extracted without duplicating its style; own HWNDs closed; no screen capture or external input.");
            }
            finally
            {
                if (slim != null) slim.Close();
                if (owner != null) { Stop(owner); owner.Close(); }
                AppAppearance.Configure(language, theme); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previousData);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
        private static byte Alpha(BitmapSource bitmap, int x, int y) { var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel[3]; }
        private static void Stop(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) { var timer = field.GetValue(window) as DispatcherTimer; if (timer != null) timer.Stop(); } }
    }
    internal static class FloatingDpiCaseHost
    {
        [STAThread] private static int Main(string[] args)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try { FloatingDpiCases.Run(args.Length == 0 ? null : args[0]); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { application.Shutdown(); }
        }
    }
}
