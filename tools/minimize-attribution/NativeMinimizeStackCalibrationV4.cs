using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class NativeMinimizeStackCalibrationV4
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("NativeMinimizeStackControl.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint ControlledMinimize(IntPtr hwnd, uint process, uint thread);

        [STAThread] private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--self-check")
                {
                    string text = Json.Serialize(NativeMinimizeStackProbe.SelfCheck()); Console.WriteLine(text);
                    return text.Contains("\"Passed\":true") ? 0 : 1;
                }
                if (args.Length != 2 || args[0] != "--output") throw new ArgumentException("Use --self-check or --output <fresh project directory>.");
                string root = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd('\\') + "\\";
                string output = Path.GetFullPath(args[1]);
                Check(output.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(output), "Fresh project output required.");
                Directory.CreateDirectory(output);
                var phases = new List<Dictionary<string, object>>();
                Window window = null; HwndSource source = null; HwndSourceHook hook = null;
                int nestedCommands = 0; bool injectNested = false;
                using (var probe = NativeMinimizeStackProbe.Start(Path.Combine(output, "native")))
                {
                    try
                    {
                        window = new Window { Width = 320, Height = 200, ShowActivated = false, Title = "Own native bracket calibration" };
                        window.SourceInitialized += delegate { probe.Register(new WindowInteropHelper(window).Handle, "calibration-only"); };
                        window.Show(); Pump(40); IntPtr hwnd = new WindowInteropHelper(window).Handle;
                        source = HwndSource.FromHwnd(hwnd);
                        hook = delegate(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
                        {
                            if (message == 0x112)
                            {
                                probe.Flush(); // Persist the real native entry before the body continues.
                                if (injectNested && (w.ToInt64() & 0xFFF0) == 0xF020)
                                {
                                    injectNested = false; nestedCommands++;
                                    SendMessage(hwnd, 0x112, new IntPtr(0xF120), IntPtr.Zero);
                                }
                            }
                            return IntPtr.Zero;
                        };
                        source.AddHook(hook);
                        Phase(phases, "native-ShowWindow", hwnd, probe, delegate {
                            Check(ControlledMinimize(hwnd, (uint)Process.GetCurrentProcess().Id, GetCurrentThreadId()) == 0, "native own control failed");
                        }); Restore(window, hwnd, probe);
                        Phase(phases, "own-SendSC", hwnd, probe, delegate { SendMessage(hwnd, 0x112, new IntPtr(0xF020), IntPtr.Zero); }); Restore(window, hwnd, probe);
                        Phase(phases, "own-PostSC", hwnd, probe, delegate { Check(PostMessage(hwnd, 0x112, new IntPtr(0xF020), IntPtr.Zero), "own PostMessage failed"); }); Restore(window, hwnd, probe);
                        injectNested = true;
                        Phase(phases, "nested-own-SendSC", hwnd, probe, delegate { SendMessage(hwnd, 0x112, new IntPtr(0xF020), IntPtr.Zero); }); Restore(window, hwnd, probe);
                        Check(nestedCommands == 1, "nested control did not execute exactly once");
                    }
                    finally
                    {
                        if (source != null && !source.IsDisposed && hook != null) source.RemoveHook(hook);
                        if (window != null) window.Close();
                    }
                }
                var rows = ReadRows(Path.Combine(output, "native", "events.jsonl"));
                var entries = rows.Where(r => Text(r, "Event") == "native-WNDPROC-entry").ToArray();
                var returns = rows.Where(r => Text(r, "Event") == "native-WNDPROC-return").ToArray();
                foreach (var entry in entries)
                {
                    var matched = returns.Where(r => U(r, "CallId") == U(entry, "CallId")).ToArray();
                    Check(matched.Length == 1, "entry must have exactly one actual return in this successful control");
                    var ret = matched[0];
                    Check(U(ret, "Qpc") >= U(entry, "Qpc") && U(ret, "ParentCallId") == U(entry, "ParentCallId") && U(ret, "Depth") == U(entry, "Depth") && U(ret, "Message") == U(entry, "Message") && Text(ret, "W") == Text(entry, "W") && Text(ret, "L") == Text(entry, "L") && Convert.ToBoolean(ret["ReturnObserved"], CultureInfo.InvariantCulture), "native entry/return identity differs");
                }
                Check(entries.Length == returns.Length, "unpaired real returns");
                var phaseResults = new List<object>();
                foreach (var phase in phases)
                {
                    ulong start = U(phase, "StartQpc"), end = U(phase, "EndQpc"); string name = Text(phase, "Name");
                    var slice = rows.Where(r => r.ContainsKey("Qpc") && U(r, "Qpc") >= start && U(r, "Qpc") <= end).ToArray();
                    var sc = slice.Where(r => Text(r, "Event") == "native-WNDPROC-entry" && U(r, "Message") == 0x112).ToArray();
                    int expected = name == "native-ShowWindow" ? 0 : name == "nested-own-SendSC" ? 2 : 1;
                    Check(sc.Length == expected, name + " SC entry count");
                    Check(slice.Any(r => Text(r, "Event") == "native-HCBT_MINMAX" && U(r, "ShowCommand") == 6), name + " CBT min attempt missing");
                    Check(slice.Any(r => Text(r, "Event") == "native-WNDPROC-entry" && U(r, "Message") == 5 && Text(r, "W") == "0x1"), name + " actual WM_SIZE1 missing");
                    if (name == "native-ShowWindow") Check(Json.Serialize(slice).Contains("NativeMinimizeStackControl.dll"), "native caller module missing");
                    if (name == "nested-own-SendSC")
                    {
                        var outer = sc.Single(r => Text(r, "W") == "0xF020"); var inner = sc.Single(r => Text(r, "W") == "0xF120");
                        Check(U(inner, "ParentCallId") == U(outer, "CallId") && U(inner, "Depth") > U(outer, "Depth"), "nested native SC parent/depth missing");
                    }
                    phaseResults.Add(new { Name = name, Passed = true, NativeScEntries = sc.Length, NativeReturnsMatched = true, ActualMinimizedSize = true });
                }
                var summary = Json.DeserializeObject(File.ReadAllText(Path.Combine(output, "native", "summary.json"))) as Dictionary<string, object>;
                Check(summary != null && Convert.ToBoolean(summary["Completed"]) && !Convert.ToBoolean(summary["Installed"]) && !Convert.ToBoolean(summary["DataLossDetected"]) && U(summary, "ActiveDepth") == 0 && U(summary, "TargetCount") == 0 && U(summary, "SubclassInstalls") == U(summary, "SubclassRemoves"), "native cleanup/completeness failed");
                File.WriteAllText(Path.Combine(output, "phases.json"), Json.Serialize(phases));
                File.WriteAllText(Path.Combine(output, "control-result.json"), Json.Serialize(new { Passed = true, UiExecuted = true, ExplicitControlledCommands = true, OnlyOwnWindow = true, Phases = phaseResults, NativeEntries = entries.Length, NativeReturns = returns.Length, NestedControls = nestedCommands, MissingReturnFabricated = false, SubclassesRemoved = true, HistoricalSenderProven = false }));
                Console.WriteLine("PASS: native ShowWindow, own SendSC, posted SC and nested SC all have genuine native brackets/state records; no historical attribution.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
        }
        private static void Phase(List<Dictionary<string, object>> phases, string name, IntPtr hwnd, NativeMinimizeStackProbe probe, Action action)
        {
            Check(!IsIconic(hwnd), name + " starts minimized"); long start = Stopwatch.GetTimestamp(); action();
            Until(delegate { return IsIconic(hwnd); }, name + " must really minimize"); Pump(15); probe.Flush();
            phases.Add(new Dictionary<string, object> { { "Name", name }, { "StartQpc", start }, { "EndQpc", Stopwatch.GetTimestamp() } });
        }
        private static void Restore(Window window, IntPtr hwnd, NativeMinimizeStackProbe probe) { window.WindowState = WindowState.Normal; Until(delegate { return !IsIconic(hwnd); }, "controlled restore"); Pump(20); probe.Flush(); }
        private static Dictionary<string, object>[] ReadRows(string path) { return File.ReadAllLines(path).Select(line => Json.DeserializeObject(line) as Dictionary<string, object>).Where(row => row != null).ToArray(); }
        private static string Text(Dictionary<string, object> row, string key) { object value; return row.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        private static ulong U(Dictionary<string, object> row, string key) { return Convert.ToUInt64(row[key], CultureInfo.InvariantCulture); }
        private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static void Until(Func<bool> predicate, string label) { var clock = Stopwatch.StartNew(); while (!predicate() && clock.ElapsedMilliseconds < 2000) Pump(10); Check(predicate(), label); }
        private static void Pump(int milliseconds) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick += delegate { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
    }
}
