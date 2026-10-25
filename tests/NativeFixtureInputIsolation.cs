using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CodexIsland
{
    // IsHitTestVisible=false isolates WPF content, not the HWND's native frame.
    // This test-only guard additionally excludes pointer hits on that frame.
    // Explicit system commands and all state/geometry assertions remain intact.
    internal sealed class NativeFixtureInputIsolation : IDisposable
    {
        private static readonly uint OwnProcessId = (uint)Process.GetCurrentProcess().Id;
        private readonly Window window;
        private HwndSource source;
        private IntPtr handle;
        private bool disposed;
        internal int GuardedQueryCount { get; private set; }

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        private NativeFixtureInputIsolation(Window target)
        {
            window = target;
            window.Dispatcher.VerifyAccess();
            window.SourceInitialized += SourceInitialized;
            window.Closed += Closed;
            AttachSource();
        }

        internal static NativeFixtureInputIsolation Attach(Window target)
        {
            if (target == null) throw new ArgumentNullException("target");
            return new NativeFixtureInputIsolation(target);
        }

        private void SourceInitialized(object sender, EventArgs args) { AttachSource(); }
        private void Closed(object sender, EventArgs args) { Dispose(); }

        private void AttachSource()
        {
            if (disposed || source != null) return;
            IntPtr candidate = new WindowInteropHelper(window).Handle;
            if (candidate == IntPtr.Zero) return; // Never creates a handle.
            uint processId;
            GetWindowThreadProcessId(candidate, out processId);
            if (!IsWindow(candidate) || processId != OwnProcessId)
                throw new InvalidOperationException("Native fixture input isolation requires this process's live HWND.");
            HwndSource candidateSource = HwndSource.FromHwnd(candidate);
            if (candidateSource == null || candidateSource.IsDisposed)
                throw new InvalidOperationException("Native fixture input isolation requires the fixture's live HwndSource.");
            handle = candidate;
            source = candidateSource;
            source.AddHook(Hook);
        }

        private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Do not inspect or handle WM_SYSCOMMAND, WM_SIZE or state changes.
            if (message != 0x0084 || disposed || hwnd != handle || window.IsHitTestVisible)
                return IntPtr.Zero;
            uint processId;
            GetWindowThreadProcessId(hwnd, out processId);
            if (processId != OwnProcessId || !IsWindow(hwnd)) return IntPtr.Zero;

            // Calling the native default routine is not SendMessage and cannot
            // recurse through this hook. Client-area WPF handling stays intact.
            long hit = DefWindowProc(hwnd, message, wParam, lParam).ToInt64();
            if (hit == 2 || hit == 3 || hit == 8 || hit == 9 ||
                (hit >= 10 && hit <= 18) || hit == 20 || hit == 21)
            {
                GuardedQueryCount++;
                handled = true;
                return IntPtr.Zero; // HTNOWHERE: no native pointer interaction.
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (disposed) return;
            window.Dispatcher.VerifyAccess();
            window.SourceInitialized -= SourceInitialized;
            window.Closed -= Closed;
            if (source != null && !source.IsDisposed)
            {
                try { source.RemoveHook(Hook); }
                catch (InvalidOperationException) { }
            }
            source = null;
            handle = IntPtr.Zero;
            disposed = true;
        }
    }
}
