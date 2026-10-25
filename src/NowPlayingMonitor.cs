using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CodexIsland
{
    // A media-only sampler remains active while compact. Audio endpoint reads stay
    // in the expanded panel; slow drivers cannot delay compact media updates.
    internal sealed class NowPlayingMonitor : IDisposable
    {
        private readonly bool demo;
        private readonly Func<Task<MediaSnapshot>> reader;
        private readonly MediaLyricsLibrary library;
        private readonly Func<MediaSnapshot, CancellationToken, Task<LrcDocument>> onlineReader;
        private readonly OnlineMediaLyrics onlineService;
        private readonly Func<string, Task<string>> commandSender;
        private readonly Func<DateTime> utcNow;
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private MediaService service;
        private CancellationTokenSource onlineCancellation;
        private bool running, disposed, reading, onlineEnabled, onlineSucceeded;
        private int generation, lyricsRevision, onlineFailures;
        private string lyricIdentity, onlineIdentity, suppressedOnlineIdentity, lastLyricsStatus;
        private DateTime nextOnlineAttemptUtc;
        private LrcDocument localLyrics;
        private string preferredSourceId = "";
        public event Action Changed;
        internal MediaSnapshot Snapshot { get; private set; }
        internal LrcDocument Lyrics { get; private set; }
        internal bool Running { get { return running; } }
        internal DateTime LastPlayingUtc { get; private set; }
        internal string LastPlayingIdentity { get; private set; }
        internal int LyricOffsetMilliseconds { get; set; }
        internal string PreferredSourceId { get { return preferredSourceId; } }
        internal string Lyric { get { return Lyrics == null ? "" : Lyrics.LineAt(MediaLyricTiming.Adjust(Snapshot.EstimatePosition(DateTime.UtcNow), LyricOffsetMilliseconds)); } }
        internal string LyricsStatus
        {
            get
            {
                if (Snapshot == null || !Snapshot.Available) return "未播放媒体，等待歌词";
                if (Lyrics != null && Lyrics.Count > 0)
                {
                    if (!Lyrics.IsSynchronized) return "已加载纯文本歌词，点击查看完整歌词，无法逐句同步";
                    if (!Snapshot.HasUsableTimeline) return "已加载歌词，但播放器未提供可用播放进度，无法同步";
                    if (!running) return "已加载歌词，媒体监控已暂停";
                    TimeSpan position = MediaLyricTiming.Adjust(Snapshot.EstimatePosition(DateTime.UtcNow), LyricOffsetMilliseconds);
                    if (position < Lyrics.Lines[0].Time) return "歌词已加载，尚未到首句";
                    if (Lyrics.LineAt(position).Length == 0) return "当前时刻暂无歌词";
                    return localLyrics == null ? "在线歌词已加载，按播放器进度显示" : "本地歌词已加载，按播放器进度显示";
                }
                if (suppressedOnlineIdentity == Identity(Snapshot)) return "当前歌曲歌词已清除，可重试在线匹配";
                if (!onlineEnabled) return "在线歌词已关闭，可导入本地 LRC";
                if (demo) return "预览模式不请求在线歌词";
                if (!running) return "媒体监控已暂停";
                if (OnlineLookupSignature(Snapshot).Length == 0) return "歌曲信息不完整，等待歌名和歌手";
                if (onlineCancellation != null) return "正在匹配在线歌词";
                return onlineFailures > 0 ? "未匹配到歌词或服务暂不可用，将稍后重试" : "等待匹配在线歌词";
            }
        }

        internal NowPlayingMonitor(bool demoMode, Func<Task<MediaSnapshot>> read = null, MediaLyricsLibrary lyricsLibrary = null,
            Func<MediaSnapshot, CancellationToken, Task<LrcDocument>> fetchOnline = null, Func<string, Task<string>> sendCommand = null,
            Func<DateTime> clockUtc = null)
        {
            demo = demoMode; reader = read; library = lyricsLibrary ?? new MediaLyricsLibrary(System.IO.Path.GetDirectoryName(new SettingsStore().FilePath), demoMode);
            commandSender = sendCommand;
            utcNow = clockUtc ?? (() => DateTime.UtcNow);
            onlineService = fetchOnline == null ? new OnlineMediaLyrics() : null;
            onlineReader = fetchOnline ?? onlineService.FindAsync;
            Snapshot = new MediaSnapshot(); lastLyricsStatus = LyricsStatus;
            timer.Interval = TimeSpan.FromSeconds(2); timer.Tick += OnTick;
        }

        internal static string Identity(MediaSnapshot track)
        {
            if (track == null || !track.Available) return "";
            return Part(track.Source) + Part(track.Title) + Part(track.Artist);
        }
        private static string Part(string value) { value = value ?? ""; return value.Length + ":" + value; }

        // Album and duration can arrive several samples after the title. A failed
        // lookup of that incomplete metadata must not suppress the completed query.
        internal static string OnlineLookupSignature(MediaSnapshot track)
        {
            if (track == null || !track.Available) return "";
            string title = LookupText(track.Title), artist = LookupText(track.Artist), album = LookupText(track.Album);
            if (String.IsNullOrEmpty(title) || String.IsNullOrEmpty(artist) || album == null) return "";
            return Part(track.Source) + Part(title) + Part(artist) + Part(album) +
                Math.Max(0, track.Duration.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        }
        private static string LookupText(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length > 512) return null;
            try { return value.Normalize(NormalizationForm.FormC).ToUpperInvariant(); }
            catch (ArgumentException) { return null; }
        }

        internal void Start()
        {
            if (disposed || running) return;
            running = true; generation++; if (demo && reader == null) return;
            if (reader == null) service = new MediaService { PreferredSourceId = preferredSourceId };
            timer.Start(); RaiseIfLyricsStatusChanged(); Refresh();
        }
        internal void Stop()
        {
            if (!running) return;
            running = false; generation++; timer.Stop(); ResetOnlineLookup(true);
            if (service != null) { service.Dispose(); service = null; }
            RaiseIfLyricsStatusChanged();
        }
        internal void EnableOnline(bool enabled)
        {
            if (onlineEnabled == enabled) return;
            onlineEnabled = enabled; lyricsRevision++; ResetOnlineLookup(true);
            Lyrics = localLyrics; RaiseChanged();
            if (enabled && running) RequestOnline();
        }
        internal void RetryOnlineLyrics()
        {
            if (disposed) return;
            lyricsRevision++; ResetOnlineLookup(true);
            if (onlineService != null) onlineService.Invalidate(Snapshot);
            Lyrics = localLyrics; RaiseChanged();
            if (onlineEnabled && running) RequestOnline();
        }
        internal void SelectPlayer(string sourceId)
        {
            sourceId = MediaSessionSelection.NormalizeSource(sourceId);
            if (disposed || preferredSourceId == sourceId) return;
            preferredSourceId = sourceId; generation++; lyricsRevision++; ResetOnlineLookup(true); lyricIdentity = null;
            localLyrics = Lyrics = null; LastPlayingUtc = DateTime.MinValue; LastPlayingIdentity = null;
            Snapshot = new MediaSnapshot { PreferredSourceId = sourceId, Status = sourceId.Length == 0 ? "正在获取媒体状态" : "指定播放器暂无媒体会话，正在等待；不会控制其他应用" };
            if (service != null) service.PreferredSourceId = sourceId;
            RaiseChanged(); if (running) Refresh();
        }
        internal async Task<string> SendCommandAsync(string command)
        {
            if (!running || disposed || (demo && commandSender == null) || !CompactMediaController.Supports(Snapshot, command)) return "当前应用不支持此控制";
            var current = service; int version = generation;
            if (commandSender == null && current == null) return "没有可控制的媒体";
            string result = await Task.Run(() => commandSender == null ? current.SendAsync(command) : commandSender(command));
            if (!disposed && running && generation == version) Refresh();
            return result;
        }
        private void OnTick(object sender, EventArgs args) { Refresh(); }
        internal async void Refresh()
        {
            if (!running || disposed || reading || (demo && reader == null)) return;
            reading = true; int version = generation, revision = lyricsRevision;
            var current = service;
            try
            {
                var value = await Task.Run(() => reader == null ? current.ReadAsync() : reader());
                if (!running || disposed || generation != version) return;
                value = value ?? new MediaSnapshot(); string identity = Identity(value);
                if (preferredSourceId.Length > 0 && value.Available && value.Source != preferredSourceId)
                    value = new MediaSnapshot { PreferredSourceId = preferredSourceId, Players = value.Players, Status = "指定播放器暂无媒体会话，正在等待；不会控制其他应用" };
                identity = Identity(value);
                LrcDocument lyrics = localLyrics;
                if (identity != lyricIdentity || revision != lyricsRevision)
                    lyrics = identity.Length == 0 ? null : await Task.Run(() => library.Find(value.Source, value.Title, value.Artist));
                if (!running || disposed || generation != version) return;
                // Import or removal during a slow read must not restore an older file.
                if (revision != lyricsRevision) return;
                if (identity != lyricIdentity)
                {
                    ResetOnlineLookup(true); lyricIdentity = identity;
                    localLyrics = lyrics; Lyrics = lyrics;
                }
                Snapshot = value;
                SyncOnlineSignature();
                if (value.Available && value.Playing) { LastPlayingUtc = DateTime.UtcNow; LastPlayingIdentity = identity; }
                RaiseChanged(); RequestOnline();
            }
            catch (Exception) { /* Preserve last sample through a transient provider failure. */ }
            finally
            {
                reading = false;
                if (running && !disposed && (generation != version || revision != lyricsRevision)) Refresh();
            }
        }
        private async void RequestOnline()
        {
            string identity = Identity(Snapshot);
            if (!running || disposed || demo || !onlineEnabled || localLyrics != null || identity.Length == 0 || suppressedOnlineIdentity == identity) return;
            string signature = SyncOnlineSignature();
            if (signature.Length == 0 || onlineCancellation != null || onlineSucceeded || utcNow() < nextOnlineAttemptUtc) return;
            var cancellation = new CancellationTokenSource(); onlineCancellation = cancellation;
            CancellationToken token = cancellation.Token;
            int version = generation, revision = lyricsRevision; var track = Snapshot;
            try
            {
                RaiseIfLyricsStatusChanged();
                if (!CanAcceptOnline(version, revision, signature, cancellation, token)) return;
                var result = await Task.Run(() => onlineReader(track, token));
                if (CanAcceptOnline(version, revision, signature, cancellation, token))
                {
                    if (result != null && result.Count > 0)
                    {
                        onlineSucceeded = true; onlineFailures = 0; nextOnlineAttemptUtc = DateTime.MinValue;
                        Lyrics = result; RaiseChanged();
                    }
                    else ScheduleOnlineRetry();
                }
            }
            catch (Exception)
            {
                if (CanAcceptOnline(version, revision, signature, cancellation, token)) ScheduleOnlineRetry();
            }
            finally
            {
                bool current = ReferenceEquals(onlineCancellation, cancellation);
                if (current) onlineCancellation = null;
                cancellation.Dispose();
                if (current) RaiseIfLyricsStatusChanged();
            }
        }
        private string SyncOnlineSignature()
        {
            string signature = OnlineLookupSignature(Snapshot);
            if (onlineIdentity != signature)
            {
                ResetOnlineLookup(false); onlineIdentity = signature;
                // Do this before publishing new metadata, including changes that
                // retain the source/title/artist used by locally imported lyrics.
                if (localLyrics == null) Lyrics = null;
            }
            return signature;
        }
        private bool CanAcceptOnline(int version, int revision, string signature, CancellationTokenSource cancellation, CancellationToken token)
        {
            return !disposed && running && generation == version && lyricsRevision == revision && onlineEnabled &&
                !token.IsCancellationRequested && ReferenceEquals(onlineCancellation, cancellation) &&
                onlineIdentity == signature && OnlineLookupSignature(Snapshot) == signature && localLyrics == null;
        }
        private void ScheduleOnlineRetry()
        {
            // One outstanding lookup per signature; failures back off rather than
            // retrying every two-second media sample. The steady-state cap is 5 min.
            onlineFailures = Math.Min(4, onlineFailures + 1);
            int seconds = onlineFailures == 1 ? 30 : onlineFailures == 2 ? 60 : onlineFailures == 3 ? 120 : 300;
            DateTime now = utcNow();
            nextOnlineAttemptUtc = now > DateTime.MaxValue.AddSeconds(-seconds) ? DateTime.MaxValue : now.AddSeconds(seconds);
        }
        private void ResetOnlineLookup(bool clearSuppression)
        {
            CancelOnline(); onlineIdentity = null; onlineFailures = 0; onlineSucceeded = false; nextOnlineAttemptUtc = DateTime.MinValue;
            if (clearSuppression) suppressedOnlineIdentity = null;
        }
        private void CancelOnline()
        {
            var cancellation = onlineCancellation; onlineCancellation = null;
            if (cancellation != null) try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
        internal async Task ImportLyricsAsync(MediaSnapshot track, string path)
        {
            if (Identity(track).Length == 0) throw new InvalidOperationException("请先播放一首歌曲");
            var document = await Task.Run(() => library.Import(track.Source, track.Title, track.Artist, path));
            if (disposed) return;
            lyricsRevision++; ResetOnlineLookup(false);
            if (Identity(Snapshot) == Identity(track))
            {
                localLyrics = Lyrics = document; lyricIdentity = Identity(track); RaiseChanged();
            }
            else { Refresh(); RequestOnline(); }
        }
        internal async Task RemoveLyricsAsync(MediaSnapshot track)
        {
            await Task.Run(() => library.Remove(track.Source, track.Title, track.Artist));
            if (disposed) return;
            lyricsRevision++; ResetOnlineLookup(false);
            if (Identity(Snapshot) == Identity(track))
            { localLyrics = Lyrics = null; suppressedOnlineIdentity = Identity(track); RaiseChanged(); }
            else { Refresh(); RequestOnline(); }
        }
        private void RaiseIfLyricsStatusChanged() { if (!disposed && lastLyricsStatus != LyricsStatus) RaiseChanged(); }
        private void RaiseChanged() { lastLyricsStatus = LyricsStatus; var handler = Changed; if (!disposed && handler != null) handler(); }
        public void Dispose()
        {
            if (disposed) return;
            // Shutdown is silent, including the status transition made by Stop.
            // Detach first so a subscriber cannot restart us during disposal.
            disposed = true; Changed = null; Stop(); timer.Tick -= OnTick;
        }
    }
}
