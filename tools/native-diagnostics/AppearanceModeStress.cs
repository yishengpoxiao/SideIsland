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
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // Independent diagnostic entry, not a registered regression-suite replacement.
    // Real production demo HWND/buttons/Rendering events; no global input, sleeps,
    // render-policy changes, retries, accounts, media commands or user-data saves.
    internal static class AppearanceModeStress
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const int DeadlineMilliseconds = 1500;
        private const string RawInput = "保存设置\n刷新 raw_key=unchanged\n原文 Original 日本語 😀";
        private const string RawOutput = "OUTPUT 保持原文 0xC0000409 & <unchanged>";
        private static readonly string[] Edges = { "left", "right", "top", "bottom" };
        private static readonly string[] Languages = { "zh", "en" }, Themes = { "light", "dark" };
        private static readonly string[] Roles = { "#111518", "#1A2323", "#E3EFE7", "#A1EACF", "#30383B", "#3A5145" };
        private static readonly Dictionary<SolidColorBrush, Color> Retired = new Dictionary<SolidColorBrush, Color>();
        private static TextBox input, output;
        private static int scenarios, appearances, languageActions, themeActions, modeRequests, reversals, renderedFrames, middleWindows, stableNativeSamples;

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                VerifyBuild();
                if (args.Length == 1 && args[0] == "--self-check")
                {
                    Check(Edges.Length * Languages.Length * Themes.Length == 16, "fixed matrix must contain 16 scenarios");
                    Check(Edges.Length * Languages.Length * Themes.Length * 4 == 64, "fixed matrix must contain 64 in-motion appearance actions");
                    foreach (string method in new[] { "ApplyAppearance", "ToggleLanguage", "ToggleTheme", "ChangeSide", "SelectTool", "SelectToolsPage" })
                        Check(typeof(IslandWindow).GetMethod(method, Private) != null, "production route missing: " + method);
                    Log("SELF-CHECK PASS scenarios=16 in-motion-appearance-actions=64 UI-created=false UI-executed=false");
                    return 0;
                }
                if (!(args.Length == 0 || (args.Length == 2 && args[0] == "--suite" && args[1] == "AppearanceModeStress")))
                    throw new ArgumentException("Use no arguments, --suite AppearanceModeStress, or --self-check (non-UI only).");
                Run();
                Log("SUITE PASS:AppearanceModeStress");
                return 0;
            }
            catch (Exception error) { Log("FAIL: " + error); return 1; }
        }

        private static void Run()
        {
            string previousDirectory = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string previousLanguage = UiLocalization.Language, previousTheme = UiTheme.Name;
            var previousContext = SynchronizationContext.Current;
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string directory = Path.GetFullPath(Path.Combine(temporaryRoot, "CodexIsland.AppearanceModeStress." + Guid.NewGuid().ToString("N")));
            Check(directory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase), "fixture storage must stay under the temporary root");
            IslandWindow window = null;
            try
            {
                Check(SystemParameters.ClientAreaAnimation, "real animation is disabled; diagnostic requires the current desktop's enabled animation setting");
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", directory);
                var store = new SettingsStore();
                store.Save(new Settings { Language = "zh", Theme = "dark", AlwaysOnTop = false, AutoCollapse = false,
                    HoverExpandEnabled = false, NotificationsEnabled = false, AutoMusic = false, OnlineLyricsEnabled = false,
                    CustomExpandedSize = true, ExpandedWidth = 397, ExpandedHeight = 611, HiddenTools = "", FavoriteTools = "" });
                string fixtureSettingsHash = Hash(store.FilePath);
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false, Topmost = false };
                window.Loaded += delegate { StopTimers(window); };
                window.Show(); window.UpdateLayout(); StopTimers(window);
                Check(Read<bool>(window, "demo"), "only a demo window may receive header actions");
                Call(window, "SelectToolsPage"); Call(window, "SelectTool", "text");
                var panels = Read<Dictionary<string, UIElement>>(window, "toolPanels");
                input = Read<TextBox>(panels["text"], "input"); output = Read<TextBox>(panels["text"], "output");
                input.Text = RawInput; output.Text = RawOutput; input.Select(2, 5);
                Log("CONFIG scenarios=16 expected-appearance-actions=64 deadline-ms=" + DeadlineMilliseconds +
                    " rendering-policy=" + RenderOptions.ProcessRenderMode + " tier=" + (RenderCapability.Tier >> 16) +
                    " no-sleep=true no-retry=true demo=true raw-input-sha256=" + HashText(RawInput));

                foreach (string edge in Edges) foreach (string language in Languages) foreach (string theme in Themes)
                {
                    string context = edge + "/" + language + "/" + theme;
                    Log("SCENARIO BEGIN " + context + " appearances=" + appearances + " reversals=" + reversals + " " + State(window));
                    var settings = Read<Settings>(window, "settings");
                    // Setup runs outside an animation. Every measured final mode
                    // below is reached naturally, with no forced final SetExpanded.
                    window.SetExpanded(false, false); modeRequests++;
                    settings.Language = language; settings.Theme = theme; Call(window, "ApplyAppearance"); Call(window, "ChangeSide", edge);
                    window.UpdateLayout(); StopTimers(window); RememberPalette(); CheckRest(window, false, context + "/setup");
                    string expectedLanguage = language, expectedTheme = theme;
                    for (int action = 0; action < 4; action++)
                    {
                        bool expanded = action % 2 == 0;
                        string step = context + "/action=" + action;
                        Log("ACTION BEGIN " + step + " route=" + (action % 2 == 0 ? "languageButton.Click" : "themeButton.Click") + " requested-expanded=" + expanded + " " + State(window));
                        window.SetExpanded(expanded, true); modeRequests++;
                        WaitForMiddleFrames(window, step + "/before-appearance");
                        Check(Active(window) && Read<double>(window, "transitionLastProgress") > 0 && Read<double>(window, "transitionLastProgress") < 1,
                            step + ": appearance route must run during an actual intermediate animation");
                        int interruptedVersion = Read<int>(window, "transitionVersion");
                        double interruptedProgress = Read<double>(window, "transitionLastProgress");
                        Rect requestedBounds = Read<Rect>(window, "transitionTargetBounds");
                        RememberPalette();
                        if (action % 2 == 0)
                        {
                            expectedLanguage = expectedLanguage == "en" ? "zh" : "en";
                            Read<Button>(window, "languageButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); languageActions++;
                        }
                        else
                        {
                            expectedTheme = expectedTheme == "light" ? "dark" : "light";
                            Read<Button>(window, "themeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); themeActions++;
                        }
                        appearances++;
                        WaitForRest(window, expanded, step + "/appearance-completion");
                        Same(window.CurrentVisualBounds(), requestedBounds, 1.1, step + ": appearance commits its actual requested mode target");
                        CheckAppearance(window, expectedLanguage, expectedTheme, step);
                        Log("ACTION END " + step + " route=" + (action % 2 == 0 ? "languageButton.Click" : "themeButton.Click") +
                            " interrupted-version=" + interruptedVersion + " interrupted-progress=" + interruptedProgress.ToString("F4", CultureInfo.InvariantCulture) +
                            " " + State(window));

                        // Reverse an actual moving outline, then let the latest
                        // request finish. Do not force a final nonanimated mode.
                        window.SetExpanded(!expanded, true); modeRequests++;
                        WaitForMiddleFrames(window, step + "/before-reversal");
                        Rect before = window.CurrentVisualBounds();
                        window.SetExpanded(expanded, true); modeRequests++; reversals++;
                        Same(window.CurrentVisualBounds(), before, 1.1, step + ": reversal starts at the last rendered outline");
                        WaitForRest(window, expanded, step + "/natural-reversal-completion");
                        CheckAppearance(window, expectedLanguage, expectedTheme, step + "/after-reversal");
                    }
                    Check(expectedLanguage == language && expectedTheme == theme, context + ": two toggles of each kind restore the matrix cell");
                    Check(Hash(store.FilePath) == fixtureSettingsHash, context + ": demo header routes must not save even the isolated settings file");
                    scenarios++; Log("SCENARIO END " + context + " appearances=" + appearances + " reversals=" + reversals + " " + State(window));
                }
                Check(scenarios == 16 && appearances == 64 && languageActions == 32 && themeActions == 32 && reversals == 64 && middleWindows == 128 && modeRequests == 208,
                    "fixed diagnostic plan did not execute exactly once in full");
                CheckRest(window, false, "final natural compact mode");
                Log("PASS: 16 four-edge/language/theme scenarios; 64 actual in-motion header appearance actions (32 language,32 theme); 64 rendered reversals; " +
                    modeRequests + " explicit mode requests; " + renderedFrames + " real rendering frames; " + stableNativeSamples + " stable in-motion native geometry samples; " + Retired.Count +
                    " immutable retained brush snapshots; raw text/selection, latest state, cleanup and native geometry verified; no retry.");
                window.Close(); Check(!Active(window), "close clears animation state"); window = null;
            }
            finally
            {
                if (window != null) window.Close();
                input = output = null;
                AppAppearance.Configure(previousLanguage, previousTheme);
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previousDirectory);
                SynchronizationContext.SetSynchronizationContext(previousContext);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static void WaitForMiddleFrames(IslandWindow window, string context)
        {
            int version = Read<int>(window, "transitionVersion"), samples = 0;
            Rect initial = window.CurrentVisualBounds(), native = Rect.Empty;
            TimeSpan previousTime = TimeSpan.MinValue;
            WaitRendering(window, delegate(RenderingEventArgs frame) {
                if (!Active(window) || Read<int>(window, "transitionVersion") != version) return false;
                double progress = Read<double>(window, "transitionLastProgress");
                if (progress <= 0 || progress >= 1 || Near(window.CurrentVisualBounds(), initial, .01) || frame.RenderingTime == previousTime) return false;
                Rect current = NativeBounds(window);
                if (samples == 0) native = current; else Same(current, native, .01, context + ": native HWND changes during intermediate frames");
                previousTime = frame.RenderingTime; samples++;
                return samples >= 2;
            }, context);
            middleWindows++;
        }

        private static void WaitForRest(IslandWindow window, bool expanded, string context)
        {
            WaitRendering(window, delegate(RenderingEventArgs frame) { return !Active(window); }, context);
            window.UpdateLayout(); StopTimers(window); CheckRest(window, expanded, context);
        }

        private static void WaitRendering(IslandWindow window, Func<RenderingEventArgs, bool> predicate, string context)
        {
            var frame = new DispatcherFrame(); bool satisfied = false, expired = false; Exception failure = null;
            int observedVersion = -1; Rect stableNative = Rect.Empty;
            var deadline = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(DeadlineMilliseconds) };
            EventHandler rendering = delegate(object sender, EventArgs args) {
                try
                {
                    renderedFrames++; VerifyPalette(); VerifyRaw();
                    Check(!DependencyPropertyHelper.GetValueSource(window, FrameworkElement.WidthProperty).IsAnimated &&
                        !DependencyPropertyHelper.GetValueSource(window, FrameworkElement.HeightProperty).IsAnimated, context + ": native dimensions must not use WPF animations");
                    if (Active(window))
                    {
                        int version = Read<int>(window, "transitionVersion");
                        Rect current = NativeBounds(window);
                        if (version != observedVersion) { observedVersion = version; stableNative = current; }
                        else { Same(current, stableNative, .01, context + ": moving outline changed its native host geometry"); stableNativeSamples++; }
                    }
                    if (predicate((RenderingEventArgs)args)) { satisfied = true; frame.Continue = false; }
                }
                catch (Exception error) { failure = error; frame.Continue = false; }
            };
            deadline.Tick += delegate { expired = true; frame.Continue = false; };
            StopTimers(window); CompositionTarget.Rendering += rendering; deadline.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { deadline.Stop(); CompositionTarget.Rendering -= rendering; StopTimers(window); }
            if (failure != null) throw new Exception(context + ": rendering invariant failed", failure);
            Check(satisfied && !expired, context + ": no required real Rendering state within " + DeadlineMilliseconds + "ms; " + State(window));
        }

        private static void CheckRest(IslandWindow window, bool expanded, string context)
        {
            VerifyPalette(); VerifyRaw();
            Check(Read<bool>(window, "expanded") == expanded && !Read<bool>(window, "peeking"), context + ": latest logical mode lost");
            Check(!Active(window) && Read<Stopwatch>(window, "transitionWatch") == null && Read<object>(window, "transitionClip") == null,
                context + ": previous outline animation/clock/clip survives");
            Check(!Read<bool>(window, "pageTransitionActive") && Read<Image>(window, "outgoingImage").Source == null &&
                Read<object>(window, "pendingTransitionSnapshot") == null, context + ": stale page or snapshot retained");
            Check(Read<ScrollViewer>(window, "detailsScroll").Visibility == (expanded ? Visibility.Visible : Visibility.Collapsed), context + ": content visibility contradicts latest mode");
            Check(Read<string>(window, "selectedTool") == "text", context + ": tool identity changed");
            Check(Read<Border>(window, "shell").Opacity == 1 && Read<Border>(window, "shell").Clip == null, context + ": crossfade shell did not reset");
            Rect logical = new Rect(window.Left, window.Top, window.Width, window.Height);
            Same(window.CurrentVisualBounds(), logical, 1.1, context + ": final drawn and logical bounds disagree");
            var source = (HwndSource)PresentationSource.FromVisual(window);
            Rect physical = new MatrixTransform(source.CompositionTarget.TransformToDevice).TransformBounds(logical);
            Same(NativeBounds(window), physical, 2.1, context + ": final physical HWND differs from logical DPI geometry");
        }

        private static void CheckAppearance(IslandWindow window, string language, string theme, string context)
        {
            var settings = Read<Settings>(window, "settings");
            Check(settings.Language == language && settings.Theme == theme && UiLocalization.Language == language && UiTheme.Name == theme,
                context + ": settings and UI language/theme disagree");
            Check(Convert.ToString(Read<Button>(window, "languageButton").Content) == (language == "en" ? "中" : "EN") &&
                Convert.ToString(Read<Button>(window, "themeButton").Content) == (theme == "light" ? "☾" : "☼"), context + ": header glyphs contradict current appearance");
            Check(Object.ReferenceEquals(input.Foreground, Display.Brush("#E3EFE7")), context + ": real editor did not adopt current theme");
            RememberPalette(); VerifyRaw();
        }

        private static void RememberPalette()
        {
            foreach (string role in Roles) { var brush = (SolidColorBrush)Display.Brush(role); if (!Retired.ContainsKey(brush)) Retired.Add(brush, brush.Color); }
            VerifyPalette();
        }
        private static void VerifyPalette()
        {
            foreach (var entry in Retired) Check(entry.Key.IsFrozen && entry.Key.Color == entry.Value && !BindingOperations.IsDataBound(entry.Key, SolidColorBrush.ColorProperty), "retired palette changed or remains mutable/bound");
        }
        private static void VerifyRaw()
        {
            if (input == null) return;
            Check(input.Text == RawInput && output.Text == RawOutput && input.SelectionStart == 2 && input.SelectionLength == 5, "appearance/mode transition changed raw text or selection");
        }
        private static string State(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings");
            return "language=" + settings.Language + " theme=" + settings.Theme + " expanded=" + Read<bool>(window, "expanded") +
                " peeking=" + Read<bool>(window, "peeking") + " active=" + Active(window) + " version=" + Read<int>(window, "transitionVersion") +
                " visual=" + window.CurrentVisualBounds().ToString(CultureInfo.InvariantCulture) + " native=" + NativeBounds(window).ToString(CultureInfo.InvariantCulture);
        }
        private static bool Active(IslandWindow window) { return Read<bool>(window, "visualTransitionActive"); }
        private static Rect NativeBounds(IslandWindow window)
        {
            NativeRect value; Check(GetWindowRect(new WindowInteropHelper(window).Handle, out value), "own native HWND has no readable rectangle");
            return new Rect(value.Left, value.Top, value.Right - value.Left, value.Bottom - value.Top);
        }
        private static bool Near(Rect a, Rect b, double tolerance) { return Math.Abs(a.X-b.X) <= tolerance && Math.Abs(a.Y-b.Y) <= tolerance && Math.Abs(a.Width-b.Width) <= tolerance && Math.Abs(a.Height-b.Height) <= tolerance; }
        private static void Same(Rect actual, Rect expected, double tolerance, string message) { Check(Near(actual, expected, tolerance), message + "; actual=" + actual + "; expected=" + expected); }
        private static T Read<T>(object value, string name) { var field = value.GetType().GetField(name, Private); Check(field != null, "missing production field " + name); return (T)field.GetValue(value); }
        private static object Call(object value, string name, params object[] arguments) { try { return value.GetType().GetMethod(name, Private).Invoke(value, arguments); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static void StopTimers(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static string Hash(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
        private static string HashText(string text) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", ""); }
        private static void VerifyBuild()
        {
            string executable = Assembly.GetExecutingAssembly().Location, directory = Path.GetDirectoryName(executable);
            string manifestPath = Path.Combine(directory, "appearance-mode-stress-build.json");
            var manifest = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(manifestPath));
            string executableHash = Hash(executable);
            Check(executableHash == (string)manifest["TargetSHA256"], "executable no longer matches its compile manifest");
            var sources = (object[])manifest["Sources"];
            foreach (var source in sources) { var item = (Dictionary<string, object>)source; Check(Hash((string)item["Path"]) == (string)item["SHA256"], "source changed since compilation: " + item["Path"]); }
            Log("BUILD executable-sha256=" + executableHash + " manifest-sha256=" + Hash(manifestPath) + " compiled-source-count=" + sources.Length +
                " stress-source-sha256=" + manifest["StressSourceSHA256"] + " all-source-hashes-match=true");
        }
        private static void Log(string message) { Console.WriteLine(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + message); Console.Out.Flush(); }
    }
}
