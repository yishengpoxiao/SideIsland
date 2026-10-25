using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class NativeMinimizeStackCalibration
    {
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("NativeMinimizeStackControl.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint ControlledMinimize(IntPtr hwnd, uint process, uint thread);
        [STAThread] private static int Main(string[] args)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
            try
            {
                if (args.Length == 1 && args[0] == "--self-check")
                {
                    object result = NativeMinimizeStackProbe.SelfCheck();
                    string text = json.Serialize(result); Console.WriteLine(text);
                    return text.Contains("\"Passed\":true") ? 0 : 1;
                }
                if (args.Length != 2 || args[0] != "--output") throw new ArgumentException("Use --self-check or --output <fresh project directory>.");
                string root = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd('\\') + "\\";
                string output = Path.GetFullPath(args[1]);
                if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Directory.Exists(output)) throw new InvalidOperationException("Fresh output in project required.");
                Directory.CreateDirectory(output);
                Window window = null;
                using (var probe = NativeMinimizeStackProbe.Start(Path.Combine(output, "native")))
                {
                    try
                    {
                        window = new Window { Width = 320, Height = 200, ShowActivated = false, Title = "Own native stack calibration" };
                        window.SourceInitialized += delegate { probe.Register(new WindowInteropHelper(window).Handle, "calibration-only"); };
                        window.Show(); Pump(40);
                        IntPtr hwnd = new WindowInteropHelper(window).Handle;
                        if (IsIconic(hwnd)) throw new InvalidOperationException("Control must start normal.");
                        uint commandError = ControlledMinimize(hwnd, (uint)Process.GetCurrentProcess().Id, GetCurrentThreadId());
                        if (commandError != 0 || !IsIconic(hwnd)) throw new InvalidOperationException("Explicit own native positive control failed: " + commandError);
                        Pump(30); probe.Flush();
                        string records;
                        using (var reader = new StreamReader(new FileStream(Path.Combine(output, "native", "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))) records = reader.ReadToEnd();
                        if (!records.Contains("native-HCBT_MINMAX") || !records.Contains("NativeMinimizeStackControl.dll")) throw new InvalidOperationException("Native attempt or known native caller module missing from stack.");
                        File.WriteAllText(Path.Combine(output, "control-result.json"), json.Serialize(new { Passed = true, UiExecuted = true, ExplicitControlledMinimize = true, OnlyOwnWindow = true, IsIconic = IsIconic(hwnd), KnownNativeCallerInStack = true, NativeCallerModule = "NativeMinimizeStackControl.dll", HistoricalSenderProven = false }));
                        Console.WriteLine("PASS: own native ShowWindow control observed, native stack contains known native caller module; no historical attribution.");
                    }
                    finally { if (window != null) window.Close(); }
                }
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
        }
        private static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
    }
}
