using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace CodexIsland
{
    // Isolated fixtures and own demo windows: never launch Codex or read account credentials.
    internal static class NativeTests
    {
        private static int checks;

        private const int SuiteTimeoutMilliseconds = 10 * 60 * 1000;

        private sealed class TestSuite
        {
            internal readonly string Name;
            internal readonly Action Run;
            internal TestSuite(string name, Action run) { Name = name; Run = run; }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                // Both redirected child streams use this explicit encoding.
                Console.OutputEncoding = new UTF8Encoding(false);
                var suites = CreateSuites();
                if (args.Length > 0)
                {
                    if (args.Length != 2 || args[0] != "--suite")
                        throw new ArgumentException("Expected --suite followed by one registered suite name.");
                    TestSuite selected = null;
                    foreach (var suite in suites) if (suite.Name == args[1]) { selected = suite; break; }
                    if (selected == null) throw new ArgumentException("Unknown test suite: " + args[1]);
                    var previousContext = SynchronizationContext.Current;
                    try
                    {
                        // A fresh STA has no UI continuation context until WPF
                        // installs one. Establish it explicitly instead of
                        // depending on a previous suite having shown a window.
                        // The listener bridge fixture intentionally blocks on
                        // worker-only operations without pumping a Dispatcher.
                        SynchronizationContext.SetSynchronizationContext(selected.Name == "NotificationListenerTests" ?
                            null : new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                        selected.Run();
                    }
                    finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
                    Console.WriteLine("SUITE PASS:" + selected.Name);
                    Console.Out.Flush(); Console.Error.Flush();
                    return 0;
                }

                // Keep the original forty primitive checks in the coordinator.
                // WPF-heavy suites run in separate fresh STA processes so a
                // native failure is attributed without contaminating later suites.
                ParseTests();
                DisplayTests();
                foreach (var suite in suites) RunSuiteWorker(suite);
                Console.WriteLine("PASS: " + suites.Length + " isolated suite workers completed; no automatic retries.");
                Console.WriteLine("PASS: " + checks + " quota/display checks plus settings, backend, notifications, media, productivity, geometry and real WPF gesture checks.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL: " + error);
                return 1;
            }
        }

        private static TestSuite[] CreateSuites()
        {
            return new[] {
                new TestSuite("SupportTests", () => SupportTests.Run()),
                new TestSuite("BackendTests", () => BackendTests.Run()),
                new TestSuite("NotificationTests", () => NotificationTests.Run()),
                new TestSuite("NotificationListenerTests", () => NotificationListenerTests.Run()),
                new TestSuite("NotificationLoadingTests", () => NotificationLoadingTests.Run()),
                new TestSuite("NotificationSyncTests", () => NotificationSyncTests.Run()),
                new TestSuite("MediaTests", () => MediaTests.Run()),
                new TestSuite("MusicControlTests", () => MusicControlTests.Run()),
                new TestSuite("ClipboardHistoryTests", () => ClipboardHistoryTests.Run()),
                new TestSuite("LogWatchTests", () => LogWatchTests.Run()),
                new TestSuite("DesktopExperienceTests", () => DesktopExperienceTests.Run()),
                new TestSuite("DesktopDragPreferencesTests", () => DesktopDragPreferencesTests.Run()),
                new TestSuite("ExperienceTests", () => ExperienceTests.Run()),
                new TestSuite("OcrSmartDropTests", () => OcrSmartDropTests.Run()),
                new TestSuite("OcrPresentationTests", () => OcrPresentationTests.Run()),
                new TestSuite("SmartDropWindowTests", () => SmartDropWindowTests.Run()),
                new TestSuite("MediaSessionSelectionTests", () => MediaSessionSelectionTests.Run()),
                new TestSuite("TextTemplateTests", () => TextTemplateTests.Run()),
                new TestSuite("ProductivityV3Tests", () => ProductivityV3Tests.Run()),
                new TestSuite("ServiceReadyTests", () => ServiceReadyTests.Run()),
                new TestSuite("SettingsBackupTests", () => SettingsBackupTests.Run()),
                new TestSuite("MediaArtworkTests", () => MediaArtworkTests.Run()),
                new TestSuite("MediaLyricsTests", () => MediaLyricsTests.Run()),
                new TestSuite("MediaLyricsPresentationTests", () => MediaLyricsPresentationTests.Run()),
                new TestSuite("NowPlayingMonitorTests", () => NowPlayingMonitorTests.Run()),
                new TestSuite("OnlineMediaLyricsTests", () => OnlineMediaLyricsTests.Run()),
                new TestSuite("NowPlayingIntegrationTests", () => NowPlayingIntegrationTests.Run()),
                new TestSuite("ProductivityTests", () => ProductivityTests.Run()),
                new TestSuite("FocusPanelTests", () => FocusPanelTests.Run()),
                new TestSuite("SystemToolsTests", () => SystemToolsTests.Run()),
                new TestSuite("QuickToolsTests", () => QuickToolsTests.Run()),
                new TestSuite("UtilityTests", () => UtilityTests.Run()),
                new TestSuite("ReminderTests", () => ReminderTests.Run()),
                new TestSuite("FileShelfTests", () => FileShelfTests.Run()),
                new TestSuite("ColorTests", () => ColorTests.Run()),
                new TestSuite("TimeToolsTests", () => TimeToolsTests.Run()),
                new TestSuite("RandomToolsTests", () => RandomToolsTests.Run()),
                new TestSuite("FileHashTests", () => FileHashTests.Run()),
                new TestSuite("BatchHashPanelTests", () => BatchHashPanelTests.Run()),
                new TestSuite("SystemUtilityTests", () => SystemUtilityTests.Run()),
                new TestSuite("ImageQuickTests", () => ImageQuickTests.Run()),
                new TestSuite("ScreenPinTests", () => ScreenPinTests.Run()),
                new TestSuite("TextDiffTests", () => TextDiffTests.Run()),
                new TestSuite("WorkspaceSceneTests", () => WorkspaceSceneTests.Run()),
                new TestSuite("QrTransferTests", () => QrTransferTests.Run()),
                new TestSuite("AdditionalToolIntegrationTests", () => AdditionalToolIntegrationTests.Run()),
                new TestSuite("CompactWidgetTests", () => CompactWidgetTests.Run()),
                new TestSuite("AppearanceTests", () => AppearanceTests.Run()),
                new TestSuite("UiThemePolishTests", () => UiThemePolishTests.Run()),
                new TestSuite("EditorDialogLayoutTests", () => EditorDialogLayoutTests.Run()),
                new TestSuite("FloatingSurfaceTests", () => FloatingSurfaceTests.Run()),
                new TestSuite("NoticeDialogTests", () => NoticeDialogTests.Run()),
                new TestSuite("AppearanceInteractionTests", () => AppearanceInteractionTests.Run()),
                new TestSuite("ToolNavigationTests", () => ToolNavigationTests.Run()),
                new TestSuite("ToolPageLayoutTests", () => ToolPageLayoutTests.Run()),
                new TestSuite("SurfaceLayoutTests", () => SurfaceLayoutTests.Run()),
                new TestSuite("CompactSurfaceLayoutTests", () => CompactSurfaceLayoutTests.Run()),
                new TestSuite("TrayMenuLayoutTests", () => TrayMenuLayoutTests.Run()),
                new TestSuite("PopupSurfaceDpiTests", () => PopupSurfaceDpiTests.Run()),
                new TestSuite("FloatingDpiCases", () => FloatingDpiCases.Run()),
                new TestSuite("StartupWindowTests", () => StartupWindowTests.Run()),
                new TestSuite("SingleInstanceTests", () => SingleInstanceTests.Run()),
                new TestSuite("WindowGeometryTests", () => WindowGeometryTests.Run()),
                new TestSuite("WindowGestureTests", () => WindowGestureTests.Run()),
                new TestSuite("CompactDragTests", () => CompactDragTests.Run()),
                new TestSuite("CompactHoverTests", () => CompactHoverTests.Run()),
                new TestSuite("HoverPreferencesTests", () => HoverPreferencesTests.Run()),
                new TestSuite("CompactQuickActionTests", () => CompactQuickActionTests.Run()),
                new TestSuite("InteractionRegressionTests", () => InteractionRegressionTests.Run()),
                new TestSuite("PageTransitionTests", () => PageTransitionTests.Run()),
                new TestSuite("ToolTransitionTests", () => ToolTransitionTests.Run()),
                new TestSuite("PickerAndChromeTests", () => PickerAndChromeTests.Run()),
                new TestSuite("SettingsAndFilterMotionTests", () => SettingsAndFilterMotionTests.Run()),
                new TestSuite("NotificationCardMotionTests", () => NotificationCardMotionTests.Run()),
                new TestSuite("NotificationPeekMotionTests", () => NotificationPeekMotionTests.Run()),
                new TestSuite("CompactMusicMotionTests", () => CompactMusicMotionTests.Run()),
                new TestSuite("ModeTransitionTests", () => ModeTransitionTests.Run()),
                new TestSuite("ModeWorkloadTests", () => ModeWorkloadTests.Run()),
                new TestSuite("AnimationFrameTests", () => AnimationFrameTests.Run()),
                new TestSuite("LongRunningTests", () => LongRunningTests.Run()),
                new TestSuite("AutoSceneWidgetTests", () => AutoSceneWidgetTests.Run()),
                new TestSuite("AudioMixerTests", () => AudioMixerTests.Run()),
                new TestSuite("LanTransferTests", () => LanTransferTests.Run()),
                new TestSuite("ScreenshotAnnotationTests", () => ScreenshotAnnotationTests.Run()),
                new TestSuite("BatchFilesTests", () => BatchFilesTests.Run()),
                new TestSuite("TaskActivityTests", () => TaskActivityTests.Run()),
                new TestSuite("NotificationTaskTests", () => NotificationTaskTests.Run()),
                new TestSuite("NextFeatureLayoutTests", () => NextFeatureLayoutTests.Run())
            };
        }

        private static void RunSuiteWorker(TestSuite suite)
        {
            var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--suite " + suite.Name) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.CurrentDirectory,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            Console.WriteLine("SUITE START:" + suite.Name);
            using (var worker = new Process { StartInfo = start })
            {
                int completionMarkers = 0, outputEnded = 0, errorEnded = 0;
                worker.OutputDataReceived += (sender, output) => {
                    if (output.Data == null) { Interlocked.Exchange(ref outputEnded, 1); return; }
                    if (output.Data == "SUITE PASS:" + suite.Name) Interlocked.Increment(ref completionMarkers);
                    Console.WriteLine(output.Data);
                };
                worker.ErrorDataReceived += (sender, output) => {
                    if (output.Data == null) Interlocked.Exchange(ref errorEnded, 1);
                    else Console.Error.WriteLine(output.Data);
                };
                if (!worker.Start()) throw new InvalidOperationException("Could not start test suite: " + suite.Name);
                worker.BeginOutputReadLine(); worker.BeginErrorReadLine();
                if (!worker.WaitForExit(SuiteTimeoutMilliseconds))
                {
                    // This is only the exact helper created above. Never target
                    // the installed application or another process by its name.
                    if (!worker.HasExited) worker.Kill();
                    worker.WaitForExit(5000);
                    throw new TimeoutException("Test suite exceeded ten minutes: " + suite.Name);
                }
                // Drain both streams within a separate bounded grace period.
                // A descendant retaining a pipe must not hang the coordinator
                // after the suite process itself has already exited.
                var drain = Stopwatch.StartNew();
                while (Interlocked.CompareExchange(ref outputEnded, 0, 0) == 0 ||
                    Interlocked.CompareExchange(ref errorEnded, 0, 0) == 0)
                {
                    if (drain.ElapsedMilliseconds >= 5000)
                        throw new TimeoutException("Test suite output did not close: " + suite.Name);
                    Thread.Sleep(5);
                }
                if (worker.ExitCode != 0)
                    throw new InvalidOperationException("Test suite " + suite.Name + " exited " + worker.ExitCode +
                        " (0x" + unchecked((uint)worker.ExitCode).ToString("X8") + "). No retry was attempted.");
                if (Interlocked.CompareExchange(ref completionMarkers, 0, 0) != 1)
                    throw new InvalidOperationException("Test suite did not report exactly one normal completion: " + suite.Name);
            }
        }

        private static void ParseTests()
        {
            var legacy = CodexClient.ParseRateLimits("{\"rateLimits\":{\"planType\":\"plus\",\"primary\":{\"usedPercent\":23.5,\"windowDurationMins\":300,\"resetsAt\":2000000000},\"secondary\":{\"usedPercent\":48,\"windowDurationMins\":10080,\"resetsAt\":2000500000}}}");
            Check(legacy.Buckets.Count == 1 && legacy.Buckets[0].LimitId == "codex", "Legacy bucket fallback");
            Check(legacy.PlanType == "plus", "Plan extracted from legacy bucket");
            Check(legacy.Buckets[0].Primary.Remaining == 76.5, "Used percentage converted to remaining");
            Check(legacy.Buckets[0].Primary.WindowMinutes == 300 && legacy.Buckets[0].Primary.ResetsAt == 2000000000, "Window metadata retained");
            Check(legacy.Buckets[0].Secondary.Remaining == 52, "Independent secondary limit");

            var mapped = CodexClient.ParseRateLimits("{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":99}},\"rateLimitsByLimitId\":{\"zeta\":{\"limitName\":\"Zeta\",\"primary\":{\"usedPercent\":30}},\"codex\":{\"primary\":{\"usedPercent\":12}},\"alpha\":{\"secondary\":{\"usedPercent\":20}}}}}");
            Check(mapped.Buckets.Count == 3, "Per-limit map preferred without duplicating legacy bucket");
            Check(mapped.Buckets[0].LimitId == "codex" && mapped.Buckets[1].LimitId == "alpha" && mapped.Buckets[2].LimitId == "zeta", "Codex bucket first, then stable alphabetical order");
            Check(mapped.Buckets[0].Primary.Remaining == 88 && mapped.Buckets[2].Name == "Zeta", "Envelope and names parsed");
            Check(mapped.Buckets[1].Primary == null && mapped.Buckets[1].Secondary.Remaining == 80, "Missing primary does not mask secondary");

            var fallback = CodexClient.ParseRateLimits("{\"rateLimitsByLimitId\":{\"invalid\":false},\"rateLimits\":{\"primary\":{\"usedPercent\":10}}}");
            Check(fallback.Buckets.Count == 1 && fallback.Buckets[0].Primary.Remaining == 90, "Unusable map falls back to legacy");
            Check(CodexClient.ParseRateLimits("{}").Buckets.Count == 0, "Absent quotas are not invented");
            Check(CodexClient.ParseRateLimits("{\"rateLimits\":{\"primary\":null,\"secondary\":null}}").Buckets[0].Primary == null, "Explicit null window remains unknown");

            var unknown = CodexClient.ParseRateLimits("{\"rateLimits\":{\"primary\":{},\"secondary\":{\"usedPercent\":\"20\",\"windowDurationMins\":true,\"resetsAt\":\"2000000000\"}}}");
            Check(!unknown.Buckets[0].Primary.Remaining.HasValue, "Missing used percent is unknown rather than full quota");
            Check(!unknown.Buckets[0].Secondary.Remaining.HasValue && !unknown.Buckets[0].Secondary.WindowMinutes.HasValue && !unknown.Buckets[0].Secondary.ResetsAt.HasValue, "Reject string and boolean numeric fields");

            var malformedWindow = CodexClient.ParseRateLimits("{\"rateLimits\":{\"primary\":{\"usedPercent\":-20,\"windowDurationMins\":-1,\"resetsAt\":-5},\"secondary\":{\"usedPercent\":130,\"windowDurationMins\":1.5,\"resetsAt\":2.5}}}");
            Check(malformedWindow.Buckets[0].Primary.Remaining == 100 && malformedWindow.Buckets[0].Secondary.Remaining == 0, "Clamp remaining percentage to 0 through 100");
            Check(!malformedWindow.Buckets[0].Primary.WindowMinutes.HasValue && !malformedWindow.Buckets[0].Primary.ResetsAt.HasValue, "Reject negative duration and reset timestamp");
            Check(!malformedWindow.Buckets[0].Secondary.WindowMinutes.HasValue && !malformedWindow.Buckets[0].Secondary.ResetsAt.HasValue, "Reject fractional duration and reset timestamp");

            foreach (double invalid in new[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity })
                Check(!new QuotaWindow { UsedPercent = invalid }.Remaining.HasValue, "Nonfinite quota cannot display as valid");
            foreach (string invalid in new[] { "not json", "[]", "null", "42", "\"string\"" })
                Throws(delegate { CodexClient.ParseRateLimits(invalid); }, "Reject invalid root payload");
        }

        private static void DisplayTests()
        {
            Check(Display.Percent(null) == "—", "Unknown percentage display");
            Check(Display.Percent(125) == "100%" && Display.Percent(-5) == "0%", "Percentage bounds");
            Check(Display.Percent(76.5) == "77%", "Percentage display rounds");
            Check(Display.Window(10080, "unknown") == "每周额度", "Week label");
            Check(Display.Window(300, "unknown") == "5 小时额度", "Hour label");
            Check(Display.Window(1440, "unknown") == "1 天额度", "Day label");
            Check(Display.Window(45, "unknown") == "45 分钟额度", "Minute label");
            Check(Display.Window(null, "unknown") == "unknown" && Display.Window(0, "unknown") == "unknown", "Unknown duration uses fallback");
            Check(Display.ShortWindow(300, "—") == "5h" && Display.ShortWindow(10080, "—") == "7d", "Compact labels reflect actual windows");
            Check(Display.ShortWindow(45, "—") == "45m" && Display.ShortWindow(-1, "—") == "—", "Compact minute and invalid labels");
            Check(Display.Reset(null) == "重置时间未知", "Unknown reset time");
            Check(Display.Reset(0) == "等待同步新的额度", "Passed reset requires fresh data");
            long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            Check(Display.Reset(now + 120).Contains("分钟后重置"), "Near reset countdown");
            Check(Display.Reset(now + 7200).Contains("小时"), "Hour reset countdown");
            Check(Display.Reset(now + 200000).Contains("天"), "Long reset countdown");
        }

        private static void Check(bool condition, string label)
        {
            checks++;
            if (!condition) throw new Exception(label);
        }

        private static void Throws(Action action, string label)
        {
            try { action(); }
            catch (CodexClientException) { checks++; return; }
            throw new Exception(label);
        }
    }
}
