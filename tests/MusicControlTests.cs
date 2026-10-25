using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class MusicControlTests
    {
        internal static void Run()
        {
            PreferencesAndTiming(); HitRegionsAndAppearance(); CleanCompactFallback(); LyricsSettingsPanel();
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { ExplicitControlsOnly(); CoalescedWheel(); MonitorCommandsAndOffset(); WindowMusicRouting(); }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            Console.WriteLine("PASS: compact music hit regions, capability gating, worker-only explicit playback/volume, precision wheel/coalescing, lyric size/offset validation and atomic preferences. No real media/audio writes.");
        }
        private static MediaSnapshot Track()
        { return new MediaSnapshot { Available = true, Playing = true, CanToggle = true, CanPrevious = true, CanNext = true, Title = "Synthetic song", Artist = "Synthetic artist", Source = "TEST", Position = TimeSpan.FromSeconds(1), Duration = TimeSpan.FromSeconds(20) }; }

        private static void PreferencesAndTiming()
        {
            var defaults = new MediaUiPreferences(); Check(defaults.LyricFontSize == 10 && defaults.LyricOffsetMilliseconds == 0, "Defaults preserve existing appearance and synchronization");
            var extreme = new MediaUiPreferences { LyricFontSize = 50, LyricOffsetMilliseconds = Int32.MinValue }; extreme.Validate();
            Check(extreme.LyricFontSize == 16 && extreme.LyricOffsetMilliseconds == -10000, "Malformed stored values are clamped");
            Check(MediaLyricTiming.Adjust(TimeSpan.MaxValue, 10000) == TimeSpan.MaxValue && MediaLyricTiming.Adjust(TimeSpan.MinValue, -10000) == TimeSpan.MinValue, "Offset cannot overflow a timeline");
            var lyrics = LrcDocument.Parse("[00:01]first\n[00:02]second");
            Check(lyrics.LineAt(MediaLyricTiming.Adjust(TimeSpan.FromMilliseconds(1500), 500)) == "second", "Positive user offset advances lyrics");
            Check(lyrics.LineAt(MediaLyricTiming.Adjust(TimeSpan.FromMilliseconds(1500), -1000)) == "", "Negative offset delays lyrics without clamping to an early cue");
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.MusicControls." + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "music-preferences.json"); var store = new MediaUiPreferencesStore(false, path);
                store.Save(new MediaUiPreferences { LyricFontSize = 15, LyricOffsetMilliseconds = -1250 });
                var loaded = new MediaUiPreferencesStore(false, path).Load();
                Check(loaded.LyricFontSize == 15 && loaded.LyricOffsetMilliseconds == -1250, "Lyrics preferences survive process restarts");
                store.Save(new MediaUiPreferences { LyricFontSize = 9, LyricOffsetMilliseconds = 10000 });
                Check(store.Load().LyricFontSize == 9 && Directory.GetFiles(folder, "*.tmp").Length == 0, "Atomic replacement leaves no temporary history");
                File.WriteAllText(path, new String('x', 17000)); Check(store.Load().LyricFontSize == 10, "Oversized preferences safely fall back");
                var memory = new MediaUiPreferencesStore(true, Path.Combine(folder, "not-written.json")); memory.Save(new MediaUiPreferences { LyricFontSize = 16 });
                Check(memory.Load().LyricFontSize == 16 && !File.Exists(Path.Combine(folder, "not-written.json")), "Demo preferences never touch disk");
            }
            finally { Directory.Delete(folder, true); }
        }

        private static void HitRegionsAndAppearance()
        {
            var visual = new CompactMusicVisual(); var track = Track();
            foreach (bool horizontal in new[] { true, false })
            foreach (string direction in new[] { "auto", "rotate180", "mirror" })
            foreach (int fontSize in new[] { 9, 10, 16 })
            {
                visual.Update(track, "长歌词 Synthetic lyric 😀", horizontal, direction, false, "zh", fontSize);
                Check(visual.ActionAt(Center(visual.CoverBounds)) == "music-toggle", "Cover is a play/pause action for both orientations");
                Check(visual.ActionAt(Center(visual.PreviousBounds)) == "music-previous" && visual.ActionAt(Center(visual.NextBounds)) == "music-next", "Text transforms do not mirror playback hit regions");
                Check(visual.ActionAt(new Point(-1, -1)) == null, "Outside the music card has no playback action");
                visual.Measure(new Size(visual.Width, visual.Height)); visual.Arrange(new Rect(0, 0, visual.Width, visual.Height));
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)visual.Width, (int)visual.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(visual);
            }
            track.CanToggle = track.CanPrevious = track.CanNext = false; visual.Update(track, "", true, "auto", true, "en");
            Check(visual.ActionAt(Center(visual.CoverBounds)) == null && visual.ActionAt(Center(visual.NextBounds)) == null, "Unavailable player commands remain detail/open regions");
        }

        private static void CleanCompactFallback()
        {
            string previousTheme = UiTheme.Name;
            try
            {
                var visual = new CompactMusicVisual(); var track = Track();
                foreach (string language in new[] { "zh", "en" })
                foreach (bool light in new[] { false, true })
                foreach (bool horizontal in new[] { true, false })
                foreach (string direction in new[] { "auto", "rotate180", "mirror" })
                foreach (bool playing in new[] { true, false })
                {
                    UiTheme.Configure(light ? "light" : "dark"); track.Playing = playing;
                    track.Duration = TimeSpan.Zero; track.SampledAtUtc = new DateTime(1601, 1, 1);
                    visual.Update(track, "This untimed first lyric must stay hidden", horizontal, direction, light, language, 16, true, true);
                    Check(visual.Lyric.Length == 0 && !Field<bool>(visual, "timelineAvailable") && Field<double>(visual, "progress") == 0, "Unavailable progress has neither a fabricated lyric nor a progress track");
                    string expected = language == "en" ? (playing ? "Now playing" : "Paused") : playing ? "正在播放" : "已暂停";
                    Check((string)typeof(CompactMusicVisual).GetProperty("DisplayLine", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(visual, null) == expected, "Compact no-progress fallback is a concise localized playback state");
                    Check(visual.Width == (horizontal ? 118 : 59) && visual.Height == (horizontal ? 62 : 133), "Clean fallback preserves the fixed compact geometry");
                    Check(visual.ActionAt(Center(visual.CoverBounds)) == "music-toggle" && visual.ActionAt(Center(visual.PreviousBounds)) == "music-previous" && visual.ActionAt(Center(visual.NextBounds)) == "music-next", "Missing timelines never disable supported compact controls or transform their hit regions");
                    visual.Measure(new Size(visual.Width, visual.Height)); visual.Arrange(new Rect(0, 0, visual.Width, visual.Height));
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)visual.Width, (int)visual.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(visual);
                }
                track.Duration = TimeSpan.FromMinutes(3); track.SampledAtUtc = new DateTime(1601, 1, 1);
                visual.Update(track, "Invalid timestamp lyric", true, "auto", false, "zh", 10, true, true);
                Check(visual.Lyric.Length == 0 && !Field<bool>(visual, "timelineAvailable"), "A duration alone does not turn an invalid timestamp into a usable timeline");
                track.SampledAtUtc = DateTime.UtcNow;
                visual.Update(track, "A plain document must stay in details", true, "auto", false, "zh", 10, true, false);
                Check(visual.Lyric.Length == 0 && Field<bool>(visual, "timelineAvailable"), "Plain lyrics are omitted while real media progress stays intact");
                visual.Update(track, "真实歌词 · original text", true, "auto", false, "en", 10, true, true);
                Check(visual.Lyric == "真实歌词 · original text", "Timed original lyrics resume as soon as the timeline becomes valid");
                visual.Update(track, "", false, "mirror", false, "en", 10, true, true);
                Check(visual.Lyric.Length == 0, "Pre-roll and empty lyric gaps do not turn into diagnostic text");
                track.Available = false; visual.Update(track, "Stale lyric", false, "mirror", false, "en", 10, true, true);
                Check(visual.Lyric.Length == 0 && !Field<bool>(visual, "timelineAvailable") && visual.ActionAt(Center(visual.CoverBounds)) == null, "A vanished session drops stale lyrics, progress and playback actions");
            }
            finally { UiTheme.Configure(previousTheme); }
        }

        private static void LyricsSettingsPanel()
        {
            using (var panel = new MediaPanel(true))
            {
                MediaUiPreferences changed = null; panel.LyricDisplayChanged += value => changed = value;
                panel.ConfigureLyricDisplay(new MediaUiPreferences { LyricFontSize = 14, LyricOffsetMilliseconds = -750 });
                Check(Field<TextBox>(panel, "lyricFontInput").Text == "14" && Field<TextBox>(panel, "lyricOffsetInput").Text == "-750", "Panel restores persisted input values");
                Field<TextBox>(panel, "lyricFontInput").Text = "17"; Invoke(panel, "ApplyLyricDisplay"); Check(changed == null, "Out-of-range font is rejected before changing state");
                Field<TextBox>(panel, "lyricFontInput").Text = "16"; Field<TextBox>(panel, "lyricOffsetInput").Text = "10001"; Invoke(panel, "ApplyLyricDisplay"); Check(changed == null, "Out-of-range offset is rejected");
                Field<TextBox>(panel, "lyricOffsetInput").Text = "1250"; Invoke(panel, "ApplyLyricDisplay");
                Check(changed != null && changed.LyricFontSize == 16 && changed.LyricOffsetMilliseconds == 1250 && Field<TextBlock>(panel, "lyricsLine").FontSize == 16, "Valid preferences immediately update expanded lyrics and emit persistence event");
            }
        }

        private static void ExplicitControlsOnly()
        {
            int calls = 0, worker = 0, ui = Thread.CurrentThread.ManagedThreadId;
            var pending = new TaskCompletionSource<string>();
            using (var controller = new CompactMediaController(false, command => { worker = Thread.CurrentThread.ManagedThreadId; Interlocked.Increment(ref calls); return pending.Task; }, () => { throw new Exception("Unexpected audio read"); }, value => { throw new Exception("Unexpected volume change"); }))
            {
                Check(calls == 0, "Controller construction never sends commands");
                Check(!controller.SendAsync(new MediaSnapshot(), "toggle").Result && !controller.SendAsync(Track(), "bad").Result, "Invalid/unavailable commands do not reach native sender");
                var watch = Stopwatch.StartNew(); var first = controller.SendAsync(Track(), "toggle"); watch.Stop();
                Check(watch.ElapsedMilliseconds < 300, "Media control returns before slow player completes"); Pump(() => calls == 1);
                Check(worker != ui, "Synchronous media provider portions run on a worker");
                Check(!controller.SendAsync(Track(), "next").Result && calls == 1, "Repeated clicks cannot overlap transport commands");
                pending.SetResult("synthetic control sent"); Pump(() => first.IsCompleted); Check(first.Result, "Accepted explicit command completes");
            }
            using (var demo = new CompactMediaController(true, command => { calls++; return Task.FromResult("bad"); }, () => { calls++; return new AudioSnapshot(); }, value => { calls++; return true; }))
            { Check(!demo.SendAsync(Track(), "toggle").Result && !demo.AdjustVolumeAsync(120).Result && calls == 1, "Demo refuses real controls even if capabilities are present"); }
        }

        private static void CoalescedWheel()
        {
            double currentVolume = 99; int reads = 0, writes = 0, ui = Thread.CurrentThread.ManagedThreadId; bool offThread = true;
            var written = new List<double>();
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var controller = new CompactMediaController(false, null, () => {
                offThread &= Thread.CurrentThread.ManagedThreadId != ui; Interlocked.Increment(ref reads); return new AudioSnapshot { OutputAvailable = true, Volume = currentVolume };
            }, value => {
                offThread &= Thread.CurrentThread.ManagedThreadId != ui; int count = Interlocked.Increment(ref writes);
                if (count == 1) { entered.Set(); release.Wait(3000); }
                currentVolume = value; lock (written) written.Add(value); return true;
            }))
            {
                try
                {
                    var partial = controller.AdjustVolumeAsync(60); Check(partial.Result && reads == 0 && writes == 0, "Partial wheel detent does not amplify volume");
                    var first = controller.AdjustVolumeAsync(60); Pump(() => entered.IsSet);
                    Check(controller.AdjustVolumeAsync(-240).Result && controller.AdjustVolumeAsync(-120).Result, "Wheel events coalesce while a driver is pending");
                    bool input = false; Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => input = true)); Pump(() => input);
                    release.Set(); Pump(() => first.IsCompleted);
                    Check(first.Result && written.Count == 2 && written[0] == 100 && written[1] == 94 && offThread, "Volume clamps and combined queued detents apply from the latest device value on workers");
                    controller.Dispose(); Check(!controller.AdjustVolumeAsync(120).Result, "Disposed controller does not schedule audio writes");
                }
                finally { release.Set(); }
            }
            using (var noDevice = new CompactMediaController(false, null, () => new AudioSnapshot(), value => { throw new Exception("No output must not write"); }))
            { var result = noDevice.AdjustVolumeAsync(120); Pump(() => result.IsCompleted); Check(!result.Result, "Missing output cannot cause a volume write"); }
        }

        private static void MonitorCommandsAndOffset()
        {
            int commands = 0; var track = Track(); var library = new MediaLyricsLibrary(Path.GetTempPath(), true);
            string temporary = Path.Combine(Path.GetTempPath(), "SideIsland.MusicControlCue." + Guid.NewGuid().ToString("N") + ".lrc");
            try { File.WriteAllText(temporary, "[00:01]first\n[00:02]second"); library.Import(track.Source, track.Title, track.Artist, temporary); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            using (var monitor = new NowPlayingMonitor(false, () => Task.FromResult(track), library, null, command => { Interlocked.Increment(ref commands); return Task.FromResult("synthetic"); }))
            {
                monitor.Start(); Pump(() => monitor.Snapshot.Available); Check(commands == 0, "Background reads cannot emit media commands");
                Check(monitor.Lyric == "first", "Monitor restores the exact track's local lyrics");
                monitor.LyricOffsetMilliseconds = 1000; Check(monitor.Lyric == "second", "Monitor applies user advance immediately");
                monitor.LyricOffsetMilliseconds = -500; Check(monitor.Lyric == "", "Monitor applies user delay before the first cue");
                var send = monitor.SendCommandAsync("next"); Pump(() => send.IsCompleted); Check(commands == 1, "Monitor routes only an explicit supported command");
                monitor.Stop(); var stopped = monitor.SendCommandAsync("toggle"); Check(stopped.IsCompleted && commands == 1, "Stopped monitor cannot send an old session command");
            }
        }

        private static void WindowMusicRouting()
        {
            string previousDirectory = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.MusicRouting." + Guid.NewGuid().ToString("N")); IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { CompactMode = "music", AutoMusic = false, AlwaysOnTop = false, NotificationsEnabled = false, HoverExpandEnabled = false });
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false };
                window.Loaded += (s, e) => StopTimers(window); window.Show(); StopTimers(window); window.SetExpanded(false, false);
                var monitor = Field<NowPlayingMonitor>(window, "nowPlaying"); var track = Track();
                typeof(NowPlayingMonitor).GetField("<Snapshot>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(monitor, track);
                Field<CompactMediaController>(window, "compactMediaController").Dispose(); int commands = 0;
                var fakeControls = new CompactMediaController(false, command => { Interlocked.Increment(ref commands); return Task.FromResult("synthetic"); }, () => new AudioSnapshot(), value => { throw new Exception("Routing tests do not write audio"); });
                typeof(IslandWindow).GetField("compactMediaController", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, fakeControls);
                var visual = Field<CompactMusicVisual>(window, "compactMusic"); var target = Field<Grid>(window, "compactMusicTarget");
                foreach (string edge in new[] { "left", "right", "top", "bottom" })
                {
                    Call(window, "ChangeSide", edge); window.SetExpanded(false, false); Call(window, "UpdateMusicVisual"); window.UpdateLayout();
                    foreach (string direction in new[] { "auto", "rotate180", "mirror" })
                    {
                        Call(window, "SetCompactTextDirection", direction); window.UpdateLayout();
                        Check(ResolveAction(window, target, target.TranslatePoint(Center(visual.CoverBounds), window)) == "music-toggle", edge + "/" + direction + " cover routes to playback");
                        Check(ResolveAction(window, target, target.TranslatePoint(Center(visual.PreviousBounds), window)) == "music-previous", edge + "/" + direction + " previous control routes correctly");
                        Check(ResolveAction(window, target, target.TranslatePoint(Center(visual.NextBounds), window)) == "music-next", edge + "/" + direction + " next control routes correctly");
                    }
                    int before = commands; Call(window, "RunCompactAction", "music-toggle");
                    try { Pump(() => commands == before + 1 && !Field<bool>(fakeControls, "commanding")); }
                    catch (Exception error) { throw new Exception("Music routing on " + edge + ": commands=" + commands + ", before=" + before + ", snapshot=" + monitor.Snapshot.Available + "/" + monitor.Snapshot.CanToggle + ", guards=" + Field<bool>(window, "shuttingDown") + "/" + Field<bool>(window, "dragging") + "/" + Field<bool>(window, "resizing") + ", disposed=" + Field<bool>(fakeControls, "disposed"), error); }
                    Check(!Field<bool>(window, "expanded"), "Playback click remains compact on " + edge);
                }
                var settings = Field<Settings>(window, "settings"); var desktop = Field<DesktopExperiencePreferences>(window, "desktopPreferences");
                settings.AutoMusic = false; settings.CompactMode = "quota"; settings.CompactSlot1 = "cpu"; settings.CompactSlot2 = "memory";
                foreach (string gesture in new[] { "ClickAction", "MiddleAction", "WheelAction" })
                {
                    typeof(DesktopExperiencePreferences).GetField(gesture).SetValue(desktop, "media-toggle"); Call(window, "SyncNowPlayingActivity");
                    Check(monitor.Running, "Configured " + gesture + " media command keeps monitoring active without an automatic music card");
                    typeof(DesktopExperiencePreferences).GetField(gesture).SetValue(desktop, "default"); Call(window, "SyncNowPlayingActivity");
                    Check(!monitor.Running, "Removing the last media gesture stops unnecessary monitoring");
                }
                settings.CompactMode = "music";
                track.CanToggle = false; Call(window, "UpdateMusicVisual");
                Check(ResolveAction(window, target, target.TranslatePoint(Center(visual.CoverBounds), window)) == "media", "A player without toggle support retains the original open-details action");
            }
            finally
            {
                if (window != null) { StopTimers(window); window.Close(); }
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previousDirectory);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
        private static string ResolveAction(IslandWindow window, DependencyObject source, Point point)
        { return (string)typeof(IslandWindow).GetMethod("FindCompactActionAt", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[] { source, point }); }
        private static void StopTimers(IslandWindow window)
        { foreach (var field in typeof(IslandWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); }
        private static object Call(object value, string name, params object[] arguments) { return value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, arguments); }

        private static Point Center(Rect rectangle) { return new Point(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2); }
        private static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
        private static void Invoke(object value, string name) { value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, null); }
        private static void Pump(Func<bool> completed)
        {
            if (completed()) return;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) }; var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
            timer.Tick += (s, e) => { if (completed() || watch.ElapsedMilliseconds > 4000) frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(completed(), "Synthetic operation timed out");
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Music controls: " + message); }
    }
}
