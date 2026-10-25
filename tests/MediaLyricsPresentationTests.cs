using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // Only this fixture's window and injected media/audio are used. No lyrics
    // service, clipboard, native screen capture or real player is contacted.
    internal static class MediaLyricsPresentationTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run()
        {
            string language = UiLocalization.Language, theme = UiTheme.Name;
            var context = SynchronizationContext.Current; MediaPanel panel = null; Window window = null;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                AppAppearance.Configure("zh", "dark");
                var track = new MediaSnapshot { Available = true, Title = "设置", Artist = "开始", Source = "Synthetic player", Playing = false,
                    Duration = TimeSpan.Zero, Position = TimeSpan.Zero, SampledAtUtc = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
                panel = new MediaPanel(false, () => new AudioSnapshot(), () => Task.FromResult(track));
                panel.ConfigureMusicOptions(false, false);
                window = new Window { Content = new ScrollViewer { Content = panel }, Width = 540, Height = 800, ShowActivated = false, ShowInTaskbar = false, IsHitTestVisible = false, Title = "Lyrics presentation fixture" };
                AppAppearance.Register(window); window.Show(); panel.Start(); Layout(window); Call(panel, "RenderMedia", track);
                var document = LrcDocument.Parse("[00:01]设置\n[00:03]保存设置\n[00:06]原文 😀");
                panel.ShowLyrics(track, "设置", true, "已加载歌词，但播放器未提供可用播放进度，无法同步", document); Layout(window);
                var complete = Field<Expander>(panel, "fullLyrics");
                Check(complete.Visibility == Visibility.Visible && !complete.IsExpanded && Field<TextBox>(panel, "fullLyricsText") == null, "full lyrics stay collapsed and do not allocate the document editor until requested");
                Check(Field<TextBlock>(panel, "lyricsLine").Text != "设置" && Field<TextBlock>(panel, "lyricsTimelineStatus").Text.Contains("未提供可靠进度"), "zero duration and invalid timestamps never masquerade as a synchronized first lyric");
                complete.IsExpanded = true; Layout(window);
                var fullText = Field<TextBox>(panel, "fullLyricsText"); string expected = String.Join(Environment.NewLine, new[] { "设置", "保存设置", "原文 😀" });
                Check(fullText != null && fullText.IsReadOnly && fullText.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && fullText.Text == expected && UiLocalization.IsProtected(fullText), "expanded full lyrics are scrollable, verbatim and protected external content");
                AppAppearance.Configure("en", "light"); panel.RefreshAppearance(); Layout(window);
                Check((string)complete.Header == "Read full lyrics" && Field<TextBlock>(panel, "lyricsTimelineStatus").Text.Contains("reliable progress"), "visible full-lyric controls and missing-progress explanation translate to English");
                Check(fullText.Text == expected && Field<TextBlock>(panel, "title").Text == "设置", "lyrics and song names matching UI labels are never translated");

                string plain = "开始" + Environment.NewLine + "保存设置" + Environment.NewLine + "Plain words 😀";
                track.Duration = TimeSpan.FromMinutes(3); track.SampledAtUtc = DateTime.UtcNow; Call(panel, "RenderMedia", track);
                var plainDocument = LrcDocument.FromPlainText(plain);
                panel.ShowLyrics(track, "开始", true, "已加载纯文本歌词，点击查看完整歌词，无法逐句同步", plainDocument); Layout(window);
                Check(fullText.Text == plain && Field<TextBlock>(panel, "lyricsLine").Text != "开始" && Field<TextBlock>(panel, "lyricsTimelineStatus").Text.Contains("plain lyrics"), "plain lyrics are readable in full without pretending to synchronize even when player progress is present");
                panel.SetLyricsStatus("Fixture action message"); panel.ShowLyrics(track, "", false, "未匹配到歌词或服务暂不可用，将稍后重试", null); Layout(window);
                Check(complete.Visibility == Visibility.Collapsed && fullText.Text.Length == 0 && Field<TextBlock>(panel, "lyricsMatchStatus").Text.Contains("service is unavailable"), "match failures show a reason and immediately remove a previous song document");
                Check(Field<TextBlock>(panel, "lyricsLine").Text.Contains("service is unavailable"), "the main visible song card also explains matching failure without requiring a scroll to settings");
                Check(Field<TextBlock>(panel, "lyricsStatus").Text == "Fixture action message", "background lyric state does not overwrite separate import or display-setting feedback");
                panel.ShowLyrics(track, "", false, "正在匹配在线歌词", null); Layout(window);
                Check(Field<TextBlock>(panel, "lyricsMatchStatus").Text == "Matching online lyrics", "pending matching is explicit in the actual UI text");

                int retries = 0; panel.RetryLyricsRequested += () => retries++; var retry = Field<Button>(panel, "retryLyrics");
                Check(retry.IsEnabled && (string)retry.Content == "Retry lyric matching", "a current synthetic media session exposes the retry action");
                retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(retries == 1 && Field<CheckBox>(panel, "onlineLyrics").IsChecked == false, "explicit retry raises its event without changing the online permission switch");

                track.Position = TimeSpan.FromSeconds(2); Call(panel, "RenderMedia", track);
                panel.ShowLyrics(track, "设置", true, "本地歌词已加载，按播放器进度显示", document); Layout(window);
                Check(Field<TextBlock>(panel, "lyricsLine").Text == "设置" && Field<TextBlock>(panel, "lyricsMatchStatus").Text.Contains("Local lyrics loaded"), "local synchronized lyrics remain visible with online matching disabled");
                track.Position = TimeSpan.Zero; Call(panel, "RenderMedia", track);
                panel.ShowLyrics(track, "", true, "歌词已加载，尚未到首句", document); Layout(window);
                Check(Field<TextBlock>(panel, "lyricsMatchStatus").Text.Contains("Waiting for the first line") && Field<TextBlock>(panel, "lyricsLine").Text.Length > 0, "pre-roll is explained instead of leaving an unexplained empty lyric");

                var compact = new CompactMusicVisual(); track.Duration = TimeSpan.Zero; track.SampledAtUtc = new DateTime(1601, 1, 1);
                compact.Update(track, "设置", true, "auto", false, "zh", 10, true, true);
                Check(compact.Lyric.Length == 0 && !Field<bool>(compact, "timelineAvailable"), "compact music hides missing-progress explanations and unusable timeline without exposing a fake first line");
                compact.Update(track, "设置", false, "mirror", true, "en", 10, true, true);
                Check(compact.Lyric.Length == 0 && !Field<bool>(compact, "timelineAvailable"), "compact English and mirrored vertical cards also stay free of missing-progress diagnostics");
                track.Duration = TimeSpan.FromMinutes(3); track.SampledAtUtc = DateTime.UtcNow;
                compact.Update(track, "开始", true, "auto", true, "en", 10, true, false);
                Check(compact.Lyric.Length == 0 && Field<bool>(compact, "timelineAvailable"), "compact plain lyrics stay hidden while the real player progress remains available");
                compact.Update(track, "设置", true, "auto", true, "en", 10, true, true);
                Check(compact.Lyric == "设置", "real timed lyric content remains verbatim in English compact mode");
                AppAppearance.Configure("zh", "dark"); panel.RefreshAppearance(); complete.IsExpanded = true; Layout(window);
                Check((string)retry.Content == "重试歌词匹配" && (string)complete.Header == "展开查看完整歌词" && fullText.Text == expected, "Chinese-English round trips preserve full document content and translate only the controls");
                Console.WriteLine("PASS: expanded lyric reasons and retry action, lazy full lyrics, clean compact no-progress/plain-text display, protected original text and bilingual/theme presentation.");
            }
            finally
            {
                if (panel != null) panel.Dispose(); if (window != null) window.Close();
                AppAppearance.Configure(language, theme); SynchronizationContext.SetSynchronizationContext(context);
            }
        }
        private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Private).GetValue(target); }
        private static void Call(object target, string name, params object[] args) { target.GetType().GetMethod(name, Private).Invoke(target, args); }
        private static void Layout(Window window)
        {
            window.UpdateLayout(); var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); window.UpdateLayout();
        }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception("Lyrics presentation: " + message); }
    }
}
