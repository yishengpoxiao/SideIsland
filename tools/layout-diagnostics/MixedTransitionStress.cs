using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // Standalone, strict diagnostic. The default 79-suite coordinator is not
    // changed. Only a demo HWND and temporary data receive production actions.
    internal static class MixedTransitionStress
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const int Rounds = 24, ActionsPerRound = 22, GlobalDeadlineMilliseconds = 60000, WaitDeadlineMilliseconds = 1500;
        private const string RawInput = "配置 keep_raw=true\n原文 Original 日本語 😀\n不要翻译我的输入";
        private const string RawOutput = "OUTPUT immutable & <original> 0xC0000409";
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private static readonly string[] Edges = { "left", "right", "top", "bottom" };
        private static readonly string[] Roles = { "#111518", "#1A2323", "#E3EFE7", "#A1EACF", "#30383B", "#3A5145" };
        private static readonly Dictionary<SolidColorBrush, Color> Retired = new Dictionary<SolidColorBrush, Color>();
        private static readonly Dictionary<string, ChromePose> Chrome = new Dictionary<string, ChromePose>();
        private static readonly Dictionary<int, Rect> ModeNative = new Dictionary<int, Rect>();
        private static readonly List<object> AppearanceMeasurements = new List<object>();
        private static readonly List<object> NearTargetCommits = new List<object>();
        private static readonly Stopwatch Total = new Stopwatch();
        private static IslandWindow Window;
        private static TextBox Input, Output;
        private static StreamWriter Events;
        private static string Evidence, ExpectedLanguage = "zh", ExpectedTheme = "dark", ExpectedTool = "focus", ExpectedPage = "messages";
        private static bool ExpectedExpanded, IdleQueued, Closed, ActionExecuting;
        private static int Round, Actions, Pages, Tools, Appearances, Modes, Reversals, PickerRequests, CompletedRounds;
        private static int Frames, UniqueFrames, IdleFrames, ModeMiddleFrames, PageMiddleFrames, ToolMiddleFrames, PickerMiddleFrames, ChromeSamples, AppearanceEpoch, EpochStartFrame, SetupActions;
        private static int InMotionAppearanceMeasurements;
        private static int ReentrantRenderingSamples;
        private static int MeaningfulMiddleWaits, MeaningfulMiddleSamples;
        private static double MaximumAppearanceInstantBoundsDelta;
        private static TimeSpan LastRenderingTime = TimeSpan.MinValue;
        private static Exception ObservationFailure;

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                VerifyBuild();
                if (args.Length == 1 && args[0] == "--self-check")
                {
                    foreach (string field in new[] { "settings", "toolPicker", "toolPanels", "toolButtons", "expandedHeader", "expandedTabs", "expandedHint", "pageTransitionActive", "toolTransitionActive", "visualTransitionActive", "transitionVersion", "pendingTransitionSnapshot", "outgoingImage" })
                        Check(typeof(IslandWindow).GetField(field, Private) != null, "missing production field " + field);
                    foreach (string method in new[] { "SelectPage", "SelectToolsPage", "SelectTool", "ChangeSide", "ApplyAppearance" })
                        Check(typeof(IslandWindow).GetMethod(method, Private) != null, "missing production route " + method);
                    Check(Rounds * ActionsPerRound == 528, "fixed action plan changed");
                    Log("SELF-CHECK PASS UI-created=false UI-executed=false rounds=24 actions=528 appearances=96 in-motion-appearances=48 reversals=48 strict-deadline-ms=60000 contract=continuous-appearance-outline-watch");
                    return 0;
                }
                Evidence = Path.Combine(Environment.CurrentDirectory, "artifacts", "full-regression-1.12.16", "mixed-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
                for (int index = 0; index < args.Length; index += 2)
                {
                    Check(index + 1 < args.Length, "every option needs a value");
                    if (args[index] == "--suite") Check(args[index + 1] == "MixedTransitionStress", "unknown suite");
                    else if (args[index] == "--output") Evidence = Path.GetFullPath(args[index + 1]);
                    else throw new ArgumentException("Use --suite MixedTransitionStress and optional --output project-directory, or --self-check.");
                }
                string project = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd('\\') + "\\";
                Evidence = Path.GetFullPath(Evidence); Check(Evidence.StartsWith(project, StringComparison.OrdinalIgnoreCase), "evidence must stay in project");
                Check(!Directory.Exists(Evidence), "fresh evidence directory required"); Directory.CreateDirectory(Evidence);
                Log("OUTPUT " + Evidence);
                Run();
                Log("SUITE PASS:MixedTransitionStress");
                return 0;
            }
            catch (Exception error)
            {
                Log("FAIL: " + error);
                if (Evidence != null && Directory.Exists(Evidence)) Save(false, error.ToString());
                return 1;
            }
        }

        private static void Run()
        {
            string oldData = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), oldLanguage = UiLocalization.Language, oldTheme = UiTheme.Name;
            var oldContext = SynchronizationContext.Current;
            string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
            string fixture = Path.GetFullPath(Path.Combine(temporary, "CodexIsland.MixedTransitions." + Guid.NewGuid().ToString("N")));
            Check(fixture.StartsWith(temporary, StringComparison.OrdinalIgnoreCase), "temporary fixture escaped its root");
            try
            {
                Check(SystemParameters.ClientAreaAnimation, "animations are disabled; required middle frames cannot be tested");
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", fixture);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var store = new SettingsStore();
                store.Save(new Settings { Language = "zh", Theme = "dark", Side = "left", AlwaysOnTop = false, AutoCollapse = false,
                    HoverExpandEnabled = false, NotificationsEnabled = false, AutoMusic = false, OnlineLyricsEnabled = false,
                    CustomExpandedSize = true, ExpandedWidth = 391, ExpandedHeight = 617, HiddenTools = "", FavoriteTools = "" });
                string settingsHash = Hash(store.FilePath);
                Window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false, Topmost = false };
                Window.Loaded += delegate { StopTimers(); }; Window.Show();
                // Initial fixture setup is outside the measured plan. All later
                // mode completion is reached by the actual production drivers.
                Window.SetExpanded(true, false); ExpectedExpanded = true;
                Call(Window, "SelectToolsPage"); ExpectedPage = "tools"; Call(Window, "SelectTool", "focus");
                var text = Panels()["text"];
                Input = Read<TextBox>(text, "input"); Output = Read<TextBox>(text, "output");
                Input.Text = RawInput; Output.Text = RawOutput; Input.Select(2, 5);
                using (Events = new StreamWriter(Path.Combine(Evidence, "frames-and-actions.jsonl"), false, new UTF8Encoding(false), 65536))
                {
                    Total.Start(); CompositionTarget.Rendering += ObserveRendering;
                    try
                    {
                        WaitForRest("initial fixture"); RememberPalette();
                        Log("CONFIG rounds=24 actions-per-round=22 global-deadline-ms=60000 rendering-policy=" + RenderOptions.ProcessRenderMode + " tier=" + (RenderCapability.Tier >> 16) + " no-sleep=true no-retry=true no-RTB=true");
                        for (Round = 1; Round <= Rounds; Round++)
                        {
                            CheckDeadline();
                            // Eight edge/width cells, each repeated three times,
                            // retain one HWND and the same editor throughout.
                            int cell = (Round - 1) / 3;
                            var settings = Read<Settings>(Window, "settings");
                            settings.ExpandedWidth = cell % 2 == 0 ? 391 : 560;
                            ActionExecuting = true;
                            try { Call(Window, "ChangeSide", Edges[cell / 2]); }
                            finally { ActionExecuting = false; }
                            SetupActions++;
                            AppearanceEpoch++; EpochStartFrame = UniqueFrames;
                            ModeNative.Clear(); WaitForRest("round setup");
                            int before = Actions;
                            Log("ROUND BEGIN " + Round + " edge=" + settings.Side + " width=" + settings.ExpandedWidth + " " + State());

                            SelectTool("focus"); WaitFrames(1, 0, "focus request");
                            SelectPage("tools"); WaitFrames(1, 0, "tools request");
                            SetMode(true); WaitForRest("prepare expanded tools");
                            Act("picker-open", "picker", delegate { Picker().IsExpanded = true; });
                            WaitForRest("open chooser naturally");
                            Check(Picker().IsExpanded && Picker().CurrentHeight > 70, "chooser did not genuinely open");
                            SelectPage("messages"); WaitFrames(2, 0, "leave open chooser");
                            SelectPage("tools"); WaitFrames(2, 35, "return incoming tools page");
                            Check(Read<bool>(Window, "pageTransitionActive"), "overlap selection did not occur during real page fade");
                            double chooserStart = Picker().CurrentHeight;
                            SelectToolDuringPageFade("text");
                            Check(!Picker().IsExpanded && Picker().IsMotionActive && Math.Abs(Picker().CurrentHeight - chooserStart) < .1,
                                "page-fade tool selection immediately committed chooser height");
                            WaitFrames(2, 0, "overlapping chooser collapse");
                            SelectPage("status"); WaitFrames(1, 0, "status interruption");
                            SelectPage("tools"); WaitFrames(1, 0, "tools interruption");
                            SelectTool("calculator"); WaitFrames(1, 0, "calculator rapid switch");
                            SelectTool("text"); WaitFrames(1, 0, "text rapid switch");
                            ToggleLanguage(); WaitFrames(1, 0, "language during content motion");
                            ToggleTheme(); WaitFrames(1, 0, "theme during content motion");
                            SetMode(false); WaitForMiddle("first collapse");
                            ReverseTo(true); WaitForMiddle("first reverse expansion");
                            // The repaired appearance route must preserve this
                            // moving outline and its time source for both clicks.
                            ToggleLanguage(); WaitFrames(1, 0, "language during mode motion");
                            SelectPage("messages"); WaitFrames(1, 0, "messages during mixed actions");
                            ToggleTheme(); WaitFrames(1, 0, "theme during page fade");
                            SelectPage("tools"); WaitFrames(1, 0, "final tools request");
                            string finalTool = Round % 2 == 0 ? "focus" : "text";
                            SelectTool(finalTool); WaitFrames(1, 0, "final tool request");
                            SetMode(false); WaitForMiddle("second collapse");
                            ReverseTo(true); WaitForRest("latest final expansion naturally");

                            Check(Actions - before == ActionsPerRound, "round did not execute exactly 22 actions");
                            Check(ExpectedLanguage == "zh" && ExpectedTheme == "dark" && ExpectedTool == finalTool && ExpectedPage == "tools" && ExpectedExpanded,
                                "fixed round plan did not restore its expected final state");
                            Check(Hash(store.FilePath) == settingsHash, "demo action unexpectedly saved fixture settings");
                            CheckRest("round final"); CompletedRounds++;
                            Log("ROUND END " + Round + " actions=" + Actions + " " + State());
                        }
                        Check(CompletedRounds == Rounds && Actions == Rounds * ActionsPerRound && Pages == 168 && Tools == 120 && Appearances == 96 && Modes == 120 && Reversals == 48 && PickerRequests == 24,
                            "fixed action counts did not all complete exactly once");
                        Check(ModeMiddleFrames >= 144 && MeaningfulMiddleWaits == 72 && MeaningfulMiddleSamples == 144 && PageMiddleFrames >= 24 && ToolMiddleFrames > 0 && PickerMiddleFrames >= 24 && IdleFrames > 0 && ChromeSamples >= 24 && InMotionAppearanceMeasurements == 48,
                            "required actual middle-frame/chrome coverage was not observed");
                        CheckDeadline(); CheckRest("final");
                        Save(true, null);
                        Log("PASS rounds=" + CompletedRounds + " actions=" + Actions + " page=" + Pages + " tool=" + Tools + " appearance=" + Appearances + " mode=" + Modes + " reversals=" + Reversals +
                            " rendering-events=" + Frames + " unique-rendering-times=" + UniqueFrames + " context-idle=" + IdleFrames + " stable-chrome=" + ChromeSamples + " elapsed-ms=" + Total.ElapsedMilliseconds + "; latest state, natural cleanup and frozen palettes verified; no retry.");
                    }
                    finally { Closed = true; CompositionTarget.Rendering -= ObserveRendering; Events.Flush(); }
                }
            }
            finally
            {
                Closed = true;
                if (Window != null) { StopTimers(); Window.Close(); Window = null; }
                Input = Output = null;
                AppAppearance.Configure(oldLanguage, oldTheme);
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", oldData);
                SynchronizationContext.SetSynchronizationContext(oldContext);
                if (Directory.Exists(fixture) && fixture.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) Directory.Delete(fixture, true);
            }
        }

        private static void Act(string name, string kind, Action action)
        {
            CheckDeadline(); ThrowObservation();
            // Dispatch at Input only after the caller's required real rendering
            // frames. The observer never mutates the layout it is measuring.
            var frame = new DispatcherFrame(); Exception failure = null;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate {
                try
                {
                    Actions++;
                    if (kind == "page") Pages++; else if (kind == "tool") Tools++; else if (kind == "appearance") Appearances++;
                    else if (kind == "mode") Modes++; else if (kind == "picker") PickerRequests++;
                    Event("ACTION BEFORE", name);
                    bool appearanceWasMoving = kind == "appearance" && ModeActive();
                    int roundAction = ((Actions - 1) % ActionsPerRound) + 1;
                    bool requiresContinuousAppearance = kind == "appearance" && (roundAction == 16 || roundAction == 18);
                    object appearanceWatch = kind == "appearance" ? Read<object>(Window, "transitionWatch") : null;
                    int appearanceVersion = kind == "appearance" ? Read<int>(Window, "transitionVersion") : -1;
                    if (requiresContinuousAppearance)
                        Check(appearanceWasMoving && appearanceWatch != null, "fixed round mode appearance must enter during a real moving outline, at action " + roundAction);
                    double appearanceProgress = kind == "appearance" ? Read<double>(Window, "transitionLastProgress") : 0;
                    Rect appearanceBefore = kind == "appearance" ? Window.CurrentVisualBounds() : Rect.Empty;
                    Rect appearanceTarget = kind == "appearance" ? Read<Rect>(Window, "transitionTargetBounds") : Rect.Empty;
                    double liveBefore = kind == "appearance" ? Read<Border>(Window, "shell").Opacity : 0;
                    var imageBefore = Read<Image>(Window, "outgoingImage");
                    double outgoingBefore = kind == "appearance" && imageBefore.Source != null ? imageBefore.Opacity : 0;
                    ActionExecuting = true;
                    try { action(); }
                    finally { ActionExecuting = false; }
                    if (kind == "appearance") EpochStartFrame = UniqueFrames;
                    StopTimers(); VerifyLogical(); RememberPalette(); Event("ACTION AFTER", name);
                    if (kind == "appearance")
                    {
                        Rect appearanceAfter = Window.CurrentVisualBounds();
                        double delta = Math.Max(Math.Max(Math.Abs(appearanceAfter.X - appearanceBefore.X), Math.Abs(appearanceAfter.Y - appearanceBefore.Y)),
                            Math.Max(Math.Abs(appearanceAfter.Width - appearanceBefore.Width), Math.Abs(appearanceAfter.Height - appearanceBefore.Height)));
                        MaximumAppearanceInstantBoundsDelta = Math.Max(MaximumAppearanceInstantBoundsDelta, delta);
                        if (appearanceWasMoving) InMotionAppearanceMeasurements++;
                        double liveAfter = Read<Border>(Window, "shell").Opacity;
                        var imageAfter = Read<Image>(Window, "outgoingImage");
                        double outgoingAfter = imageAfter.Source == null ? 0 : imageAfter.Opacity;
                        bool modeStillActive = ModeActive(), watchPreserved = Object.ReferenceEquals(appearanceWatch, Read<object>(Window, "transitionWatch"));
                        int versionAfter = Read<int>(Window, "transitionVersion");
                        var measurement = new { Kind = "appearance-instant-bounds-measurement", Round = Round, ActionIndex = Actions, Action = name,
                            ModeWasActive = appearanceWasMoving, ProgressBefore = appearanceProgress, ModeStillActive = modeStillActive,
                            RequiredContinuousModeContract = requiresContinuousAppearance, RoundAction = roundAction, ModeWatchReferencePreserved = watchPreserved,
                            TransitionVersionBefore = appearanceVersion, TransitionVersionAfter = versionAfter,
                            Before = appearanceBefore.ToString(CultureInfo.InvariantCulture), After = appearanceAfter.ToString(CultureInfo.InvariantCulture),
                            RequestedTargetBefore = appearanceTarget.ToString(CultureInfo.InvariantCulture), InstantMaximumComponentDeltaDip = delta,
                            LiveAlphaBefore = liveBefore, OutgoingAlphaBefore = outgoingBefore, ContributionSumBefore = liveBefore + outgoingBefore,
                            SimpleOverCompositeAlphaBefore = 1 - (1 - liveBefore) * (1 - outgoingBefore),
                            LiveAlphaAfter = liveAfter, OutgoingAlphaAfter = outgoingAfter, ContributionSumAfter = liveAfter + outgoingAfter,
                            SimpleOverCompositeAlphaAfter = 1 - (1 - liveAfter) * (1 - outgoingAfter),
                            Interpretation = "V3 requires both per-round mode appearance actions to preserve the active outline/watch/version and <=.02 DIP instantaneous continuity. Other appearance samples remain measured data." };
                        AppearanceMeasurements.Add(measurement); Events.WriteLine(Json.Serialize(measurement));
                        if (requiresContinuousAppearance)
                        {
                            Check(modeStillActive, "appearance ended the moving outline at fixed round action " + roundAction);
                            Check(watchPreserved && appearanceVersion == versionAfter, "appearance replaced the active outline clock or version at fixed round action " + roundAction);
                            Same(appearanceAfter, appearanceBefore, .02, "appearance jumped the moving outline at fixed round action " + roundAction);
                        }
                    }
                }
                catch (Exception error) { failure = error; }
                finally { frame.Continue = false; }
            }));
            Dispatcher.PushFrame(frame);
            if (failure != null) throw new InvalidOperationException("action " + name + " failed", failure);
        }
        private static void SelectPage(string page)
        {
            Act("page-" + page, "page", delegate {
                ExpectedPage = page;
                Read<Button>(Window, page == "tools" ? "toolsTab" : page == "messages" ? "notificationsTab" : "statusTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
        }
        private static void SelectTool(string key)
        {
            Act("tool-" + key, "tool", delegate { ExpectedTool = key; Read<Dictionary<string, Button>>(Window, "toolButtons")[key].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
        }
        private static void SelectToolDuringPageFade(string key)
        {
            Act("tool-" + key + "-during-page-fade", "tool", delegate {
                Check(Read<bool>(Window, "pageTransitionActive"), "page fade ended before the queued overlap selection actually ran");
                ExpectedTool = key; Read<Dictionary<string, Button>>(Window, "toolButtons")[key].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
        }
        private static void SetMode(bool expanded)
        { Act("mode-" + expanded, "mode", delegate { ExpectedExpanded = expanded; Window.SetExpanded(expanded, true); }); }
        private static void ReverseTo(bool expanded)
        {
            Act("mode-reverse-" + expanded, "mode", delegate {
                // Compare within the actual Input callback: a higher-priority
                // render may legitimately run between queueing and execution.
                Check(ModeActive(), "reversal was not requested during actual mode motion");
                Rect before = Window.CurrentVisualBounds(); Reversals++; ExpectedExpanded = expanded;
                Window.SetExpanded(expanded, true);
                Rect after = Window.CurrentVisualBounds(); Rect target = Read<Rect>(Window, "transitionTargetBounds");
                // TransitionSize's exact changed test uses > .5 DIP for every
                // component. A near-target request may commit and pixel-round
                // rather than start another animation. Keep that branch as a
                // distinct measurement, never a general continuity tolerance.
                if (!ModeActive() && Near(before, target, .5))
                {
                    var source = (HwndSource)PresentationSource.FromVisual(Window);
                    Matrix toDevice = source.CompositionTarget.TransformToDevice, fromDevice = source.CompositionTarget.TransformFromDevice;
                    Point targetPosition = toDevice.Transform(target.TopLeft); Vector targetSize = toDevice.Transform(new Vector(target.Width, target.Height));
                    var nativeTarget = new Rect(Math.Round(targetPosition.X), Math.Round(targetPosition.Y), Math.Max(1, Math.Round(targetSize.X)), Math.Max(1, Math.Round(targetSize.Y)));
                    Same(NativeBounds(), nativeTarget, .01, "near-target branch did not use the exact rounded native target");
                    var expected = new MatrixTransform(fromDevice).TransformBounds(nativeTarget);
                    Same(after, expected, .02, "near-target branch committed an unrelated visual rectangle");
                    var measurement = new { Kind = "exact-near-target-commit", Round = Round, ActionIndex = Actions, Before = before.ToString(CultureInfo.InvariantCulture),
                        After = after.ToString(CultureInfo.InvariantCulture), RequestedTarget = target.ToString(CultureInfo.InvariantCulture), RoundedNativeTarget = nativeTarget.ToString(CultureInfo.InvariantCulture),
                        Predicate = "all abs(CurrentVisual component - requested target component) <= .5 DIP; mode inactive after request", ContinuityToleranceUnchanged = .02 };
                    NearTargetCommits.Add(measurement); Events.WriteLine(Json.Serialize(measurement));
                }
                else Same(after, before, .02, "reversal did not start from the last rendered outline");
            });
        }
        private static void ToggleLanguage()
        {
            Act("language-button", "appearance", delegate { ExpectedLanguage = ExpectedLanguage == "zh" ? "en" : "zh"; AppearanceEpoch++; EpochStartFrame = UniqueFrames;
                Read<Button>(Window, "languageButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
        }
        private static void ToggleTheme()
        {
            Act("theme-button", "appearance", delegate { ExpectedTheme = ExpectedTheme == "dark" ? "light" : "dark"; AppearanceEpoch++; EpochStartFrame = UniqueFrames;
                Read<Button>(Window, "themeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
        }

        private static void WaitFrames(int count, int minimumMilliseconds, string context)
        {
            int first = UniqueFrames; var watch = Stopwatch.StartNew();
            Wait(delegate { return UniqueFrames - first >= count && watch.ElapsedMilliseconds >= minimumMilliseconds; }, context);
        }
        private static void WaitForMiddle(string context)
        {
            int observed = 0, version = Read<int>(Window, "transitionVersion"); TimeSpan last = TimeSpan.MinValue;
            Wait(delegate {
                double progress = Read<double>(Window, "transitionLastProgress");
                if (ModeActive() && Read<int>(Window, "transitionVersion") == version && progress >= .08 && progress <= .8 && LastRenderingTime != last)
                {
                    Rect host = Read<Rect>(Window, "transitionHostBounds"), surface = Read<Rect>(Window, "transitionFromSurface");
                    var from = new Rect(host.X + surface.X - 8, host.Y + surface.Y - 8, surface.Width + 16, surface.Height + 16);
                    Rect current = Window.CurrentVisualBounds(), target = Read<Rect>(Window, "transitionTargetBounds");
                    Matrix scale = ((HwndSource)PresentationSource.FromVisual(Window)).CompositionTarget.TransformToDevice;
                    double fromDistance = PhysicalComponentDistance(current, from, scale), targetDistance = PhysicalComponentDistance(current, target, scale);
                    if (fromDistance >= 1 && targetDistance >= 1)
                    {
                        observed++; MeaningfulMiddleSamples++; last = LastRenderingTime;
                        Events.WriteLine(Json.Serialize(new { Kind = "meaningful-mode-middle", Context = context, Round = Round, TransitionVersion = version,
                            RenderingTimeMilliseconds = LastRenderingTime.TotalMilliseconds, ModeProgress = progress,
                            DistanceFromStartPhysicalPixels = fromDistance, DistanceFromTargetPhysicalPixels = targetDistance,
                            VisualBounds = current.ToString(CultureInfo.InvariantCulture) }));
                    }
                }
                return observed >= 2;
            }, context);
            MeaningfulMiddleWaits++;
        }
        private static void WaitForRest(string context)
        {
            int stable = 0; TimeSpan last = TimeSpan.MinValue;
            Wait(delegate {
                if (LastRenderingTime != last) { stable = IsAtRest() ? stable + 1 : 0; last = LastRenderingTime; }
                return stable >= 2;
            }, context);
            var idle = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate { idle.Continue = false; }));
            Dispatcher.PushFrame(idle); ThrowObservation();
            Check(IsAtRest(), context + ": driver restarted after natural completion");
            CheckRest(context);
        }
        private static void Wait(Func<bool> predicate, string context)
        {
            var frame = new DispatcherFrame(); bool satisfied = false, expired = false;
            var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(WaitDeadlineMilliseconds) };
            EventHandler rendering = delegate {
                try { CheckDeadline(); if (ObservationFailure != null || predicate()) { satisfied = true; frame.Continue = false; } }
                catch (Exception error) { ObservationFailure = error; frame.Continue = false; }
            };
            timeout.Tick += delegate { expired = true; frame.Continue = false; };
            CompositionTarget.Rendering += rendering; timeout.Start(); StopTimers();
            try { Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); CompositionTarget.Rendering -= rendering; StopTimers(); }
            ThrowObservation();
            Check(satisfied && !expired, context + ": required actual Rendering state not reached within 1500ms; " + State());
        }

        private static void ObserveRendering(object sender, EventArgs args)
        {
            if (Closed) return;
            try
            {
                Frames++; var rendering = (RenderingEventArgs)args;
                if (rendering.RenderingTime != LastRenderingTime) { UniqueFrames++; LastRenderingTime = rendering.RenderingTime; }
                // Native ApplyBounds may synchronously reenter Rendering while
                // a production API is committing several related properties.
                // Keep that measurement, but assert the completed request only
                // after the Input callback has returned, as normal workers do.
                if (ActionExecuting) ReentrantRenderingSamples++; else VerifyLogical();
                VerifyPalette(); VerifyRaw(); CheckDeadline();
                if (!ActionExecuting && ModeActive())
                {
                    double p = Read<double>(Window, "transitionLastProgress"); if (p > 0 && p < 1) ModeMiddleFrames++;
                    int version = Read<int>(Window, "transitionVersion"); Rect native = NativeBounds(); Rect previous;
                    if (ModeNative.TryGetValue(version, out previous)) Same(native, previous, .01, "native host moved within one outline version");
                    else ModeNative.Add(version, native);
                }
                double pageOpacity = CurrentPage().Opacity, toolOpacity = Panels()[ExpectedTool].Opacity;
                if (!ActionExecuting && Read<bool>(Window, "pageTransitionActive") && pageOpacity > .351 && pageOpacity < .999) PageMiddleFrames++;
                if (!ActionExecuting && Read<bool>(Window, "toolTransitionActive") && toolOpacity > .351 && toolOpacity < .999) ToolMiddleFrames++;
                if (!ActionExecuting && Picker().IsMotionActive)
                {
                    double start = Read<double>(Picker(), "fromHeight"), target = Picker().TargetHeight, height = Picker().CurrentHeight;
                    if (height > Math.Min(start, target) + .5 && height < Math.Max(start, target) - .5) PickerMiddleFrames++;
                }
                double liveAlpha = Read<Border>(Window, "shell").Opacity;
                var outgoing = Read<Image>(Window, "outgoingImage");
                if (!ActionExecuting) Check(liveAlpha + (outgoing.Source == null ? 0 : outgoing.Opacity) > .001,
                    "neither live content nor an outgoing snapshot contributes a visible frame after the request committed");
                Event("Rendering", null);
                if (!IdleQueued)
                {
                    IdleQueued = true; int scheduledFrame = UniqueFrames, scheduledEpoch = AppearanceEpoch;
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate {
                        IdleQueued = false; if (Closed) return;
                        try
                        {
                            IdleFrames++; if (!ActionExecuting) VerifyLogical(); VerifyPalette(); VerifyRaw();
                            if (!ActionExecuting && scheduledEpoch == AppearanceEpoch && UniqueFrames - EpochStartFrame >= 2 && !ModeActive() && ExpectedExpanded && Window.IsArrangeValid)
                                VerifyChrome();
                            Event("ContextIdle", "scheduled-frame=" + scheduledFrame);
                        }
                        catch (Exception error) { if (ObservationFailure == null) ObservationFailure = error; }
                    }));
                }
            }
            catch (Exception error) { if (ObservationFailure == null) ObservationFailure = error; }
        }
        private static void VerifyLogical()
        {
            var settings = Read<Settings>(Window, "settings");
            Check(settings.Language == ExpectedLanguage && UiLocalization.Language == ExpectedLanguage && settings.Theme == ExpectedTheme && UiTheme.Name == ExpectedTheme,
                "latest language/theme lost");
            Check(Read<bool>(Window, "expanded") == ExpectedExpanded && !Read<bool>(Window, "peeking"), "latest mode lost");
            Check(Read<string>(Window, "selectedTool") == ExpectedTool, "latest tool identity lost");
            Check(Read<bool>(Window, "toolsPageSelected") == (ExpectedPage == "tools") && Read<bool>(Window, "notificationPageSelected") == (ExpectedPage == "messages"), "latest page identity lost");
            foreach (var panel in Panels())
                Check(panel.Value.Visibility == (panel.Key == ExpectedTool ? Visibility.Visible : Visibility.Collapsed), "old tool panel remains visible: " + panel.Key);
            Check(!DependencyPropertyHelper.GetValueSource(Window, FrameworkElement.WidthProperty).IsAnimated && !DependencyPropertyHelper.GetValueSource(Window, FrameworkElement.HeightProperty).IsAnimated,
                "native window dimensions acquired WPF animation clocks");
        }
        private static bool IsAtRest() { return !ModeActive() && !Read<bool>(Window, "pageTransitionActive") && !Read<bool>(Window, "toolTransitionActive") && !Picker().IsMotionActive; }
        private static void CheckRest(string context)
        {
            VerifyLogical(); VerifyPalette(); VerifyRaw();
            Check(IsAtRest(), context + ": old driver remains active");
            Check(Read<object>(Window, "transitionWatch") == null && Read<object>(Window, "transitionClip") == null && Read<object>(Window, "pendingTransitionSnapshot") == null && Read<Image>(Window, "outgoingImage").Source == null,
                context + ": previous snapshot/clip/clock retained");
            Check(Read<object>(Window, "pageTransitionWatch") == null && Read<object>(Window, "toolTransitionWatch") == null, context + ": content clock retained");
            Check(Read<Border>(Window, "shell").Opacity == 1 && Read<Border>(Window, "shell").Clip == null, context + ": shell remains clipped or faded");
            Check(Read<ScrollViewer>(Window, "detailsScroll").Visibility == (ExpectedExpanded ? Visibility.Visible : Visibility.Collapsed), context + ": final content visibility wrong");
            foreach (var panel in Panels()) Check(panel.Value.Opacity == 1, context + ": retired tool alpha retained: " + panel.Key);
            foreach (string name in new[] { "notificationPage", "statusPage", "toolsPage" }) Check(Read<UIElement>(Window, name).Opacity == 1, context + ": retired page alpha retained: " + name);
            Same(Window.CurrentVisualBounds(), new Rect(Window.Left, Window.Top, Window.Width, Window.Height), 1.1, context + ": final visual bounds disagree");
        }

        private sealed class ChromePose
        {
            internal Rect Header, Tabs, Footer;
            internal Dictionary<string, Point> Glyphs;
        }
        private static void VerifyChrome()
        {
            var header = Read<FrameworkElement>(Window, "expandedHeader"); var tabs = Read<FrameworkElement>(Window, "expandedTabs"); var footer = Read<FrameworkElement>(Window, "expandedHint");
            if (!header.IsVisible || !footer.IsVisible || !header.IsArrangeValid || !footer.IsArrangeValid) return;
            var settings = Read<Settings>(Window, "settings"); var source = (HwndSource)PresentationSource.FromVisual(Window);
            string key = ExpectedLanguage + "/" + ExpectedTheme + "/" + settings.Side + "/" + settings.ExpandedWidth + "/" + source.CompositionTarget.TransformToDevice.ToString(CultureInfo.InvariantCulture);
            var pose = new ChromePose { Header = Bounds(header), Tabs = Bounds(tabs), Footer = Bounds(footer), Glyphs = new Dictionary<string, Point>() };
            CollectGlyphs(header, "header", pose.Glyphs); CollectGlyphs(tabs, "tabs", pose.Glyphs); CollectGlyphs(footer, "footer", pose.Glyphs);
            Check(pose.Glyphs.Count > 0, "fixed chrome drawing has no glyph runs");
            ChromePose previous;
            if (!Chrome.TryGetValue(key, out previous)) Chrome.Add(key, pose);
            else
            {
                Same(pose.Header, previous.Header, .02, "fixed header box changed within same layout cell");
                Same(pose.Tabs, previous.Tabs, .02, "fixed page tab box changed within same layout cell");
                Same(pose.Footer, previous.Footer, .02, "fixed footer box changed within same layout cell");
                Check(pose.Glyphs.Count == previous.Glyphs.Count && pose.Glyphs.Keys.All(previous.Glyphs.ContainsKey), "same-language/font chrome glyph identity changed");
                foreach (var glyph in pose.Glyphs)
                {
                    Point expected = previous.Glyphs[glyph.Key];
                    Check(Math.Abs(glyph.Value.X - expected.X) <= .02 && Math.Abs(glyph.Value.Y - expected.Y) <= .02,
                        "fixed same-language/font glyph baseline moved: " + glyph.Key + " " + expected + " -> " + glyph.Value);
                }
            }
            ChromeSamples++;
        }
        private static void CollectGlyphs(Visual owner, string path, Dictionary<string, Point> glyphs)
        {
            var text = owner as TextBlock;
            WalkGlyphs(VisualTreeHelper.GetDrawing(owner), Matrix.Identity, owner, path + "/" + (text == null ? "" : text.Text), glyphs);
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(owner); index++) CollectGlyphs((Visual)VisualTreeHelper.GetChild(owner, index), path + "/v" + index, glyphs);
        }
        private static void WalkGlyphs(Drawing drawing, Matrix parent, Visual owner, string path, Dictionary<string, Point> glyphs)
        {
            var group = drawing as DrawingGroup;
            if (group != null)
            {
                Matrix next = group.Transform == null ? Matrix.Identity : group.Transform.Value; next.Append(parent);
                for (int index = 0; index < group.Children.Count; index++) WalkGlyphs(group.Children[index], next, owner, path + "/d" + index, glyphs);
                return;
            }
            var runDrawing = drawing as GlyphRunDrawing; if (runDrawing == null || runDrawing.GlyphRun == null) return;
            GlyphRun run = runDrawing.GlyphRun;
            Point baseline = owner.TransformToAncestor(Window).Transform(parent.Transform(run.BaselineOrigin));
            string identity = path + "/" + run.GlyphTypeface.FontUri + "/" + run.FontRenderingEmSize.ToString("R", CultureInfo.InvariantCulture) + "/" + String.Join(",", run.GlyphIndices.Select(value => value.ToString(CultureInfo.InvariantCulture)));
            glyphs.Add(identity, baseline);
        }
        private static Rect Bounds(FrameworkElement element) { return element.TransformToAncestor(Window).TransformBounds(new Rect(element.RenderSize)); }
        private static void RememberPalette() { foreach (string role in Roles) { var brush = (SolidColorBrush)Display.Brush(role); if (!Retired.ContainsKey(brush)) Retired.Add(brush, brush.Color); } VerifyPalette(); }
        private static void VerifyPalette() { foreach (var pair in Retired) Check(pair.Key.IsFrozen && pair.Key.Color == pair.Value && !BindingOperations.IsDataBound(pair.Key, SolidColorBrush.ColorProperty), "retired semantic brush mutated"); }
        private static void VerifyRaw() { if (Input != null) Check(Input.Text == RawInput && Output.Text == RawOutput && Input.SelectionStart == 2 && Input.SelectionLength == 5, "mixed switches changed raw editor text or selection"); }
        private static void ThrowObservation() { if (ObservationFailure != null) throw new InvalidOperationException("real Rendering/ContextIdle invariant failed", ObservationFailure); }
        private static void CheckDeadline() { Check(Total.ElapsedMilliseconds < GlobalDeadlineMilliseconds, "strict global 60-second deadline exceeded; partial plans cannot pass"); }
        private static bool ModeActive() { return Read<bool>(Window, "visualTransitionActive"); }
        private static UIElement CurrentPage() { return Read<UIElement>(Window, ExpectedPage == "tools" ? "toolsPage" : ExpectedPage == "messages" ? "notificationPage" : "statusPage"); }
        private static SmoothToolPicker Picker() { return Read<SmoothToolPicker>(Window, "toolPicker"); }
        private static Dictionary<string, UIElement> Panels() { return Read<Dictionary<string, UIElement>>(Window, "toolPanels"); }
        private static Rect NativeBounds() { NativeRect value; Check(GetWindowRect(new WindowInteropHelper(Window).Handle, out value), "own native bounds unavailable"); return new Rect(value.Left, value.Top, value.Right - value.Left, value.Bottom - value.Top); }
        private static void Same(Rect actual, Rect expected, double tolerance, string message) { Check(Math.Abs(actual.X - expected.X) <= tolerance && Math.Abs(actual.Y - expected.Y) <= tolerance && Math.Abs(actual.Width - expected.Width) <= tolerance && Math.Abs(actual.Height - expected.Height) <= tolerance, message + "; actual=" + actual + "; expected=" + expected); }
        private static bool Near(Rect actual, Rect expected, double tolerance) { return Math.Abs(actual.X - expected.X) <= tolerance && Math.Abs(actual.Y - expected.Y) <= tolerance && Math.Abs(actual.Width - expected.Width) <= tolerance && Math.Abs(actual.Height - expected.Height) <= tolerance; }
        private static double PhysicalComponentDistance(Rect actual, Rect expected, Matrix scale)
        {
            return Math.Max(Math.Max(Math.Abs(actual.X - expected.X) * scale.M11, Math.Abs(actual.Y - expected.Y) * scale.M22),
                Math.Max(Math.Abs(actual.Width - expected.Width) * scale.M11, Math.Abs(actual.Height - expected.Height) * scale.M22));
        }
        private static void StopTimers()
        {
            if (Window == null) return;
            var targets = new List<object> { Window }; targets.AddRange(Panels().Values.Cast<object>());
            foreach (var target in targets) foreach (var field in target.GetType().GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) { var timer = field.GetValue(target) as DispatcherTimer; if (timer != null) timer.Stop(); }
        }
        private static T Read<T>(object target, string name) { var field = target.GetType().GetField(name, Private); Check(field != null, "missing field " + name); return (T)field.GetValue(target); }
        private static object Call(object target, string name, params object[] args) { try { return target.GetType().GetMethod(name, Private).Invoke(target, args); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static string State() { return "page=" + ExpectedPage + " tool=" + ExpectedTool + " language=" + ExpectedLanguage + " theme=" + ExpectedTheme + " expanded=" + ExpectedExpanded + " mode-active=" + ModeActive() + " page-active=" + Read<bool>(Window, "pageTransitionActive") + " tool-active=" + Read<bool>(Window, "toolTransitionActive") + " picker-active=" + Picker().IsMotionActive; }
        private static void Event(string kind, string action)
        {
            if (Events == null || Window == null) return;
            Events.WriteLine(Json.Serialize(new { Kind = kind, Action = action, Round = Round, ActionIndex = Actions, Frame = Frames, UniqueRenderingTime = UniqueFrames,
                RenderingTimeMilliseconds = LastRenderingTime.TotalMilliseconds, ElapsedMilliseconds = Total.ElapsedMilliseconds, AppearanceEpoch = AppearanceEpoch, ActionExecuting = ActionExecuting, State = State(),
                NativeBounds = NativeBounds().ToString(CultureInfo.InvariantCulture), VisualBounds = Window.CurrentVisualBounds().ToString(CultureInfo.InvariantCulture),
                ShellOpacity = Read<Border>(Window, "shell").Opacity, PageOpacity = CurrentPage().Opacity, ToolOpacity = Panels()[ExpectedTool].Opacity,
                PickerHeight = Picker().CurrentHeight, OutgoingImage = Read<Image>(Window, "outgoingImage").Source != null, OutgoingOpacity = Read<Image>(Window, "outgoingImage").Opacity,
                ModeProgress = Read<double>(Window, "transitionLastProgress"), ModeTarget = Read<Rect>(Window, "transitionTargetBounds").ToString(CultureInfo.InvariantCulture) }));
        }
        private static void Save(bool passed, string failure)
        {
            var result = new { Passed = passed, Failure = failure, RoundCountExpected = Rounds, CompletedRounds = CompletedRounds, ActionsExpected = Rounds * ActionsPerRound, Actions = Actions,
                PageActions = Pages, ToolActions = Tools, AppearanceActions = Appearances, ModeRequests = Modes, Reversals = Reversals, PickerRequests = PickerRequests, SetupActions = SetupActions,
                RenderingEvents = Frames, UniqueRenderingTimes = UniqueFrames, ContextIdleSamples = IdleFrames, ReentrantRenderingSamples = ReentrantRenderingSamples, ModeIntermediateSamples = ModeMiddleFrames, PageIntermediateSamples = PageMiddleFrames,
                ToolIntermediateSamples = ToolMiddleFrames, PickerIntermediateSamples = PickerMiddleFrames, FixedChromeGlyphSamples = ChromeSamples, ChromeCells = Chrome.Count, FrozenRetainedBrushes = Retired.Count,
                AppearanceInstantBoundsMeasurements = AppearanceMeasurements, InMotionAppearanceMeasurements = InMotionAppearanceMeasurements, MaximumAppearanceInstantBoundsDeltaDip = MaximumAppearanceInstantBoundsDelta,
                RequiredInMotionAppearanceMeasurements = 48, AppearanceContract = "continuous outline/watch/version for per-round actions16and18; instantaneous bounds delta<=.02DIP",
                MeaningfulMiddleWaits = MeaningfulMiddleWaits, MeaningfulMiddleSamples = MeaningfulMiddleSamples, ExactNearTargetCommitMeasurements = NearTargetCommits,
                ElapsedMilliseconds = Total.ElapsedMilliseconds, StrictDeadlineMilliseconds = GlobalDeadlineMilliseconds, AutomaticRetry = false, RenderPolicyChanged = false, GlobalInputUsed = false, RTBUsed = false,
                ObservationFailure = ObservationFailure == null ? null : ObservationFailure.ToString(), FramesFile = "frames-and-actions.jsonl", CompletedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            File.WriteAllText(Path.Combine(Evidence, "result.json"), Json.Serialize(result), new UTF8Encoding(false));
        }
        private static void VerifyBuild()
        {
            string target = Assembly.GetExecutingAssembly().Location, manifestPath = Path.Combine(Path.GetDirectoryName(target), "mixed-transition-stress-build.json");
            var manifest = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(manifestPath));
            Check(Hash(target) == (string)manifest["TargetSHA256"], "diagnostic executable differs from compile manifest");
            foreach (var raw in (object[])manifest["Sources"]) { var item = (Dictionary<string, object>)raw; Check(Hash((string)item["Path"]) == (string)item["SHA256"], "source changed since compilation: " + item["Path"]); }
            Log("BUILD executable-sha256=" + Hash(target) + " manifest-sha256=" + Hash(manifestPath) + " source-hashes-match=true");
        }
        private static string Hash(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
        private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static void Log(string value) { Console.WriteLine(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + value); Console.Out.Flush(); }
    }
}
