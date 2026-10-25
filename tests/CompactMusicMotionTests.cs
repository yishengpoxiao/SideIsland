using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    // Synthetic metadata in this process's own WPF windows. No real player,
    // notification read, global input, desktop capture or online lyric request.
    internal static class CompactMusicMotionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
        private static int transitions, frames, rapidRequests, parentCases, lifecycleCases, widgetCases, dpiCases, nativeMessages, sequence;
        private static readonly List<double> Handlers = new List<double>(), Gaps = new List<double>();
        private static BitmapSource[] covers;

        internal static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), language = UiLocalization.Language, theme = UiTheme.Name;
            string folder = Path.Combine(Path.GetTempPath(), "CodexIsland.MusicMotion." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            transitions = frames = rapidRequests = parentCases = lifecycleCases = widgetCases = dpiCases = nativeMessages = sequence = 0;
            Handlers.Clear(); Gaps.Clear(); covers = Artwork();
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false, HoverExpandEnabled = false,
                    AutoMusic = false, OnlineLyricsEnabled = false, CompactMode = "music", CustomExpandedSize = true, ExpandedWidth = 391, ExpandedHeight = 680 });
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false };
                window.Loaded += (s, e) => StopTimers(window); window.Show(); Settle(window);
                Console.WriteLine("CHECK: compact music live track/lyric transitions; " + PopupDpiTests.Describe(window));
                Matrix(window); RepeatedAndRapid(window); ParentModes(window); WidgetSlots(window); MaximumFontFrames(window); Lifecycles(window); window = null;
                DpiLayouts();
                var gaps = Gaps.OrderBy(value => value).ToArray();
                Console.WriteLine("TIMING: compact music update handler mean/max " + Handlers.DefaultIfEmpty(0).Average().ToString("0.0") + "/" + Handlers.DefaultIfEmpty(0).Max().ToString("0.0") + " ms.");
                Console.WriteLine("TIMING: compact music render gap p95/max " + (gaps.Length == 0 ? 0 : gaps[(int)Math.Ceiling(gaps.Length * .95) - 1]).ToString("0.0") + "/" + gaps.DefaultIfEmpty(0).Max().ToString("0.0") + " ms; setup/rapid dispatch excluded.");
                Console.WriteLine("PASS: " + transitions + " real compact music transitions, " + rapidRequests + " mixed rapid requests, " + parentCases + " parent-mode interruptions, " + lifecycleCases + " lifecycle paths, " + widgetCases + " media-slot cases, " + frames + " rendered geometry frames and " + dpiCases + " WPF DPI layouts; latest cover/title/lyric and tooltips, independent lyric clocks, fixed controls/geometry, bounded source references and interruption cleanup; native move/size messages=" + nativeMessages + ".");
            }
            catch (Exception error) { Console.Error.WriteLine("CHECK FAILURE: " + error.GetType().Name + ": " + error.Message); throw; }
            finally
            {
                if (window != null && !Read<bool>(window, "shuttingDown")) { StopTimers(window); window.Close(); }
                covers = null; AppAppearance.Configure(language, theme); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        private static void Matrix(IslandWindow window)
        {
            foreach (string edge in new[] { "left", "right", "top", "bottom" })
            foreach (string language in new[] { "zh", "en" }) foreach (string theme in new[] { "light", "dark" })
            foreach (string direction in new[] { "auto", "rotate180", "mirror" })
            {
                Configure(window, edge, language, theme, direction);
                var first = Track(); Publish(window, first, Line(first, 0)); Wait(window);
                var next = Track(); Observe(window, next, Line(next, 0), true, true, edge + "/" + language + "/" + theme + "/" + direction + "/track");
                Observe(window, next, Line(next, 1), false, true, edge + "/lyric");
            }
        }

        private static void Observe(IslandWindow window, MediaSnapshot track, string lyric, bool contentMoves, bool lyricMoves, string context)
        {
            var visual = Read<CompactMusicVisual>(window, "compactMusic"); var target = Read<Grid>(window, "compactMusicTarget");
            Rect native = NativeBounds(window), surface = window.CurrentVisualBounds(), physical = ScreenBounds(target);
            Rect cover = visual.CoverBounds, previous = visual.PreviousBounds, next = visual.NextBounds;
            Size footprint = visual.RenderSize; int messages = 0; double last = -1;
            var contentAlpha = new List<double>(); var lyricAlpha = new List<double>();
            var source = (HwndSource)PresentationSource.FromVisual(window);
            HwndSourceHook hook = (IntPtr hwnd, int code, IntPtr w, IntPtr l, ref bool handled) => { if (code == 3 || code == 5) messages++; return IntPtr.Zero; };
            EventHandler render = (s, e) => {
                Same(NativeBounds(window), native, .01, context + ": native bounds moved"); Same(window.CurrentVisualBounds(), surface, .01, context + ": outline moved");
                Same(ScreenBounds(target), physical, .01, context + ": card screen bounds moved");
                Check(visual.RenderSize == footprint && visual.CoverBounds == cover && visual.PreviousBounds == previous && visual.NextBounds == next, context + ": control layout changed");
                Check(Read<Border>(window, "shell").Opacity == 1 && target.Opacity == 1 && visual.Opacity == 1, context + ": content dims its shell or controls");
                VerifyLatest(window, track, lyric, context); VerifyRange(visual, context); frames++;
                contentAlpha.Add(visual.ContentTransitionOpacity); lyricAlpha.Add(visual.LyricTransitionOpacity);
                double now = ((RenderingEventArgs)e).RenderingTime.TotalMilliseconds; if (last >= 0 && now > last) Gaps.Add(now - last); last = now;
            };
            source.AddHook(hook);
            try
            {
                var timer = Stopwatch.StartNew(); Publish(window, track, lyric); Handlers.Add(timer.Elapsed.TotalMilliseconds);
                window.UpdateLayout(); VerifyLatest(window, track, lyric, context + "/immediate");
                if (SystemParameters.ClientAreaAnimation)
                {
                    Check(visual.ContentTransitionActive == contentMoves, context + ": wrong track-content channel");
                    Check(visual.LyricTransitionActive == lyricMoves, context + ": wrong lyric channel");
                }
                CompositionTarget.Rendering += render; try { Wait(window); } finally { CompositionTarget.Rendering -= render; }
            }
            finally { source.RemoveHook(hook); }
            Check(messages == 0, context + ": content updates send native move/size messages"); nativeMessages += messages;
            if (SystemParameters.ClientAreaAnimation)
            {
                if (contentMoves) Check(contentAlpha.Any(value => value > .351 && value < .999), context + ": track content has no intermediate frame");
                if (lyricMoves) Check(lyricAlpha.Any(value => value > .351 && value < .999), context + ": lyric has no intermediate frame");
            }
            Monotonic(contentAlpha, context + "/content"); Monotonic(lyricAlpha, context + "/lyric");
            CheckRest(visual, context); CheckCurrentDrawing(visual, track.Artwork, context); transitions++;
        }

        private static void RepeatedAndRapid(IslandWindow window)
        {
            Configure(window, "right", "en", "dark", "auto"); var track = Track(); string lyric = Line(track, 0); Publish(window, track, lyric); Wait(window);
            var visual = Read<CompactMusicVisual>(window, "compactMusic");
            var late = Track(); late.Artwork = null; Publish(window, late, Line(late, 0)); Wait(window);
            int lyricVersion = visual.LyricTransitionVersion; late.Artwork = covers[2]; Publish(window, late, Line(late, 0));
            if (SystemParameters.ClientAreaAnimation) Check(visual.ContentTransitionActive && !visual.LyricTransitionActive && visual.LyricTransitionVersion == lyricVersion, "late artwork unnecessarily replays the lyric channel");
            Wait(window); CheckCurrentDrawing(visual, late.Artwork, "late artwork");
            track = Track(); lyric = Line(track, 0); Publish(window, track, lyric); Pump(25); StopTimers(window);
            int contentVersion = visual.ContentTransitionVersion; lyricVersion = visual.LyricTransitionVersion;
            object contentWatch = Property(visual, "ContentTransitionWatch"), lyricWatch = Property(visual, "LyricTransitionWatch");
            double alpha = visual.ContentTransitionOpacity, lineAlpha = visual.LyricTransitionOpacity;
            track.Position += TimeSpan.FromMilliseconds(100); track.Playing = false; track.CanNext = false;
            Publish(window, track, lyric);
            Check(visual.ContentTransitionVersion == contentVersion && visual.LyricTransitionVersion == lyricVersion && ReferenceEquals(contentWatch, Property(visual, "ContentTransitionWatch")) && ReferenceEquals(lyricWatch, Property(visual, "LyricTransitionWatch")), "same track progress/play state/capabilities restart content clocks");
            Check(Math.Abs(alpha - visual.ContentTransitionOpacity) < .001 && Math.Abs(lineAlpha - visual.LyricTransitionOpacity) < .001, "same track refresh flashes current alpha");
            Check(visual.ActionAt(Center(visual.NextBounds)) == null && visual.ActionAt(Center(visual.CoverBounds)) == "music-toggle", "transport capability update lags the content transition");
            Wait(window);
            for (int index = 0; index < 80; index++)
            {
                if (index % 10 == 0) window.SetExpanded(true, true);
                else if (index % 10 == 1) window.SetExpanded(false, true);
                else if (index % 10 == 2) Call(window, "ShowPeek", new IslandNotification { Id = "music-peek-" + index, AppId = "fixture.music", AppName = "Synthetic preview", Title = "Synthetic message", Body = "Synthetic body", CreatedAt = DateTime.UtcNow });
                else if (index % 10 == 3) Call(window, "SelectNotificationFilter", (NotificationFilter)(index % 4));
                else if (index % 10 == 4) Call(window, "ClearNotificationsWithMotion");
                else if (index % 10 == 5) { window.SetExpanded(false, false); Settle(window); }
                track = Track(); lyric = Line(track, index % 2);
                bool sameMode = !Read<bool>(window, "visualTransitionActive") && !Read<bool>(window, "expanded") && !Read<bool>(window, "peeking");
                bool active = visual.ContentTransitionActive; alpha = visual.ContentTransitionOpacity;
                Publish(window, track, lyric); VerifyLatest(window, track, lyric, "rapid/" + index);
                if (sameMode && active && SystemParameters.ClientAreaAnimation) Check(visual.ContentTransitionOpacity + .001 >= alpha, "rapid track replacement deepens the incoming fade");
                Pump(3); StopTimers(window); rapidRequests++;
            }
            window.SetExpanded(false, false); Publish(window, track, lyric); Wait(window); VerifyLatest(window, track, lyric, "rapid/final"); CheckCurrentDrawing(visual, track.Artwork, "rapid/final");
            var noTimeline = Track(); noTimeline.SampledAtUtc = DateTime.MinValue; noTimeline.Duration = TimeSpan.Zero;
            Publish(window, noTimeline, "OLD LYRIC MUST NOT DRAW"); Wait(window);
            Check(visual.Lyric.Length == 0 && !Read<bool>(visual, "timelineAvailable"), "missing timeline retains an old synchronized lyric or progress");
            Publish(window, new MediaSnapshot(), "OLD LYRIC MUST NOT DRAW"); Wait(window);
            Check(Read<BitmapSource>(visual, "artwork") == null && visual.Lyric.Length == 0, "unavailable session retains old artwork or lyrics");
        }

        private static void ParentModes(IslandWindow window)
        {
            foreach (string edge in new[] { "left", "right", "top", "bottom" }) foreach (bool expanding in new[] { true, false })
            {
                Configure(window, edge, "en", "light", "auto"); var first = Track(); Publish(window, first, Line(first, 0)); Wait(window);
                if (!expanding) window.SetExpanded(true, false);
                window.SetExpanded(expanding, true); Pump(30); StopTimers(window);
                var visual = Read<CompactMusicVisual>(window, "compactMusic");
                object clock = Read<object>(window, "transitionWatch"); int version = Read<int>(window, "transitionVersion");
                Rect native = NativeBounds(window), surface = window.CurrentVisualBounds(); double shellAlpha = Read<Border>(window, "shell").Opacity;
                var next = Track(); Publish(window, next, Line(next, 0));
                Check(ReferenceEquals(clock, Read<object>(window, "transitionWatch")) && version == Read<int>(window, "transitionVersion"), "track switch restarts parent mode clock on " + edge);
                Same(NativeBounds(window), native, .01, "track switch changes parent union HWND"); Same(window.CurrentVisualBounds(), surface, .01, "track switch jumps parent geometry");
                Check(Math.Abs(Read<Border>(window, "shell").Opacity - shellAlpha) <= .001, "track replacement flashes parent incoming alpha");
                CheckRest(visual, "parent mode");
                Check(Read<Image>(window, "outgoingImage").Source == null && Read<object>(window, "pendingTransitionSnapshot") == null, "track replacement keeps old media bitmap during mode change");
                Wait(window); window.SetExpanded(false, false); Publish(window, next, Line(next, 0)); Wait(window); VerifyLatest(window, next, Line(next, 0), "parent/final"); parentCases++;
            }
        }

        private static void WidgetSlots(IslandWindow window)
        {
            Configure(window, "right", "en", "dark", "auto"); var settings = Read<Settings>(window, "settings"); settings.CompactMode = "quota"; settings.CompactSlot1 = "media"; settings.CompactSlot2 = "codex-secondary";
            var first = Track(); Publish(window, first, Line(first, 0)); Wait(window);
            var media = Read<CompactWidgetVisual>(window, "compactWidget1"); var quota = Read<CompactWidgetVisual>(window, "compactWidget2");
            string quotaValue = quota.State.Value, quotaCaption = quota.State.Caption; Rect native = NativeBounds(window);
            var next = Track(); Publish(window, next, Line(next, 0));
            Check(media.State.Key == "media" && media.State.Caption == next.Title && ReferenceEquals(media.State.Artwork, next.Artwork), "manual media slot keeps old song or artwork");
            if (SystemParameters.ClientAreaAnimation) Check(Bool(media, "ContentTransitionActive"), "manual media slot does not animate track replacement");
            for (int index = 0; index < 20; index++)
            {
                next = Track(); Publish(window, next, Line(next, 0)); Pump(3); StopTimers(window); Same(NativeBounds(window), native, .01, "media+quota update moves window");
                Check(quota.State.Value == quotaValue && quota.State.Caption == quotaCaption && quota.State.Key == "codex-secondary", "media motion modifies its quota neighbor"); widgetCases++;
            }
            Wait(window); Check(!Bool(media, "ContentTransitionActive"), "manual media slot motion never settles");
            settings.CompactSlot1 = "cpu"; Call(window, "UpdateToolkitCompact"); Settle(window);
            Check(media.State.Key == "cpu" && media.State.Artwork == null && !Bool(media, "ContentTransitionActive"), "leaving manual media slot retains old media state"); widgetCases++;
            settings.CompactSlot1 = "media"; Publish(window, next, Line(next, 0)); Wait(window);
            window.SetExpanded(true, true); Pump(30); StopTimers(window);
            object clock = Read<object>(window, "transitionWatch"); int version = Read<int>(window, "transitionVersion"); double alpha = Read<Border>(window, "shell").Opacity;
            Publish(window, Track(), "Synthetic widget mode lyric");
            Check(ReferenceEquals(clock, Read<object>(window, "transitionWatch")) && version == Read<int>(window, "transitionVersion") && Math.Abs(alpha - Read<Border>(window, "shell").Opacity) <= .001, "manual media switch restarts or flashes parent mode");
            Check(!Bool(media, "ContentTransitionActive") && Read<Image>(window, "outgoingImage").Source == null, "manual media slot double-fades or retains mode bitmap"); Wait(window); widgetCases++;
        }

        private static void MaximumFontFrames(IslandWindow window)
        {
            string directory = Environment.GetEnvironmentVariable("CODEX_ISLAND_MUSIC_MOTION_ARTIFACTS");
            foreach (string language in new[] { "zh", "en" })
            {
                string theme = language == "zh" ? "dark" : "light";
                Configure(window, "top", language, theme, "auto");
                var preferences = Read<MediaUiPreferences>(window, "musicPreferences"); int previous = preferences.LyricFontSize;
                try
                {
                    preferences.LyricFontSize = 16; var track = Track(); Publish(window, track, "静静听歌 · Agypq 09"); Wait(window);
                    var visual = Read<CompactMusicVisual>(window, "compactMusic"); Publish(window, track, "海阔天空 · Agypq 09"); Pump(30); StopTimers(window);
                    if (SystemParameters.ClientAreaAnimation) Check(visual.LyricTransitionActive && visual.LyricTransitionOpacity < .999, "maximum-font sample has no real intermediate lyric frame");
                    PopupDpiTests.Save(visual, directory, "compact-music-horizontal-" + language + "-" + theme + "-font16-mid", (int)Math.Round(VisualTreeHelper.GetDpi(visual).PixelsPerInchX));
                    string intermediateClips = DescribeGlyphClips(visual); Console.WriteLine("GLYPH MID: " + intermediateClips);
                    Console.WriteLine("MEASURED: horizontal font16 intermediate lyric Y=" + visual.LyricTransitionY.ToString("0.000") + "; alpha=" + visual.LyricTransitionOpacity.ToString("0.000") + "; language=" + language);
                    Wait(window); CheckRest(visual, "maximum-font final"); string settledClips = DescribeGlyphClips(visual); Console.WriteLine("GLYPH FINAL: " + settledClips);
                    Check(String.Equals(intermediateClips, settledClips, StringComparison.Ordinal), "maximum-font animation introduces extra glyph clipping relative to settled drawing");
                    PopupDpiTests.Save(visual, directory, "compact-music-horizontal-" + language + "-" + theme + "-font16-final", (int)Math.Round(VisualTreeHelper.GetDpi(visual).PixelsPerInchX));
                    transitions++;
                }
                finally { preferences.LyricFontSize = previous; }
            }
        }
        private static void Lifecycles(IslandWindow window)
        {
            foreach (string action in new[] { "expand", "collapse", "appearance", "hide", "unload", "widget", "suspend", "close" })
            {
                Configure(window, "right", "zh", "dark", "auto"); var first = Track(); Publish(window, first, Line(first, 0)); Wait(window);
                var next = Track(); Publish(window, next, Line(next, 0)); Pump(20); StopTimers(window);
                var visual = Read<CompactMusicVisual>(window, "compactMusic");
                if (SystemParameters.ClientAreaAnimation) Check(visual.ContentTransitionActive, action + ": fixture lacks active music motion");
                if (action == "expand") window.SetExpanded(true, true);
                else if (action == "collapse") window.SetExpanded(false, false);
                else if (action == "appearance") { var settings = Read<Settings>(window, "settings"); settings.Language = "en"; settings.Theme = "light"; settings.HideNotificationBody = true; Call(window, "ApplyAppearance"); }
                else if (action == "hide") window.Hide();
                else if (action == "unload") { Read<Grid>(window, "compactMusicTarget").Children.Remove(visual); Pump(1); StopTimers(window); }
                else if (action == "widget") { Read<Settings>(window, "settings").CompactMode = "clock"; Call(window, "UpdateToolkitCompact"); }
                else if (action == "suspend") Call(window, "ApplyRuntimePowerMode", Microsoft.Win32.PowerModes.Suspend);
                else window.Close();
                CheckRest(visual, action + "/immediate"); lifecycleCases++;
                if (action == "close") break;
                if (action == "hide") { window.Show(); Settle(window); }
                if (action == "unload") { Read<Grid>(window, "compactMusicTarget").Children.Add(visual); Settle(window); }
                if (action == "suspend") Call(window, "ApplyRuntimePowerMode", Microsoft.Win32.PowerModes.Resume);
                Read<Settings>(window, "settings").CompactMode = "music"; window.SetExpanded(false, false); Wait(window); Publish(window, next, Line(next, 0)); Wait(window);
                VerifyLatest(window, next, Line(next, 0), action + "/restored");
            }
        }

        private static void DpiLayouts()
        {
            string artifacts = Environment.GetEnvironmentVariable("CODEX_ISLAND_MUSIC_MOTION_ARTIFACTS");
            foreach (string language in new[] { "zh", "en" }) foreach (string theme in new[] { "light", "dark" })
            foreach (int dpi in PopupDpiTests.Values) foreach (bool horizontal in new[] { true, false })
            foreach (string direction in new[] { "auto", "rotate180", "mirror" }) foreach (int font in new[] { 9, 16 })
            {
                AppAppearance.Configure(language, theme);
                var visual = new CompactMusicVisual(); var root = new Border { Background = UiTheme.IsLight ? Brushes.WhiteSmoke : Brushes.Black, Child = visual, Padding = new Thickness(8), UseLayoutRounding = true };
                var first = Track(); string lyric = Line(first, 0) + " 长歌词 😀 original long line";
                visual.Update(first, lyric, horizontal, direction, theme == "light", language, font, true, true, true);
                double width = (horizontal ? 118 : 59) + 16, height = (horizontal ? 62 : 133) + 16;
                PopupDpiTests.Apply(root, dpi); Layout(root, width, height); PopupDpiTests.Render(root, dpi); Layout(root, width, height);
                visual.Update(first, lyric + " next", horizontal, direction, theme == "light", language, font, true, true, true); Layout(root, width, height); PopupDpiTests.Render(root, dpi);
                CheckRest(visual, "DPI detached");
                if (language == "zh" && theme == "light" && !horizontal && direction == "auto" && font == 9)
                    Console.WriteLine("MEASURED: cold music DPI=" + dpi + "; requested=" + visual.Width + "x" + visual.Height + "; actual=" + visual.ActualWidth + "x" + visual.ActualHeight + "; physical-pixel=" + (96d / dpi));
                Check(visual.Width == width - 16 && visual.Height == height - 16 && Math.Abs(visual.ActualWidth - visual.Width) <= 96d / dpi + .01 && Math.Abs(visual.ActualHeight - visual.Height) <= 96d / dpi + .01,
                    "DPI music footprint exceeds one physical rounding pixel; dpi=" + dpi + "; requested=" + visual.Width + "x" + visual.Height + "; actual=" + visual.ActualWidth + "x" + visual.ActualHeight);
                Check(PopupDpiTests.VerifyGlyphDpi(root, dpi, "compact music") > 0, "DPI compact music has no rendered glyphs");
                CheckCurrentDrawing(visual, first.Artwork, "DPI/" + dpi + "/" + direction + "/" + font); dpiCases++;
                if (dpi == 144 && !horizontal && direction == "auto" && font == 16) PopupDpiTests.Save(root, artifacts, "compact-music-" + language + "-" + theme + "-144", dpi);
                root.Child = null; visual.SettleContentTransition();
            }
        }

        private static MediaSnapshot Track()
        {
            int id = ++sequence;
            return new MediaSnapshot { Available = true, Playing = true, CanToggle = true, CanPrevious = true, CanNext = true,
                Title = "Synthetic song " + id + " · 测试歌曲 😀 original", Artist = "Synthetic artist " + id + " · 演唱者", Album = "Synthetic album", Source = "TEST.Player." + id % 3,
                Position = TimeSpan.FromSeconds(43), Duration = TimeSpan.FromSeconds(180), SampledAtUtc = DateTime.UtcNow, Artwork = covers[id % covers.Length] };
        }
        private static string Line(MediaSnapshot track, int index) { return "Synthetic cue " + sequence + "/" + index + " · 原文歌词 😀"; }
        private static BitmapSource[] Artwork()
        {
            return new[] { Colors.MediumPurple, Colors.CornflowerBlue, Colors.Coral, Colors.SeaGreen }.Select(color => {
                var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, 48, 48)); dc.DrawEllipse(Brushes.White, null, new Point(13, 14), 6, 6); }
                var bitmap = new RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing); bitmap.Freeze(); return (BitmapSource)bitmap;
            }).ToArray();
        }
        private static void Publish(IslandWindow window, MediaSnapshot track, string lyric)
        {
            var monitor = Read<NowPlayingMonitor>(window, "nowPlaying"); SetProperty(monitor, "Snapshot", track);
            SetProperty(monitor, "Lyrics", String.IsNullOrEmpty(lyric) ? null : LrcDocument.Parse("[00:00.00]" + lyric));
            Call(window, "UpdateToolkitCompact"); StopTimers(window);
        }
        private static void VerifyLatest(IslandWindow window, MediaSnapshot track, string lyric, string context)
        {
            var visual = Read<CompactMusicVisual>(window, "compactMusic");
            Check(Read<string>(visual, "title") == track.Title && Read<string>(visual, "artist") == track.Artist && ReferenceEquals(Read<BitmapSource>(visual, "artwork"), track.Artwork), context + ": current track fields remain stale");
            Check(visual.Lyric == (track.HasUsableTimeline ? lyric : ""), context + ": current lyric remains stale");
            string tooltip = Convert.ToString(Read<Grid>(window, "compactMusicTarget").ToolTip);
            Check(tooltip.Contains(track.Title) && tooltip.Contains(track.Artist) && (!track.HasUsableTimeline || tooltip.Contains(lyric)), context + ": current tooltip remains stale");
        }
        private static void VerifyRange(CompactMusicVisual visual, string context)
        {
            Check(visual.ContentTransitionOpacity >= .349 && visual.ContentTransitionOpacity <= 1.001 && visual.LyricTransitionOpacity >= .349 && visual.LyricTransitionOpacity <= 1.001, context + ": invalid or blank content alpha");
            Check(visual.ContentTransitionY == 0 && visual.LyricTransitionY == 0, context + ": music motion moves tight text slots and adds glyph cropping");
        }
        private static void CheckRest(CompactMusicVisual visual, string context)
        {
            Check(!visual.ContentTransitionActive && !visual.LyricTransitionActive && !visual.TransitionRenderingAttached && visual.ContentTransitionOpacity == 1 && visual.LyricTransitionOpacity == 1 && visual.ContentTransitionY == 0 && visual.LyricTransitionY == 0, context + ": music animation or temporary opacity remains");
        }
        private static string DescribeGlyphClips(CompactMusicVisual visual)
        {
            PopupDpiTests.Render(visual, (int)Math.Round(VisualTreeHelper.GetDpi(visual).PixelsPerInchX));
            var values = new List<string>();
            GlyphClips(VisualTreeHelper.GetDrawing(visual), System.Windows.Media.Matrix.Identity, new Rect(-1000000, -1000000, 2000000, 2000000), values);
            return String.Join(" | ", values);
        }
        private static void GlyphClips(Drawing drawing, Matrix parent, Rect parentClip, List<string> values)
        {
            var group = drawing as DrawingGroup;
            if (group != null)
            {
                Matrix transform = parent; if (group.Transform != null) transform.Prepend(group.Transform.Value);
                Rect clip = parentClip;
                if (group.ClipGeometry != null) clip.Intersect((new MatrixTransform(transform)).TransformBounds(group.ClipGeometry.Bounds));
                foreach (Drawing child in group.Children) GlyphClips(child, transform, clip, values);
                return;
            }
            var glyph = drawing as GlyphRunDrawing;
            if (glyph == null) return;
            Rect ink = (new MatrixTransform(parent)).TransformBounds(glyph.Bounds);
            double loss = Math.Max(0, parentClip.Top - ink.Top) + Math.Max(0, ink.Bottom - parentClip.Bottom);
            values.Add("ink=" + ink.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";clip=" + parentClip.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";verticalLoss=" + loss.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
        }
        private static void CheckCurrentDrawing(CompactMusicVisual visual, BitmapSource artwork, string context)
        {
            PopupDpiTests.Render(visual, (int)Math.Round(VisualTreeHelper.GetDpi(visual).PixelsPerInchX));
            var images = Images(VisualTreeHelper.GetDrawing(visual)).ToArray();
            Check(images.All(image => ReferenceEquals(image, artwork)) && (artwork == null || images.Length == 1), context + ": rendered drawing retains a previous cover");
        }
        private static IEnumerable<ImageSource> Images(Drawing drawing)
        {
            var image = drawing as ImageDrawing; if (image != null) { yield return image.ImageSource; yield break; }
            var group = drawing as DrawingGroup; if (group != null) foreach (Drawing child in group.Children) foreach (var source in Images(child)) yield return source;
        }
        private static void Configure(IslandWindow window, string edge, string language, string theme, string direction)
        {
            window.SetExpanded(false, false); var settings = Read<Settings>(window, "settings"); settings.CompactMode = "music"; settings.AutoMusic = false; settings.Language = language; settings.Theme = theme; settings.CompactTextDirection = direction;
            Call(window, "ApplyAppearance"); Call(window, "ChangeSide", edge); Settle(window);
        }
        private static void Wait(IslandWindow window)
        {
            var clock = Stopwatch.StartNew(); do { Pump(15); StopTimers(window); window.UpdateLayout(); }
            while ((Read<CompactMusicVisual>(window, "compactMusic").TransitionRenderingAttached || Read<bool>(window, "visualTransitionActive") || Bool(Read<CompactWidgetVisual>(window, "compactWidget1"), "ContentTransitionActive") || Bool(Read<CompactWidgetVisual>(window, "compactWidget2"), "ContentTransitionActive")) && clock.ElapsedMilliseconds < 1800);
            Check(!Read<bool>(window, "visualTransitionActive") && !Read<CompactMusicVisual>(window, "compactMusic").TransitionRenderingAttached && !Bool(Read<CompactWidgetVisual>(window, "compactWidget1"), "ContentTransitionActive") && !Bool(Read<CompactWidgetVisual>(window, "compactWidget2"), "ContentTransitionActive"), "compact music motion exceeds bounded completion wait");
        }
        private static void Settle(Window window) { window.UpdateLayout(); Pump(1); var island = window as IslandWindow; if (island != null) StopTimers(island); window.UpdateLayout(); }
        private static void StopTimers(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); var monitor = Read<NowPlayingMonitor>(window, "nowPlaying"); if (monitor != null) monitor.Stop(); var focus = Read<FocusPanel>(window, "focusPanel"); if (focus != null) focus.Stop(); }
        private static void Layout(FrameworkElement root, double width, double height) { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
        private static Rect NativeBounds(Window window) { NativeRect value; Check(GetWindowRect(new WindowInteropHelper(window).Handle, out value), "own native bounds unavailable"); return new Rect(value.Left, value.Top, value.Right - value.Left, value.Bottom - value.Top); }
        private static Rect ScreenBounds(FrameworkElement value) { return new Rect(value.PointToScreen(new Point()), value.PointToScreen(new Point(value.ActualWidth, value.ActualHeight))); }
        private static Point Center(Rect value) { return new Point(value.Left + value.Width / 2, value.Top + value.Height / 2); }
        private static void Same(Rect value, Rect expected, double tolerance, string context) { Check(Math.Abs(value.Left - expected.Left) <= tolerance && Math.Abs(value.Top - expected.Top) <= tolerance && Math.Abs(value.Width - expected.Width) <= tolerance && Math.Abs(value.Height - expected.Height) <= tolerance, context + "; actual=" + value + "; expected=" + expected); }
        private static void Monotonic(IList<double> values, string context) { for (int index = 1; index < values.Count; index++) Check(values[index] + .001 >= values[index - 1], context + ": alpha reverses"); }
        private static T Read<T>(object value, string field) { return (T)value.GetType().GetField(field, Private).GetValue(value); }
        private static object Property(object value, string name) { return value.GetType().GetProperty(name, Private).GetValue(value, null); }
        private static bool Bool(object value, string name) { return (bool)Property(value, name); }
        private static void SetProperty(object value, string name, object propertyValue) { value.GetType().GetProperty(name, Private).GetSetMethod(true).Invoke(value, new[] { propertyValue }); }
        private static object Call(object value, string method, params object[] args) { try { return value.GetType().GetMethod(method, Private).Invoke(value, args); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static void Pump(int milliseconds) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
        private static void Check(bool value, string context) { if (!value) throw new Exception("Compact music motion: " + context); }
    }
}
