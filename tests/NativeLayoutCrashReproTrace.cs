using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    // Opt-in crash breadcrumbs. A file-backed mapping survives termination and
    // can be read while a native debugger has all target threads suspended.
    // No per-record disk/console flush, Dispatcher yield or test delay occurs.
    internal static class NativeLayoutCrashTrace
    {
        internal const int HeaderSize = 128, RecordSize = 512, Capacity = 4096;
        private static readonly object Gate = new object();
        private static bool initialized, quiet;
        private static TraceWriter writer;
        internal static bool Enabled { get { Initialize(); return writer != null; } }
        internal static bool Quiet { get { Initialize(); return writer != null && quiet; } }

        private static void Initialize()
        {
            if (initialized) return;
            lock (Gate)
            {
                if (initialized) return;
                string path = Environment.GetEnvironmentVariable("CODEX_ISLAND_NATIVE_TRACE_PATH");
                if (!String.IsNullOrWhiteSpace(path))
                {
                    path = WorkspacePath(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    writer = new TraceWriter(path);
                    quiet = Environment.GetEnvironmentVariable("CODEX_ISLAND_NATIVE_TRACE_QUIET") == "1";
                }
                initialized = true;
            }
        }

        internal static string WorkspacePath(string path)
        {
            string root = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string result = Path.GetFullPath(path);
            if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Native trace output must be inside the current project directory.");
            return result;
        }

        internal static void Mark(string stage)
        {
            Initialize(); var target = writer; if (target != null) target.Write(stage);
        }
        internal static void Mark(string area, string context, string stage)
        {
            Initialize(); var target = writer;
            if (target != null) target.Write(area + " / " + context + " / " + stage);
        }

        internal static void MarkQrState(string stage, Dictionary<string, System.Windows.UIElement> panels)
        {
            if (!Enabled) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            System.Windows.UIElement value;
            if (!panels.TryGetValue("qr", out value)) { Mark(stage + " qr=missing"); return; }
            Func<string, object> field = name => value.GetType().GetField(name, flags).GetValue(value);
            var timer = (DispatcherTimer)field("debounce");
            var current = (BitmapSource)field("current");
            var displayed = ((Image)field("image")).Source;
            var cancellation = (CancellationTokenSource)field("workCancellation");
            string cancelState = cancellation == null ? "null" : cancellation.IsCancellationRequested ? "cancelled" : "active";
            Mark(stage + " qr{visible=" + value.IsVisible + ";revision=" + field("revision") +
                ";debounce=" + timer.IsEnabled + ";work=" + cancelState + ";textLength=" + ((TextBox)field("input")).Text.Length +
                ";correction=" + ((ComboBox)field("correction")).SelectedIndex +
                ";current=" + (current == null ? "null" : RuntimeHelpers.GetHashCode(current).ToString()) +
                ";image=" + (displayed == null ? "null" : RuntimeHelpers.GetHashCode(displayed).ToString()) +
                ";frozen=" + (current == null ? "n/a" : current.IsFrozen.ToString()) + "}");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeStartupInfo
        {
            internal int cb;
            internal IntPtr reserved, desktop, title;
            internal uint x, y, width, height, xChars, yChars, fill, flags;
            internal ushort show, reservedLength;
            internal IntPtr reservedBytes, input, output, error;
        }
        [DllImport("kernel32.dll", EntryPoint = "GetStartupInfoW")]
        private static extern void GetStartupInfo(ref NativeStartupInfo information);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowStyle(IntPtr handle, int index);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr handle);

        internal static void MarkWindowState(string stage, Window window)
        {
            if (!Enabled) return;
            // All calls read an already-created target. Do not EnsureHandle,
            // ShowWindow, invoke layout/rendering or change any process policy.
            var startup = new NativeStartupInfo { cb = Marshal.SizeOf(typeof(NativeStartupInfo)) };
            GetStartupInfo(ref startup);
            IntPtr handle = new WindowInteropHelper(window).Handle;
            var source = handle == IntPtr.Zero ? null : HwndSource.FromHwnd(handle);
            var target = source == null ? null : source.CompositionTarget as HwndTarget;
            var dpi = VisualTreeHelper.GetDpi(window);
            uint style = handle == IntPtr.Zero ? 0 : unchecked((uint)GetWindowStyle(handle, -16));
            Mark(stage + " window{hwnd=0x" + handle.ToInt64().ToString("X") +
                ";visible=" + (handle != IntPtr.Zero && IsWindowVisible(handle)) +
                ";WS_VISIBLE=" + ((style & 0x10000000) != 0) + ";style=0x" + style.ToString("X8") +
                ";wpf=" + window.Visibility + ";loaded=" + window.IsLoaded +
                ";renderMode=" + (target == null ? "no-target" : target.RenderMode.ToString()) +
                ";tier=" + (RenderCapability.Tier >> 16) + ";nativeDpi=" + (handle == IntPtr.Zero ? 0 : GetDpiForWindow(handle)) +
                ";wpfDpi=" + dpi.PixelsPerInchX.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "/" + dpi.PixelsPerInchY.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                ";startupFlags=0x" + startup.flags.ToString("X8") + ";startupShow=" + startup.show + "}");
        }

        private sealed class TraceWriter
        {
            private readonly FileStream backingFile;
            private readonly MemoryMappedFile file;
            private readonly MemoryMappedViewAccessor view;
            private readonly object writeGate = new object();
            private long sequence;
            internal TraceWriter(string path)
            {
                // The path overload opens its own backing handle and can deny
                // a debugger's live FileStream reader. Retain the shared handle
                // and let the mapping own it for the writer's entire lifetime.
                backingFile = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                backingFile.SetLength(HeaderSize + (long)RecordSize * Capacity);
                file = MemoryMappedFile.CreateFromFile(backingFile, null, HeaderSize + (long)RecordSize * Capacity,
                    MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
                view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
                view.WriteArray(0, Encoding.ASCII.GetBytes("CILAYT01"), 0, 8);
                view.Write(8, 1); view.Write(12, RecordSize); view.Write(16, Capacity);
                view.Write(24, (long)0); view.Write(32, System.Diagnostics.Process.GetCurrentProcess().Id);
            }
            internal void Write(string text)
            {
                lock (writeGate)
                {
                    long next = ++sequence, offset = HeaderSize + ((next - 1) % Capacity) * RecordSize;
                    var encoded = Encoding.UTF8.GetBytes(text ?? ""); int length = Math.Min(RecordSize - 24, encoded.Length);
                    view.Write(offset, -next);
                    view.Write(offset + 8, DateTime.UtcNow.Ticks);
                    view.Write(offset + 16, Thread.CurrentThread.ManagedThreadId);
                    view.Write(offset + 20, length);
                    view.WriteArray(offset + 24, encoded, 0, length);
                    Thread.MemoryBarrier(); view.Write(offset, next);
                    Thread.MemoryBarrier(); view.Write(24, next);
                }
            }
        }

        // This method never initializes the target writer, so decoding cannot
        // truncate an existing trace even when diagnostic environment is set.
        internal static string[] Decode(string path)
        {
            var rows = new List<KeyValuePair<long, string>>();
            using (var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var input = new BinaryReader(stream, Encoding.UTF8))
            {
                if (Encoding.ASCII.GetString(input.ReadBytes(8)) != "CILAYT01") throw new InvalidDataException("Unknown native trace format.");
                int version = input.ReadInt32(), size = input.ReadInt32(), count = input.ReadInt32();
                if (version != 1 || size != RecordSize || count != Capacity) throw new InvalidDataException("Unsupported native trace layout.");
                stream.Position = 24; long latest = input.ReadInt64(); int process = input.ReadInt32();
                rows.Add(new KeyValuePair<long, string>(0, "# Native layout trace v1; process=" + process + "; latest=" + latest));
                long first = Math.Max(1, latest - Capacity + 1);
                for (long expected = first; expected <= latest; expected++)
                {
                    stream.Position = HeaderSize + ((expected - 1) % Capacity) * RecordSize;
                    long committed = input.ReadInt64(), ticks = input.ReadInt64(); int thread = input.ReadInt32(), length = input.ReadInt32();
                    if (committed != expected || length < 0 || length > RecordSize - 24) continue;
                    string text = Encoding.UTF8.GetString(input.ReadBytes(length));
                    rows.Add(new KeyValuePair<long, string>(expected, expected.ToString("D6") + " " + new DateTime(ticks, DateTimeKind.Utc).ToString("o") + " tid=" + thread + " " + text));
                }
            }
            return rows.OrderBy(row => row.Key).Select(row => row.Value).ToArray();
        }
    }
}
