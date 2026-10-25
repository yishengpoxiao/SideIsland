using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Threading;

namespace CodexIsland
{
    // Diagnostic startup prefix only. Never claims to execute a complete suite.
    internal static class SettingsStartupProbe
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private static readonly HashSet<IntPtr> Registered = new HashSet<IntPtr>();
        private static string Project, ChildDirectory;
        private static StreamWriter OriginWriter;
        private static OwnThreadMessageOriginObserver Origin;
        private static object NativeProbe;
        private static MethodInfo NativeRegister, NativeFlush;
        private static int OriginLines, OriginBytes, OriginFlushes, NativeRegisterErrors;
        private static bool OriginLimit;
        private const int OriginMaximumLines = 32768, OriginMaximumBytes = 16 * 1024 * 1024;
        private const string ChildVariable = "CODEX_ISLAND_STARTUP_PROBE_CHILD_DIR";
        private const string TraceVariable = "CODEX_ISLAND_MINIMIZE_TRACE_DIR";
        private const string RequireNativeVariable = "CODEX_ISLAND_REQUIRE_NATIVE_MINIMIZE_PROBE";

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Project = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar);
            try
            {
                if (args.Length == 1 && args[0] == "--self-check")
                {
                    if (typeof(SettingsAndFilterMotionTests).GetField("settingTransitions", BindingFlags.NonPublic | BindingFlags.Static) == null) throw new InvalidOperationException("prefix counter contract missing");
                    Console.WriteLine("SELF-CHECK PASS; no UI, hook, ETW session or process started; startup prefix is not a complete suite.");
                    return 0;
                }
                if (!File.Exists(Path.Combine(Project, "tests", "NativeTests.cs"))) throw new InvalidOperationException("Project working directory required.");
                string expected = Path.Combine(Project, ".runtime", "tests", "NativeTests.exe");
                if (!String.Equals(Assembly.GetExecutingAssembly().Location, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Run only at the original .runtime/tests/NativeTests.exe path.");
                if (args.Length == 2 && args[0] == "--suite" && args[1] == "SettingsAndFilterMotionTests") return Child();
                return Parent(args);
            }
            catch (Exception error) { Console.Error.WriteLine("STARTUP PREFIX FAILURE: " + StartupWatchdogCapture.SafeError(error)); return 1; }
        }

        private static int Parent(string[] args)
        {
            int count = 24; string output = null, watch = null;
            for (int index = 0; index < args.Length; index += 2)
            {
                if (index + 1 == args.Length) throw new ArgumentException("Every option needs a value.");
                if (args[index] == "--startup-count") count = Int32.Parse(args[index + 1], CultureInfo.InvariantCulture);
                else if (args[index] == "--output") output = Inside(args[index + 1]);
                else if (args[index] == "--watchfile") watch = Inside(args[index + 1]);
                else throw new ArgumentException("Use --startup-count 1..24 --output fresh-project-dir --watchfile project-file.");
            }
            if (count < 1 || count > 24 || output == null || watch == null) throw new ArgumentException("Count/output/watch required; count 1..24.");
            if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Fresh coordinator output required.");
            Directory.CreateDirectory(output);
            Directory.CreateDirectory(Path.GetDirectoryName(watch));
            var clock = Stopwatch.StartNew(); var children = new List<object>(); bool completed = false; string failure = null; int startedCount = 0;
            Atomic(watch, "{\"Version\":1,\"Targets\":[]}");
            try
            {
                for (int index = 1; index <= count; index++)
                {
                    if (clock.Elapsed.TotalSeconds >= 800) throw new TimeoutException("Coordinator 800 second deadline; no retry.");
                    string childRoot = Path.Combine(output, "child-" + index.ToString("D2", CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(childRoot);
                    var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--suite SettingsAndFilterMotionTests")
                    {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = Project, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
                    };
                    start.EnvironmentVariables[ChildVariable] = childRoot;
                    start.EnvironmentVariables[TraceVariable] = Path.Combine(childRoot, "native-trace");
                    var childClock = Stopwatch.StartNew(); bool timedOut = false, limit = false; int pid = 0, exit = -1; string created = null;
                    using (var capture = new BoundedCapture(childRoot))
                    using (var child = new Process { StartInfo = start })
                    {
                        bool childStarted = false;
                        try
                        {
                        child.OutputDataReceived += delegate(object sender, DataReceivedEventArgs data) { capture.Output(data.Data); };
                        child.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs data) { capture.Error(data.Data); };
                        if (!child.Start()) throw new InvalidOperationException("Could not start owned child.");
                        childStarted = true; startedCount++;
                        IntPtr retained = child.Handle; pid = child.Id; created = child.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                        Save(Path.Combine(childRoot, "launch.json"), new { ProcessId = pid, ProcessStartUtc = created, Image = start.FileName, Arguments = start.Arguments, StartedUtc = DateTime.UtcNow.ToString("o"), PrefixOnly = true, AutomaticRetry = false });
                        Console.WriteLine("STARTUP CHILD START index=" + index + " pid=" + pid + " creation=" + created); Console.Out.Flush();
                        child.BeginOutputReadLine(); child.BeginErrorReadLine();
                        string childWatch = Path.Combine(childRoot, "native-trace", "targets.json"); string previous = null;
                        while (!child.WaitForExit(25))
                        {
                            Relay(childWatch, watch, pid, created, ref previous);
                            if (capture.LimitReached || childClock.Elapsed.TotalSeconds >= 60 || clock.Elapsed.TotalSeconds >= 800)
                            {
                                limit = capture.LimitReached; timedOut = !limit;
                                CaptureBeforeKill(child, childRoot, limit ? "stdout/stderr hard limit" : "60-second child or 800-second coordinator watchdog");
                                child.Kill(); // Only the exact process created and retained above, after dump attempt.
                                child.WaitForExit(); break;
                            }
                        }
                        child.WaitForExit(); exit = child.ExitCode; Relay(childWatch, watch, pid, created, ref previous); capture.Flush();
                        GC.KeepAlive(retained);
                        bool resultCompleted = false;
                        string childResultPath = Path.Combine(childRoot, "result.json");
                        if (File.Exists(childResultPath))
                        {
                            var detail = Json.DeserializeObject(File.ReadAllText(childResultPath)) as Dictionary<string, object>;
                            resultCompleted = detail != null && detail.ContainsKey("Completed") && Convert.ToBoolean(detail["Completed"], CultureInfo.InvariantCulture);
                        }
                        bool childPassed = exit == 0 && resultCompleted && !timedOut && !limit && capture.PrefixMarkers == 1 && capture.FullSuiteMarkers == 0 && capture.ErrorBytes == 0;
                        object record = new { Index = index, ProcessId = pid, ProcessStartUtc = created, ExitCode = exit, ExitHex = "0x" + unchecked((uint)exit).ToString("X8"), PrefixPassed = childPassed, PrefixOnly = true, AutomaticRetry = false, TimedOut = timedOut, LogLimitReached = limit, PrefixMarkers = capture.PrefixMarkers, FullSuiteMarkers = capture.FullSuiteMarkers, StdoutBytes = capture.OutputBytes, StderrBytes = capture.ErrorBytes, DurationMilliseconds = childClock.ElapsedMilliseconds, NativeTrace = "child-" + index.ToString("D2") + "/native-trace", NativeCallerProbe = "child-" + index.ToString("D2") + "/native-stacks" };
                        children.Add(record); Save(Path.Combine(childRoot, "coordinator-result.json"), record); Save(Path.Combine(output, "children.json"), children);
                        Console.WriteLine("STARTUP CHILD END index=" + index + " pid=" + pid + " exit=" + exit + " prefixPassed=" + childPassed); Console.Out.Flush();
                        Thread.Sleep(1000); // Drain existing events; does not pump or restore any window.
                        if (!childPassed) throw new InvalidOperationException("First nonpassing child retained; stop without retry: index=" + index + " pid=" + pid);
                        }
                        finally
                        {
                            if (childStarted && !child.HasExited)
                            {
                                Save(Path.Combine(childRoot, "coordinator-exception-cleanup.json"), new { ProcessId = child.Id, ExactCreatedChildOnly = true, Reason = "Coordinator exception while own child still running; terminate and wait before releasing logs or restarting installed app.", AutomaticRetry = false, PrefixOnly = true });
                                CaptureBeforeKill(child, childRoot, "coordinator exception cleanup");
                                child.Kill(); child.WaitForExit();
                            }
                        }
                    }
                }
                completed = true; return 0;
            }
            catch (Exception error) { failure = StartupWatchdogCapture.SafeError(error); throw; }
            finally
            {
                Save(Path.Combine(output, "result.json"), new { Completed = completed, RequestedChildren = count, StartedChildren = startedCount, RecordedTerminalChildren = children.Count, PrefixOnly = true, FullSuiteExecuted = false, AutomaticRetries = 0, StopOnFirstFailure = true, NoAutomaticRestore = true, ElapsedMilliseconds = clock.ElapsedMilliseconds, Failure = failure, HistoricalSenderProven = false, EndedUtc = DateTime.UtcNow.ToString("o") });
            }
        }

        private static int Child()
        {
            ChildDirectory = Inside(Environment.GetEnvironmentVariable(ChildVariable));
            if (!Directory.Exists(ChildDirectory)) throw new IOException("Coordinator-created child directory required.");
            string expectedTrace = Path.Combine(ChildDirectory, "native-trace");
            if (!String.Equals(Path.GetFullPath(Environment.GetEnvironmentVariable(TraceVariable) ?? ""), expectedTrace, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Exact child trace environment required.");
            var clock = Stopwatch.StartNew(); bool completed = false; string failure = null; int transitions = 0; bool nativeAvailable = false;
            OriginWriter = new StreamWriter(new FileStream(Path.Combine(ChildDirectory, "message-origin.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false), 8192);
            var previousContext = SynchronizationContext.Current;
            UnhandledExceptionEventHandler domainObserver = delegate(object sender, UnhandledExceptionEventArgs data) { SaveUnhandled("AppDomain.UnhandledException", data.ExceptionObject as Exception, data.IsTerminating); };
            DispatcherUnhandledExceptionEventHandler dispatcherObserver = delegate(object sender, DispatcherUnhandledExceptionEventArgs data) { SaveUnhandled("Dispatcher.UnhandledException", data.Exception, false); };
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                AppDomain.CurrentDomain.UnhandledException += domainObserver;
                Dispatcher.CurrentDispatcher.UnhandledException += dispatcherObserver;
                Origin = OwnThreadMessageOriginObserver.Attach(IsRegistered, OriginSink);
                var probeType = Assembly.GetExecutingAssembly().GetType("CodexIsland.NativeMinimizeStackProbe", false);
                if (probeType != null)
                {
                    NativeProbe = probeType.GetMethod("Start", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, new object[] { Path.Combine(ChildDirectory, "native-stacks") });
                    NativeRegister = probeType.GetMethod("Register", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    NativeFlush = probeType.GetMethod("Flush", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!(NativeProbe is IDisposable) || NativeRegister == null || NativeFlush == null) throw new InvalidOperationException("Native probe contract incomplete.");
                    nativeAvailable = true;
                }
                else if (Environment.GetEnvironmentVariable(RequireNativeVariable) == "1") throw new InvalidOperationException("Native caller probe required but not compiled.");
                SettingsAndFilterMotionTests.Run();
                transitions = (int)typeof(SettingsAndFilterMotionTests).GetField("settingTransitions", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                if (transitions != 48) throw new InvalidOperationException("Original full settings matrix must execute exactly 48 transitions; actual=" + transitions);
                if (OriginLimit || Origin.CallbackErrors != 0 || NativeRegisterErrors != 0) throw new InvalidOperationException("Observation data was truncated or callback failed.");
                completed = true;
                Console.WriteLine("PREFIX PASS:SettingsStartupMatrix;Cases=16;Transitions=48;FullSuite=False"); Console.Out.Flush();
                return 0;
            }
            catch (Exception error) { failure = StartupWatchdogCapture.SafeError(error); Console.Error.WriteLine("PREFIX FAILURE: " + failure); return 1; }
            finally
            {
                try
                {
                    if (NativeProbe != null) { NativeFlush.Invoke(NativeProbe, null); ((IDisposable)NativeProbe).Dispose(); }
                }
                catch (Exception error) { NativeRegisterErrors++; failure = (failure ?? "") + " Native cleanup: " + StartupWatchdogCapture.SafeError(error); completed = false; }
                if (Origin != null) Origin.Dispose();
                if (Origin != null && Origin.CleanupErrors != 0) { completed = false; failure = (failure ?? "") + " Origin cleanup errors=" + Origin.CleanupErrors; }
                AppDomain.CurrentDomain.UnhandledException -= domainObserver;
                Dispatcher.CurrentDispatcher.UnhandledException -= dispatcherObserver;
                OriginWriter.Flush(); OriginFlushes++; OriginWriter.Dispose(); OriginWriter = null;
                SynchronizationContext.SetSynchronizationContext(previousContext);
                Save(Path.Combine(ChildDirectory, "result.json"), new { Completed = completed, PrefixOnly = true, FullSuiteExecuted = false, SettingsCases = 16, SettingsTransitions = transitions, RegisteredOwnHwnds = Registered.Count, NativeCallerProbeAvailable = nativeAvailable, NativeProbeErrors = NativeRegisterErrors, OriginLines = OriginLines, OriginBytes = OriginBytes, OriginFlushes = OriginFlushes, OriginMaximumLines = OriginMaximumLines, OriginMaximumBytes = OriginMaximumBytes, OriginLimitReached = OriginLimit, QueueRecords = Origin == null ? 0 : Origin.QueueRecords, MouseRecords = Origin == null ? 0 : Origin.MouseRecords, CallbackErrors = Origin == null ? 0 : Origin.CallbackErrors, CleanupErrors = Origin == null ? 0 : Origin.CleanupErrors, NoInputGeneration = true, GlobalHooks = false, InputGuardEnabled = false, AutomaticRestore = false, AutomaticRetries = 0, ElapsedMilliseconds = clock.ElapsedMilliseconds, Failure = failure, HistoricalSenderProven = false });
            }
        }

        internal static void RegisterTarget(IntPtr hwnd, string role)
        {
            Registered.Add(hwnd);
            if (NativeProbe != null)
            {
                try { NativeRegister.Invoke(NativeProbe, new object[] { hwnd, role }); }
                catch { NativeRegisterErrors++; throw; }
            }
        }
        internal static void FlushNativeAtCaseMark(string label)
        {
            if (label == "settings-motion-dialog") FlushNative("case-mark");
        }
        internal static void FlushNativeCritical(int message, IntPtr wParam)
        {
            if ((message == 5 && wParam.ToInt64() == 1) || (message == 0x112 && (wParam.ToInt64() & 0xFFF0) == 0xF020)) FlushNative("critical-minimize-entry");
        }
        private static void FlushNative(string reason)
        {
            if (NativeProbe == null || NativeFlush == null) return;
            try { NativeFlush.Invoke(NativeProbe, null); }
            catch (Exception error) { NativeRegisterErrors++; Console.Error.WriteLine("NATIVE PROBE FLUSH ERROR " + reason + ": " + StartupWatchdogCapture.SafeError(error)); }
        }
        private static void CaptureBeforeKill(Process child, string directory, string reason)
        {
            if (File.Exists(Path.Combine(directory, "watchdog-dump.json"))) return;
            try { StartupWatchdogCapture.Capture(child, directory, reason); }
            catch (Exception error) { Console.Error.WriteLine("WATCHDOG CAPTURE ERROR: " + StartupWatchdogCapture.SafeError(error)); }
        }
        private static int UnhandledRecords;
        private static void SaveUnhandled(string origin, Exception error, bool terminating)
        {
            try
            {
                if (UnhandledRecords >= 16) return;
                Save(Path.Combine(ChildDirectory, "exception-observation-" + (++UnhandledRecords).ToString("D2") + ".json"), new { Event = origin, Utc = DateTime.UtcNow.ToString("o"), Qpc = Stopwatch.GetTimestamp(), QpcFrequency = Stopwatch.Frequency, IsTerminating = terminating, SafeException = StartupWatchdogCapture.SafeError(error), ExceptionToStringInvoked = false, ExceptionHandledOrSuppressed = false });
            }
            catch { }
        }
        private static bool IsRegistered(IntPtr hwnd) { return Registered.Contains(hwnd); }
        private static void OriginSink(OwnThreadMessageOriginObserver.Observation record)
        {
            if (OriginWriter == null || OriginLimit) return;
            string line = Json.Serialize(record); int bytes = Encoding.UTF8.GetByteCount(line) + 2;
            if (OriginLines >= OriginMaximumLines || OriginBytes + bytes > OriginMaximumBytes) { OriginLimit = true; OriginWriter.Flush(); OriginFlushes++; return; }
            OriginWriter.WriteLine(line); OriginLines++; OriginBytes += bytes;
            if (OriginLines % 32 == 0 || record.Message == 0x112 || record.Event == "OWN_THREAD_MOUSE_BUTTON") { OriginWriter.Flush(); OriginFlushes++; }
        }
        private static void Relay(string path, string watch, int expectedPid, string expectedCreated, ref string previous)
        {
            if (!File.Exists(path)) return;
            string value = File.ReadAllText(path); if (value == previous) return;
            var map = Json.DeserializeObject(value) as Dictionary<string, object>;
            if (map == null || !map.ContainsKey("Targets")) throw new InvalidOperationException("Child watch contract missing.");
            var targets = map["Targets"] as object[];
            if (targets == null || targets.Length > 2) throw new InvalidOperationException("Only the two original child HWNDs are permitted.");
            foreach (object item in targets)
            {
                var target = item as Dictionary<string, object>;
                if (target == null || Convert.ToInt32(target["ProcessId"], CultureInfo.InvariantCulture) != expectedPid || !String.Equals(Convert.ToString(target["ProcessStartUtc"], CultureInfo.InvariantCulture), expectedCreated, StringComparison.Ordinal)) throw new InvalidOperationException("Child watch identity mismatch.");
                string role = Convert.ToString(target["Role"], CultureInfo.InvariantCulture);
                if (role != "settings-motion-owner" && role != "settings-motion-dialog") throw new InvalidOperationException("Unexpected child role.");
            }
            Atomic(watch, value); previous = value;
        }
        private static string Inside(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Project path required.");
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(Project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Path must remain in project.");
            for (var folder = new DirectoryInfo(Path.GetDirectoryName(full)); folder != null && folder.FullName.Length >= Project.Length; folder = folder.Parent)
                if (folder.Exists && (folder.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Reparse path forbidden.");
            return full;
        }
        private static void Save(string path, object value) { File.WriteAllText(path, Json.Serialize(value), new UTF8Encoding(false)); }
        private static void Atomic(string path, string text)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private sealed class BoundedCapture : IDisposable
        {
            private readonly object gate = new object(); private readonly StreamWriter output, error;
            internal int OutputBytes, ErrorBytes, PrefixMarkers, FullSuiteMarkers; internal bool LimitReached;
            internal BoundedCapture(string directory)
            {
                output = new StreamWriter(new FileStream(Path.Combine(directory, "stdout.log"), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                error = new StreamWriter(new FileStream(Path.Combine(directory, "stderr.log"), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            }
            internal void Output(string line) { Write(line, false); }
            internal void Error(string line) { Write(line, true); }
            private void Write(string line, bool isError)
            {
                if (line == null) return;
                lock (gate)
                {
                    if (LimitReached) return; int bytes = Encoding.UTF8.GetByteCount(line) + 2;
                    if (OutputBytes + ErrorBytes + bytes > 4 * 1024 * 1024) { LimitReached = true; return; }
                    if (!isError && line == "PREFIX PASS:SettingsStartupMatrix;Cases=16;Transitions=48;FullSuite=False") PrefixMarkers++;
                    if (line.StartsWith("SUITE PASS:", StringComparison.Ordinal)) FullSuiteMarkers++;
                    if (isError) { error.WriteLine(line); error.Flush(); ErrorBytes += bytes; }
                    else { output.WriteLine(line); output.Flush(); OutputBytes += bytes; }
                }
            }
            internal void Flush() { lock (gate) { output.Flush(); error.Flush(); } }
            public void Dispose() { lock (gate) { output.Dispose(); error.Dispose(); } }
        }
    }
}
