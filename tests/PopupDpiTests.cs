using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    // SetRootDpi exercises WPF's real text/visual DPI metadata and rendering at
    // 100/125/150/200%. It deliberately does not claim to change monitor DPI,
    // native caption metrics or HWND transforms. The separate host below tests
    // real OS-created HWNDs under explicit unaware/system awareness contexts.
    internal static class PopupDpiTests
    {
        [StructLayout(LayoutKind.Sequential)] private struct ClientCaptureRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr handle, out ClientCaptureRect rectangle);
        internal static readonly int[] Values = { 96, 120, 144, 192 };

        internal static Visual Apply(Visual visual, int dpi)
        {
            if (visual == null) throw new ArgumentNullException("visual");
            if (dpi < 48 || dpi > 480) throw new ArgumentOutOfRangeException("dpi");
            Visual root = Root(visual);
            VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi / 96d, dpi / 96d));
            foreach (var node in Descendants(root).OfType<UIElement>())
            {
                node.InvalidateMeasure(); node.InvalidateArrange(); node.InvalidateVisual();
            }
            var element = root as UIElement;
            if (element != null) element.UpdateLayout();
            Pump();
            // Editors can allocate internal line DrawingVisuals during their
            // first measure. Apply metadata to those newly created children as
            // well; their actual glyph formatter is verified after rendering.
            VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi / 96d, dpi / 96d));
            Verify(root, dpi, "Apply");
            return root;
        }

        internal static void Verify(Visual visual, int dpi, string context)
        {
            foreach (Visual node in Descendants(visual).OfType<Visual>())
            {
                DpiScale value = VisualTreeHelper.GetDpi(node);
                if (Math.Abs(value.PixelsPerInchX - dpi) > .01 || Math.Abs(value.PixelsPerInchY - dpi) > .01)
                    throw new Exception("Popup WPF DPI: " + context + "; " + node.GetType().Name + " retained " + value.PixelsPerInchX + "/" + value.PixelsPerInchY + " instead of " + dpi);
            }
        }

        internal static int VerifyGlyphDpi(Visual visual, int dpi, string context)
        {
            int count = 0;
            foreach (Visual node in Descendants(visual).OfType<Visual>())
                foreach (GlyphRunDrawing glyph in Glyphs(VisualTreeHelper.GetDrawing(node)))
                {
                    if (Math.Abs(glyph.GlyphRun.PixelsPerDip - dpi / 96d) > .001)
                        throw new Exception("Popup WPF glyph DPI: " + context + "; " + node.GetType().Name + " formatted at " + glyph.GlyphRun.PixelsPerDip + " instead of " + dpi / 96d);
                    count++;
                }
            return count;
        }

        private static IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing)
        {
            var text = drawing as GlyphRunDrawing;
            if (text != null) { yield return text; yield break; }
            var group = drawing as DrawingGroup;
            if (group != null) foreach (Drawing child in group.Children) foreach (var glyph in Glyphs(child)) yield return glyph;
        }

        internal static Visual Root(Visual visual)
        {
            Visual root = visual, parent;
            while ((parent = VisualTreeHelper.GetParent(root) as Visual) != null) root = parent;
            return root;
        }

        internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
        }

        internal static void Save(FrameworkElement element, string directory, string name, int dpi)
        {
            if (String.IsNullOrEmpty(directory)) return;
            var bitmap = Render(element, dpi);
            Directory.CreateDirectory(directory);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(directory, name + ".png"))) encoder.Save(stream);
        }

        internal static RenderTargetBitmap Render(FrameworkElement element, int dpi)
        {
            if (PresentationSource.FromVisual(element) != null)
            {
                // Native hosts already received their DPI from Windows. Do not
                // force a root Window through a synthetic DPI measure pass.
                Verify(element, dpi, "native render"); element.UpdateLayout(); PumpRendering();
            }
            else { Apply(element, dpi); Pump(); }
            Size size = element.RenderSize;
            var nativeWindow = element as Window;
            var nativeSource = PresentationSource.FromVisual(element) as HwndSource;
            ClientCaptureRect client;
            if (nativeWindow != null && nativeSource != null && GetClientRect(nativeSource.Handle, out client))
            {
                Matrix transform = nativeSource.CompositionTarget.TransformToDevice;
                size = new Size((client.Right - client.Left) / transform.M11, (client.Bottom - client.Top) / transform.M22);
            }
            if (size.Width < 1 || size.Height < 1) throw new Exception("Popup WPF DPI: cannot render an empty surface");
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(size.Width * dpi / 96d)), Math.Max(1, (int)Math.Ceiling(size.Height * dpi / 96d)), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(element);
            return bitmap;
        }

        internal static string Describe(Visual visual)
        {
            Visual root = Root(visual);
            DpiScale dpi = VisualTreeHelper.GetDpi(root);
            var source = PresentationSource.FromVisual(root) as HwndSource;
            string native = source == null || source.Handle == IntPtr.Zero ? "none" : NativeDpi(source.Handle).ToString(CultureInfo.InvariantCulture);
            string transform = source == null || source.CompositionTarget == null ? "none" : source.CompositionTarget.TransformToDevice.ToString(CultureInfo.InvariantCulture);
            return "WpfDpi=" + dpi.PixelsPerInchX.ToString(CultureInfo.InvariantCulture) + "; NativeDpi=" + native + "; NativeTransform=" + transform + "; method=public VisualTreeHelper.SetRootDpi; no OS setting change";
        }

        internal static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        internal static void PumpRendering()
        {
            var frame = new DispatcherFrame();
            EventHandler rendered = null;
            var timeout = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
            rendered = (sender, args) => {
                CompositionTarget.Rendering -= rendered;
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            };
            timeout.Tick += (sender, args) => { timeout.Stop(); frame.Continue = false; };
            CompositionTarget.Rendering += rendered; timeout.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); CompositionTarget.Rendering -= rendered; }
        }

        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        internal static uint NativeDpi(IntPtr handle) { return GetDpiForWindow(handle); }
    }

    // Compile /main:CodexIsland.PopupDpiTestHost without the production manifest.
    // Run once with "unaware" and once with "system". Awareness is established
    // before any WPF type is touched; settings are memory-only fixtures.
    internal static class PopupDpiTestHost
    {
        [StructLayout(LayoutKind.Sequential)] private struct RECT { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr handle, out RECT rect);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);

        [STAThread] private static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "system";
            if (mode != "unaware" && mode != "system") { Console.Error.WriteLine("Expected unaware or system."); return 2; }
            if (!SetProcessDpiAwarenessContext(new IntPtr(mode == "unaware" ? -1 : -2)))
            {
                Console.Error.WriteLine("Cannot establish child-process DPI context; error=" + Marshal.GetLastWin32Error()); return 2;
            }
            return RunNative(mode, args.Length > 1 ? args[1] : null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static int RunNative(string mode, string directory)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Window window = null; ToolTip tip = null; IntPtr handle = IntPtr.Zero;
            var evidence = new List<string>();
            try
            {
                int awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
                Check(awareness == (mode == "unaware" ? 0 : 1), "requested process/thread awareness took effect");
                foreach (string locale in new[] { "zh", "en" })
                foreach (string theme in new[] { "light", "dark" })
                {
                    AppAppearance.Configure(locale, theme);
                    window = new SettingsWindow(new Settings { Language = locale, Theme = theme, NotificationsEnabled = false })
                    { ShowActivated = false, IsHitTestVisible = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 64, Top = 64 };
                    AppAppearance.Register(window); window.Show(); PopupDpiTests.Pump();
                    handle = new WindowInteropHelper(window).Handle;
                    uint native = PopupDpiTests.NativeDpi(handle);
                    var source = (HwndSource)PresentationSource.FromVisual(window);
                    Check(native > 0 && (mode != "unaware" || native == 96), "real HWND native DPI matches awareness");
                    Check(Math.Abs(VisualTreeHelper.GetDpi(window).PixelsPerInchX - native) < .1, "initial WPF DPI matches actual native HWND DPI");
                    foreach (Size size in new[] { new Size(320, 360), new Size(430, 520) })
                    {
                        window.Width = size.Width; window.Height = size.Height; window.UpdateLayout(); PopupDpiTests.PumpRendering();
                        RECT client; Check(GetClientRect(handle, out client), "native client rectangle available");
                        Matrix device = source.CompositionTarget.TransformToDevice;
                        var content = (FrameworkElement)window.Content;
                        Check(Math.Abs((content.ActualWidth + content.Margin.Left + content.Margin.Right) * device.M11 - (client.Right - client.Left)) < 2 && Math.Abs((content.ActualHeight + content.Margin.Top + content.Margin.Bottom) * device.M22 - (client.Bottom - client.Top)) < 2, "native/WPF client bounds stay aligned after resize");
                        evidence.Add(mode + "/" + locale + "/" + theme + "/" + size + "; NativeDpi=" + native + "; WpfDpi=" + VisualTreeHelper.GetDpi(window).PixelsPerInchX + "; NativeTransform=" + device + "; Client=" + (client.Right - client.Left) + "x" + (client.Bottom - client.Top));
                    }
                    tip = new ToolTip { Content = UiLocalization.Localize("拖动拉伸 · 朝屏幕内侧拖动可放大"), PlacementTarget = window, Placement = PlacementMode.Center, StaysOpen = true };
                    UiTheme.Install(tip.Resources); tip.IsOpen = true; PopupDpiTests.PumpRendering();
                    var popupSource = PresentationSource.FromVisual(tip) as HwndSource;
                    Check(popupSource != null && PopupDpiTests.NativeDpi(popupSource.Handle) == native, "real tooltip popup inherits the parent native DPI context");
                    Check(Math.Abs(VisualTreeHelper.GetDpi(tip).PixelsPerInchX - native) < .1, "tooltip WPF and native DPI agree");
                    IntPtr popupHandle = popupSource.Handle;
                    if (!String.IsNullOrEmpty(directory))
                    {
                        PopupDpiTests.Save(window, directory, "native-" + mode + "-" + locale + "-" + theme, (int)native);
                        PopupDpiTests.Save(tip, directory, "native-tooltip-" + mode + "-" + locale + "-" + theme, (int)native);
                    }
                    tip.IsOpen = false; tip = null; WaitForNativeClose(popupHandle);
                    Check(!IsWindow(popupHandle), "tooltip native popup closes and releases its HWND");
                    window.Close(); window = null; PopupDpiTests.Pump(); Check(!IsWindow(handle), "resized fixture releases its HWND when closed"); handle = IntPtr.Zero;
                }
                if (!String.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); File.WriteAllLines(Path.Combine(directory, "native-" + mode + "-measurements.txt"), evidence); }
                Console.WriteLine("PASS: native DPI " + mode + " fixtures; 8 bilingual/theme resize cases, 4 actual tooltip popup contexts, native/client alignment and close cleanup; " + String.Join(" | ", evidence.Take(2)));
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { if (tip != null) tip.IsOpen = false; if (window != null) window.Close(); application.Shutdown(); }
        }

        private static void Check(bool value, string message) { if (!value) throw new Exception("Native popup DPI: " + message); }

        private static void WaitForNativeClose(IntPtr handle)
        {
            // ToolTip's system popup fade can outlive IsOpen=false. Keep the
            // dispatcher running until its real HWND is destroyed or a bound
            // above the system animation duration expires.
            var frame = new DispatcherFrame();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (sender, args) => { if (!IsWindow(handle) || elapsed.ElapsedMilliseconds >= 800) frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        }
    }
}
