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
    // Diagnostic bridge to a native-only, current-thread WH_CBT observer. The
    // native callback never enters managed code; flushing occurs on this owner
    // thread outside the hook. Does not alter input or window state.
    internal sealed class NativeMinimizeStackProbe : IDisposable
    {
        private const int FrameCapacity = 48;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
        private readonly string output;
        private readonly uint thread;
        private readonly Dictionary<uint, string> roles = new Dictionary<uint, string>();
        private StreamWriter writer;
        private bool disposed, installed;
        private int records, errors;
        private string firstError;
        private uint nextRole = 1;

        [StructLayout(LayoutKind.Sequential)] private struct NativeRecord
        {
            internal uint Size, Version, EventKind, HookCode, ProcessId, ThreadId;
            internal ulong Qpc, Hwnd;
            internal long Command;
            internal uint Ismex, InputAvailable, DeviceType, OriginId, FrameCount, Role;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = FrameCapacity)] internal ulong[] Frames;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Status
        {
            internal uint Size, Version, ProcessId, ThreadId, Installed, Captured;
            internal uint Dropped, UnregisteredHookSeen, RegistrationRejected, Drained;
            internal uint LastError, ReentrantDrops;
            internal ulong QpcFrequency;
            internal uint RecordSize, TargetCount;
        }
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("NativeMinimizeStackProbe.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint ProbeAbiSize();
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("NativeMinimizeStackProbe.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint ProbeSelfCapture(out NativeRecord record, uint size);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("NativeMinimizeStackProbe.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint ProbeInstall();
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("NativeMinimizeStackProbe.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint ProbeRegister(IntPtr hwnd, uint role);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("NativeMinimizeStackProbe.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint ProbeCopyRecords(IntPtr records, uint capacity, out uint copied);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("NativeMinimizeStackProbe.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint ProbeGetStatus(out Status status, uint size);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("NativeMinimizeStackProbe.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint ProbeStop();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool GetModuleHandleEx(uint flags, IntPtr address, out IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetModuleFileName(IntPtr module, StringBuilder path, int count);

        private NativeMinimizeStackProbe(string directory)
        {
            if (!Environment.Is64BitProcess) throw new InvalidOperationException("Native stack probe requires x64 process.");
            string project = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar);
            output = Path.GetFullPath(directory);
            if (!output.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Probe output must be within project.");
            for (var path = new DirectoryInfo(output); path != null && path.FullName.Length >= project.Length; path = path.Parent)
                if (path.Exists && (path.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Probe output may not traverse reparse points.");
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length != 0) throw new InvalidOperationException("Fresh or empty native probe output required.");
            Directory.CreateDirectory(output);
            thread = GetCurrentThreadId();
            uint size = (uint)Marshal.SizeOf(typeof(NativeRecord));
            if (ProbeAbiSize() != size) throw new InvalidOperationException("Native stack probe ABI size mismatch.");
            writer = new StreamWriter(new FileStream(Path.Combine(output, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            try
            {
                Check(ProbeInstall(), "install own-thread hook"); installed = true;
                Save("owner.json", new { Version = 1, ProcessId = Process.GetCurrentProcess().Id, ThreadId = thread, StartedUtc = DateTime.UtcNow.ToString("o"), Dll = "NativeMinimizeStackProbe.dll", RecordSize = size, OwnThreadOnly = true, OwnRegisteredWindowsOnly = true, GlobalHook = false, InputGenerated = false, WindowStateChanges = false, NativeCallbackEntersManaged = false, HistoricalSenderProven = false });
                SaveSummary(false);
            }
            catch { if (installed) ProbeStop(); writer.Dispose(); writer = null; throw; }
        }
        public static NativeMinimizeStackProbe Start(string outputDirectory) { return new NativeMinimizeStackProbe(outputDirectory); }
        public void Register(IntPtr hwnd, string role)
        {
            EnsureThread();
            uint id = nextRole++;
            Check(ProbeRegister(hwnd, id), "register own HWND");
            roles[id] = role ?? "";
            writer.WriteLine(Json.Serialize(new { Event = "target-registered", Utc = DateTime.UtcNow.ToString("o"), Qpc = Stopwatch.GetTimestamp(), ProcessId = Process.GetCurrentProcess().Id, ThreadId = thread, Hwnd = Hex(unchecked((ulong)hwnd.ToInt64())), Role = role, RoleId = id })); writer.Flush();
        }
        public void Flush()
        {
            EnsureThread();
            int size = Marshal.SizeOf(typeof(NativeRecord));
            IntPtr memory = Marshal.AllocHGlobal(size * 16);
            try
            {
                uint copied;
                do
                {
                    Check(ProbeCopyRecords(memory, 16, out copied), "drain own native ring");
                    for (int i = 0; i < copied; i++)
                    {
                        var record = (NativeRecord)Marshal.PtrToStructure(new IntPtr(memory.ToInt64() + (long)i * size), typeof(NativeRecord));
                        writer.WriteLine(Json.Serialize(Describe(record))); records++;
                    }
                } while (copied != 0);
                writer.Flush();
            }
            catch (Exception error) { errors++; if (firstError == null) firstError = error.GetType().Name + ": " + error.Message; throw; }
            finally { Marshal.FreeHGlobal(memory); SaveSummary(false); }
        }
        private object Describe(NativeRecord record)
        {
            var frames = new List<object>();
            for (int i = 0; i < Math.Min(record.FrameCount, FrameCapacity); i++)
            {
                ulong address = record.Frames[i]; IntPtr module; string path = null, offset = null;
                if (GetModuleHandleEx(0x00000006, new IntPtr(unchecked((long)address)), out module))
                {
                    var buffer = new StringBuilder(2048); if (GetModuleFileName(module, buffer, buffer.Capacity) != 0) path = buffer.ToString();
                    offset = Hex(address - unchecked((ulong)module.ToInt64()));
                }
                frames.Add(new { Index = i, Address = Hex(address), Module = path == null ? "unmapped" : Path.GetFileName(path), Image = path, Offset = offset });
            }
            string role; roles.TryGetValue(record.Role, out role);
            return new { Event = record.EventKind == 1 ? "native-HCBT_MINMAX" : "native-self-capture", Version = record.Version, Qpc = record.Qpc, QpcFrequency = Stopwatch.Frequency, ProcessId = record.ProcessId, ThreadId = record.ThreadId, Hwnd = Hex(record.Hwnd), Role = role, RoleId = record.Role, HookCode = record.HookCode, ShowCommand = record.Command, Ismex = record.Ismex, InputSource = new { Available = record.InputAvailable != 0, DeviceType = record.DeviceType, OriginId = record.OriginId, Meaning = "receiver thread input context, not sender PID" }, NativeStack = new { Available = record.FrameCount != 0, FrameCount = record.FrameCount, Frames = frames, CapturedBeforeManagedTransition = true }, Interpretation = "Native attempt observation only. Correlate with real WM_SIZE/placement state and ETW; callback always calls the next hook and may be cancelled by another hook." };
        }
        private void SaveSummary(bool completed)
        {
            Status status; Check(ProbeGetStatus(out status, (uint)Marshal.SizeOf(typeof(Status))), "get own probe status");
            Save("summary.json", new { Completed = completed, ProcessId = status.ProcessId, ThreadId = status.ThreadId, Installed = status.Installed != 0, Captured = status.Captured, Drained = status.Drained, TargetCount = status.TargetCount, SavedRecords = records, Dropped = status.Dropped, ReentrantDrops = status.ReentrantDrops, RegistrationRejected = status.RegistrationRejected, UnregisteredHookSeen = status.UnregisteredHookSeen, LastNativeError = status.LastError, Errors = errors, FirstError = firstError, DataLossDetected = status.Dropped != 0 || status.ReentrantDrops != 0 || errors != 0, QpcFrequency = status.QpcFrequency, RecordSize = status.RecordSize, CallbackAlwaysCallNext = true, InputOrWindowChanges = false, HistoricalSenderProven = false });
        }
        private void Save(string file, object value) { File.WriteAllText(Path.Combine(output, file), Json.Serialize(value), new UTF8Encoding(false)); }
        private void EnsureThread() { if (disposed) throw new ObjectDisposedException("NativeMinimizeStackProbe"); if (GetCurrentThreadId() != thread) throw new InvalidOperationException("Probe can only be used by its installing UI thread."); }
        private static void Check(uint error, string operation) { if (error != 0) throw new InvalidOperationException("Native stack probe " + operation + ": Win32=" + error); }
        private static string Hex(ulong value) { return "0x" + value.ToString("X", CultureInfo.InvariantCulture); }
        public void Dispose()
        {
            if (disposed) return; EnsureThread();
            try { Flush(); }
            finally
            {
                try { if (installed) { Check(ProbeStop(), "remove own-thread hook"); installed = false; } SaveSummary(true); }
                finally { if (writer != null) writer.Dispose(); writer = null; disposed = true; }
            }
        }
        public static object SelfCheck()
        {
            uint size = (uint)Marshal.SizeOf(typeof(NativeRecord));
            if (ProbeAbiSize() != size) throw new InvalidOperationException("Native stack probe ABI size mismatch.");
            NativeRecord record; Check(ProbeSelfCapture(out record, size), "non-UI native stack self-check");
            var probe = new NativeMinimizeStackProbeFormatter();
            return new { Passed = record.FrameCount > 0, UiExecuted = false, HookInstalled = false, NativeRecordSize = size, NativeFrameCount = record.FrameCount, RawFrames = probe.HexFrames(record), Meaning = "Only native export stack smoke check, not calibration of an actual minimization." };
        }
        private sealed class NativeMinimizeStackProbeFormatter
        {
            internal string[] HexFrames(NativeRecord record) { var result = new string[record.FrameCount]; for (int i = 0; i < result.Length; i++) result[i] = Hex(record.Frames[i]); return result; }
        }
    }
}
