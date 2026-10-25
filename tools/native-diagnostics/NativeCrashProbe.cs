using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Diagnostic launcher for this repository's own NativeTests.exe only. It never
// attaches to an unowned process, registers a debugger or changes Windows policy.
internal static class NativeCrashProbe
{
    private const uint DebugOnlyThisProcess = 2, CreateNoWindow = 0x08000000;
    private const uint DebugContinue = 0x00010002, DebugExceptionNotHandled = 0x80010001;
    private const uint ExceptionEvent = 1, CreateThreadEvent = 2, CreateProcessEvent = 3, ExitThreadEvent = 4,
        ExitProcessEvent = 5, LoadDllEvent = 6, UnloadDllEvent = 7, DebugStringEvent = 8;
    private const uint Breakpoint = 0x80000003, FastFail = 0xC0000409;
    private const uint ContextFull = 0x0010000B, MachineAmd64 = 0x8664;
    private const uint MinidumpFlags = 0x2 | 0x20 | 0x800 | 0x1000 | 0x20000;
    private static readonly Dictionary<uint, IntPtr> Threads = new Dictionary<uint, IntPtr>();
    private static readonly SortedDictionary<ulong, Module> Modules = new SortedDictionary<ulong, Module>();
    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    private static StreamWriter Log;
    private static IntPtr ProcessHandle;
    private static uint ProcessId;
    private static string OutputDirectory;
    private static ulong AbortAddress;
    private static byte AbortOriginalByte;
    private static bool AbortBreakpointArmed, FatalCaptured, SymbolsInitialized;
    private static int CppExceptionsObserved;
    private static bool ProcessExited;

    [STAThread]
    private static int Main(string[] args)
    {
        try { return args.Length == 1 && args[0] == "--self-test" ? SelfTest() : Run(args); }
        catch (Exception error) { Console.Error.WriteLine(error); return 2; }
        finally
        {
            if (ProcessHandle != IntPtr.Zero && !ProcessExited)
            {
                uint status;
                if (GetExitCodeProcess(ProcessHandle, out status) && status == 259)
                    TerminateProcess(ProcessHandle, 0xDEAD0002);
            }
            if (SymbolsInitialized) SymCleanup(ProcessHandle);
            foreach (var thread in Threads.Values) if (thread != IntPtr.Zero) CloseHandle(thread);
            if (ProcessHandle != IntPtr.Zero) CloseHandle(ProcessHandle);
            if (Log != null) Log.Dispose();
        }
    }

    private static int SelfTest()
    {
        Console.WriteLine("DEBUG_EVENT=" + Marshal.SizeOf(typeof(DebugEvent)) + "; EXCEPTION_RECORD=" + Marshal.SizeOf(typeof(ExceptionRecord)) + "; STACKFRAME64=" + Marshal.SizeOf(typeof(StackFrame64)) + "; KDHELP64=" + Marshal.SizeOf(typeof(KdHelp64)) + "; MINIDUMP_EXCEPTION_INFORMATION=" + Marshal.SizeOf(typeof(DumpExceptionInformation)));
        if (Marshal.SizeOf(typeof(DebugEvent)) != 176 || Marshal.SizeOf(typeof(ExceptionRecord)) != 152 || Marshal.SizeOf(typeof(StackFrame64)) != 264 || Marshal.SizeOf(typeof(KdHelp64)) != 112) throw new InvalidOperationException("Unexpected Win64 native structure layout.");
        if (Marshal.SizeOf(typeof(DumpExceptionInformation)) != 16 || Marshal.OffsetOf(typeof(DumpExceptionInformation), "ExceptionPointers").ToInt32() != 4 || Marshal.OffsetOf(typeof(DumpExceptionInformation), "ClientPointers").ToInt32() != 12) throw new InvalidOperationException("Unexpected packed minidump ABI.");
        if (Marshal.SizeOf(typeof(MemoryBasicInformation)) != 48 || Marshal.OffsetOf(typeof(MemoryBasicInformation), "Protect").ToInt32() != 36) throw new InvalidOperationException("Unexpected Win64 VirtualQueryEx ABI.");
        string runtime = Path.Combine(Environment.SystemDirectory, "ucrtbase_clr0400.dll");
        Console.WriteLine("abort export RVA=0x" + Pe.ExportRva(runtime, "abort").ToString("X"));
        return 0;
    }

    private static int Run(string[] args)
    {
        if (args.Length < 3 || args.Length > 5) throw new ArgumentException("Usage: NativeCrashProbe.exe <NativeTests.exe> <suite> <output-directory> [timeout-seconds] [late-attach-trace-marker]");
        string root = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string target = Path.GetFullPath(args[0]), suite = args[1];
        OutputDirectory = Path.GetFullPath(args[2]);
        if (!target.StartsWith(root + Path.DirectorySeparatorChar + ".runtime" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(Path.GetFileName(target), "NativeTests.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
            throw new ArgumentException("Target must be the existing project .runtime NativeTests.exe.");
        if (!OutputDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Evidence must stay inside the project.");
        if (suite.Length == 0 || suite.Any(character => !Char.IsLetterOrDigit(character) && character != '_')) throw new ArgumentException("Invalid suite name.");
        int timeout = args.Length >= 4 ? Int32.Parse(args[3], CultureInfo.InvariantCulture) : 240;
        if (timeout < 10 || timeout > 1200) throw new ArgumentException("Timeout outside 10..1200 seconds.");
        string lateMarker = args.Length == 5 ? args[4] : null;
        if (lateMarker != null && (lateMarker.Length == 0 || lateMarker.Length > 256)) throw new ArgumentException("Invalid late-attach marker.");
        Directory.CreateDirectory(OutputDirectory);
        Log = new StreamWriter(Path.Combine(OutputDirectory, "debugger.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
        Write("Target=" + target + "; suite=" + suite + "; launcher64=" + Environment.Is64BitProcess);
        Write("Input desktop before launch: " + InputDesktopName());
        if (!Environment.Is64BitProcess) throw new InvalidOperationException("Compile/run the diagnostic launcher as x64.");
        using (var stdout = new FileStream(Path.Combine(OutputDirectory, "stdout.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        using (var stderr = new FileStream(Path.Combine(OutputDirectory, "stderr.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            SetHandleInformation(stdout.SafeFileHandle.DangerousGetHandle(), 1, 1);
            SetHandleInformation(stderr.SafeFileHandle.DangerousGetHandle(), 1, 1);
            var startup = new StartupInfo { Size = (uint)Marshal.SizeOf(typeof(StartupInfo)), Flags = 0x100,
                StdOutput = stdout.SafeFileHandle.DangerousGetHandle(), StdError = stderr.SafeFileHandle.DangerousGetHandle(), StdInput = GetStdHandle(-10) };
            ProcessInformation process;
            if (!CreateProcess(target, new StringBuilder("\"" + target + "\" --suite " + suite), IntPtr.Zero, IntPtr.Zero, true,
                (lateMarker == null ? DebugOnlyThisProcess : 0) | CreateNoWindow, IntPtr.Zero, root, ref startup, out process)) throw new Win32Exception();
            ProcessHandle = process.Process; ProcessId = process.ProcessId; Threads[process.ThreadId] = process.Thread;
            Write("Started exact child PID=" + ProcessId + "; initial thread=" + process.ThreadId);
            if (lateMarker != null && !AttachAtTraceMarker(root, target, lateMarker, timeout)) return 2;
            SymSetOptions(0x2 | 0x4 | 0x10 | 0x200 | 0x400 | 0x80000);
            string symbolDirectory = Path.Combine(OutputDirectory, "symbols"); Directory.CreateDirectory(symbolDirectory);
            SymbolsInitialized = SymInitialize(ProcessHandle, symbolDirectory, false);
            Write("SymInitialize=" + SymbolsInitialized + "; search=" + symbolDirectory + "; network symbols disabled by default.");
            bool initialBreakpoint = true, exited = false; uint exitCode = 0;
            while (!exited)
            {
                DebugEvent state;
                if (!WaitForDebugEventEx(out state, 250))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 121 && error != 0) throw new Win32Exception(error);
                    if (Watch.Elapsed.TotalSeconds > timeout)
                    {
                        Write("Own child deadline exceeded; terminating only PID=" + ProcessId);
                        TerminateProcess(ProcessHandle, 0xDEAD0001); timeout += 10;
                    }
                    continue;
                }
                uint disposition = DebugContinue;
                try
                {
                    if (state.ProcessId != ProcessId) throw new InvalidOperationException("Unexpected debug child; DEBUG_ONLY_THIS_PROCESS contract failed.");
                    switch (state.Code)
                    {
                        case CreateProcessEvent:
                            if (state.CreateProcess.Thread != IntPtr.Zero && !Threads.ContainsKey(state.ThreadId)) Threads[state.ThreadId] = state.CreateProcess.Thread;
                            else if (state.CreateProcess.Thread != IntPtr.Zero && state.CreateProcess.Thread != Threads[state.ThreadId]) CloseHandle(state.CreateProcess.Thread);
                            if (state.CreateProcess.Process != IntPtr.Zero && state.CreateProcess.Process != ProcessHandle) CloseHandle(state.CreateProcess.Process);
                            AddModule(state.CreateProcess.BaseImage, state.CreateProcess.File, target);
                            if (state.CreateProcess.File != IntPtr.Zero) CloseHandle(state.CreateProcess.File);
                            break;
                        case CreateThreadEvent:
                            Threads[state.ThreadId] = state.CreateThread.Thread;
                            break;
                        case ExitThreadEvent:
                            IntPtr ended; if (Threads.TryGetValue(state.ThreadId, out ended)) { CloseHandle(ended); Threads.Remove(state.ThreadId); }
                            break;
                        case LoadDllEvent:
                            AddModule(state.LoadDll.Base, state.LoadDll.File, null);
                            if (state.LoadDll.File != IntPtr.Zero) CloseHandle(state.LoadDll.File);
                            break;
                        case UnloadDllEvent:
                            ulong address = unchecked((ulong)state.UnloadBase.ToInt64());
                            Modules.Remove(address); if (SymbolsInitialized) SymUnloadModule64(ProcessHandle, address);
                            break;
                        case DebugStringEvent:
                            string value = ReadString(state.DebugString.Address, state.DebugString.Length, state.DebugString.Unicode != 0);
                            Write("OutputDebugString T" + state.ThreadId + ": " + value.Replace("\r", "\\r").Replace("\n", "\\n"));
                            break;
                        case ExceptionEvent:
                            uint code = state.Exception.Record.Code; ulong exceptionAddress = state.Exception.Record.Address;
                            if (code == Breakpoint && AbortBreakpointArmed && exceptionAddress == AbortAddress)
                            {
                                RestoreAbortBreakpoint();
                                using (var context = ThreadContext(state.ThreadId))
                                {
                                    Marshal.WriteInt64(context.Pointer, 0xF8, unchecked((long)AbortAddress));
                                    if (!SetThreadContext(Threads[state.ThreadId], context.Pointer)) throw new Win32Exception();
                                }
                                Capture("abort-entry", state);
                                // Resume the original abort. Capturing a diagnostic must
                                // never convert this fatal failure into a passing test.
                            }
                            else if (code == Breakpoint && initialBreakpoint)
                            {
                                initialBreakpoint = false; Write("Initial loader breakpoint at " + Resolve(exceptionAddress));
                            }
                            else
                            {
                                Write("Exception code=0x" + code.ToString("X8") + "; first=" + state.Exception.FirstChance + "; T" + state.ThreadId + "; " + Resolve(exceptionAddress));
                                if (code == 0xE06D7363 && state.Exception.FirstChance != 0) CaptureFirstChanceCpp(state);
                                if (code == FastFail || state.Exception.FirstChance == 0) Capture("exception-" + code.ToString("X8"), state);
                                disposition = DebugExceptionNotHandled;
                            }
                            break;
                        case ExitProcessEvent:
                            exitCode = state.ExitCode; exited = ProcessExited = true;
                            Write("Child exit=0x" + exitCode.ToString("X8") + "; capturedFatal=" + FatalCaptured);
                            break;
                    }
                }
                finally { if (!ContinueDebugEvent(state.ProcessId, state.ThreadId, disposition)) Write("ContinueDebugEvent failed: " + Marshal.GetLastWin32Error()); }
            }
            File.WriteAllText(Path.Combine(OutputDirectory, "exit-code.txt"), "0x" + exitCode.ToString("X8") + Environment.NewLine + "FatalCaptured=" + FatalCaptured, new UTF8Encoding(false));
            return exitCode == 0 && !FatalCaptured ? 0 : 1;
        }
    }

    private static bool AttachAtTraceMarker(string root, string target, string marker, int timeout)
    {
        string trace = Environment.GetEnvironmentVariable("CODEX_ISLAND_NATIVE_TRACE_PATH");
        if (String.IsNullOrEmpty(trace)) throw new ArgumentException("Late attach requires CODEX_ISLAND_NATIVE_TRACE_PATH.");
        trace = Path.GetFullPath(trace);
        if (!trace.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Trace must be inside this project.");
        Write("Normal launch; waiting for committed trace marker=" + marker + "; file=" + trace);
        MemoryMappedFile map = null; MemoryMappedViewAccessor view = null; long scanned = 0; int traceOpenFailures = 0;
        try
        {
            while (Watch.Elapsed.TotalSeconds <= timeout)
            {
                uint exit;
                if (!GetExitCodeProcess(ProcessHandle, out exit)) throw new Win32Exception();
                if (exit != 259)
                {
                    ProcessExited = true; Write("Child exited before attachment; exit=0x" + exit.ToString("X8") + "; marker not reached. Capture inconclusive.");
                    File.WriteAllText(Path.Combine(OutputDirectory, "exit-code.txt"), "0x" + exit.ToString("X8") + Environment.NewLine + "Attached=False; marker=" + marker, new UTF8Encoding(false));
                    return false;
                }
                if (view == null && File.Exists(trace))
                {
                    try
                    {
                        var stream = new FileStream(trace, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (stream.Length < 128) stream.Dispose();
                        else { map = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false); view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read); if (traceOpenFailures > 0) Write("Trace mapping opened after " + traceOpenFailures + " retryable IO failure(s)."); }
                    }
                    catch (IOException error) { if (++traceOpenFailures == 1) Write("Trace mapping open retry (first IOException): " + error.Message + "; HResult=0x" + error.HResult.ToString("X8") + "; child continues normally."); }
                }
                if (view != null && view.Capacity >= 128)
                {
                    byte[] magic = new byte[8]; view.ReadArray(0, magic, 0, magic.Length);
                    if (Encoding.ASCII.GetString(magic) == "CILAYT01")
                    {
                        int pid = view.ReadInt32(32);
                        if (pid != 0 && pid != ProcessId) throw new InvalidDataException("Trace PID does not match the exact child created by this launcher.");
                        long latest = view.ReadInt64(24);
                        long first = Math.Max(scanned + 1, Math.Max(1, latest - 4095));
                        for (long sequence = first; sequence <= latest; sequence++)
                        {
                            long offset = 128 + ((sequence - 1) % 4096) * 512;
                            if (offset + 512 > view.Capacity) throw new InvalidDataException("Trace record exceeds mapping.");
                            long committed = view.ReadInt64(offset); if (committed != sequence) break;
                            int length = view.ReadInt32(offset + 20); if (length < 0 || length > 488) continue;
                            byte[] bytes = new byte[length]; view.ReadArray(offset + 24, bytes, 0, bytes.Length);
                            if (view.ReadInt64(offset) != sequence) continue;
                            string text = Encoding.UTF8.GetString(bytes); scanned = sequence;
                            if (text.IndexOf(marker, StringComparison.Ordinal) < 0) continue;
                            var path = new StringBuilder(32768); uint capacity = (uint)path.Capacity;
                            if (!QueryFullProcessImageName(ProcessHandle, 0, path, ref capacity) || !String.Equals(Path.GetFullPath(path.ToString()), target, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Owned child executable path changed before attachment.");
                            if (!GetExitCodeProcess(ProcessHandle, out exit) || exit != 259) continue;
                            Write("Late-attach marker seq=" + sequence + "; " + text + "; path verified=" + path);
                            if (!DebugActiveProcess(ProcessId)) throw new Win32Exception();
                            Write("DebugActiveProcess attached only owned PID=" + ProcessId + "; input desktop=" + InputDesktopName());
                            return true;
                        }
                    }
                }
                Thread.Sleep(5);
            }
            throw new TimeoutException("Owned child did not reach late-attach marker before deadline.");
        }
        finally { if (view != null) view.Dispose(); if (map != null) map.Dispose(); }
    }

    private static void AddModule(IntPtr imageBase, IntPtr fileHandle, string fallback)
    {
        string path = fallback;
        if (fileHandle != IntPtr.Zero)
        {
            var buffer = new StringBuilder(32768); uint length = GetFinalPathNameByHandle(fileHandle, buffer, buffer.Capacity, 0);
            if (length > 0 && length < buffer.Capacity) { path = buffer.ToString(); if (path.StartsWith("\\\\?\\")) path = path.Substring(4); }
        }
        ulong address = unchecked((ulong)imageBase.ToInt64());
        var module = new Module { Base = address, Path = path ?? "<unknown>", Size = Pe.ImageSize(path) };
        Modules[address] = module;
        if (SymbolsInitialized) SymLoadModuleEx(ProcessHandle, fileHandle, path, null, address, 0, IntPtr.Zero, 0);
        Write("Module " + address.ToString("X16") + " size=" + module.Size.ToString("X") + " " + module.Path);
        if (String.Equals(System.IO.Path.GetFileName(path), "ucrtbase_clr0400.dll", StringComparison.OrdinalIgnoreCase))
        {
            uint rva = Pe.ExportRva(path, "abort");
            if (rva == 0) { Write("ERROR: abort export absent; fatal event capture remains enabled."); return; }
            AbortAddress = address + rva; byte[] original = ReadMemory(AbortAddress, 1); AbortOriginalByte = original[0];
            WriteMemory(AbortAddress, new byte[] { 0xCC }); AbortBreakpointArmed = true;
            Write("Armed own-child abort entry INT3 at " + Resolve(AbortAddress) + "; RVA=0x" + rva.ToString("X") + "; original=0x" + AbortOriginalByte.ToString("X2"));
        }
    }

    private static void RestoreAbortBreakpoint()
    {
        WriteMemory(AbortAddress, new[] { AbortOriginalByte }); AbortBreakpointArmed = false;
        Write("Restored original abort entry byte before capture/resume.");
    }

    private static void CaptureFirstChanceCpp(DebugEvent state)
    {
        int number = ++CppExceptionsObserved;
        if (number > 32) { if (number == 33) Write("First-chance C++ stack cap reached (32). No additional C++ stacks/dumps will be written."); return; }
        string path = Path.Combine(OutputDirectory, "cpp-first-" + number.ToString("000") + ".stack.txt");
        using (var log = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            var record = state.Exception.Record;
            ulong[] information = { record.Information0, record.Information1, record.Information2, record.Information3, record.Information4, record.Information5, record.Information6, record.Information7, record.Information8, record.Information9, record.Information10, record.Information11, record.Information12, record.Information13, record.Information14 };
            log.WriteLine("FIRST-CHANCE C++ ONLY; handled by original program after capture; no fatal inference. TID=" + state.ThreadId);
            for (int index = 0; index < Math.Min(15, record.Parameters); index++) log.WriteLine("EH[" + index + "]=0x" + information[index].ToString("X16") + " " + Resolve(information[index]));
            if (record.Parameters >= 3)
            {
                try
                {
                    byte[] instance = ReadMemory(information[1], 64); log.WriteLine("OBJECT64=" + BitConverter.ToString(instance));
                    if (record.Parameters >= 4 && information[3] != 0)
                    {
                        ulong image = information[3]; byte[] throwInfo = ReadMemory(information[2], 16);
                        ulong catchArray = checked(image + BitConverter.ToUInt32(throwInfo, 12)); byte[] array = ReadMemory(catchArray, 8);
                        int types = BitConverter.ToInt32(array, 0); log.WriteLine("CATCHABLE_TYPE_COUNT=" + types);
                        if (types > 0 && types < 1000)
                        {
                            ulong catchType = checked(image + BitConverter.ToUInt32(array, 4)); byte[] type = ReadMemory(catchType, 28);
                            ulong descriptor = checked(image + BitConverter.ToUInt32(type, 4));
                            log.WriteLine("DECORATED_TYPE=" + ReadCString(descriptor + 16, 512));
                        }
                    }
                    ulong candidate = BitConverter.ToUInt64(instance, 8);
                    if (candidate > 0x10000 && candidate < 0x0000800000000000) log.WriteLine("OBJECT_PLUS8_CSTRING_CANDIDATE=" + ReadCString(candidate, 512));
                }
                catch (Exception error) { log.WriteLine("Guarded EH/object read stopped: " + error.Message); }
            }
            try
            {
                using (var context = ThreadContext(state.ThreadId))
                {
                    var frame = new StackFrame64 { PC = new Address64 { Offset = unchecked((ulong)Marshal.ReadInt64(context.Pointer, 0xF8)), Mode = 3 }, Stack = new Address64 { Offset = unchecked((ulong)Marshal.ReadInt64(context.Pointer, 0x98)), Mode = 3 }, Frame = new Address64 { Offset = unchecked((ulong)Marshal.ReadInt64(context.Pointer, 0xA0)), Mode = 3 }, Parameters = new ulong[4], Reserved = new ulong[3], Helper = new KdHelp64 { Reserved = new ulong[2] } };
                    ulong lastPc = 0, lastStack = 0;
                    for (int index = 0; index < 100; index++)
                    {
                        if (!StackWalk64(MachineAmd64, ProcessHandle, Threads[state.ThreadId], ref frame, context.Pointer, IntPtr.Zero, FunctionTableAccess, ModuleBase, IntPtr.Zero)) break;
                        if (frame.PC.Offset == 0 || (lastPc == frame.PC.Offset && lastStack == frame.Stack.Offset)) break;
                        if (!ExecutableAddress(frame.PC.Offset)) { log.WriteLine("STOP: non-executable unwind PC 0x" + frame.PC.Offset.ToString("X16") + "; native chain is partial (use dump/ClrMD at managed boundary)."); break; }
                        log.WriteLine(index.ToString("000") + " SP=" + frame.Stack.Offset.ToString("X16") + " " + Resolve(frame.PC.Offset));
                        lastPc = frame.PC.Offset; lastStack = frame.Stack.Offset;
                    }
                }
            }
            catch (Exception error) { log.WriteLine("Guarded C++ stack read stopped: " + error); }
        }
        Write("Saved first-chance C++ native stack/EH evidence " + path);
    }

    private static string ReadCString(ulong address, int maximum)
    {
        var bytes = new List<byte>();
        // One-byte reads avoid reading through a terminator into an unmapped
        // page when a native object or type descriptor is near a boundary.
        for (int index = 0; index < maximum; index++) { byte value = ReadMemory(address + (uint)index, 1)[0]; if (value == 0) break; bytes.Add(value); }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string InputDesktopName()
    {
        IntPtr desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return "OpenInputDesktop failed=" + Marshal.GetLastWin32Error();
        try
        {
            var text = new StringBuilder(512); uint required;
            return GetUserObjectInformation(desktop, 2, text, (uint)(text.Capacity * 2), out required) ? text.ToString() : "GetUserObjectInformation failed=" + Marshal.GetLastWin32Error();
        }
        finally { CloseDesktop(desktop); }
    }

    private static void Capture(string label, DebugEvent state)
    {
        Write("CAPTURE " + label + "; T" + state.ThreadId + "; first=" + state.Exception.FirstChance);
        Write("Input desktop at capture: " + InputDesktopName());
        string prefix = Path.Combine(OutputDirectory, label);
        using (var context = ThreadContext(state.ThreadId))
        using (var stack = new StreamWriter(prefix + ".stack.txt", false, new UTF8Encoding(false)))
        {
            ulong rip = unchecked((ulong)Marshal.ReadInt64(context.Pointer, 0xF8)), rsp = unchecked((ulong)Marshal.ReadInt64(context.Pointer, 0x98)), rbp = unchecked((ulong)Marshal.ReadInt64(context.Pointer, 0xA0));
            stack.WriteLine("PID=" + ProcessId + "; TID=" + state.ThreadId + "; RIP=" + Resolve(rip) + "; RSP=0x" + rsp.ToString("X16") + "; RBP=0x" + rbp.ToString("X16"));
            try { byte[] raw = ReadMemory(rsp, 512); for (int offset = 0; offset + 8 <= raw.Length; offset += 8) { ulong candidate = BitConverter.ToUInt64(raw, offset); stack.WriteLine("[RSP+0x" + offset.ToString("X3") + "] 0x" + candidate.ToString("X16") + " " + Resolve(candidate)); } } catch (Exception error) { stack.WriteLine("Raw stack read: " + error.Message); }
            stack.WriteLine("UNWOUND STACK:");
            var frame = new StackFrame64 { PC = new Address64 { Offset = rip, Mode = 3 }, Stack = new Address64 { Offset = rsp, Mode = 3 }, Frame = new Address64 { Offset = rbp, Mode = 3 }, Parameters = new ulong[4], Reserved = new ulong[3], Helper = new KdHelp64 { Reserved = new ulong[2] } };
            ulong lastPc = 0, lastStack = 0;
            for (int index = 0; index < 160; index++)
            {
                if (!StackWalk64(MachineAmd64, ProcessHandle, Threads[state.ThreadId], ref frame, context.Pointer, IntPtr.Zero, FunctionTableAccess, ModuleBase, IntPtr.Zero)) { stack.WriteLine("StackWalk64 stopped: " + Marshal.GetLastWin32Error()); break; }
                if (frame.PC.Offset == 0 || (lastPc == frame.PC.Offset && lastStack == frame.Stack.Offset)) break;
                if (!ExecutableAddress(frame.PC.Offset)) { stack.WriteLine("STOP: non-executable unwind PC 0x" + frame.PC.Offset.ToString("X16") + "; native chain is partial (use dump/ClrMD at managed boundary)."); break; }
                stack.WriteLine(index.ToString("000") + " SP=" + frame.Stack.Offset.ToString("X16") + " " + Resolve(frame.PC.Offset) + "; return=" + Resolve(frame.Return.Offset));
                lastPc = frame.PC.Offset; lastStack = frame.Stack.Offset;
            }
        }
        using (var all = new StreamWriter(prefix + ".all-threads.stack.txt", false, new UTF8Encoding(false)))
        {
            foreach (var thread in Threads.Keys.OrderBy(value => value).ToArray())
            {
                try
                {
                    using (var context = ThreadContext(thread))
                    {
                        ulong rip = unchecked((ulong)Marshal.ReadInt64(context.Pointer, 0xF8)), rsp = unchecked((ulong)Marshal.ReadInt64(context.Pointer, 0x98)), rbp = unchecked((ulong)Marshal.ReadInt64(context.Pointer, 0xA0));
                        all.WriteLine("THREAD " + thread + (thread == state.ThreadId ? " CAPTURE THREAD" : "") + "; IP=" + Resolve(rip));
                        var frame = new StackFrame64 { PC = new Address64 { Offset = rip, Mode = 3 }, Stack = new Address64 { Offset = rsp, Mode = 3 }, Frame = new Address64 { Offset = rbp, Mode = 3 }, Parameters = new ulong[4], Reserved = new ulong[3], Helper = new KdHelp64 { Reserved = new ulong[2] } };
                        ulong lastPc = 0, lastStack = 0;
                        for (int index = 0; index < 160; index++)
                        {
                            if (!StackWalk64(MachineAmd64, ProcessHandle, Threads[thread], ref frame, context.Pointer, IntPtr.Zero, FunctionTableAccess, ModuleBase, IntPtr.Zero)) break;
                            if (frame.PC.Offset == 0 || (lastPc == frame.PC.Offset && lastStack == frame.Stack.Offset)) break;
                            if (!ExecutableAddress(frame.PC.Offset)) { all.WriteLine("  STOP: non-executable unwind PC 0x" + frame.PC.Offset.ToString("X16") + "; native chain is partial (use dump/ClrMD at managed boundary)."); break; }
                            all.WriteLine("  " + index.ToString("000") + " SP=" + frame.Stack.Offset.ToString("X16") + " " + Resolve(frame.PC.Offset));
                            lastPc = frame.PC.Offset; lastStack = frame.Stack.Offset;
                        }
                    }
                }
                catch (Exception error) { all.WriteLine("THREAD " + thread + " partial native context: " + error.Message); }
            }
        }
        SaveModules(prefix + ".modules.txt");
        if (FatalCaptured) { Write("Full dump already captured; retained additional stack only."); return; }
        FatalCaptured = true;
        var record = state.Exception.Record; record.Record = 0;
        IntPtr nativeRecord = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ExceptionRecord)));
        IntPtr pointers = Marshal.AllocHGlobal(IntPtr.Size * 2);
        using (var context = ThreadContext(state.ThreadId))
        using (var dump = new FileStream(prefix + ".dmp", FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            try
            {
                Marshal.StructureToPtr(record, nativeRecord, false); Marshal.WriteIntPtr(pointers, 0, nativeRecord); Marshal.WriteIntPtr(pointers, IntPtr.Size, context.Pointer);
                var exception = new DumpExceptionInformation { ThreadId = state.ThreadId, ExceptionPointers = pointers, ClientPointers = false };
                bool written = MiniDumpWriteDump(ProcessHandle, ProcessId, dump.SafeFileHandle.DangerousGetHandle(), MinidumpFlags, ref exception, IntPtr.Zero, IntPtr.Zero);
                Write("MiniDumpWriteDump=" + written + "; error=" + Marshal.GetLastWin32Error() + "; " + prefix + ".dmp");
                if (!written) throw new Win32Exception();
            }
            finally { Marshal.FreeHGlobal(pointers); Marshal.FreeHGlobal(nativeRecord); }
        }
    }

    private static void SaveModules(string path) { File.WriteAllLines(path, Modules.Values.Select(module => module.Base.ToString("X16") + " " + module.Size.ToString("X8") + " " + module.Path), new UTF8Encoding(false)); }
    private static bool ExecutableAddress(ulong address)
    {
        MemoryBasicInformation information;
        UIntPtr size = VirtualQueryEx(ProcessHandle, new IntPtr(unchecked((long)address)), out information, new UIntPtr((uint)Marshal.SizeOf(typeof(MemoryBasicInformation))));
        return size != UIntPtr.Zero && information.State == 0x1000 && (information.Protect & 0xF0) != 0 && (information.Protect & 0x100) == 0;
    }
    private static string Resolve(ulong address)
    {
        if (address == 0) return "0";
        Module module = Modules.Values.LastOrDefault(item => item.Base <= address && (item.Size == 0 || address < item.Base + item.Size));
        string value = module == null ? "0x" + address.ToString("X16") : Path.GetFileName(module.Path) + "+0x" + (address - module.Base).ToString("X");
        if (!SymbolsInitialized) return value;
        IntPtr symbol = Marshal.AllocHGlobal(88 + 2048);
        try
        {
            for (int offset = 0; offset < 88; offset += 4) Marshal.WriteInt32(symbol, offset, 0);
            Marshal.WriteInt32(symbol, 0, 88); Marshal.WriteInt32(symbol, 80, 2047); ulong displacement;
            if (SymFromAddr(ProcessHandle, address, out displacement, symbol))
            {
                string name = Marshal.PtrToStringAnsi(new IntPtr(symbol.ToInt64() + 84), Math.Min(2047, Marshal.ReadInt32(symbol, 76)));
                return value + " [" + name + "+0x" + displacement.ToString("X") + "]";
            }
        }
        finally { Marshal.FreeHGlobal(symbol); }
        return value;
    }
    private static ContextBuffer ThreadContext(uint thread)
    {
        IntPtr handle; if (!Threads.TryGetValue(thread, out handle)) throw new InvalidOperationException("Debug thread handle absent.");
        var context = new ContextBuffer(); Marshal.WriteInt32(context.Pointer, 0x30, unchecked((int)ContextFull));
        if (!GetThreadContext(handle, context.Pointer)) { context.Dispose(); throw new Win32Exception(); } return context;
    }
    private static byte[] ReadMemory(ulong address, int count)
    {
        byte[] buffer = new byte[count]; UIntPtr read;
        if (!ReadProcessMemory(ProcessHandle, new IntPtr(unchecked((long)address)), buffer, new UIntPtr((uint)count), out read) || read.ToUInt64() != (uint)count) throw new Win32Exception(); return buffer;
    }
    private static void WriteMemory(ulong address, byte[] bytes)
    {
        IntPtr location = new IntPtr(unchecked((long)address)); uint previous; UIntPtr written;
        if (!VirtualProtectEx(ProcessHandle, location, new UIntPtr((uint)bytes.Length), 0x40, out previous)) throw new Win32Exception();
        try { if (!WriteProcessMemory(ProcessHandle, location, bytes, new UIntPtr((uint)bytes.Length), out written) || written.ToUInt64() != (uint)bytes.Length) throw new Win32Exception(); FlushInstructionCache(ProcessHandle, location, new UIntPtr((uint)bytes.Length)); }
        finally { uint ignored; VirtualProtectEx(ProcessHandle, location, new UIntPtr((uint)bytes.Length), previous, out ignored); }
    }
    private static string ReadString(IntPtr address, int length, bool unicode) { if (length == 0) return ""; try { byte[] bytes = ReadMemory(unchecked((ulong)address.ToInt64()), length * (unicode ? 2 : 1)); string text = (unicode ? Encoding.Unicode : Encoding.Default).GetString(bytes); int end = text.IndexOf('\0'); return end < 0 ? text : text.Substring(0, end); } catch (Exception error) { return "<read failed: " + error.Message + ">"; } }
    private static void Write(string value) { string line = Watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms " + value; Console.WriteLine(line); if (Log != null) Log.WriteLine(line); }

    private sealed class Module { internal ulong Base; internal uint Size; internal string Path; }
    private sealed class ContextBuffer : IDisposable
    {
        private IntPtr allocation; internal IntPtr Pointer;
        internal ContextBuffer() { allocation = Marshal.AllocHGlobal(1248); Pointer = new IntPtr((allocation.ToInt64() + 15) & ~15L); for (int index = 0; index < 1232; index += 8) Marshal.WriteInt64(Pointer, index, 0); }
        public void Dispose() { if (allocation != IntPtr.Zero) { Marshal.FreeHGlobal(allocation); allocation = IntPtr.Zero; } }
    }
    private static class Pe
    {
        internal static uint ImageSize(string path) { try { if (String.IsNullOrEmpty(path) || !File.Exists(path)) return 0; using (var reader = new BinaryReader(File.OpenRead(path))) { reader.BaseStream.Position = 0x3C; int header = reader.ReadInt32(); reader.BaseStream.Position = header + 24 + 56; return reader.ReadUInt32(); } } catch (IOException) { return 0; } catch (UnauthorizedAccessException) { return 0; } }
        internal static uint ExportRva(string path, string name)
        {
            byte[] bytes = File.ReadAllBytes(path); int pe = BitConverter.ToInt32(bytes, 0x3C), optional = pe + 24; int sections = BitConverter.ToUInt16(bytes, pe + 6), headerSize = BitConverter.ToUInt16(bytes, pe + 20);
            uint exports = BitConverter.ToUInt32(bytes, optional + (BitConverter.ToUInt16(bytes, optional) == 0x20B ? 112 : 96));
            Func<uint, int> offset = rva => { for (int index = 0; index < sections; index++) { int section = optional + headerSize + index * 40; uint start = BitConverter.ToUInt32(bytes, section + 12), size = Math.Max(BitConverter.ToUInt32(bytes, section + 8), BitConverter.ToUInt32(bytes, section + 16)); if (rva >= start && rva < start + size) return checked((int)(rva - start + BitConverter.ToUInt32(bytes, section + 20))); } return checked((int)rva); };
            int directory = offset(exports); uint count = BitConverter.ToUInt32(bytes, directory + 24), functions = BitConverter.ToUInt32(bytes, directory + 28), names = BitConverter.ToUInt32(bytes, directory + 32), ordinals = BitConverter.ToUInt32(bytes, directory + 36);
            for (uint index = 0; index < count; index++) { int text = offset(BitConverter.ToUInt32(bytes, offset(names) + (int)index * 4)); int end = text; while (end < bytes.Length && bytes[end] != 0) end++; if (Encoding.ASCII.GetString(bytes, text, end - text) == name) return BitConverter.ToUInt32(bytes, offset(functions) + BitConverter.ToUInt16(bytes, offset(ordinals) + (int)index * 2) * 4); } return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { internal uint Size; internal string Reserved, Desktop, Title; internal uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags; internal ushort ShowWindow, Reserved2; internal IntPtr ReservedPointer, StdInput, StdOutput, StdError; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Explicit, Size = 176)] private struct DebugEvent { [FieldOffset(0)] internal uint Code; [FieldOffset(4)] internal uint ProcessId; [FieldOffset(8)] internal uint ThreadId; [FieldOffset(16)] internal ExceptionDebugInformation Exception; [FieldOffset(16)] internal CreateProcessDebugInformation CreateProcess; [FieldOffset(16)] internal CreateThreadDebugInformation CreateThread; [FieldOffset(16)] internal LoadDllDebugInformation LoadDll; [FieldOffset(16)] internal OutputDebugStringInformation DebugString; [FieldOffset(16)] internal uint ExitCode; [FieldOffset(16)] internal IntPtr UnloadBase; }
    [StructLayout(LayoutKind.Sequential)] private struct ExceptionRecord { internal uint Code, Flags; internal ulong Record, Address; internal uint Parameters, Alignment; internal ulong Information0, Information1, Information2, Information3, Information4, Information5, Information6, Information7, Information8, Information9, Information10, Information11, Information12, Information13, Information14; }
    [StructLayout(LayoutKind.Sequential)] private struct ExceptionDebugInformation { internal ExceptionRecord Record; internal uint FirstChance; }
    [StructLayout(LayoutKind.Sequential)] private struct CreateProcessDebugInformation { internal IntPtr File, Process, Thread, BaseImage; internal uint DebugOffset, DebugSize; internal IntPtr ThreadLocal, Start, ImageName; internal ushort Unicode; }
    [StructLayout(LayoutKind.Sequential)] private struct CreateThreadDebugInformation { internal IntPtr Thread, ThreadLocal, Start; }
    [StructLayout(LayoutKind.Sequential)] private struct LoadDllDebugInformation { internal IntPtr File, Base; internal uint DebugOffset, DebugSize; internal IntPtr ImageName; internal ushort Unicode; }
    [StructLayout(LayoutKind.Sequential)] private struct OutputDebugStringInformation { internal IntPtr Address; internal ushort Unicode, Length; }
    [StructLayout(LayoutKind.Sequential)] private struct Address64 { internal ulong Offset; internal ushort Segment; internal uint Mode; }
    [StructLayout(LayoutKind.Sequential)] private struct KdHelp64 { internal ulong Thread; internal uint CallbackStack, CallbackBStore, NextCallback, FramePointer; internal ulong KiCallUserMode, KeUserCallbackDispatcher, SystemRangeStart, KiUserExceptionDispatcher, StackBase, StackLimit; internal uint BuildVersion, RetpolineTableSize; internal ulong RetpolineTable; internal uint RetpolineOffset, RetpolineSize; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct StackFrame64 { internal Address64 PC, Return, Frame, Stack, BackingStore; internal IntPtr FunctionTable; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] internal ulong[] Parameters; internal int Far, Virtual; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] internal ulong[] Reserved; internal KdHelp64 Helper; }
    // minidumpapiset.h explicitly includes pshpack4.h even on Win64. This
    // information structure is 16 bytes: ThreadId@0, pointers@4, BOOL@12.
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct DumpExceptionInformation { internal uint ThreadId; internal IntPtr ExceptionPointers; [MarshalAs(UnmanagedType.Bool)] internal bool ClientPointers; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryBasicInformation { internal IntPtr BaseAddress, AllocationBase; internal uint AllocationProtect; internal ushort PartitionId, Alignment; internal UIntPtr RegionSize; internal uint State, Protect, Type, Padding; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr FunctionTableCallback(IntPtr process, ulong address);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate ulong ModuleBaseCallback(IntPtr process, ulong address);
    private static readonly FunctionTableCallback FunctionTableAccess = SymFunctionTableAccess64;
    private static readonly ModuleBaseCallback ModuleBase = SymGetModuleBase64;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WaitForDebugEventEx(out DebugEvent state, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ContinueDebugEvent(uint process, uint thread, uint status);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadContext(IntPtr thread, IntPtr context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetThreadContext(IntPtr thread, IntPtr context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr length, out UIntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr length, out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtectEx(IntPtr process, IntPtr address, UIntPtr length, uint protect, out uint previous);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int standard);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(IntPtr file, StringBuilder path, int length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DebugActiveProcess(uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation information, UIntPtr length);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetUserObjectInformationW")] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder information, uint length, out uint required);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("dbghelp.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern bool SymInitialize(IntPtr process, string searchPath, bool invade);
    [DllImport("dbghelp.dll", SetLastError = true)] private static extern bool SymCleanup(IntPtr process);
    [DllImport("dbghelp.dll")] private static extern uint SymSetOptions(uint options);
    [DllImport("dbghelp.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern ulong SymLoadModuleEx(IntPtr process, IntPtr file, string image, string module, ulong baseAddress, uint size, IntPtr data, uint flags);
    [DllImport("dbghelp.dll", SetLastError = true)] private static extern bool SymUnloadModule64(IntPtr process, ulong baseAddress);
    [DllImport("dbghelp.dll", SetLastError = true)] private static extern bool SymFromAddr(IntPtr process, ulong address, out ulong displacement, IntPtr symbol);
    [DllImport("dbghelp.dll", SetLastError = true)] private static extern IntPtr SymFunctionTableAccess64(IntPtr process, ulong address);
    [DllImport("dbghelp.dll", SetLastError = true)] private static extern ulong SymGetModuleBase64(IntPtr process, ulong address);
    [DllImport("dbghelp.dll", SetLastError = true)] private static extern bool StackWalk64(uint machine, IntPtr process, IntPtr thread, ref StackFrame64 frame, IntPtr context, IntPtr readMemory, FunctionTableCallback functionTable, ModuleBaseCallback moduleBase, IntPtr translate);
    [DllImport("dbghelp.dll", SetLastError = true)] private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, IntPtr file, uint type, ref DumpExceptionInformation exception, IntPtr userStream, IntPtr callback);
}
