using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    // Only synthetic metadata and this process's demo HWND are used. These
    // fixtures never start/control a player, move the cursor or fetch lyrics.
    internal static class NowPlayingIntegrationTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        [DllImport("user32.dll")] private static extern IntPtr GetCapture();
        [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        internal static void Run()
        {
            MusicPolicy();
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string previousLanguage = UiLocalization.Language, previousTheme = UiTheme.Name;
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.NowPlayingIntegration." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            MouseEventHandler ignorePhysicalButton = (s, e) => e.Handled = true;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false, AutoSnapToEdge = false, AutoMusic = false, CompactMode = "music" });
                window = new IslandWindow(true) { ShowActivated = false };
                window.Loaded += (s, e) => StopTimers(window);
                window.Show(); Pump(1); window.UpdateLayout(); StopTimers(window);
                var shell = Read<Border>(window, "shell"); shell.PreviewMouseMove += ignorePhysicalButton;
                try
                {
                    var track = SyntheticTrack();
                    var monitor = Read<NowPlayingMonitor>(window, "nowPlaying");
                    SetProperty(monitor, "Snapshot", track);
                    SetProperty(monitor, "Lyrics", LrcDocument.Parse("[00:00.00]Synthetic first cue\n[00:40.00]测试歌词 · synthetic cue\n[01:00.00]Synthetic next cue"));
                    FourEdgesAndAppearance(window, track);
                    MediaSlotBesideQuota(window, track);
                    MusicCardClickAndDrag(window);
                    ExpandedMetadataAndCover(window, track);
                }
                finally { shell.PreviewMouseMove -= ignorePhysicalButton; }
                Console.WriteLine("PASS: compact music auto-switch policy, paused grace/identity/focus precedence, four dock edges, fixed music footprints, themes/text directions, media plus weekly quota, release-only music routing, drag cancellation and protected expanded metadata/artwork.");
            }
            finally
            {
                if (window != null) { ReleaseOwnCapture(window); StopTimers(window); window.Close(); }
                AppAppearance.Configure(previousLanguage, previousTheme);
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        private static void MusicPolicy()
        {
            DateTime now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
            var settings = new Settings();
            var playing = new MediaSnapshot { Available = true, Playing = true, Title = "Synthetic", Artist = "Fixture", Source = "DEMO" };
            string identity = NowPlayingMonitor.Identity(playing);
            Check(settings.AutoMusic && !settings.OnlineLyricsEnabled && settings.CompactMode == "quota", "defaults enable local now-playing display and leave online lyrics off");
            Check(!CompactMusicPolicy.Show(settings, null, false, DateTime.MinValue, null, now), "no media leaves default widgets visible");
            Check(!CompactMusicPolicy.Show(settings, new MediaSnapshot(), false, now, identity, now), "unavailable session cannot replace widgets");
            Check(CompactMusicPolicy.Show(settings, playing, false, now, identity, now), "playing session activates automatic music");
            settings.AutoMusic = false;
            Check(!CompactMusicPolicy.Show(settings, playing, false, now, identity, now), "automatic display respects its disabled preference");
            settings.AutoMusic = true; settings.CompactSlot1 = "focus";
            Check(!CompactMusicPolicy.Show(settings, playing, true, now, identity, now), "running visible focus widget takes precedence");
            Check(CompactMusicPolicy.Show(settings, playing, false, now, identity, now), "idle focus widget allows automatic music");
            settings.CompactSlot1 = "codex-primary"; settings.CompactSlot2 = "focus";
            Check(!CompactMusicPolicy.Show(settings, playing, true, now, identity, now), "running focus in the second slot also takes precedence");
            settings.CompactMode = "focus";
            Check(!CompactMusicPolicy.Show(settings, playing, true, now, identity, now), "running large focus view takes precedence");
            settings.CompactMode = "clock";
            Check(CompactMusicPolicy.Show(settings, playing, true, now, identity, now), "background focus does not hide a selected clock's music view");
            settings.CompactMode = "quota"; settings.CompactSlot2 = "codex-secondary"; playing.Playing = false;
            Check(CompactMusicPolicy.Show(settings, playing, false, now.AddSeconds(-11.999), identity, now), "same paused track remains during the 12-second grace period");
            Check(!CompactMusicPolicy.Show(settings, playing, false, now.AddSeconds(-12), identity, now), "paused track returns to widgets at the grace boundary");
            Check(!CompactMusicPolicy.Show(settings, playing, false, now.AddSeconds(1), identity, now), "a future activity timestamp does not extend the grace period");
            Check(!CompactMusicPolicy.Show(settings, playing, false, now, identity + "changed", now), "a different paused track cannot inherit another track's grace period");
            settings.AutoMusic = false; settings.CompactMode = "music";
            Check(CompactMusicPolicy.Show(settings, null, true, DateTime.MinValue, null, now), "explicit music mode retains its empty state independently of automatic display");
        }

        private static MediaSnapshot SyntheticTrack()
        {
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(Brushes.MediumPurple, null, new Rect(0, 0, 48, 48));
                dc.DrawEllipse(Brushes.MediumAquamarine, null, new Point(12, 14), 18, 18);
            }
            var artwork = new RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32); artwork.Render(drawing); artwork.Freeze();
            return new MediaSnapshot { Available = true, Playing = true, Title = "设置", Artist = "刷新", Album = "复制", Source = "DEMO · Settings", Status = "正在播放", Artwork = artwork, Position = TimeSpan.FromSeconds(43), Duration = TimeSpan.FromSeconds(180), SampledAtUtc = DateTime.UtcNow };
        }

        private static void FourEdgesAndAppearance(IslandWindow window, MediaSnapshot track)
        {
            var settings = Read<Settings>(window, "settings");
            settings.CompactMode = "music"; settings.AutoMusic = false;
            var target = Read<Grid>(window, "compactMusicTarget"); var visual = Read<CompactMusicVisual>(window, "compactMusic");
            foreach (string edge in new[] { "left", "right", "top", "bottom" })
            {
                Call(window, "ChangeSide", edge); window.SetExpanded(false, false);
                bool horizontal = edge == "top" || edge == "bottom";
                foreach (string direction in new[] { "auto", "rotate180", "mirror" })
                foreach (string theme in new[] { "dark", "light" })
                {
                    settings.CompactTextDirection = direction; settings.Theme = theme; settings.Language = theme == "light" ? "en" : "zh";
                    Call(window, "ApplyAppearance"); Call(window, "UpdateToolkitCompact"); window.UpdateLayout(); StopTimers(window);
                    Check(target.Visibility == Visibility.Visible && Read<Grid>(window, "compactSlot1").Visibility == Visibility.Collapsed && Read<Grid>(window, "compactSlot2").Visibility == Visibility.Collapsed, edge + " music replaces the two widget slots");
                    Check(Read<StackPanel>(window, "compactAlternate").Visibility == Visibility.Collapsed, "large clock/focus text stays hidden during music");
                    Check(visual.ActualWidth == (horizontal ? 118 : 59) && visual.ActualHeight == (horizontal ? 62 : 133), edge + " music footprint remains fixed (actual " + visual.ActualWidth + "x" + visual.ActualHeight + ", requested " + visual.Width + "x" + visual.Height + ", target " + target.ActualWidth + "x" + target.ActualHeight + ")");
                    Check(target.ActualWidth == visual.ActualWidth && target.ActualHeight == visual.ActualHeight && visual.Horizontal == horizontal, edge + " target follows the music drawing dimensions");
                    Check(Near(window.Width, horizontal ? 238 : 88) && Near(window.Height, horizontal ? 88 : 238), edge + " native compact dimensions do not change");
                    Check(Read<string>(visual, "direction") == direction && visual.LightTheme == (theme == "light"), "music honors the selected text direction and theme");
                    Check(Read<string>(visual, "title") == track.Title && Read<string>(visual, "artist") == track.Artist && visual.Lyric == "测试歌词 · synthetic cue", "metadata and synchronized lyric remain external text in both languages");
                    Check(ReferenceEquals(Read<BitmapSource>(visual, "artwork"), track.Artwork) && track.Artwork.IsFrozen, "compact cover retains the frozen source image");
                    RenderOwnVisual(visual);
                    if (direction == "auto") CaptureOwnWindow(window, "music-" + edge + "-" + theme + ".png");
                }
            }
        }

        private static void MediaSlotBesideQuota(IslandWindow window, MediaSnapshot track)
        {
            var settings = Read<Settings>(window, "settings"); settings.AutoMusic = false; settings.CompactMode = "quota"; settings.CompactSlot1 = "media"; settings.CompactSlot2 = "codex-secondary";
            Call(window, "UpdateToolkitCompact"); window.UpdateLayout(); StopTimers(window);
            var first = Read<CompactWidgetVisual>(window, "compactWidget1"); var second = Read<CompactWidgetVisual>(window, "compactWidget2");
            var secondary = Read<QuotaRing>(window, "secondaryRing"); double? remaining = secondary.Remaining;
            Check(Read<Grid>(window, "compactMusicTarget").Visibility == Visibility.Collapsed && Read<Grid>(window, "compactSlot1").Visibility == Visibility.Visible && Read<Grid>(window, "compactSlot2").Visibility == Visibility.Visible, "manual media slot coexists with quota when automatic music is disabled");
            Check(first.State.Key == "media" && first.State.Caption == track.Title && ReferenceEquals(first.State.Artwork, track.Artwork), "media slot carries actual title and cover");
            Check(second.State.Key == "codex-secondary" && second.State.ShowRing && second.State.Caption == "7d", "weekly quota keeps its independent semantics beside media");
            Check(first.State.Tooltip.Contains(track.Title) && first.State.Tooltip.Contains(track.Artist) && first.State.Tooltip.Contains("测试歌词"), "media tooltip carries protected title, artist and current lyric");
            Check((string)Call(window, "FindCompactAction", Read<Grid>(window, "compactSlot1")) == "media", "manual media slot resolves to music details");
            Call(window, "UpdateToolkitCompact"); Check(secondary.Remaining == remaining, "media refresh leaves original quota data intact");
        }

        private static void MusicCardClickAndDrag(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings"); settings.CompactMode = "music"; settings.AutoMusic = false;
            Call(window, "ChangeSide", "right"); window.SetExpanded(false, false); Call(window, "UpdateToolkitCompact"); window.UpdateLayout(); StopTimers(window);
            var target = Read<Grid>(window, "compactMusicTarget"); var shell = Read<Border>(window, "shell");
            Check((string)Call(window, "FindCompactAction", Read<CompactMusicVisual>(window, "compactMusic")) == "media", "music drawing descendants resolve the containing card");
            Point center = target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2), window);
            Check((string)Call(window, "FindCompactAction", window.InputHitTest(center) as DependencyObject) == "media", "the visible music card center is interactive");
            Press(window, target);
            Check(!Read<bool>(window, "expanded") && Read<string>(window, "pendingCompactAction") == "media", "music press only records the action and remains compact");
            Release(window, shell);
            Check(Read<bool>(window, "expanded") && Read<bool>(window, "toolsPageSelected") && Read<string>(window, "selectedTool") == "media", "music release opens its media panel without sending playback controls");
            window.SetExpanded(false, false); Call(window, "UpdateToolkitCompact"); window.UpdateLayout(); StopTimers(window);
            Point start = Press(window, target);
            var source = PresentationSource.FromVisual(window); Vector offset = source.CompositionTarget.TransformToDevice.Transform(new Vector(35, 28));
            Check((bool)Call(window, "MoveDrag", start + offset), "dragging the music card crosses the standard compact drag threshold");
            Check(String.IsNullOrEmpty(Read<string>(window, "pendingCompactAction")), "music drag cancels the pending open action");
            Release(window, shell);
            Check(!Read<bool>(window, "expanded") && !Read<bool>(window, "peeking") && !Read<bool>(window, "visualTransitionActive"), "music dragging and release remain in compact form");
            Check(Near(window.Width, 88) && Near(window.Height, 238), "music drag preserves compact native dimensions");
        }

        private static void ExpandedMetadataAndCover(IslandWindow window, MediaSnapshot track)
        {
            Call(window, "RunCompactAction", "media"); window.SetExpanded(true, false); window.UpdateLayout(); StopTimers(window);
            var panel = Read<MediaPanel>(window, "mediaPanel");
            Call(panel, "RenderMedia", track);
            panel.ShowLyrics(track, "设置", true);
            var settings = Read<Settings>(window, "settings");
            foreach (string language in new[] { "en", "zh", "en" })
            {
                settings.Language = language; settings.Theme = language == "en" ? "light" : "dark"; Call(window, "ApplyAppearance"); panel.RefreshAppearance(); window.UpdateLayout(); StopTimers(window);
                Check(Read<TextBlock>(panel, "title").Text == "设置" && Read<TextBlock>(panel, "artist").Text == "刷新", "song title/artist matching UI commands are never translated");
                Check(Read<TextBlock>(panel, "lyricsLine").Text == "设置", "lyric text matching a UI command is never translated");
                Image cover = FindCover(panel, track.Artwork);
                Check(cover != null && ReferenceEquals(cover.Source, track.Artwork) && ((BitmapSource)cover.Source).IsFrozen, "expanded media displays the frozen artwork without theme recoloring");
                CaptureOwnWindow(window, "music-expanded-" + language + ".png");
            }
        }

        private static Image FindCover(DependencyObject root, ImageSource source)
        {
            var image = root as Image; if (image != null && ReferenceEquals(image.Source, source)) return image;
            if (!(root is Visual)) return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++) { var found = FindCover(VisualTreeHelper.GetChild(root, index), source); if (found != null) return found; }
            return null;
        }

        private static void RenderOwnVisual(FrameworkElement visual)
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            int stride = bitmap.PixelWidth * 4; byte[] pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
            int visible = 0; for (int index = 3; index < pixels.Length; index += 4) if (pixels[index] != 0) visible++;
            Check(visible > 100, "music view produces its own nonempty rendered cover/text/progress");
        }

        private static void CaptureOwnWindow(IslandWindow window, string name)
        {
            string folder = Environment.GetEnvironmentVariable("CODEX_ISLAND_TEST_ARTIFACT_DIR");
            if (String.IsNullOrWhiteSpace(folder)) return;
            Directory.CreateDirectory(folder);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(folder, name))) encoder.Save(stream);
        }

        private static Point Press(IslandWindow window, UIElement target)
        {
            var source = PresentationSource.FromVisual(window) as HwndSource;
            Check(source != null && !source.IsDisposed && GetCapture() == IntPtr.Zero && Mouse.Captured == null, "music input fixture starts without a capture");
            SetCapture(source.Handle);
            try { SendMessage(source.Handle, 0x0200, IntPtr.Zero, new IntPtr(40 | (40 << 16))); }
            finally { if (GetCapture() == source.Handle) ReleaseCapture(); }
            Check(ReferenceEquals(Mouse.PrimaryDevice.ActiveSource, source), "synthetic mouse reports target only the demo HWND");
            target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent, Source = target });
            Check(Read<bool>(window, "dragging") && ReferenceEquals(Mouse.Captured, Read<Border>(window, "shell")), "music press captures the persistent shell");
            Point origin = target.PointToScreen(new Point(Math.Max(1, target.RenderSize.Width / 2), Math.Max(1, target.RenderSize.Height / 2)));
            Write(window, "motionStart", origin); return origin;
        }
        private static void Release(IslandWindow window, UIElement target)
        {
            target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent, Source = target }); StopTimers(window);
        }
        private static void ReleaseOwnCapture(IslandWindow window)
        {
            var capture = Mouse.Captured as DependencyObject; if (capture != null && Window.GetWindow(capture) == window) Mouse.Capture(null);
        }
        private static void StopTimers(IslandWindow window)
        {
            foreach (FieldInfo field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop();
            var monitor = Read<NowPlayingMonitor>(window, "nowPlaying"); if (monitor != null) monitor.Stop();
            var focus = Read<FocusPanel>(window, "focusPanel"); if (focus != null) focus.Stop();
        }
        private static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        private static T Read<T>(object value, string name) { return (T)value.GetType().GetField(name, Private).GetValue(value); }
        private static void Write(object value, string name, object fieldValue) { value.GetType().GetField(name, Private).SetValue(value, fieldValue); }
        private static void SetProperty(object value, string name, object propertyValue) { value.GetType().GetProperty(name, Private).GetSetMethod(true).Invoke(value, new[] { propertyValue }); }
        private static object Call(object value, string name, params object[] arguments)
        {
            var method = value.GetType().GetMethod(name, Private); if (method == null) throw new Exception("Missing now-playing method: " + name);
            try { return method.Invoke(value, arguments); } catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private static bool Near(double value, double expected) { return Math.Abs(value - expected) < .05; }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Now-playing integration: " + message); }
    }
}
