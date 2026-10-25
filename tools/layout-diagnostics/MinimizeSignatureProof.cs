using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // Own demo windows only. This is an intentional minimization control, not
    // evidence of who sent the original run-001 window-state change.
    internal static class MinimizeSignatureProof
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private static string output;
        [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RectI { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct Placement
        {
            internal uint Length, Flags, ShowCmd;
            internal Point Minimum, Maximum;
            internal RectI Normal;
        }
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RectI rectangle);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref Placement placement);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                VerifyBuild();
                if (args.Length == 1 && args[0] == "--self-check")
                {
                    Check(typeof(SettingsWindow).GetConstructor(new[] { typeof(Settings) }) != null, "production settings constructor missing");
                    Check(typeof(NativeWindowStateTrace).GetMethod("Attach", BindingFlags.Static | BindingFlags.NonPublic) != null, "state trace missing");
                    Console.WriteLine("SELF-CHECK PASS UI-created=false controlled-own-HWND-minimization=true original-sender-proven=false");
                    return 0;
                }
                string project = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                output = Path.Combine(project, "artifacts", "full-regression-1.12.16", "minimize-signature-proof-data");
                for (int index = 0; index < args.Length; index += 2)
                {
                    Check(index + 1 < args.Length, "options need a value");
                    if (args[index] == "--suite") Check(args[index + 1] == "MinimizeSignatureProof", "wrong suite");
                    else if (args[index] == "--output") output = Path.GetFullPath(args[index + 1]);
                    else throw new ArgumentException("Use --suite MinimizeSignatureProof and optional --output project-directory.");
                }
                Check(Path.GetFullPath(output).StartsWith(project, StringComparison.OrdinalIgnoreCase), "proof output must stay inside project");
                Check(!Directory.Exists(output), "fresh proof output directory required");
                Directory.CreateDirectory(output);
                Run();
                Console.WriteLine("SUITE PASS:MinimizeSignatureProof");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine("PROOF FAILURE: " + error); return 1; }
        }

        private static void Run()
        {
            string previousData = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), language = UiLocalization.Language, theme = UiTheme.Name;
            var previousContext = SynchronizationContext.Current;
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fixture = Path.GetFullPath(Path.Combine(tempRoot, "CodexIsland.MinimizeSignature." + Guid.NewGuid().ToString("N")));
            IslandWindow island = null; SettingsWindow dialog = null;
            NativeWindowStateTrace ownerTrace = null, dialogTrace = null;
            var result = new Dictionary<string, object> {
                { "Kind", "intentional-own-window-minimization-signature-control" }, { "StartedUtc", DateTime.UtcNow.ToString("o") },
                { "UiExecuted", true }, { "OwnWindowsOnly", true }, { "ControlledCommand", "SendMessage own HWND WM_SYSCOMMAND SC_MINIMIZE" },
                { "OriginalRun001SenderProven", false }, { "OriginalFailureRecordModified", false }, { "Completed", false }
            };
            try
            {
                Check(fixture.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase), "temporary fixture must stay inside temp root");
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", fixture);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, NotificationsEnabled = false, AutoCollapse = false,
                    HoverExpandEnabled = false, AutoMusic = false, OnlineLyricsEnabled = false,
                    Language = "zh", Theme = "dark", CustomExpandedSize = true, ExpandedWidth = 391, ExpandedHeight = 638 });
                island = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false };
                ownerTrace = NativeWindowStateTrace.Attach(island, "controlled-minimize-owner");
                island.Loaded += delegate { StopTimers(island); }; island.Show(); island.SetExpanded(true, false); StopTimers(island);
                dialog = new SettingsWindow(new Settings { AlwaysOnTop = false, NotificationsEnabled = false, CodexPath = "", CodexHome = "" }) {
                    Owner = island, ShowActivated = false, IsHitTestVisible = false, Width = 320, Height = 750
                };
                dialogTrace = NativeWindowStateTrace.Attach(dialog, "controlled-minimize-dialog");
                AppAppearance.Register(dialog); dialog.Show();
                WaitFor(island, delegate { return dialog.IsVisible && dialog.WindowState == WindowState.Normal && !IsIconic(new WindowInteropHelper(dialog).Handle); }, "normal presentation");
                IntPtr hwnd = new WindowInteropHelper(dialog).Handle; RequireOwn(hwnd);
                var before = Capture(dialog, hwnd); result["Before"] = before;
                result["SM_CXMINIMIZED"] = GetSystemMetrics(57); result["SM_CYMINIMIZED"] = GetSystemMetrics(58);
                result["DpiTransform"] = PresentationSource.FromVisual(dialog).CompositionTarget.TransformToDevice.ToString(CultureInfo.InvariantCulture);
                Console.WriteLine("CONTROL BEGIN: normal settings window at320x750; metrics=" + GetSystemMetrics(57) + "x" + GetSystemMetrics(58) + "; HWND ownership verified.");
                dialogTrace.Mark("intentional-own-command-before-SendMessage"); ownerTrace.Mark("intentional-own-command-before-SendMessage");
                RequireOwn(hwnd); SendMessage(hwnd, 0x0112, new IntPtr(0xF020), IntPtr.Zero);
                WaitFor(island, delegate { return IsIconic(hwnd); }, "intentional minimization");
                var minimized = Capture(dialog, hwnd); result["Minimized"] = minimized;
                RectI native; Check(GetWindowRect(hwnd, out native), "minimized GetWindowRect failed");
                var placement = ReadPlacement(hwnd);
                Check(IsIconic(hwnd), "own controlled window did not become iconic");
                Check(placement.ShowCmd == 2 || placement.ShowCmd == 6, "placement does not show a minimized window");
                Check(native.Right - native.Left == GetSystemMetrics(57) && native.Bottom - native.Top == GetSystemMetrics(58), "minimized native dimensions differ from minimized-icon system metrics");
                Console.WriteLine("CONTROL MATCH: intentionally minimized native rectangle=" + RectText(native) + " iconic=true showCmd=" + placement.ShowCmd +
                    "; this matches the237x39 run-001 signature only, not its original sender.");
                Console.Error.WriteLine("EXPECTED CALIBRATION TRACE follows: own intentional minimize, not an unexpected product failure.");
                dialogTrace.DumpFailure("intentional-own-minimize calibration"); ownerTrace.DumpFailure("intentional-own-minimize calibration");
                RequireOwn(hwnd); ShowWindow(hwnd, 9);
                WaitFor(island, delegate { return !IsIconic(hwnd) && dialog.WindowState == WindowState.Normal; }, "restore own control");
                var restored = Capture(dialog, hwnd); result["Restored"] = restored;
                Check(IsWindow(hwnd) && !IsIconic(hwnd) && dialog.WindowState == WindowState.Normal, "own control did not restore to normal");
                RectI final; Check(GetWindowRect(hwnd, out final), "restored GetWindowRect failed");
                Check(final.Right - final.Left > GetSystemMetrics(57) && final.Bottom - final.Top > GetSystemMetrics(58), "restored own window retained icon dimensions");
                result["Completed"] = true;
                Console.WriteLine("CONTROL PASS: normal -> own SC_MINIMIZE -> iconic/system-icon dimensions -> own SW_RESTORE -> normal. No original sender attribution.");
            }
            catch (Exception error)
            {
                result["Error"] = error.ToString();
                if (dialogTrace != null) dialogTrace.DumpFailure("proof unexpected failure");
                if (ownerTrace != null) ownerTrace.DumpFailure("proof unexpected failure");
                throw;
            }
            finally
            {
                if (dialog != null) dialog.Close();
                if (island != null) { StopTimers(island); island.Close(); }
                if (dialogTrace != null) dialogTrace.Dispose(); if (ownerTrace != null) ownerTrace.Dispose();
                AppAppearance.Configure(language, theme); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previousData);
                SynchronizationContext.SetSynchronizationContext(previousContext);
                if (Directory.Exists(fixture) && fixture.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) Directory.Delete(fixture, true);
                result["EndedUtc"] = DateTime.UtcNow.ToString("o");
                File.WriteAllText(Path.Combine(output, "proof.json"), Json.Serialize(result), new UTF8Encoding(false));
            }
        }
        private static object Capture(Window window, IntPtr hwnd)
        {
            RequireOwn(hwnd); RectI bounds; Check(GetWindowRect(hwnd, out bounds), "GetWindowRect failed"); var placement = ReadPlacement(hwnd);
            return new { Utc = DateTime.UtcNow.ToString("o"), Hwnd = "0x" + hwnd.ToInt64().ToString("X"), NativeRectangle = RectText(bounds),
                NativeWidth = bounds.Right - bounds.Left, NativeHeight = bounds.Bottom - bounds.Top, Iconic = IsIconic(hwnd), ShowCmd = placement.ShowCmd,
                PlacementNormal = RectText(placement.Normal), ManagedState = window.WindowState.ToString(), window.Width, window.Height, window.ActualWidth, window.ActualHeight };
        }
        private static Placement ReadPlacement(IntPtr hwnd)
        {
            var placement = new Placement { Length = (uint)Marshal.SizeOf(typeof(Placement)) };
            Check(GetWindowPlacement(hwnd, ref placement), "GetWindowPlacement failed"); return placement;
        }
        private static void RequireOwn(IntPtr hwnd)
        {
            uint process; GetWindowThreadProcessId(hwnd, out process);
            Check(hwnd != IntPtr.Zero && IsWindow(hwnd) && process == (uint)Process.GetCurrentProcess().Id, "command target must be this proof's own live window");
        }
        private static void WaitFor(IslandWindow owner, Func<bool> predicate, string context)
        {
            var frame = new DispatcherFrame(); bool timedOut = false; Exception failure = null; int stable = 0; TimeSpan previous = TimeSpan.MinValue;
            var deadline = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(1500) };
            EventHandler rendering = delegate(object sender, EventArgs args) {
                try { StopTimers(owner); var actual = (RenderingEventArgs)args; if (actual.RenderingTime == previous) return; previous = actual.RenderingTime;
                    stable = predicate() ? stable + 1 : 0; if (stable >= 2) frame.Continue = false; }
                catch (Exception error) { failure = error; frame.Continue = false; }
            };
            deadline.Tick += delegate { timedOut = true; frame.Continue = false; };
            CompositionTarget.Rendering += rendering; deadline.Start();
            try { Dispatcher.PushFrame(frame); } finally { deadline.Stop(); CompositionTarget.Rendering -= rendering; StopTimers(owner); }
            if (failure != null) throw failure; Check(!timedOut && stable >= 2, context + " did not complete naturally within deadline");
            var idle = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate { idle.Continue = false; })); Dispatcher.PushFrame(idle);
            Check(predicate(), context + " did not retain the required state after ContextIdle");
        }
        private static void StopTimers(IslandWindow window)
        {
            foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop();
        }
        private static string RectText(RectI value) { return value.Left + "," + value.Top + "," + (value.Right - value.Left) + "," + (value.Bottom - value.Top); }
        private static string Hash(string path) { using (var file = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""); }
        private static void VerifyBuild()
        {
            string target = Assembly.GetExecutingAssembly().Location;
            var manifest = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(Path.Combine(Path.GetDirectoryName(target), "minimize-signature-build.json")));
            Check(Hash(target) == (string)manifest["TargetSHA256"], "EXE identity differs from compiled manifest");
            foreach (var raw in (object[])manifest["Sources"]) { var item = (Dictionary<string, object>)raw; Check(Hash((string)item["Path"]) == (string)item["SHA256"], "source changed after proof compilation: " + item["Path"]); }
        }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
