using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace CodexIsland
{
    // Test-only, bounded observation of an owned HWND. Never restores, activates,
    // resizes or otherwise changes a window. No titles, input text or key states.
    internal sealed class NativeWindowStateTrace : IDisposable
    {
        private const int Capacity = 384;
        private readonly Window window;
        private readonly string name;
        private readonly Queue<string> records = new Queue<string>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private HwndSource source;
        private readonly NativeMinimizeAttribution attribution;
        private IntPtr handle;
        private bool recording, disposed, dumped;
        private int messages, states, moves, minimizes, destroys;
        private string context = "created";

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct Placement
        {
            internal uint Length, Flags, ShowCmd;
            internal NativePoint Minimum, Maximum;
            internal NativeRect Normal;
        }
        [StructLayout(LayoutKind.Sequential)] private struct WindowPosition
        {
            internal IntPtr Window, InsertAfter;
            internal int X, Y, Width, Height;
            internal uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Styles { internal uint Old, New; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect bounds);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref Placement placement);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint InSendMessageEx(IntPtr reserved);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private NativeWindowStateTrace(Window target, string label)
        {
            window = target; name = label;
            attribution = NativeMinimizeAttribution.Attach(label);
            window.SourceInitialized += SourceInitialized;
            window.StateChanged += StateChanged;
            window.Closed += Closed;
            Record("attach-before-show", false);
            AttachSource();
        }
        internal static NativeWindowStateTrace Attach(Window target, string label)
        {
            if (target == null) throw new ArgumentNullException("target");
            return new NativeWindowStateTrace(target, label);
        }
        private void SourceInitialized(object sender, EventArgs args) { AttachSource(); Record("source-initialized", false); }
        private void AttachSource()
        {
            if (disposed || source != null) return;
            IntPtr candidate = new WindowInteropHelper(window).Handle;
            if (candidate == IntPtr.Zero) return;
            var native = HwndSource.FromHwnd(candidate);
            if (native == null) return;
            handle = candidate; source = native; source.AddHook(Hook);
            if (attribution != null) attribution.Register(handle);
            Record("source-hook-attached", false);
        }
        internal void Mark(string value)
        {
            context = value ?? "";
            if (attribution != null) attribution.Lifecycle("case/request", context, Stopwatch.GetTimestamp(), false);
            Record("case/request", false);
        }
        private void StateChanged(object sender, EventArgs args) { states++; Record("managed-StateChanged", true); }
        private void Closed(object sender, EventArgs args) { Record("managed-Closed", true); }

        private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            long entryQpc = Stopwatch.GetTimestamp();
            if (!Interesting(message)) return IntPtr.Zero;
            uint entryThread = GetCurrentThreadId(), ismex = InSendMessageEx(IntPtr.Zero);
            if (attribution != null) attribution.Native(hwnd, message, wParam, lParam, entryQpc, entryThread, ismex, context);
            messages++;
            if (message == 0x0003 || message == 0x0005) moves++;
            if ((message == 0x0005 && wParam.ToInt64() == 1) || (message == 0x0112 && (wParam.ToInt64() & 0xFFF0) == 0xF020)) minimizes++;
            if (message == 0x0002 || message == 0x0082) destroys++;
            string detail = "message=" + MessageName(message) + " w=0x" + wParam.ToInt64().ToString("X", CultureInfo.InvariantCulture) +
                " l=0x" + lParam.ToInt64().ToString("X", CultureInfo.InvariantCulture);
            try
            {
                if ((message == 0x0046 || message == 0x0047) && lParam != IntPtr.Zero)
                {
                    var position = (WindowPosition)Marshal.PtrToStructure(lParam, typeof(WindowPosition));
                    detail += " WINDOWPOS=" + position.X + "," + position.Y + "," + position.Width + "," + position.Height +
                        " flags=0x" + position.Flags.ToString("X", CultureInfo.InvariantCulture) + " after=" + Hex(position.InsertAfter);
                }
                if ((message == 0x007C || message == 0x007D) && lParam != IntPtr.Zero)
                {
                    var styles = (Styles)Marshal.PtrToStructure(lParam, typeof(Styles));
                    detail += " STYLE=0x" + styles.Old.ToString("X", CultureInfo.InvariantCulture) + "->0x" + styles.New.ToString("X", CultureInfo.InvariantCulture);
                }
                if (message == 0x0005)
                {
                    long packed = lParam.ToInt64(); detail += " SIZEkind=" + wParam.ToInt64() + " client=" + (packed & 0xFFFF) + "," + ((packed >> 16) & 0xFFFF);
                }
            }
            catch (Exception error) { detail += " detail-error=" + error.GetType().Name; }
            Record(detail, false, entryQpc);
            return IntPtr.Zero;
        }
        private static bool Interesting(int value)
        {
            return value == 0x0002 || value == 0x0003 || value == 0x0005 || value == 0x0006 || value == 0x0018 || value == 0x001C ||
                value == 0x0021 || value == 0x0046 || value == 0x0047 || value == 0x007C || value == 0x007D || value == 0x0082 ||
                value == 0x0112 || value == 0x02E0 || (value >= 0x00A1 && value <= 0x00A9);
        }
        private static string MessageName(int value)
        {
            switch (value)
            {
                case 0x0002: return "WM_DESTROY"; case 0x0003: return "WM_MOVE"; case 0x0005: return "WM_SIZE";
                case 0x0006: return "WM_ACTIVATE"; case 0x0018: return "WM_SHOWWINDOW"; case 0x001C: return "WM_ACTIVATEAPP";
                case 0x0021: return "WM_MOUSEACTIVATE"; case 0x0046: return "WM_WINDOWPOSCHANGING"; case 0x0047: return "WM_WINDOWPOSCHANGED";
                case 0x007C: return "WM_STYLECHANGING"; case 0x007D: return "WM_STYLECHANGED"; case 0x0082: return "WM_NCDESTROY";
                case 0x0112: return "WM_SYSCOMMAND"; case 0x02E0: return "WM_DPICHANGED";
                default: return "WM_NCBUTTON_0x" + value.ToString("X", CultureInfo.InvariantCulture);
            }
        }
        private void Record(string value, bool stack, long entryQpc = 0)
        {
            if (disposed || recording) return;
            recording = true;
            try
            {
                if (entryQpc == 0) entryQpc = Stopwatch.GetTimestamp();
                string text = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " absolute-qpc=" + entryQpc + " qpc-frequency=" + Stopwatch.Frequency + " native-tid=" + GetCurrentThreadId() + " elapsed-ms=" + clock.ElapsedMilliseconds +
                    " label=" + name + " context=" + context + " " + value + " " + Snapshot();
                if (stack) text += " managed-stack=" + Environment.StackTrace.Replace("\r", "").Replace("\n", " | ");
                if (records.Count == Capacity) records.Dequeue();
                records.Enqueue(text);
            }
            catch (Exception error)
            {
                if (records.Count == Capacity) records.Dequeue();
                records.Enqueue("trace-snapshot-error=" + error.GetType().Name + " label=" + name + " context=" + context);
            }
            finally { recording = false; }
        }
        private string Snapshot()
        {
            IntPtr foreground = GetForegroundWindow();
            string result = "managed-state=" + window.WindowState + " visible=" + window.IsVisible + " width-height=" + F(window.Width) + "," + F(window.Height) +
                " actual=" + F(window.ActualWidth) + "," + F(window.ActualHeight) + " source-disposed=" + (source == null ? "none" : source.IsDisposed.ToString()) +
                " ISMEX=0x" + InSendMessageEx(IntPtr.Zero).ToString("X", CultureInfo.InvariantCulture) + " native={" + Describe(handle) + "} foreground={" + DescribeIdentity(foreground) + "}";
            if (window.Owner != null)
            {
                IntPtr owner = new WindowInteropHelper(window.Owner).Handle;
                result += " owner-managed-state=" + window.Owner.WindowState + " owner-native={" + Describe(owner) + "}";
            }
            return result;
        }
        private static string Describe(IntPtr hwnd)
        {
            var placement = new Placement { Length = (uint)Marshal.SizeOf(typeof(Placement)) };
            NativeRect bounds;
            string value = DescribeIdentity(hwnd) + " valid=" + IsWindow(hwnd) + " visible=" + IsWindowVisible(hwnd) + " iconic=" + IsIconic(hwnd) +
                " style=0x" + unchecked((uint)GetWindowLong(hwnd, -16)).ToString("X", CultureInfo.InvariantCulture);
            value += GetWindowRect(hwnd, out bounds) ? " rect=" + RectText(bounds) : " rect=unavailable";
            value += GetWindowPlacement(hwnd, ref placement) ? " showCmd=" + placement.ShowCmd + " placementFlags=0x" + placement.Flags.ToString("X", CultureInfo.InvariantCulture) +
                " normal=" + RectText(placement.Normal) + " min=" + placement.Minimum.X + "," + placement.Minimum.Y : " placement=unavailable";
            return value;
        }
        private static string DescribeIdentity(IntPtr hwnd)
        {
            uint process; uint thread = GetWindowThreadProcessId(hwnd, out process); var text = new StringBuilder(256);
            GetClassName(hwnd, text, text.Capacity);
            return "hwnd=" + Hex(hwnd) + " pid=" + process + " tid=" + thread + " class=" + text;
        }
        private static string RectText(NativeRect value) { return value.Left + "," + value.Top + "," + (value.Right - value.Left) + "," + (value.Bottom - value.Top); }
        private static string F(double value) { return value.ToString("0.###", CultureInfo.InvariantCulture); }
        private static string Hex(IntPtr value) { return "0x" + value.ToInt64().ToString("X", CultureInfo.InvariantCulture); }
        internal void DumpFailure(string stage)
        {
            if (dumped || disposed) return;
            if (attribution != null) attribution.Lifecycle("failure-snapshot", stage, Stopwatch.GetTimestamp(), true);
            Record("failure-snapshot " + stage, true); dumped = true;
            Console.Error.WriteLine("NATIVE WINDOW STATE TRACE BEGIN label=" + name + " bounded=" + Capacity + " records=" + records.Count + " stage=" + stage);
            foreach (string value in records) Console.Error.WriteLine(value);
            Console.Error.WriteLine("NATIVE WINDOW STATE TRACE END label=" + name + "; no restore/activation/state mutation performed.");
        }
        public void Dispose()
        {
            if (disposed) return;
            Record("dispose", false);
            window.SourceInitialized -= SourceInitialized; window.StateChanged -= StateChanged; window.Closed -= Closed;
            if (source != null && !source.IsDisposed) { try { source.RemoveHook(Hook); } catch (InvalidOperationException) { } }
            if (attribution != null) attribution.Dispose();
            disposed = true;
            Console.WriteLine("TRACE SUMMARY: " + name + " native-messages=" + messages + " move-size=" + moves + " minimized-messages=" + minimizes +
                " state-events=" + states + " destroy-messages=" + destroys + " failure-dumped=" + dumped + "; bounded observation only.");
        }
    }
}
