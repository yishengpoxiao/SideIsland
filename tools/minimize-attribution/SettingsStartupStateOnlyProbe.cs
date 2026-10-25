using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
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
        private static readonly List<NativeWindowStateTrace> PreludeTraces = new List<NativeWindowStateTrace>();
        private static readonly HashSet<Window> PreludeWindows = new HashSet<Window>();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long created, out long ended, out long kernel, out long user);

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
                    var keyMenu = OwnThreadMessageOriginObserver.CreateQueue(new IntPtr(1), 0x112, new IntPtr(0xF10A), new IntPtr(0x41), 1, 0, 1, 1, 1, 1, 1);
                    var move = OwnThreadMessageOriginObserver.CreateQueue(new IntPtr(1), 0x112, new IntPtr(0xF012), new IntPtr(0x123456), 1, 0, 1, 2, 2, 1, 1);
                    if (keyMenu.W != "0xF100" || keyMenu.L != "0x0" || !keyMenu.MessageFieldsSuppressed || move.W != "0xF012" || move.L != "0x123456" || move.MessageFieldsSuppressed) throw new InvalidOperationException("State-only system-command parameter suppression failed.");
                    Console.WriteLine("SELF-CHECK PASS; no UI, hook, ETW session or process started; startup prefix is not a complete suite.");
                    return 0;
                }
                if (!File.Exists(Path.Combine(Project, "tests", "NativeTests.cs"))) throw new InvalidOperationException("Project working directory required.");
                string expected = Path.Combine(Project, ".runtime", "tests", "NativeTests.exe");
                if (!String.Equals(Assembly.GetExecutingAssembly().Location, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Run only at the original .runtime/tests/NativeTests.exe path.");
                if (args.Length == 2 && args[0] == "--suite" && args[1] == "SettingsAndFilterMotionTests") return Child(false);
                if (args.Length == 2 && args[0] == "--suite" && args[1] == "PickerAndChromeTests") return Child(true);
                return Parent(args);
            }
            catch (Exception error) { Console.Error.WriteLine("STARTUP PREFIX FAILURE: " + StartupWatchdogCapture.SafeError(error)); return 1; }
        }

        private static int Parent(string[] args)
        {
            int count = 4; string output = null, watch = null, stopFile = null; bool prelude = false;
            for (int index = 0; index < args.Length; index += 2)
            {
                if (index + 1 == args.Length) throw new ArgumentException("Every option needs a value.");
                if (args[index] == "--startup-count") count = Int32.Parse(args[index + 1], CultureInfo.InvariantCulture);
                else if (args[index] == "--output") output = Inside(args[index + 1]);
                else if (args[index] == "--watchfile") watch = Inside(args[index + 1]);
                else if (args[index] == "--prelude-picker") prelude = args[index + 1] == "1";
                else if (args[index] == "--stop-file") stopFile = Inside(args[index + 1]);
                else throw new ArgumentException("Use --startup-count 1..24 --output fresh-project-dir --watchfile project-file [--prelude-picker 1].");
            }
            if (count < 1 || count > (prelude ? 4 : 24) || output == null || watch == null) throw new ArgumentException("Count/output/watch required; at most 4 prelude pairs or 24 direct prefixes.");
            if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Fresh coordinator output required.");
            Directory.CreateDirectory(output); Directory.CreateDirectory(Path.GetDirectoryName(watch));
            var clock = Stopwatch.StartNew(); var children = new List<object>(); var pairs = new List<object>(); bool completed = false, stopRequested = false; string failure = null; int startedCount = 0;
            Atomic(watch, "{\"Version\":1,\"Targets\":[]}");
            try
            {
                for (int index = 1; index <= count; index++)
                {
                    if (StopAtBoundary(stopFile, output, "before-pair-" + index)) { stopRequested = true; return 0; }
                    if (clock.Elapsed.TotalSeconds >= 800) throw new TimeoutException("Coordinator 800 second deadline; no retry.");
                    string pairRoot = Path.Combine(output, "pair-" + index.ToString("D2", CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(pairRoot);
                    PreparedChild picker = prelude ? new PreparedChild(Path.Combine(pairRoot, "picker-prelude"), true, index) : null;
                    PreparedChild settings = new PreparedChild(Path.Combine(pairRoot, "settings-prefix"), false, index);
                    bool pairFailure = false;
                    try
                    {
                        if (picker != null)
                        {
                            picker.StartOnly(); startedCount++; picker.RecordLaunch();
                            WaitChild(picker, watch, clock);
                            if (!picker.Passed()) { picker.RecordTerminal(children); throw new InvalidOperationException("Prelude failed; settings prefix not started."); }
                            if (StopAtBoundary(stopFile, output, "after-picker-before-settings-" + index)) { picker.RecordTerminal(children); stopRequested = true; return 0; }
                        }
                        // All directories/capture streams and StartInfo already exist.
                        // No artificial drain or terminal-log writes between these children.
                        settings.StartOnly(); startedCount++;
                        long gap = picker == null ? 0 : settings.Created - picker.Ended;
                        if (picker != null) picker.RecordTerminal(children);
                        settings.RecordLaunch();
                        if (picker != null)
                        {
                            object pair = new { Pair = index, PreludeProcessId = picker.Pid, PreludeCreationFileTime = HexTime(picker.Created), PreludeExitFileTime = HexTime(picker.Ended), SettingsProcessId = settings.Pid, SettingsCreationFileTime = HexTime(settings.Created), Gap100ns = gap, GapMilliseconds = gap / 10000.0, TimesFromRetainedProcessHandles = true, ArtificialInterstageDelayMilliseconds = 0, OriginalHistoricalGapMilliseconds = 15.7382, Full79Executed = false };
                            pairs.Add(pair); Save(Path.Combine(pairRoot, "pair-timing.json"), pair); Save(Path.Combine(output, "pairs.json"), pairs);
                        }
                        WaitChild(settings, watch, clock); settings.RecordTerminal(children);
                        Save(Path.Combine(output, "children.json"), children);
                        if (!settings.Passed()) throw new InvalidOperationException("Settings prefix failed; first failure retained without retry.");
                        if (index < count && StopAtBoundary(stopFile, output, "after-settings-" + index)) { stopRequested = true; return 0; }
                        Thread.Sleep(1000); // Drain after settings only, never between the pair.
                    }
                    catch (Exception error)
                    {
                        pairFailure = true;
                        SaveNoThrow(Path.Combine(pairRoot, "coordinator-original-error.json"), new { Event = "coordinator-original-error-before-cleanup", Utc = DateTime.UtcNow.ToString("o"), ErrorType = error.GetType().FullName, HResult = "0x" + error.HResult.ToString("X8"), SafeError = StartupWatchdogCapture.SafeError(error), Pair = index, PickerPid = picker == null ? 0 : picker.Pid, SettingsPid = settings.Pid, PreservedBeforeCleanup = true, HistoricalSenderProven = false });
                        throw;
                    }
                    finally
                    {
                        // Cleanup has its own records and may never replace original error.
                        CleanupExact(settings, pairFailure ? "coordinator exception cleanup" : "normal cleanup");
                        if (picker != null) CleanupExact(picker, pairFailure ? "coordinator exception cleanup" : "normal cleanup");
                        settings.DisposeNoThrow(); if (picker != null) picker.DisposeNoThrow();
                        SaveNoThrow(Path.Combine(output, "children.json"), children);
                    }
                }
                completed = true; return 0;
            }
            catch (Exception error) { failure = StartupWatchdogCapture.SafeError(error); throw; }
            finally
            {
                SaveNoThrow(Path.Combine(output, "result.json"), new { Completed = completed, DiagnosticStopRequested = stopRequested, DiagnosticStopFile = stopFile, StopHonoredOnlyAtChildBoundary = true, ApplicationFailure = failure != null, RequestedIterations = count, PreludePicker = prelude, StartedChildren = startedCount, RecordedTerminalChildren = children.Count, CompletePickerPreludesRequested = prelude ? count : 0, SettingsPrefixesRequested = count, Full79Executed = false, SettingsFullSuiteExecuted = false, AutomaticRetries = 0, StopOnFirstFailure = true, NoAutomaticRestore = true, ElapsedMilliseconds = clock.ElapsedMilliseconds, Failure = failure, HistoricalSenderProven = false, EndedUtc = DateTime.UtcNow.ToString("o") });
            }
        }
        private static bool StopAtBoundary(string file, string output, string boundary)
        {
            if (file == null || !File.Exists(file)) return false;
            SaveNoThrow(Path.Combine(output, "diagnostic-stop.json"), new { Utc = DateTime.UtcNow.ToString("o"), StopRequested = true, StopFile = file, Boundary = boundary, ActiveChildCount = 0, NoChildKilledForStop = true, ApplicationFailure = false, Completed = false, Full79Executed = false });
            return true;
        }
        private static void WaitChild(PreparedChild child, string watch, Stopwatch coordinatorClock)
        {
            string previous = null; string childWatch = Path.Combine(child.Directory, "native-trace", "targets.json");
            int seconds = child.Prelude ? 150 : 60;
            while (!child.Process.WaitForExit(25))
            {
                Relay(childWatch, watch, child.Pid, child.CreatedUtc, ref previous);
                if (child.Capture.LimitReached || child.Clock.Elapsed.TotalSeconds >= seconds || coordinatorClock.Elapsed.TotalSeconds >= 800)
                {
                    child.LogLimit = child.Capture.LimitReached; child.TimedOut = !child.LogLimit;
                    CaptureBeforeKill(child.Process, child.Directory, child.LogLimit ? "stdout/stderr hard limit" : "child/coordinator watchdog");
                    KillIfStillRunning(child); break;
                }
            }
            child.Process.WaitForExit(); child.RefreshTimes();
            Relay(childWatch, watch, child.Pid, child.CreatedUtc, ref previous); child.Capture.Flush();
        }
        private static void CleanupExact(PreparedChild child, string reason)
        {
            if (!child.Started) return;
            try
            {
                if (child.Process.HasExited) { child.Process.WaitForExit(); return; }
                CaptureBeforeKill(child.Process, child.Directory, reason);
                // A full dump can take seconds. The child may exit during it.
                KillIfStillRunning(child);
            }
            catch (Exception error)
            {
                SaveNoThrow(Path.Combine(child.Directory, "coordinator-cleanup-error.json"), new { ErrorType = error.GetType().FullName, HResult = "0x" + error.HResult.ToString("X8"), SafeError = StartupWatchdogCapture.SafeError(error), OriginalErrorNotReplaced = true });
            }
        }
        private static void KillIfStillRunning(PreparedChild child)
        {
            bool killed = false, alreadyExited = false; string cleanupError = null;
            try
            {
                child.Process.Refresh();
                alreadyExited = child.Process.HasExited;
                if (!alreadyExited) { child.Process.Kill(); killed = true; }
                child.Process.WaitForExit();
            }
            catch (InvalidOperationException error)
            {
                // Kill may race with a normal exit after the second check.
                try { alreadyExited = child.Process.HasExited; if (alreadyExited) child.Process.WaitForExit(); } catch { }
                if (!alreadyExited) cleanupError = StartupWatchdogCapture.SafeError(error);
            }
            catch (Exception error) { cleanupError = StartupWatchdogCapture.SafeError(error); }
            finally { SaveNoThrow(Path.Combine(child.Directory, "coordinator-termination.json"), new { ProcessId = child.Pid, ExactCreatedChildOnly = true, KilledAfterDumpAttempt = killed, AlreadyExitedAfterDump = alreadyExited, Error = cleanupError, OriginalErrorNotReplaced = true }); }
        }
        private static string HexTime(long value) { return "0x" + value.ToString("X"); }
        private static void SaveNoThrow(string path, object value) { try { Save(path, value); } catch { } }
        private sealed class PreparedChild
        {
            internal readonly string Directory; internal readonly bool Prelude; internal readonly int Pair;
            internal readonly BoundedCapture Capture; internal readonly Process Process; internal readonly Stopwatch Clock = new Stopwatch();
            internal int Pid, Exit = -1; internal long Created, Ended; internal string CreatedUtc; internal bool Started, TimedOut, LogLimit;
            private bool terminalSaved;
            internal PreparedChild(string directory, bool prelude, int pair)
            {
                Directory = directory; Prelude = prelude; Pair = pair; System.IO.Directory.CreateDirectory(directory); Capture = new BoundedCapture(directory);
                var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--suite " + (prelude ? "PickerAndChromeTests" : "SettingsAndFilterMotionTests"))
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Project,
                    RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
                };
                start.EnvironmentVariables[ChildVariable] = directory; start.EnvironmentVariables[TraceVariable] = Path.Combine(directory, "native-trace");
                Process = new Process { StartInfo = start };
                Process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs data) { Capture.Output(data.Data); };
                Process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs data) { Capture.Error(data.Data); };
            }
            internal void StartOnly()
            {
                Clock.Start(); if (!Process.Start()) throw new InvalidOperationException("Could not create own child."); Started = true; Pid = Process.Id;
                IntPtr retained = Process.Handle; RefreshTimes(); Process.BeginOutputReadLine(); Process.BeginErrorReadLine(); GC.KeepAlive(retained);
            }
            internal void RefreshTimes()
            {
                long kernel, user;
                if (!GetProcessTimes(Process.Handle, out Created, out Ended, out kernel, out user)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                CreatedUtc = DateTime.FromFileTimeUtc(Created).ToString("o", CultureInfo.InvariantCulture);
                if (Process.HasExited) Exit = Process.ExitCode;
            }
            internal void RecordLaunch()
            {
                Save(Path.Combine(Directory, "launch.json"), new { Pair = Pair, Prelude = Prelude, ProcessId = Pid, ProcessStartUtc = CreatedUtc, CreationFileTime = HexTime(Created), Image = Process.StartInfo.FileName, Arguments = Process.StartInfo.Arguments, Full79Executed = false, AutomaticRetry = false });
                Console.WriteLine("STARTUP CHILD START pair=" + Pair + " kind=" + (Prelude ? "picker-prelude" : "settings-prefix") + " pid=" + Pid); Console.Out.Flush();
            }
            internal bool Passed()
            {
                bool resultCompleted = false; string resultPath = Path.Combine(Directory, "result.json");
                if (File.Exists(resultPath)) { var map = Json.DeserializeObject(SharedText(resultPath)) as Dictionary<string, object>; resultCompleted = map != null && map.ContainsKey("Completed") && Convert.ToBoolean(map["Completed"], CultureInfo.InvariantCulture); }
                return Exit == 0 && resultCompleted && !TimedOut && !LogLimit && Capture.FullSuiteMarkers == 0 && Capture.ErrorBytes == 0 && (Prelude ? Capture.PreludeMarkers == 1 && Capture.PrefixMarkers == 0 : Capture.PrefixMarkers == 1 && Capture.PreludeMarkers == 0);
            }
            internal void RecordTerminal(List<object> records)
            {
                if (terminalSaved) return; terminalSaved = true;
                object record = new { Pair = Pair, Prelude = Prelude, ProcessId = Pid, ProcessStartUtc = CreatedUtc, CreationFileTime = HexTime(Created), ExitFileTime = HexTime(Ended), ExitCode = Exit, ExitHex = "0x" + unchecked((uint)Exit).ToString("X8"), Passed = Passed(), Full79Executed = false, AutomaticRetry = false, TimedOut = TimedOut, LogLimitReached = LogLimit, PrefixMarkers = Capture.PrefixMarkers, PreludeMarkers = Capture.PreludeMarkers, FullSuiteMarkers = Capture.FullSuiteMarkers, StdoutBytes = Capture.OutputBytes, StderrBytes = Capture.ErrorBytes, DurationMilliseconds = Clock.ElapsedMilliseconds, ChildDirectory = Directory };
                records.Add(record); Save(Path.Combine(Directory, "coordinator-result.json"), record);
                Console.WriteLine("STARTUP CHILD END pair=" + Pair + " kind=" + (Prelude ? "picker-prelude" : "settings-prefix") + " pid=" + Pid + " exit=" + Exit); Console.Out.Flush();
            }
            internal void DisposeNoThrow()
            {
                try { Process.Dispose(); } catch { }
                try { Capture.Dispose(); } catch { }
            }
        }
        private static int Child(bool prelude)
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
                if (prelude)
                {
                    EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(delegate(object sender, RoutedEventArgs data)
                    {
                        var window = sender as Window;
                        if (window != null && PreludeWindows.Add(window)) PreludeTraces.Add(NativeWindowStateTrace.Attach(window, "picker-chrome-prelude"));
                    }));
                    PickerAndChromeTests.Run();
                }
                else
                {
                    SettingsAndFilterMotionTests.Run();
                    transitions = (int)typeof(SettingsAndFilterMotionTests).GetField("settingTransitions", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                    if (transitions != 48) throw new InvalidOperationException("Original full settings matrix must execute exactly 48 transitions; actual=" + transitions);
                }
                if (OriginLimit || Origin.CallbackErrors != 0 || NativeRegisterErrors != 0) throw new InvalidOperationException("Observation data was truncated or callback failed.");
                completed = true;
                Console.WriteLine(prelude ? "PRELUDE PASS:PickerAndChromeTests;StandaloneComplete=True;Full79=False" : "PREFIX PASS:SettingsStartupMatrix;Cases=16;Transitions=48;FullSuite=False"); Console.Out.Flush();
                return 0;
            }
            catch (Exception error) { failure = StartupWatchdogCapture.SafeError(error); Console.Error.WriteLine("PREFIX FAILURE: " + failure); return 1; }
            finally
            {
                foreach (var trace in PreludeTraces) trace.Dispose();
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
                Save(Path.Combine(ChildDirectory, "result.json"), new { Completed = completed, Kind = prelude ? "complete-picker-prelude" : "settings-matrix-prefix", PrefixOnly = !prelude, Full79Executed = false, CompletePickerSuiteExecuted = prelude, SettingsCases = prelude ? 0 : 16, SettingsTransitions = transitions, RegisteredOwnHwnds = Registered.Count, NativeCallerProbeAvailable = nativeAvailable, NativeProbeErrors = NativeRegisterErrors, OriginLines = OriginLines, OriginBytes = OriginBytes, OriginFlushes = OriginFlushes, OriginMaximumLines = OriginMaximumLines, OriginMaximumBytes = OriginMaximumBytes, OriginLimitReached = OriginLimit, QueueRecords = Origin == null ? 0 : Origin.QueueRecords, MouseRecords = Origin == null ? 0 : Origin.MouseRecords, CallbackErrors = Origin == null ? 0 : Origin.CallbackErrors, CleanupErrors = Origin == null ? 0 : Origin.CleanupErrors, NoInputGeneration = true, GlobalHooks = false, InputGuardEnabled = false, AutomaticRestore = false, AutomaticRetries = 0, ElapsedMilliseconds = clock.ElapsedMilliseconds, Failure = failure, HistoricalSenderProven = false });
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
            string value = SharedText(path); if (value == previous) return;
            var map = Json.DeserializeObject(value) as Dictionary<string, object>;
            if (map == null || !map.ContainsKey("Targets")) throw new InvalidOperationException("Child watch contract missing.");
            var targets = map["Targets"] as object[];
            if (targets == null || targets.Length > 2) throw new InvalidOperationException("Only the two original child HWNDs are permitted.");
            foreach (object item in targets)
            {
                var target = item as Dictionary<string, object>;
                if (target == null || Convert.ToInt32(target["ProcessId"], CultureInfo.InvariantCulture) != expectedPid || !String.Equals(Convert.ToString(target["ProcessStartUtc"], CultureInfo.InvariantCulture), expectedCreated, StringComparison.Ordinal)) throw new InvalidOperationException("Child watch identity mismatch.");
                string role = Convert.ToString(target["Role"], CultureInfo.InvariantCulture);
                if (role != "settings-motion-owner" && role != "settings-motion-dialog" && role != "picker-chrome-prelude") throw new InvalidOperationException("Unexpected child role.");
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
        private static string SharedText(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            { if (stream.Length > 1024 * 1024) throw new InvalidOperationException("Bounded child metadata file exceeded 1MiB."); return reader.ReadToEnd(); }
        }
        private static void Atomic(string path, string text)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private sealed class BoundedCapture : IDisposable
        {
            private readonly object gate = new object(); private readonly StreamWriter output, error;
            internal int OutputBytes, ErrorBytes, PrefixMarkers, PreludeMarkers, FullSuiteMarkers; internal bool LimitReached;
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
                    if (!isError && line == "PRELUDE PASS:PickerAndChromeTests;StandaloneComplete=True;Full79=False") PreludeMarkers++;
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
