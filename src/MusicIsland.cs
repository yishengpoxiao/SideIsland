using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class CompactMusicPolicy
    {
        internal static bool Show(Settings settings, MediaSnapshot media, bool focusRunning, DateTime lastPlayingUtc, string lastPlayingIdentity, DateTime now)
        {
            if (settings.CompactMode == "music") return true;
            bool focusSelected = settings.CompactMode == "focus" || (settings.CompactMode == "quota" && (settings.CompactSlot1 == "focus" || settings.CompactSlot2 == "focus"));
            if (!settings.AutoMusic || media == null || !media.Available || (focusRunning && focusSelected)) return false;
            double elapsed = (now - lastPlayingUtc).TotalSeconds;
            return media.Playing || (elapsed >= 0 && elapsed < 12 && NowPlayingMonitor.Identity(media) == lastPlayingIdentity);
        }
    }
    internal partial class IslandWindow
    {
        private NowPlayingMonitor nowPlaying;
        private Grid compactMusicTarget;
        private CompactMusicVisual compactMusic;
        private readonly DispatcherTimer musicUiTimer = new DispatcherTimer();
        private bool musicImporting;
        private CompactMediaController compactMediaController;
        private MediaUiPreferencesStore musicPreferencesStore;
        private MediaUiPreferences musicPreferences = new MediaUiPreferences();
        private bool musicPreferencesSaving;
        private int musicPreferencesRevision;
        private MenuItem musicPreviousMenu, musicToggleMenu, musicNextMenu;
        private bool ShowCompactMusic { get { return ActiveWidgetLayout == null && !TaskActivityOverridesCompact && CompactMusicPolicy.Show(settings, nowPlaying == null ? null : nowPlaying.Snapshot, focusPanel != null && focusPanel.IsRunning,
            nowPlaying == null ? DateTime.MinValue : nowPlaying.LastPlayingUtc, nowPlaying == null ? null : nowPlaying.LastPlayingIdentity, DateTime.UtcNow); } }

        private void BuildCompactMusic()
        {
            compactMusic = new CompactMusicVisual();
            compactMusicTarget = new Grid { Background = Brushes.Transparent, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
            compactMusicTarget.Children.Add(compactMusic); compact.Children.Insert(1, compactMusicTarget);
        }
        private void InitializeNowPlaying()
        {
            nowPlaying = new NowPlayingMonitor(demo);
            musicPreferencesStore = new MediaUiPreferencesStore(demo); musicPreferences = musicPreferencesStore.Load();
            nowPlaying.LyricOffsetMilliseconds = musicPreferences.LyricOffsetMilliseconds;
            nowPlaying.SelectPlayer(musicPreferences.PreferredSourceId);
            mediaPanel.ConfigurePreferredPlayer(musicPreferences.PreferredSourceId);
            mediaPanel.ConfigureLyricDisplay(musicPreferences);
            mediaPanel.LyricDisplayChanged += value => {
                value.PreferredSourceId = musicPreferences.PreferredSourceId;
                musicPreferences = value.Copy(); musicPreferences.Validate();
                nowPlaying.LyricOffsetMilliseconds = musicPreferences.LyricOffsetMilliseconds;
                musicPreferencesRevision++; UpdateMusicVisual(); UpdateMusicLyrics(); SaveMusicPreferences();
            };
            mediaPanel.PlayerSelectionChanged += sourceId => {
                musicPreferences.PreferredSourceId = sourceId; nowPlaying.SelectPlayer(sourceId);
                musicPreferencesRevision++; SaveMusicPreferences();
            };
            mediaPanel.PlayerRefreshRequested += () => { if (nowPlaying != null) nowPlaying.Refresh(); };
            compactMediaController = new CompactMediaController(demo, nowPlaying.SendCommandAsync);
            compactMediaController.StatusChanged += status => { if (!shuttingDown) mediaPanel.SetControlStatus(status); };
            Closed += (s, e) => compactMediaController.Dispose();
            nowPlaying.Changed += () => { if (!shuttingDown) { UpdateToolkitCompact(); UpdateMusicLyrics(); } };
            mediaPanel.LyricsImportRequested += ImportMusicLyrics;
            mediaPanel.LyricsRemoveRequested += RemoveMusicLyrics;
            mediaPanel.RetryLyricsRequested += () => { if (!shuttingDown && nowPlaying != null) nowPlaying.RetryOnlineLyrics(); };
            mediaPanel.AutoMusicChanged += value => { settings.AutoMusic = value; SaveSettings(); SyncNowPlayingActivity(); UpdateToolkitCompact(); };
            mediaPanel.OnlineLyricsChanged += value => { settings.OnlineLyricsEnabled = value; SaveSettings(); SyncNowPlayingActivity(); };
            musicUiTimer.Interval = TimeSpan.FromMilliseconds(250);
            musicUiTimer.Tick += (s, e) => { if (ShowCompactMusic && !expanded && !peeking) UpdateMusicVisual(); if (expanded && toolsPageSelected && selectedTool == "media") UpdateMusicLyrics(); };
        }
        private void SyncNowPlayingActivity()
        {
            if (!CanAnimateCompactMediaContent) SettleCompactMediaContentTransition();
            if (nowPlaying == null) return;
            nowPlaying.EnableOnline(settings.OnlineLyricsEnabled);
            mediaPanel.ConfigureMusicOptions(settings.AutoMusic, settings.OnlineLyricsEnabled);
            bool musicGesture = desktopPreferences != null && (IsMusicGesture(desktopPreferences.ClickAction) || IsMusicGesture(desktopPreferences.MiddleAction) || IsMusicGesture(desktopPreferences.WheelAction));
            bool needed = settings.AutoMusic || settings.CompactMode == "music" || settings.CompactSlot1 == "media" || settings.CompactSlot2 == "media" || WidgetPoolContains("media") || musicGesture || (expanded && toolsPageSelected && selectedTool == "media");
            if (IsLoaded && IsVisible && !shuttingDown && !runtimeSuspended && needed) { nowPlaying.Start(); musicUiTimer.Start(); }
            else { SettleCompactMediaContentTransition(); nowPlaying.Stop(); musicUiTimer.Stop(); }
        }
        private static bool IsMusicGesture(string action) { return action != null && action.StartsWith("media-", StringComparison.Ordinal); }

        private bool CanAnimateCompactMediaContent
        {
            get
            {
                return IsLoaded && IsVisible && Opacity > 0 && !expanded && !peeking &&
                    !visualTransitionActive && !pageTransitionActive && !shuttingDown &&
                    !dragging && !resizing && !runtimeSuspended && compact != null && compact.IsVisible;
            }
        }

        private void SettleCompactMediaContentTransition()
        {
            if (compactMusic != null) compactMusic.SettleContentTransition();
            if (compactWidget1 != null) compactWidget1.SettleContentTransition();
            if (compactWidget2 != null) compactWidget2.SettleContentTransition();
        }

        private void RetainCompactMediaParentClock(int previousRevision, int currentRevision)
        {
            // A mode bitmap may still contain the previous song. Drop only its
            // old pixels; preserve the parent's current alpha and geometry clock.
            if (previousRevision != currentRevision && visualTransitionActive)
                RetainIncomingOpacityForPeekReplacement();
        }

        private void UpdateMusicVisual()
        {
            if (compactMusic == null) return;
            compactMusicTarget.Width = HorizontalDock ? 118 : 59; compactMusicTarget.Height = HorizontalDock ? 62 : 133;
            var media = nowPlaying == null ? new MediaSnapshot() : nowPlaying.Snapshot;
            string lyric = nowPlaying == null ? "" : nowPlaying.Lyric;
            var document = nowPlaying == null ? null : nowPlaying.Lyrics;
            int previousRevision = compactMusic.ContentRevision;
            compactMusic.Update(media, lyric, HorizontalDock, settings.CompactTextDirection, settings.Theme == "light", settings.Language, musicPreferences.LyricFontSize,
                document != null && document.Count > 0, document == null || document.IsSynchronized, CanAnimateCompactMediaContent && ShowCompactMusic);
            RetainCompactMediaParentClock(previousRevision, compactMusic.ContentRevision);
            lyric = compactMusic.Lyric;
            ConfigureCompactActionTarget(compactMusicTarget, "media", media.Available ? media.Title + "\n" + media.Artist + (String.IsNullOrEmpty(lyric) ? "" : "\n" + lyric) : UiLocalization.Localize("打开支持 Windows 系统媒体控制的播放器"));
            compactMusicTarget.ToolTip = (media.Available ? media.Title + "\n" + media.Artist + (String.IsNullOrEmpty(lyric) ? "" : "\n" + lyric) + "\n" : "") + UiLocalization.Localize("点击封面播放 / 暂停 · 滚轮调整系统音量\n两侧小按钮切歌 · 点击文字打开音乐设置 · 按住拖动");
            EnsureCompactMusicMenu();
            musicPreviousMenu.IsEnabled = !demo && CompactMediaController.Supports(media, "previous");
            musicToggleMenu.IsEnabled = !demo && CompactMediaController.Supports(media, "toggle");
            musicNextMenu.IsEnabled = !demo && CompactMediaController.Supports(media, "next");
        }
        private void EnsureCompactMusicMenu()
        {
            if (compactMusicTarget.ContextMenu != null) return;
            var menu = new ContextMenu(); UiTheme.Install(menu.Resources);
            musicPreviousMenu = UiLocalization.Header(new MenuItem(), "上一首"); musicPreviousMenu.Click += (s, e) => TryCompactMusicAction("music-previous"); menu.Items.Add(musicPreviousMenu);
            musicToggleMenu = UiLocalization.Header(new MenuItem(), "播放 / 暂停"); musicToggleMenu.Click += (s, e) => TryCompactMusicAction("music-toggle"); menu.Items.Add(musicToggleMenu);
            musicNextMenu = UiLocalization.Header(new MenuItem(), "下一首"); musicNextMenu.Click += (s, e) => TryCompactMusicAction("music-next"); menu.Items.Add(musicNextMenu);
            menu.Items.Add(new Separator()); var open = UiLocalization.Header(new MenuItem(), "音乐与歌词设置…"); open.Click += (s, e) => OpenCompactTool("media", true); menu.Items.Add(open);
            menu.Closed += (s, e) => QueueInteractionRecheck(); compactMusicTarget.ContextMenu = menu;
        }
        private string FindCompactMusicAction(DependencyObject source, Point islandPosition)
        {
            if (expanded || peeking || !ShowCompactMusic || compactMusicTarget == null || compactMusic == null) return null;
            return IsCompactMusicSource(source) ? compactMusic.ActionAt(TranslatePoint(islandPosition, compactMusicTarget)) : null;
        }
        private bool IsCompactMusicSource(DependencyObject source)
        {
            while (source != null && !ReferenceEquals(source, this))
            {
                if (ReferenceEquals(source, compactMusicTarget)) return true;
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            return false;
        }
        private bool TryCompactMusicAction(string action)
        {
            if (shuttingDown || dragging || resizing || compactMediaController == null || nowPlaying == null || String.IsNullOrEmpty(action) || !action.StartsWith("music-", StringComparison.Ordinal)) return false;
            string command = action.Substring(6);
            if (!CompactMediaController.Supports(nowPlaying.Snapshot, command)) return false;
            // Existing drag handling dispatches this only on a stationary mouse release.
            SendCompactMusicCommand(command); return true;
        }
        private bool TryCompactMusicWheel(DependencyObject source, int delta)
        {
            if (shuttingDown || expanded || peeking || dragging || resizing || !ShowCompactMusic || compactMediaController == null || !IsCompactMusicSource(source)) return false;
            AdjustCompactMusicVolume(delta); return true;
        }
        private async void SendCompactMusicCommand(string command) { await compactMediaController.SendAsync(nowPlaying.Snapshot, command); }
        private async void AdjustCompactMusicVolume(int delta) { await compactMediaController.AdjustVolumeAsync(delta); }
        private async void SaveMusicPreferences()
        {
            if (musicPreferencesSaving) return;
            musicPreferencesSaving = true;
            try
            {
                int saved;
                do {
                    saved = musicPreferencesRevision; var value = musicPreferences.Copy();
                    await System.Threading.Tasks.Task.Run(() => musicPreferencesStore.Save(value));
                } while (!shuttingDown && saved != musicPreferencesRevision);
                if (!shuttingDown) mediaPanel.SetLyricsStatus("歌词显示设置已保存");
            }
            catch (Exception) { if (!shuttingDown) mediaPanel.SetLyricsStatus("歌词显示设置保存失败，请重试"); }
            finally { musicPreferencesSaving = false; }
        }
        private void UpdateMusicLyrics()
        {
            if (mediaPanel == null || nowPlaying == null) return;
            mediaPanel.ShowPlayerSessions(nowPlaying.Snapshot.Players);
            mediaPanel.ShowLyrics(nowPlaying.Snapshot, nowPlaying.Lyric, nowPlaying.Lyrics != null, nowPlaying.LyricsStatus, nowPlaying.Lyrics);
        }
        private async void ImportMusicLyrics(MediaSnapshot track)
        {
            if (demo || musicImporting || nowPlaying == null || !track.Available) return;
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "LRC (*.lrc)|*.lrc", Multiselect = false, CheckFileExists = true, Title = UiLocalization.Localize("为当前歌曲导入 LRC 歌词") };
            if (dialog.ShowDialog(this) != true) return;
            musicImporting = true; mediaPanel.SetLyricsBusy(true);
            try { await nowPlaying.ImportLyricsAsync(track, dialog.FileName); mediaPanel.SetLyricsStatus("已导入当前歌曲的同步歌词"); }
            catch (Exception) { mediaPanel.SetLyricsStatus("歌词导入失败：需要带时间戳的 LRC 文件（最大 256 KB）"); }
            finally { musicImporting = false; if (!shuttingDown) { mediaPanel.SetLyricsBusy(false); UpdateMusicLyrics(); } }
        }
        private async void RemoveMusicLyrics(MediaSnapshot track)
        {
            if (demo || musicImporting || nowPlaying == null || !track.Available) return;
            musicImporting = true; mediaPanel.SetLyricsBusy(true);
            try { await nowPlaying.RemoveLyricsAsync(track); mediaPanel.SetLyricsStatus("已清除当前歌曲的歌词"); }
            catch (Exception) { mediaPanel.SetLyricsStatus("歌词移除失败，请稍后重试"); }
            finally { musicImporting = false; if (!shuttingDown) { mediaPanel.SetLyricsBusy(false); UpdateMusicLyrics(); } }
        }
    }
}
