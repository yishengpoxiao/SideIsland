using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class MediaPanel : StackPanel, IDisposable
    {
        private readonly bool demo;
        private readonly Func<AudioSnapshot> audioReader;
        private readonly Func<Task<MediaSnapshot>> mediaReader;
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private MediaService service;
        private bool running, disposed, reading, controlling, rendering, dirtyVolume;
        private int generation, audioRevision;
        private readonly TextBlock title, artist, source, mediaStatus, time, volumeLabel, outputStatus, microphoneStatus, actionStatus;
        private readonly ProgressBar progress;
        private readonly Slider volume;
        private readonly Button previous, toggle, next, mute, microphone, applyVolume;
        private MediaSnapshot media = new MediaSnapshot();
        private AudioSnapshot audio = new AudioSnapshot();
        private readonly Image cover;
        private readonly ColumnDefinition artworkColumn;
        private readonly TextBlock lyricsLine, lyricsStatus, lyricsMatchStatus, lyricsTimelineStatus;
        private readonly Button importLyrics, removeLyrics, retryLyrics;
        private readonly Expander fullLyrics;
        private TextBox fullLyricsText;
        private LrcDocument lyricsDocument, renderedLyricsDocument;
        private string lyricMatchReason = "";
        private bool legacyLyricsCall;
        private readonly CheckBox automaticMusic, onlineLyrics;
        private readonly TextBox lyricFontInput, lyricOffsetInput;
        private readonly ComboBox playerSelection;
        private readonly TextBlock playerSelectionStatus;
        private string preferredSourceId = "", playerOptionsKey;
        private MediaPlayerOption[] displayedPlayerOptions = new MediaPlayerOption[0];
        private bool updatingPlayerSelection;
        private sealed class PlayerSelectionItem { public string SourceId { get; set; } public string Label { get; set; } }
        private bool updatingOptions, lyricsBusy, hasLyrics;
        private MediaSnapshot lyricsTrack;
        private string currentLyric = "";
        public event Action<MediaSnapshot> LyricsImportRequested, LyricsRemoveRequested;
        public event Action<bool> AutoMusicChanged, OnlineLyricsChanged;
        public event Action<MediaUiPreferences> LyricDisplayChanged;
        public event Action<string> PlayerSelectionChanged;
        public event Action PlayerRefreshRequested;
        public event Action RetryLyricsRequested;

        public MediaPanel(bool demoMode) : this(demoMode, AudioService.Read, null) { }

        internal MediaPanel(bool demoMode, Func<AudioSnapshot> readAudio, Func<Task<MediaSnapshot>> readMedia)
        {
            demo = demoMode;
            audioReader = readAudio;
            mediaReader = readMedia;
            var heading = IslandWindow.Text("音乐与声音", 13, "#EFF8F3", true); heading.Margin = new Thickness(0, 0, 0, 8); Children.Add(heading);
            var playerOptions = new StackPanel(); playerOptions.Children.Add(Label("跟随播放器", 12, "#DDE9E2"));
            var playerLabel = new FrameworkElementFactory(typeof(TextBlock));
            playerLabel.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Label"));
            playerLabel.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Label"));
            playerLabel.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            playerSelection = new ComboBox { MinHeight = 32, FontSize = 11, Margin = new Thickness(0, 6, 0, 6), ItemTemplate = new DataTemplate { VisualTree = playerLabel }, MaxDropDownHeight = 260 };
            playerSelection.SelectionChanged += (s, e) => {
                if (updatingPlayerSelection) return; var choice = playerSelection.SelectedItem as PlayerSelectionItem; if (choice == null || choice.SourceId == preferredSourceId) return;
                ConfigurePreferredPlayer(choice.SourceId); var handler = PlayerSelectionChanged; if (handler != null) handler(choice.SourceId);
            };
            playerOptions.Children.Add(playerSelection);
            var refreshPlayers = IslandWindow.Button("刷新播放器列表", () => { Refresh(); var handler = PlayerRefreshRequested; if (handler != null) handler(); }); playerOptions.Children.Add(PanelActionLayout.Create(1, refreshPlayers));
            playerSelectionStatus = Label("自动选择正在播放的媒体会话。", 10, "#879690"); playerSelectionStatus.TextWrapping = TextWrapping.Wrap; playerSelectionStatus.Margin = new Thickness(0, 6, 0, 0); playerOptions.Children.Add(playerSelectionStatus);
            var playerHint = Label("列表来自 Windows 系统媒体会话。指定应用暂时退出时保持等待，不会改为控制浏览器或其他播放器。", 10, "#879690"); playerHint.TextWrapping = TextWrapping.Wrap; playerHint.Margin = new Thickness(0, 7, 0, 0); playerOptions.Children.Add(playerHint); Children.Add(Card(playerOptions));
            var music = new StackPanel();
            var metadata = new StackPanel(); var trackHeader = new Grid();
            artworkColumn = new ColumnDefinition { Width = new GridLength(74) }; trackHeader.ColumnDefinitions.Add(artworkColumn); trackHeader.ColumnDefinitions.Add(new ColumnDefinition());
            cover = new Image { Width = 62, Height = 62, Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Top, Clip = new RectangleGeometry(new Rect(0, 0, 62, 62), 10, 10) };
            trackHeader.Children.Add(cover); Grid.SetColumn(metadata, 1); trackHeader.Children.Add(metadata); music.Children.Add(trackHeader);
            source = Label(UiLocalization.Localize("系统媒体"), 9, "#A1EACF", false); source.TextTrimming = TextTrimming.CharacterEllipsis; metadata.Children.Add(source);
            title = Label(UiLocalization.Localize("暂无正在播放的媒体"), 17, "#F0F5F2", false); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 6, 0, 6); title.TextWrapping = TextWrapping.Wrap; metadata.Children.Add(title);
            artist = Label(UiLocalization.Localize("打开音乐或视频应用后自动显示"), 11, "#9AA9A1", false); artist.TextTrimming = TextTrimming.CharacterEllipsis; metadata.Children.Add(artist);
            lyricsLine = Label("", 13, "#A1EACF", false); lyricsLine.TextWrapping = TextWrapping.Wrap; lyricsLine.Margin = new Thickness(0, 8, 0, 0); music.Children.Add(lyricsLine);
            progress = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, Foreground = Display.Brush("#A1EACF"), Background = Display.Brush("#30383B"), BorderThickness = new Thickness(0), Margin = new Thickness(0, 8, 0, 6) }; music.Children.Add(progress);
            time = Label("— / —", 9, "#879690"); music.Children.Add(time);
            previous = IslandWindow.Button("上一首", () => Send("previous")); toggle = IslandWindow.Button("播放", () => Send("toggle")); next = IslandWindow.Button("下一首", () => Send("next"));
            var controls = PanelActionLayout.Create(3, previous, toggle, next); controls.Margin = new Thickness(0, 8, 0, 6); music.Children.Add(controls);
            mediaStatus = Label("正在获取媒体状态", 10, "#879690"); mediaStatus.TextWrapping = TextWrapping.Wrap; music.Children.Add(mediaStatus); Children.Add(Card(music));

            var lyricOptions = new StackPanel();
            automaticMusic = UiLocalization.Content(new CheckBox { Foreground = Display.Brush("#DDE9E2"), Margin = new Thickness(0, 0, 0, 6) }, "播放时自动显示音乐卡片（专注计时优先）"); lyricOptions.Children.Add(automaticMusic);
            onlineLyrics = UiLocalization.Content(new CheckBox { Foreground = Display.Brush("#DDE9E2"), Margin = new Thickness(0, 0, 0, 8) }, "在线匹配歌词（发送歌名和歌手到 LRCLIB）"); lyricOptions.Children.Add(onlineLyrics);
            WrapOption(automaticMusic, Double.PositiveInfinity); WrapOption(onlineLyrics, Double.PositiveInfinity);
            automaticMusic.Checked += (s, e) => ChangeMusicOption(false); automaticMusic.Unchecked += (s, e) => ChangeMusicOption(false);
            onlineLyrics.Checked += (s, e) => ChangeMusicOption(true); onlineLyrics.Unchecked += (s, e) => ChangeMusicOption(true);
            importLyrics = IslandWindow.Button("导入 LRC", () => { var handler = LyricsImportRequested; if (handler != null) handler(media); });
            removeLyrics = IslandWindow.Button("清除当前歌词", () => { var handler = LyricsRemoveRequested; if (handler != null) handler(media); });
            retryLyrics = IslandWindow.Button("重试歌词匹配", () => { var handler = RetryLyricsRequested; if (handler != null) handler(); });
            var lyricButtons = PanelActionLayout.Create(3, importLyrics, removeLyrics, retryLyrics); lyricButtons.Margin = new Thickness(0, 0, 0, 6); lyricOptions.Children.Add(lyricButtons);
            var lyricHint = Label("Windows 不提供统一歌词；可在线匹配或导入本地 LRC。歌曲信息不完整或无匹配歌词时，仍显示歌名和封面。", 10, "#879690"); lyricHint.TextWrapping = TextWrapping.Wrap; lyricOptions.Children.Add(lyricHint);
            lyricsMatchStatus = Label("", 10, "#879690"); lyricsMatchStatus.TextWrapping = TextWrapping.Wrap; lyricsMatchStatus.Margin = new Thickness(0, 7, 0, 0); lyricOptions.Children.Add(lyricsMatchStatus);
            lyricsTimelineStatus = Label("", 10, "#879690"); lyricsTimelineStatus.TextWrapping = TextWrapping.Wrap; lyricsTimelineStatus.Margin = new Thickness(0, 7, 0, 0); lyricOptions.Children.Add(lyricsTimelineStatus);
            fullLyrics = new Expander { Foreground = Display.Brush("#DDE9E2"), Margin = new Thickness(0, 10, 0, 0), IsExpanded = false, Visibility = Visibility.Collapsed };
            UiLocalization.Mark(fullLyrics, HeaderedContentControl.HeaderProperty, "展开查看完整歌词"); fullLyrics.Expanded += (s, e) => RenderFullLyrics(); lyricOptions.Children.Add(fullLyrics);
            lyricsStatus = Label("", 10, "#879690"); lyricsStatus.TextWrapping = TextWrapping.Wrap; lyricsStatus.Margin = new Thickness(0, 6, 0, 0); lyricsStatus.Visibility = Visibility.Collapsed; lyricOptions.Children.Add(lyricsStatus); Children.Add(Card(lyricOptions));

            var displayOptions = new StackPanel();
            displayOptions.Children.Add(Label("歌词显示", 12, "#DDE9E2"));
            var lyricFields = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            lyricFields.ColumnDefinitions.Add(new ColumnDefinition()); lyricFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); lyricFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            lyricFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); lyricFields.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) }); lyricFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var fontLabel = Label("歌词字号（9–16）", 10, "#9AA9A1"); fontLabel.VerticalAlignment = VerticalAlignment.Center; fontLabel.TextWrapping = TextWrapping.Wrap; lyricFields.Children.Add(fontLabel);
            lyricFontInput = LyricInput("10", 76, 2); Grid.SetColumn(lyricFontInput, 2); lyricFields.Children.Add(lyricFontInput);
            var offsetLabel = Label("同步偏移（毫秒）", 10, "#9AA9A1"); offsetLabel.VerticalAlignment = VerticalAlignment.Center; offsetLabel.TextWrapping = TextWrapping.Wrap; Grid.SetRow(offsetLabel, 2); lyricFields.Children.Add(offsetLabel);
            lyricOffsetInput = LyricInput("0", 76, 6); Grid.SetColumn(lyricOffsetInput, 2); Grid.SetRow(lyricOffsetInput, 2); lyricFields.Children.Add(lyricOffsetInput); displayOptions.Children.Add(lyricFields);
            var saveDisplay = IslandWindow.Button("应用歌词显示", ApplyLyricDisplay); displayOptions.Children.Add(PanelActionLayout.Create(1, saveDisplay));
            var displayHint = Label("正数让歌词提前，负数让歌词延后；范围 ±10000 毫秒。收起卡片上滚轮调音量，点击封面播放 / 暂停。", 10, "#879690"); displayHint.TextWrapping = TextWrapping.Wrap; displayHint.Margin = new Thickness(0, 9, 0, 0); displayOptions.Children.Add(displayHint);
            Children.Add(Card(displayOptions));
            lyricFontInput.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { ApplyLyricDisplay(); e.Handled = true; } };
            lyricOffsetInput.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { ApplyLyricDisplay(); e.Handled = true; } };

            var audio = new StackPanel();
            var volumeRow = new Grid(); volumeRow.ColumnDefinitions.Add(new ColumnDefinition()); volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            volumeRow.Children.Add(Label("系统音量", 12, "#DDE9E2")); volumeLabel = Label("—", 12, "#A1EACF"); Grid.SetColumn(volumeLabel, 1); volumeRow.Children.Add(volumeLabel); audio.Children.Add(volumeRow);
            volume = new Slider { Minimum = 0, Maximum = 100, TickFrequency = 1, SmallChange = 1, LargeChange = 5, IsSnapToTickEnabled = true, Margin = new Thickness(0, 12, 0, 8) };
            volume.ValueChanged += (s, e) => { if (!rendering) { dirtyVolume = true; volumeLabel.Text = Math.Round(volume.Value) + "% · 待应用"; applyVolume.IsEnabled = !demo && running; } };
            audio.Children.Add(volume);
            applyVolume = IslandWindow.Button("应用音量", ApplyVolume);
            mute = IslandWindow.Button("静音输出", () => ChangeMute(false)); audio.Children.Add(PanelActionLayout.Create(2, applyVolume, mute));
            outputStatus = Label("正在检测音频设备", 10, "#879690"); outputStatus.Margin = new Thickness(0, 10, 0, 0); outputStatus.TextWrapping = TextWrapping.Wrap; audio.Children.Add(outputStatus);
            audio.Children.Add(new Border { Height = 1, Background = Display.Brush("#30383B"), Margin = new Thickness(0, 8, 0, 8) });
            microphoneStatus = Label("正在检测默认通信麦克风", 11, "#C6D1CB"); microphoneStatus.TextWrapping = TextWrapping.Wrap; audio.Children.Add(microphoneStatus);
            microphone = IslandWindow.Button("静音麦克风", () => ChangeMute(true)); var microphoneActions = PanelActionLayout.Create(1, microphone); microphoneActions.Margin = new Thickness(0, 6, 0, 0); audio.Children.Add(microphoneActions);
            var microphoneHint = Label("控制 Windows 默认通信麦克风；其他输入设备请在应用内设置。", 9, "#879690"); microphoneHint.Margin = new Thickness(0, 9, 0, 0); microphoneHint.TextWrapping = TextWrapping.Wrap; audio.Children.Add(microphoneHint);
            Children.Add(Card(audio));
            actionStatus = Label(demo ? "演示数据 · 所有真实控制均已禁用" : "媒体应用需支持 Windows 系统媒体控制。", 10, "#879690"); actionStatus.TextWrapping = TextWrapping.Wrap; Children.Add(actionStatus);
            timer.Interval = TimeSpan.FromSeconds(2); timer.Tick += OnTick;
            RenderMedia(media); RenderAudio(new AudioSnapshot());
        }

        private static TextBlock Label(string text, double size, string color, bool localize = true) { return IslandWindow.Text(text, size, color, false, localize); }
        private static TextBox LyricInput(string value, double width, int maxLength)
        { return new TextBox { Text = value, Width = width, MaxLength = maxLength, Padding = new Thickness(6, 5, 6, 5), Background = Display.Brush("#1D2827"), Foreground = Display.Brush("#E0ECE5"), BorderBrush = Display.Brush("#384843"), VerticalContentAlignment = VerticalAlignment.Center }; }
        internal static void WrapOption(CheckBox control, double width)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding()); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); text.SetValue(TextBlock.MaxWidthProperty, width);
            control.ContentTemplate = new DataTemplate { VisualTree = text };
        }
        private static Border Card(UIElement content) { return ToolStyle.Card(content); }

        public void Start()
        {
            if (disposed || running) return;
            running = true; generation++;
            if (demo)
            {
                RenderMedia(new MediaSnapshot { Available = true, Title = "夜色与微风", Artist = "演示音乐 · 不是真实播放内容", Source = "DEMO · 系统媒体", Status = "正在播放 · 演示", Playing = true, Position = TimeSpan.FromSeconds(81), Duration = TimeSpan.FromSeconds(228) });
                RenderAudio(new AudioSnapshot { OutputAvailable = true, Volume = 42, OutputStatus = "演示输出设备", MicrophoneAvailable = true, MicrophoneMuted = true, MicrophoneStatus = "演示麦克风已静音" });
                return;
            }
            service = new MediaService { PreferredSourceId = preferredSourceId }; timer.Start(); Refresh();
        }

        public void Stop()
        {
            if (!running) return;
            running = false; generation++; timer.Stop();
            if (service != null) { service.Dispose(); service = null; }
            UpdateControls(); volume.IsEnabled = mute.IsEnabled = microphone.IsEnabled = applyVolume.IsEnabled = false;
        }

        private void OnTick(object sender, EventArgs e) { Refresh(); }
        private async void Refresh()
        {
            if (!running || disposed || demo || reading || controlling) return;
            reading = true; int currentGeneration = generation, currentAudioRevision = audioRevision;
            var currentService = service;
            try
            {
                // Endpoint activation can wait on an audio driver. WinRT reads also have
                // synchronous portions, so keep the entire sampling path off Dispatcher.
                var audioSnapshot = await Task.Run(audioReader);
                if (!running || disposed || generation != currentGeneration) return;
                if (!controlling && currentAudioRevision == audioRevision) RenderAudio(audioSnapshot);
                var snapshot = await Task.Run(() => mediaReader == null ? currentService.ReadAsync() : mediaReader());
                if (running && !disposed && generation == currentGeneration) RenderMedia(snapshot);
            }
            catch (Exception)
            {
                if (running && !disposed && generation == currentGeneration)
                    mediaStatus.Text = "媒体或音频设备暂不可用，稍后重试";
            }
            finally
            {
                reading = false;
                // A pane may be reopened while the old device read is still finishing.
                // Retry immediately instead of waiting for the next two-second tick.
                if (running && !disposed && currentGeneration != generation) Refresh();
            }
        }

        private void RenderMedia(MediaSnapshot value)
        {
            media = value;
            UpdatePlayerOptions(value.Players);
            RenderMediaContent(value);
        }
        private void RenderMediaContent(MediaSnapshot value)
        {
            cover.Source = value.Artwork;
            cover.Visibility = value.Artwork == null ? Visibility.Collapsed : Visibility.Visible; artworkColumn.Width = new GridLength(value.Artwork == null ? 0 : 74);
            // Available metadata is external content, even when a title happens to equal an app command.
            title.Text = !value.Available && value.Title == "暂无正在播放的媒体" ? UiLocalization.Localize(value.Title) : value.Title;
            artist.Text = !value.Available && value.Artist == "打开支持 Windows 媒体控制的音乐或视频应用" ? UiLocalization.Localize(value.Artist) : value.Artist; artist.ToolTip = artist.Text; title.ToolTip = title.Text;
            source.Text = !value.Available && value.Source == "系统媒体" ? UiLocalization.Localize(value.Source) : value.Source; source.ToolTip = source.Text;
            mediaStatus.Text = value.Status; progress.Value = MediaSnapshot.Progress(value.Position, value.Duration);
            time.Text = value.Duration > TimeSpan.Zero ? Clock(value.Position) + " / " + Clock(value.Duration) : "此媒体未提供时长";
            toggle.Content = value.Playing ? "暂停" : "播放"; RenderLyrics(); UpdateControls();
        }

        internal void ConfigureMusicOptions(bool automatic, bool online)
        {
            updatingOptions = true;
            try { automaticMusic.IsChecked = automatic; onlineLyrics.IsChecked = online; }
            finally { updatingOptions = false; }
        }
        internal void ConfigurePreferredPlayer(string sourceId)
        {
            sourceId = MediaSessionSelection.NormalizeSource(sourceId);
            if (sourceId == preferredSourceId) { UpdatePlayerOptions(displayedPlayerOptions); return; }
            preferredSourceId = sourceId; generation++; playerOptionsKey = null;
            if (service != null) service.PreferredSourceId = sourceId;
            RenderMedia(new MediaSnapshot { Players = displayedPlayerOptions, PreferredSourceId = sourceId, Status = sourceId.Length == 0 ? "正在获取媒体状态" : "指定播放器暂无媒体会话，正在等待；不会控制其他应用" });
            if (running && !demo) Refresh();
        }
        private void UpdatePlayerOptions(MediaPlayerOption[] players)
        {
            players = players ?? new MediaPlayerOption[0];
            displayedPlayerOptions = players;
            string key = preferredSourceId + "\n" + UiLocalization.Language + "\n" + String.Join("\n", players.Where(item => item != null).Select(item => (item.SourceId ?? "") + ":" + item.Playing));
            if (key != playerOptionsKey)
            {
                var choices = new List<PlayerSelectionItem> { new PlayerSelectionItem { SourceId = "", Label = UiLocalization.Localize("自动选择（跟随正在播放的会话）") } };
                foreach (var player in players.Where(item => item != null && !String.IsNullOrEmpty(item.SourceId)).GroupBy(item => item.SourceId, StringComparer.Ordinal).Select(group => group.First()).Take(64))
                    choices.Add(new PlayerSelectionItem { SourceId = player.SourceId, Label = player.SourceId + (player.Playing ? " · " + UiLocalization.Localize("播放中") : "") });
                if (preferredSourceId.Length > 0 && !choices.Any(item => item.SourceId == preferredSourceId)) choices.Add(new PlayerSelectionItem { SourceId = preferredSourceId, Label = preferredSourceId + " · " + UiLocalization.Localize("当前无会话") });
                updatingPlayerSelection = true;
                try { playerSelection.ItemsSource = choices; playerSelection.SelectedItem = choices.First(item => item.SourceId == preferredSourceId); playerOptionsKey = key; }
                finally { updatingPlayerSelection = false; }
            }
            playerSelectionStatus.Text = preferredSourceId.Length == 0 ? UiLocalization.Localize("自动选择正在播放的媒体会话。") : UiLocalization.Localize("已锁定播放器：") + preferredSourceId + (players.Any(item => item != null && item.SourceId == preferredSourceId) ? "" : "\n" + UiLocalization.Localize("指定播放器暂无媒体会话，正在等待；不会控制其他应用"));
        }
        internal void ShowPlayerSessions(MediaPlayerOption[] players) { UpdatePlayerOptions(players); }
        private void ChangeMusicOption(bool online)
        {
            if (updatingOptions) return;
            var handler = online ? OnlineLyricsChanged : AutoMusicChanged;
            if (handler != null) handler(online ? onlineLyrics.IsChecked == true : automaticMusic.IsChecked == true);
        }
        internal void ShowLyrics(MediaSnapshot track, string text, bool available, string status = null, LrcDocument document = null)
        {
            if (!ReferenceEquals(lyricsDocument, document) || NowPlayingMonitor.Identity(lyricsTrack) != NowPlayingMonitor.Identity(track))
            { renderedLyricsDocument = null; if (fullLyricsText != null) fullLyricsText.Clear(); }
            lyricsTrack = track; currentLyric = text ?? ""; hasLyrics = available; lyricMatchReason = status ?? ""; lyricsDocument = document;
            legacyLyricsCall = status == null && document == null; RenderLyrics();
        }
        private void RenderLyrics()
        {
            bool match = media.Available && lyricsTrack != null && NowPlayingMonitor.Identity(media) == NowPlayingMonitor.Identity(lyricsTrack);
            bool documentAvailable = match && lyricsDocument != null && lyricsDocument.Count > 0;
            bool canSynchronize = documentAvailable && lyricsDocument.IsSynchronized && lyricsTrack.HasUsableTimeline;
            if (match && hasLyrics && (legacyLyricsCall || canSynchronize))
                lyricsLine.Text = currentLyric.Length > 0 ? currentLyric : UiLocalization.Localize(lyricMatchReason.Length > 0 ? lyricMatchReason : "尚未到首句歌词，或当前时刻没有歌词");
            else if (documentAvailable) lyricsLine.Text = UiLocalization.Localize(!lyricsTrack.HasUsableTimeline ? "播放器未提供进度，可展开阅读完整歌词" : "纯文本歌词可展开阅读，不能逐句同步");
            else lyricsLine.Text = UiLocalization.Localize(match && lyricMatchReason.Length > 0 ? lyricMatchReason : "暂无同步歌词");
            lyricsMatchStatus.Text = !media.Available ? "未播放媒体，等待歌词" : !match ? "正在等待当前歌曲的歌词状态" : lyricMatchReason.Length > 0 ? lyricMatchReason :
                hasLyrics ? "歌词已加载" : onlineLyrics.IsChecked == true ? "等待匹配在线歌词" : "在线歌词已关闭，可导入本地 LRC";
            lyricsTimelineStatus.Text = !documentAvailable ? "" : !lyricsTrack.HasUsableTimeline ? "播放器未提供可靠进度，无法逐句同步；展开后仍可阅读完整歌词。" :
                !lyricsDocument.IsSynchronized ? "找到的是纯文本歌词，不能逐句同步；展开后可阅读完整歌词。" : "";
            lyricsTimelineStatus.Visibility = lyricsTimelineStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            fullLyrics.Visibility = documentAvailable ? Visibility.Visible : Visibility.Collapsed;
            if (documentAvailable && fullLyrics.IsExpanded) RenderFullLyrics();
            else if (!documentAvailable && fullLyricsText != null) { fullLyricsText.Clear(); renderedLyricsDocument = null; }
            importLyrics.IsEnabled = running && !disposed && !demo && media.Available && !lyricsBusy;
            removeLyrics.IsEnabled = importLyrics.IsEnabled && match && hasLyrics;
            retryLyrics.IsEnabled = importLyrics.IsEnabled;
        }
        private void RenderFullLyrics()
        {
            if (!fullLyrics.IsExpanded || lyricsDocument == null || !media.Available || NowPlayingMonitor.Identity(media) != NowPlayingMonitor.Identity(lyricsTrack)) return;
            if (fullLyricsText == null)
            {
                fullLyricsText = UiLocalization.Protect(new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 210,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Background = Display.Brush("#1D2827"), Foreground = Display.Brush("#E0ECE5"), BorderBrush = Display.Brush("#384843"), Padding = new Thickness(10), Margin = new Thickness(0, 8, 0, 0), FontSize = lyricsLine.FontSize });
                fullLyrics.Content = fullLyricsText;
            }
            if (ReferenceEquals(renderedLyricsDocument, lyricsDocument)) return;
            fullLyricsText.Text = String.Join(Environment.NewLine, lyricsDocument.Lines.Select(line => line.Text)); renderedLyricsDocument = lyricsDocument;
        }
        internal void SetLyricsBusy(bool value) { lyricsBusy = value; RenderLyrics(); }
        internal void SetLyricsStatus(string value) { lyricsStatus.Text = value; lyricsStatus.Visibility = String.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible; }
        internal void SetControlStatus(string value) { actionStatus.Text = value; }
        internal void ConfigureLyricDisplay(MediaUiPreferences value)
        {
            value = value.Copy(); value.Validate();
            lyricFontInput.Text = value.LyricFontSize.ToString(CultureInfo.InvariantCulture);
            lyricOffsetInput.Text = value.LyricOffsetMilliseconds.ToString(CultureInfo.InvariantCulture);
            lyricsLine.FontSize = value.LyricFontSize;
            if (fullLyricsText != null) fullLyricsText.FontSize = value.LyricFontSize;
        }
        private void ApplyLyricDisplay()
        {
            int size, offset;
            if (!Int32.TryParse(lyricFontInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out size) || size < 9 || size > 16 ||
                !Int32.TryParse(lyricOffsetInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) || offset < -10000 || offset > 10000)
            { SetLyricsStatus("请输入 9–16 的整数字号，以及 -10000 到 10000 的同步偏移。"); return; }
            var value = new MediaUiPreferences { LyricFontSize = size, LyricOffsetMilliseconds = offset };
            ConfigureLyricDisplay(value); var handler = LyricDisplayChanged; if (handler != null) handler(value);
        }

        internal void RefreshAppearance()
        {
            // The independent compact monitor may know newer session choices
            // than the audio pane's last sample. Translate that displayed list
            // without replacing it with stale metadata or changing SourceId.
            UpdatePlayerOptions(displayedPlayerOptions); RenderMediaContent(media);
        }

        private void UpdateControls()
        {
            bool enabled = running && !disposed && !demo && !controlling && media.Available;
            previous.IsEnabled = enabled && media.CanPrevious; toggle.IsEnabled = enabled && media.CanToggle; next.IsEnabled = enabled && media.CanNext;
            previous.Opacity = previous.IsEnabled ? 1 : .4; toggle.Opacity = toggle.IsEnabled ? 1 : .4; next.Opacity = next.IsEnabled ? 1 : .4;
            RenderLyrics();
        }

        private void RenderAudio(AudioSnapshot value)
        {
            audio = value;
            rendering = true;
            try
            {
                if (!dirtyVolume) { volume.Value = value.Volume; volumeLabel.Text = value.OutputAvailable ? Math.Round(value.Volume) + "%" : "—"; }
                bool enabled = running && !disposed && !demo && !controlling;
                volume.IsEnabled = mute.IsEnabled = enabled && value.OutputAvailable;
                applyVolume.IsEnabled = enabled && value.OutputAvailable && dirtyVolume;
                microphone.IsEnabled = enabled && value.MicrophoneAvailable;
                mute.Content = value.OutputMuted ? "取消输出静音" : "静音输出";
                microphone.Content = value.MicrophoneMuted ? "取消麦克风静音" : "静音麦克风";
                outputStatus.Text = value.OutputStatus; microphoneStatus.Text = value.MicrophoneStatus;
            }
            finally { rendering = false; }
        }

        private async void Send(string command)
        {
            if (demo || disposed || !running || controlling || service == null) return;
            controlling = true; UpdateControls(); RenderAudio(audio); int currentGeneration = generation;
            var currentService = service;
            try
            {
                string status = await Task.Run(() => currentService.SendAsync(command));
                if (!disposed && running && currentGeneration == generation) actionStatus.Text = status;
            }
            catch (Exception)
            {
                if (!disposed && running && currentGeneration == generation) actionStatus.Text = "媒体会话暂不可用，请稍后重试";
            }
            finally { controlling = false; UpdateControls(); RenderAudio(audio); if (running && !disposed) Refresh(); }
        }

        private async void ApplyVolume()
        {
            if (demo || disposed || !running || controlling) return;
            double requestedVolume = volume.Value;
            await ChangeAudio(() => AudioService.SetVolume(requestedVolume), "音量已应用", "音量设置失败，请检查音频设备", true);
        }

        private async void ChangeMute(bool input)
        {
            if (demo || disposed || !running || controlling) return;
            await ChangeAudio(() => AudioService.ToggleMute(input), input ? "默认通信麦克风状态已切换" : "默认输出静音状态已切换", "设置失败，请检查音频设备", false);
        }

        private async Task ChangeAudio(Func<bool> change, string successText, string failureText, bool appliedVolume)
        {
            audioRevision++;
            controlling = true; UpdateControls(); RenderAudio(audio); int currentGeneration = generation;
            try
            {
                bool success = await Task.Run(change);
                if (disposed || !running || currentGeneration != generation) return;
                actionStatus.Text = success ? successText : failureText;
                if (success && appliedVolume) dirtyVolume = false;
                var snapshot = await Task.Run(audioReader);
                if (!disposed && running && currentGeneration == generation) RenderAudio(snapshot);
            }
            catch (Exception)
            {
                if (!disposed && running && currentGeneration == generation) actionStatus.Text = failureText;
            }
            finally { controlling = false; UpdateControls(); RenderAudio(audio); if (running && !disposed) Refresh(); }
        }

        internal static string Clock(TimeSpan value)
        { return value.TotalHours >= 1 ? ((int)value.TotalHours) + ":" + value.Minutes.ToString("00") + ":" + value.Seconds.ToString("00") : ((int)Math.Max(0, value.TotalMinutes)) + ":" + Math.Max(0, value.Seconds).ToString("00"); }

        public void Dispose() { if (disposed) return; Stop(); disposed = true; timer.Tick -= OnTick; lyricsDocument = renderedLyricsDocument = null; if (fullLyricsText != null) fullLyricsText.Clear(); }
    }
}
