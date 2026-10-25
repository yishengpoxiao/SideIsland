using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    // Parent-only capture of the exact child that it created and still holds.
    internal static class StartupWatchdogCapture
    {
        private const uint DumpFlags = 0x2 | 0x20 | 0x800 | 0x1000 | 0x20000;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] private struct Placement { internal uint Length, Flags, ShowCommand; internal Point Minimum, Maximum; internal Rect Normal; }
        [StructLayout(LayoutKind.Sequential)] private struct GuiThread { internal uint Size, Flags; internal IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; internal Rect CaretRectangle; }
        private delegate bool EnumProc(IntPtr hwnd, IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr state);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref Placement placement);
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThread information);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long created, out long ended, out long kernel, out long user);
        [DllImport("dbghelp.dll", SetLastError = true)] private static extern bool MiniDumpWriteDump(IntPtr process, uint pid, IntPtr file, uint flags, IntPtr exception, IntPtr streams, IntPtr callback);

        internal static void Capture(Process child, string directory, string reason)
        {
            var started = DateTime.UtcNow; var clock = Stopwatch.StartNew(); int pid = child.Id;
            bool valid = false, written = false; int nativeError = 0; string error = null;
            long created = 0, ended = 0, kernel = 0, user = 0, bytes = 0; object before = null, after = null;
            string image = null, dump = Path.Combine(directory, "watchdog-before-kill-full.dmp");
            try
            {
                IntPtr retained = child.Handle;
                image = child.MainModule.FileName;
                string expected = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".runtime", "tests", "NativeTests.exe"));
                if (!String.Equals(image, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only the original-path child image is permitted.");
                if (!GetProcessTimes(retained, out created, out ended, out kernel, out user)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (created != child.StartTime.ToUniversalTime().ToFileTimeUtc() || child.HasExited) throw new InvalidOperationException("Retained child identity changed or child already exited.");
                valid = true; before = Snapshot((uint)pid);
                Save(Path.Combine(directory, "watchdog-window-before-dump.json"), before);
                using (var output = new FileStream(dump, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
                {
                    written = MiniDumpWriteDump(retained, (uint)pid, output.SafeFileHandle.DangerousGetHandle(), DumpFlags, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    nativeError = written ? 0 : Marshal.GetLastWin32Error(); output.Flush(true); bytes = output.Length;
                }
                after = Snapshot((uint)pid); Save(Path.Combine(directory, "watchdog-window-after-dump.json"), after);
            }
            catch (Exception failure) { error = SafeError(failure); }
            finally
            {
                Save(Path.Combine(directory, "watchdog-dump.json"), new { StartedUtc = started.ToString("o"), EndedUtc = DateTime.UtcNow.ToString("o"), ElapsedMilliseconds = clock.ElapsedMilliseconds, Reason = reason, ProcessId = pid, Image = image, RetainedCreationFileTime = "0x" + created.ToString("X"), IdentityValidated = valid, OnlyCreatedChild = true, DumpAttemptedBeforeKill = true, FullMemoryRequested = true, ThreadInfoRequested = true, DumpFlags = "0x" + DumpFlags.ToString("X"), Written = written, NativeError = nativeError, Bytes = bytes, DumpFile = Path.GetFileName(dump), Error = error, SnapshotBefore = before != null, SnapshotAfter = after != null, SenderOrRootCauseProven = false });
            }
        }
        private static object Snapshot(uint pid)
        {
            var windows = new List<object>(); var threads = new HashSet<uint>();
            EnumProc callback = delegate(IntPtr hwnd, IntPtr ignored)
            {
                uint owner; uint thread = GetWindowThreadProcessId(hwnd, out owner);
                if (owner != pid || !IsWindow(hwnd)) return true;
                Rect bounds; bool gotRect = GetWindowRect(hwnd, out bounds);
                var placement = new Placement { Length = (uint)Marshal.SizeOf(typeof(Placement)) };
                bool gotPlacement = GetWindowPlacement(hwnd, ref placement); var className = new StringBuilder(256); GetClassName(hwnd, className, className.Capacity);
                windows.Add(new { Hwnd = Hex(hwnd), ProcessId = owner, ThreadId = thread, Class = className.ToString(), Visible = IsWindowVisible(hwnd), Enabled = IsWindowEnabled(hwnd), Iconic = IsIconic(hwnd), Zoomed = IsZoomed(hwnd), Style = "0x" + unchecked((uint)GetWindowLong(hwnd, -16)).ToString("X"), ExtendedStyle = "0x" + unchecked((uint)GetWindowLong(hwnd, -20)).ToString("X"), Owner = Hex(GetWindow(hwnd, 4)), RectangleAvailable = gotRect, Rectangle = new[] { bounds.L, bounds.T, bounds.R - bounds.L, bounds.B - bounds.T }, PlacementAvailable = gotPlacement, ShowCommand = placement.ShowCommand, PlacementFlags = placement.Flags, NormalRectangle = new[] { placement.Normal.L, placement.Normal.T, placement.Normal.R - placement.Normal.L, placement.Normal.B - placement.Normal.T } });
                threads.Add(thread); return true;
            };
            bool enumerated = EnumWindows(callback, IntPtr.Zero); GC.KeepAlive(callback); var gui = new List<object>();
            foreach (uint thread in threads)
            {
                var information = new GuiThread { Size = (uint)Marshal.SizeOf(typeof(GuiThread)) }; bool available = GetGUIThreadInfo(thread, ref information);
                gui.Add(new { ThreadId = thread, Available = available, Flags = "0x" + information.Flags.ToString("X"), InMoveSize = (information.Flags & 2) != 0, InMenuMode = (information.Flags & 4) != 0, Active = Hex(information.Active), Focus = Hex(information.Focus), Capture = Hex(information.Capture), MenuOwner = Hex(information.MenuOwner), MoveSize = Hex(information.MoveSize), Caret = Hex(information.Caret) });
            }
            return new { Utc = DateTime.UtcNow.ToString("o"), Qpc = Stopwatch.GetTimestamp(), QpcFrequency = Stopwatch.Frequency, ProcessId = pid, EnumerationCompleted = enumerated, OwnTopLevelWindows = windows, OwnGuiThreads = gui, NoWindowTitlesOrText = true, WindowStateChanged = false };
        }
        internal static string SafeError(Exception error)
        {
            if (error == null) return "null exception";
            string value = error.GetType().FullName + " HResult=0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture);
            try { value += " Message=" + error.Message; } catch { value += " Message=<unavailable>"; }
            try { string stack = error.StackTrace; value += " Stack=" + (stack == null ? "<none>" : stack.Length > 16000 ? stack.Substring(0, 16000) + " [truncated]" : stack); } catch { value += " Stack=<unavailable>"; }
            return value;
        }
        private static string Hex(IntPtr pointer) { return "0x" + unchecked((ulong)pointer.ToInt64()).ToString("X"); }
        private static void Save(string path, object value) { File.WriteAllText(path, Json.Serialize(value), new UTF8Encoding(false)); }
    }
}
