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
    // Opt-in observation for the original suite. No message handling, hooks,
    // input generation, activation, restore, resize or window-state changes.
    internal sealed class NativeMinimizeAttribution : IDisposable
    {
        internal const string EnvironmentName = "CODEX_ISLAND_MINIMIZE_TRACE_DIR";
        private const int MaximumLines = 8192, MaximumBytes = 8 * 1024 * 1024;
        private static readonly object Gate = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        private static readonly Dictionary<ulong, Target> Targets = new Dictionary<ulong, Target>();
        private static readonly List<ModuleRange> Modules = new List<ModuleRange>();
        private static bool initialized, enabled, limitReached;
        private static string output, processStart, firstError, moduleError;
        private static int pid, activeObservers, lines, bytes, writesSinceFlush, flushes, errors;
        private static long initialQpc;
        private static StreamWriter writer;
        private static FileStream ownership;
        private readonly string label;
        private IntPtr handle;
        private bool disposed;

        private sealed class Target { internal ulong Hwnd; internal uint ThreadId; internal string Role; }
        private sealed class ModuleRange { internal ulong Address; internal int Size; internal string Name; }
        [StructLayout(LayoutKind.Sequential)] private struct InputMessageSource { internal uint DeviceType, OriginId; }
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
        [DllImport("user32.dll")] private static extern uint InSendMessageEx(IntPtr reserved);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCurrentInputMessageSource(out InputMessageSource source);
        [DllImport("ntdll.dll")] private static extern ushort RtlCaptureStackBackTrace(uint skip, uint capture, [Out] IntPtr[] frames, out uint hash);

        private NativeMinimizeAttribution(string name) { label = name; activeObservers++; }
        internal static NativeMinimizeAttribution Attach(string name)
        {
            if (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentName))) return null;
            lock (Gate)
            {
                if (!initialized) Initialize();
                if (!enabled) return null;
                return new NativeMinimizeAttribution(name);
            }
        }
        private static void Initialize()
        {
            initialized = true;
            string project = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (!File.Exists(Path.Combine(project, "build.ps1")) || !File.Exists(Path.Combine(project, "tests", "NativeTests.cs"))) throw new InvalidOperationException("Minimize tracing requires project working directory.");
            output = Path.GetFullPath(Environment.GetEnvironmentVariable(EnvironmentName));
            if (!output.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Minimize trace directory must be within project.");
            for (var directory = new DirectoryInfo(output); directory != null && directory.FullName.Length >= project.Length; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Minimize trace path cannot traverse a reparse point.");
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length != 0) throw new InvalidOperationException("Minimize trace directory must be fresh or empty.");
            Directory.CreateDirectory(output);
            try
            {
                ownership = new FileStream(Path.Combine(output, "owner.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using (var process = Process.GetCurrentProcess())
                {
                    pid = process.Id; processStart = process.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                    initialQpc = Stopwatch.GetTimestamp();
                    FreshJson("owner.json", new { Version = 1, ProcessId = pid, ProcessStartUtc = processStart, Image = process.MainModule.FileName, StartedUtc = DateTime.UtcNow.ToString("o"), AbsoluteQpc = initialQpc, QpcFrequency = Stopwatch.Frequency, OnlyOwnWindows = true, OriginalHistoricalSenderProven = false });
                    var modules = new List<object>();
                    try
                    {
                        foreach (ProcessModule module in process.Modules)
                        {
                            var range = new ModuleRange { Address = unchecked((ulong)module.BaseAddress.ToInt64()), Size = module.ModuleMemorySize, Name = module.ModuleName };
                            Modules.Add(range); modules.Add(new { Name = range.Name, BaseAddress = Hex(range.Address), Size = range.Size, Image = module.FileName });
                        }
                    }
                    catch (Exception error) { moduleError = error.GetType().Name; }
                    FreshJson("modules.json", new { OwnProcessId = pid, Ranges = modules, Error = moduleError, Meaning = "Snapshot of this process only; no symbol downloads or other-process module inspection." });
                }
                writer = new StreamWriter(new FileStream(Path.Combine(output, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false), 8192);
                enabled = true;
                Publish();
                SaveSummary(false);
            }
            catch
            {
                if (writer != null) { writer.Dispose(); writer = null; }
                if (ownership != null) { ownership.Dispose(); ownership = null; }
                enabled = false; throw;
            }
        }
        internal void Register(IntPtr hwnd)
        {
            lock (Gate)
            {
                try
                {
                    uint actualPid; uint tid = GetWindowThreadProcessId(hwnd, out actualPid);
                    if (actualPid != pid || tid == 0 || tid != GetCurrentThreadId()) throw new InvalidOperationException("Only the attaching thread's owned HWND is permitted.");
                    handle = hwnd; Targets[unchecked((ulong)hwnd.ToInt64())] = new Target { Hwnd = unchecked((ulong)hwnd.ToInt64()), ThreadId = tid, Role = SafeRole(label) };
                    Publish();
                    Lifecycle("target-published", "source-hook-attached", Stopwatch.GetTimestamp(), true);
                }
                catch (Exception error) { Failure(error); }
            }
        }
        internal void Native(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, long entryQpc, uint entryThread, uint ismex, string context)
        {
            lock (Gate)
            {
                if (!enabled || disposed || handle != hwnd) return;
                try
                {
                    uint actualPid; uint tid = GetWindowThreadProcessId(hwnd, out actualPid);
                    if (actualPid != pid || tid != entryThread) throw new InvalidOperationException("Native observation target identity no longer matches.");
                    bool sc = message == 0x112, nonClient = message >= 0xA1 && message <= 0xA9;
                    bool critical = sc || (message == 5 && wParam.ToInt64() == 1) || message == 0x82 || message == 2;
                    Write(new { Utc = DateTime.UtcNow.ToString("o"), Qpc = entryQpc, QpcFrequency = Stopwatch.Frequency, Event = "WM", Context = context, Label = label, Controlled = false, ReceiverProcessId = pid, CurrentThreadId = entryThread, Detail = new { Hwnd = Hex(unchecked((ulong)hwnd.ToInt64())), Message = message, W = Hex(unchecked((ulong)wParam.ToInt64())), L = Hex(unchecked((ulong)lParam.ToInt64())), ISMEX = ismex, InputSource = sc || nonClient ? InputSource() : null, NativeStack = sc ? Stack() : null, ManagedEntryStack = sc ? Bounded(Environment.StackTrace, 12000) : null }, Interpretation = "Receiver entry observation only; ISMEX/input source/native stack do not independently identify a sender process." }, critical);
                }
                catch (Exception error) { Failure(error); }
            }
        }
        internal void Lifecycle(string kind, string context, long entryQpc, bool flush)
        {
            lock (Gate)
            {
                if (!enabled || disposed) return;
                try { Write(new { Utc = DateTime.UtcNow.ToString("o"), Qpc = entryQpc, QpcFrequency = Stopwatch.Frequency, Event = kind, Context = context, Label = label, ReceiverProcessId = pid, CurrentThreadId = GetCurrentThreadId(), Detail = new { Hwnd = Hex(unchecked((ulong)handle.ToInt64())) } }, flush); }
                catch (Exception error) { Failure(error); }
            }
        }
        private static object InputSource()
        {
            try
            {
                InputMessageSource value; bool available = GetCurrentInputMessageSource(out value);
                return new { Available = available, CaptureThreadId = GetCurrentThreadId(), ThreadLocalContext = true, DeviceType = value.DeviceType, OriginId = value.OriginId, LastError = available ? 0 : Marshal.GetLastWin32Error(), Interpretation = "Receiver-thread input context, not sender PID." };
            }
            catch (Exception error) { return new { Available = false, Error = error.GetType().Name }; }
        }
        private static object Stack()
        {
            try
            {
                var addresses = new IntPtr[32]; uint hash; ushort count = RtlCaptureStackBackTrace(0, 32, addresses, out hash); var frames = new List<object>();
                for (int index = 0; index < count; index++)
                {
                    ulong address = unchecked((ulong)addresses[index].ToInt64()); string moduleName = "unmapped"; string offset = null;
                    foreach (var module in Modules) if (address >= module.Address && address - module.Address < (ulong)module.Size) { moduleName = module.Name; offset = Hex(address - module.Address); break; }
                    frames.Add(new { Address = Hex(address), Module = moduleName, Offset = offset });
                }
                return new { Available = count > 0, FrameCount = count, Frames = frames, ModuleSnapshotError = moduleError, CaptureMechanism = "RtlCaptureStackBackTrace; zero frames are explicitly unavailable, not proof of absence of a caller." };
            }
            catch (Exception error) { return new { Available = false, Error = error.GetType().Name }; }
        }
        private static void Write(object record, bool flush)
        {
            if (writer == null || limitReached) return;
            string line = Json.Serialize(record); int size = Encoding.UTF8.GetByteCount(line) + 2;
            if (lines >= MaximumLines || bytes + size > MaximumBytes) { limitReached = true; Flush(); SaveSummary(false); return; }
            writer.WriteLine(line); lines++; bytes += size; writesSinceFlush++;
            if (flush || writesSinceFlush >= 32) Flush();
        }
        private static void Flush() { if (writer != null) { writer.Flush(); flushes++; writesSinceFlush = 0; } }
        private static void Publish()
        {
            var targets = new List<object>(); foreach (var target in Targets.Values) targets.Add(new { Hwnd = Hex(target.Hwnd), ProcessId = pid, ThreadId = target.ThreadId, ProcessStartUtc = processStart, Role = target.Role });
            AtomicJson("targets.json", new { Version = 1, Targets = targets });
        }
        private static void FreshJson(string name, object value)
        {
            using (var file = new FileStream(Path.Combine(output, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) using (var text = new StreamWriter(file, new UTF8Encoding(false))) text.WriteLine(Json.Serialize(value));
        }
        private static void AtomicJson(string name, object value)
        {
            string final = Path.Combine(output, name), temporary = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, Json.Serialize(value), new UTF8Encoding(false)); if (File.Exists(final)) File.Replace(temporary, final, null); else File.Move(temporary, final); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static void SaveSummary(bool completed)
        {
            AtomicJson("summary.json", new { Version = 1, Completed = completed, ProcessId = pid, ProcessStartUtc = processStart, QpcFrequency = Stopwatch.Frequency, FirstAbsoluteQpc = initialQpc, Lines = lines, Bytes = bytes, MaximumLines = MaximumLines, MaximumBytes = MaximumBytes, FlushCount = flushes, FlushEveryRecords = 32, CriticalEventsImmediatelyFlushed = true, LimitReached = limitReached, Errors = errors, FirstError = firstError, DataLossDetected = limitReached || errors != 0, OriginalHistoricalSenderProven = false, NoInputOrWindowStateChanges = true, NoExternalHooksOrSessions = true });
        }
        private static void Failure(Exception error)
        {
            errors++; if (firstError == null) firstError = error.GetType().Name + ": " + Bounded(error.Message, 256);
            try { Flush(); SaveSummary(false); } catch { }
            if (errors == 1) Console.Error.WriteLine("MINIMIZE ATTRIBUTION OBSERVER ERROR: " + firstError + "; no window-state recovery performed.");
        }
        private static string SafeRole(string value)
        {
            var text = new StringBuilder(); foreach (char c in value ?? "window") if (text.Length < 48) text.Append(Char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '-');
            return text.Length == 0 ? "window" : text.ToString();
        }
        private static string Bounded(string value, int limit) { return value == null || value.Length <= limit ? value : value.Substring(0, limit) + " [truncated]"; }
        private static string Hex(ulong value) { return "0x" + value.ToString("X", CultureInfo.InvariantCulture); }
        public void Dispose()
        {
            lock (Gate)
            {
                if (disposed) return;
                Lifecycle("observer-dispose", "cleanup", Stopwatch.GetTimestamp(), true); disposed = true;
                try
                {
                    Targets.Remove(unchecked((ulong)handle.ToInt64())); Publish(); activeObservers--;
                    if (activeObservers == 0) { Flush(); SaveSummary(true); writer.Dispose(); writer = null; ownership.Dispose(); ownership = null; enabled = false; }
                }
                catch (Exception error) { Failure(error); }
            }
        }
    }
}
