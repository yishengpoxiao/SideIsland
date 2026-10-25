using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace CodexIsland
{
    internal sealed class MediaSnapshot
    {
        public string Title = "暂无正在播放的媒体", Artist = "打开支持 Windows 媒体控制的音乐或视频应用", Source = "系统媒体", Status = "未发现媒体会话";
        public bool Available, Playing, CanToggle, CanPrevious, CanNext;
        public TimeSpan Position, Duration;
        public string Album = "";
        public BitmapSource Artwork;
        public DateTime SampledAtUtc;
        public double PlaybackRate = 1;
        public MediaPlayerOption[] Players = new MediaPlayerOption[0];
        public string PreferredSourceId = "";
        internal bool HasUsableTimeline { get { return Duration > TimeSpan.Zero && SampledAtUtc.Year > 1970; } }

        internal TimeSpan EstimatePosition(DateTime utcNow)
        {
            double seconds = Position.TotalSeconds;
            if (Playing && SampledAtUtc != DateTime.MinValue && utcNow >= SampledAtUtc)
            {
                double elapsed = (utcNow - SampledAtUtc).TotalSeconds;
                double rate = Double.IsNaN(PlaybackRate) || Double.IsInfinity(PlaybackRate) ? 1 : PlaybackRate;
                // Very old system timelines are not a useful synchronization source.
                if (elapsed <= 86400) seconds += elapsed * Math.Max(0, Math.Min(16, rate));
            }
            seconds = Math.Max(0, seconds);
            if (Duration > TimeSpan.Zero) seconds = Math.Min(seconds, Duration.TotalSeconds);
            return TimeSpan.FromSeconds(seconds);
        }

        internal static bool SupportsToggle(bool playing, bool toggle, bool play, bool pause)
        { return toggle || (playing ? pause : play); }

        internal static double Progress(TimeSpan position, TimeSpan duration)
        { return duration.TotalSeconds <= 0 ? 0 : Math.Max(0, Math.Min(100, position.TotalSeconds / duration.TotalSeconds * 100)); }
    }

    // Polling only reads. Every transport command originates in an explicit panel click.
    internal sealed class MediaService : IDisposable
    {
        private GlobalSystemMediaTransportControlsSessionManager manager;
        private MediaSessionEndpoint session;
        private readonly Func<CancellationToken, Task<MediaSessionEndpoint[]>> sessionProvider;
        private string preferredSourceId = "";
        private int selectionRevision;
        private readonly object stateLock = new object();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private volatile bool disposed;
        private int activeOperations;
        private bool lifetimeDisposed;
        private readonly MediaArtworkCache artworkCache = new MediaArtworkCache();
        internal MediaService(Func<CancellationToken, Task<MediaSessionEndpoint[]>> provider = null) { sessionProvider = provider; }
        internal string PreferredSourceId
        {
            get { lock (stateLock) return preferredSourceId; }
            set { value = MediaSessionSelection.NormalizeSource(value); lock (stateLock) { if (preferredSourceId == value) return; preferredSourceId = value; selectionRevision++; session = null; } }
        }

        public async Task<MediaSnapshot> ReadAsync()
        {
            var result = new MediaSnapshot();
            CancellationToken cancellation;
            if (!TryBeginOperation(out cancellation)) { result.Status = "媒体监控已停止"; return result; }
            string preferred; int revision;
            lock (stateLock) { preferred = preferredSourceId; revision = selectionRevision; }
            try
            {
                var endpoints = await (sessionProvider == null ? ReadSystemSessionsAsync(cancellation) : sessionProvider(cancellation)).ConfigureAwait(false);
                var options = MediaSessionSelection.Options(endpoints);
                var current = MediaSessionSelection.Select(endpoints, preferred);
                result.Players = options; result.PreferredSourceId = preferred;
                if (current == null)
                {
                    if (preferred.Length > 0) result.Status = "指定播放器暂无媒体会话，正在等待；不会控制其他应用";
                    lock (stateLock) { if (revision != selectionRevision) return new MediaSnapshot { PreferredSourceId = preferredSourceId, Status = "播放器选择已更改，正在重新读取" }; session = null; }
                    return result;
                }
                result = await current.Read(cancellation).ConfigureAwait(false) ?? new MediaSnapshot();
                if (result.Available && !String.IsNullOrEmpty(current.SourceId)) result.Source = current.SourceId;
                result.Players = options; result.PreferredSourceId = preferred;
                lock (stateLock)
                {
                    if (disposed || revision != selectionRevision) return new MediaSnapshot { PreferredSourceId = preferredSourceId, Status = "播放器选择已更改，正在重新读取" };
                    session = result.Available ? current : null;
                }
                return result;
            }
            catch (OperationCanceledException) { result = new MediaSnapshot { Status = disposed ? "媒体监控已停止" : "媒体响应超时，稍后重试", PreferredSourceId = preferred }; }
            catch (Exception)
            {
                lock (stateLock) { if (revision == selectionRevision) { session = null; manager = null; } }
                result = new MediaSnapshot { Status = "媒体控制暂不可用，请使用已安装的通知版", PreferredSourceId = preferred };
            }
            finally { EndOperation(); }
            return result;
        }
        private async Task<MediaSessionEndpoint[]> ReadSystemSessionsAsync(CancellationToken cancellation)
        {
            GlobalSystemMediaTransportControlsSessionManager currentManager;
            lock (stateLock) currentManager = manager;
            if (currentManager == null) currentManager = await Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), cancellation).ConfigureAwait(false);
            lock (stateLock) { if (disposed) return new MediaSessionEndpoint[0]; manager = currentManager; }
            var current = currentManager.GetCurrentSession(); var results = new List<MediaSessionEndpoint>();
            string currentSourceId = "";
            if (current != null) try { currentSourceId = current.SourceAppUserModelId; } catch (Exception) { }
            // SourceAppUserModelId is Windows' source-app identity; titles are never selectors.
            foreach (var native in currentManager.GetSessions())
            {
                if (results.Count >= 64) break;
                try
                {
                    var target = native; string sourceId = target.SourceAppUserModelId;
                    results.Add(new MediaSessionEndpoint { SourceId = sourceId, Playing = target.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        Current = Object.Equals(target, current) || MediaSessionSelection.IsCurrentSource(sourceId, currentSourceId), Read = token => ReadSessionAsync(target, token), Send = (command, token) => SendSessionAsync(target, command, token) });
                }
                catch (Exception) { }
            }
            return results.ToArray();
        }
        private async Task<MediaSnapshot> ReadSessionAsync(GlobalSystemMediaTransportControlsSession current, CancellationToken cancellation)
        {
            var result = new MediaSnapshot();
                var info = current.GetPlaybackInfo();
                var properties = await Await(current.TryGetMediaPropertiesAsync(), cancellation).ConfigureAwait(false);
                if (disposed) return result;
                result.Available = true;
                result.Playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                result.Title = String.IsNullOrWhiteSpace(properties.Title) ? "未提供媒体标题" : properties.Title;
                result.Artist = String.IsNullOrWhiteSpace(properties.Artist) ? "未提供作者" : properties.Artist;
                result.Album = properties.AlbumTitle ?? "";
                result.Source = String.IsNullOrWhiteSpace(current.SourceAppUserModelId) ? "系统媒体" : current.SourceAppUserModelId;
                result.Status = result.Playing ? "正在播放" : "已暂停 / 已停止";
                result.CanToggle = MediaSnapshot.SupportsToggle(result.Playing, info.Controls.IsPlayPauseToggleEnabled, info.Controls.IsPlayEnabled, info.Controls.IsPauseEnabled);
                result.CanPrevious = info.Controls.IsPreviousEnabled;
                result.CanNext = info.Controls.IsNextEnabled;
                var timeline = current.GetTimelineProperties();
                result.Duration = timeline.EndTime - timeline.StartTime;
                result.Position = timeline.Position - timeline.StartTime;
                if (result.Position < TimeSpan.Zero) result.Position = TimeSpan.Zero;
                if (result.Duration < TimeSpan.Zero) result.Duration = TimeSpan.Zero;
                if (result.Duration > TimeSpan.Zero && result.Position > result.Duration) result.Position = result.Duration;
                result.SampledAtUtc = timeline.LastUpdatedTime.UtcDateTime;
                if (result.SampledAtUtc == DateTime.MinValue) result.SampledAtUtc = DateTime.UtcNow;
                result.PlaybackRate = info.PlaybackRate.HasValue ? info.PlaybackRate.Value : 1;
                // A broken or slow artwork provider must not discard valid track metadata.
                try
                {
                    string artworkKey = result.Source + "\n" + result.Title + "\n" + result.Artist + "\n" + result.Album;
                    var thumbnail = properties.Thumbnail;
                    result.Artwork = await artworkCache.ReadAsync(artworkKey, DateTime.UtcNow, thumbnail != null, async delegate {
                        using (var coverTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                        {
                            coverTimeout.CancelAfter(1500);
                            using (var stream = await Await(thumbnail.OpenReadAsync(), coverTimeout.Token).ConfigureAwait(false))
                            {
                                ulong length = stream.Size;
                                if (length == 0 || length > MediaArtwork.MaxEncodedBytes) return null;
                                using (var reader = new DataReader(stream))
                                {
                                    uint loaded = await Await(reader.LoadAsync((uint)length), coverTimeout.Token).ConfigureAwait(false);
                                    if (loaded != (uint)length) return null;
                                    var bytes = new byte[loaded];
                                    reader.ReadBytes(bytes);
                                    coverTimeout.Token.ThrowIfCancellationRequested();
                                    return MediaArtwork.Decode(bytes);
                                }
                            }
                        }
                    }).ConfigureAwait(false);
                }
                catch (Exception) { }
            return result;
        }

        public async Task<string> SendAsync(string command)
        {
            CancellationToken cancellation;
            if (!TryBeginOperation(out cancellation)) return "没有可控制的媒体";
            try
            {
                MediaSessionEndpoint current; int revision;
                lock (stateLock) { current = session; revision = selectionRevision; }
                if (current == null) return "没有可控制的媒体";
                lock (stateLock) { if (disposed || revision != selectionRevision || (!String.IsNullOrEmpty(preferredSourceId) && current.SourceId != preferredSourceId)) return "播放器选择已更改，正在重新读取"; }
                cancellation.ThrowIfCancellationRequested();
                return await current.Send(command, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return "媒体控制响应超时"; }
            catch (Exception) { return "媒体会话已改变，请稍后重试"; }
            finally { EndOperation(); }
        }
        private static async Task<string> SendSessionAsync(GlobalSystemMediaTransportControlsSession current, string command, CancellationToken cancellation)
        {
                var info = current.GetPlaybackInfo();
                cancellation.ThrowIfCancellationRequested();
                IAsyncOperation<bool> operation;
                if (command == "previous" && info.Controls.IsPreviousEnabled) operation = current.TrySkipPreviousAsync();
                else if (command == "next" && info.Controls.IsNextEnabled) operation = current.TrySkipNextAsync();
                else if (command == "toggle")
                {
                    if (info.Controls.IsPlayPauseToggleEnabled) operation = current.TryTogglePlayPauseAsync();
                    else if (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing && info.Controls.IsPauseEnabled) operation = current.TryPauseAsync();
                    else if (info.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing && info.Controls.IsPlayEnabled) operation = current.TryPlayAsync();
                    else return "当前应用不支持此控制";
                }
                else return "当前应用不支持此控制";
                return await Await(operation, cancellation).ConfigureAwait(false) ? "已发送媒体控制" : "应用未接受此控制";
        }

        // Use the WinRT operation contract directly; no external runtime or NuGet package.
        private static async Task<T> Await<T>(IAsyncOperation<T> operation, CancellationToken cancellation)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(4000);
                try
                {
                    while (operation.Status == AsyncStatus.Started) await Task.Delay(60, timeout.Token).ConfigureAwait(false);
                    timeout.Token.ThrowIfCancellationRequested();
                    if (operation.Status == AsyncStatus.Canceled) throw new OperationCanceledException();
                    return operation.GetResults();
                }
                finally
                {
                    try
                    {
                        if (operation.Status == AsyncStatus.Started)
                        {
                            operation.Completed = (finished, state) => { try { finished.Close(); } catch (Exception) { } };
                            operation.Cancel();
                        }
                        else operation.Close();
                    }
                    catch (Exception) { }
                }
            }
        }

        private bool TryBeginOperation(out CancellationToken cancellation)
        {
            lock (stateLock)
            {
                cancellation = default(CancellationToken);
                if (disposed) return false;
                activeOperations++;
                cancellation = lifetime.Token;
                return true;
            }
        }

        private void EndOperation()
        {
            bool release;
            lock (stateLock)
            {
                activeOperations--;
                release = disposed && activeOperations == 0 && !lifetimeDisposed;
                if (release) lifetimeDisposed = true;
            }
            if (release) lifetime.Dispose();
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (disposed) return;
                disposed = true; session = null; manager = null;
                // Keep the token source alive through cancellation itself as well as
                // all reads/commands already in flight. No WinRT work runs under lock.
                activeOperations++;
            }
            Task.Run(delegate
            {
                // Cancellation can synchronously execute a WinRT cleanup continuation.
                // Closing the pane must never wait for that work on Dispatcher.
                try { lifetime.Cancel(); }
                catch (Exception) { }
                finally { EndOperation(); }
            });
        }
    }

    internal sealed class AudioSnapshot
    {
        public bool OutputAvailable, OutputMuted, MicrophoneAvailable, MicrophoneMuted;
        public double Volume;
        public string OutputStatus = "未检测到默认输出设备", MicrophoneStatus = "未检测到默认麦克风";
    }

    internal static class AudioService
    {
        public static double ClampVolume(double value)
        { return Double.IsNaN(value) || Double.IsInfinity(value) ? 0 : Math.Max(0, Math.Min(100, value)); }

        public static AudioSnapshot Read()
        {
            var result = new AudioSnapshot();
            try
            {
                WithEndpoint(false, volume => {
                    float level; bool muted;
                    Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out level));
                    Marshal.ThrowExceptionForHR(volume.GetMute(out muted));
                    result.Volume = ClampVolume(level * 100); result.OutputMuted = muted; result.OutputAvailable = true;
                    result.OutputStatus = muted ? "默认输出已静音" : "默认输出设备";
                });
            }
            catch (Exception) { }
            try
            {
                WithEndpoint(true, volume => {
                    bool muted; Marshal.ThrowExceptionForHR(volume.GetMute(out muted));
                    result.MicrophoneMuted = muted; result.MicrophoneAvailable = true;
                    result.MicrophoneStatus = muted ? "默认通信麦克风已静音" : "默认通信麦克风未静音";
                });
            }
            catch (Exception) { }
            return result;
        }

        public static bool SetVolume(double percent)
        {
            if (Double.IsNaN(percent) || Double.IsInfinity(percent)) return false;
            return TryChange(false, volume => Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar((float)(ClampVolume(percent) / 100), IntPtr.Zero)));
        }

        public static bool ToggleMute(bool microphone)
        {
            return TryChange(microphone, volume => {
                bool muted; Marshal.ThrowExceptionForHR(volume.GetMute(out muted));
                Marshal.ThrowExceptionForHR(volume.SetMute(!muted, IntPtr.Zero));
            });
        }

        private static bool TryChange(bool microphone, Action<IAudioEndpointVolume> action)
        { try { WithEndpoint(microphone, action); return true; } catch (Exception) { return false; } }

        private static void WithEndpoint(bool microphone, Action<IAudioEndpointVolume> action)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            object endpoint = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                // eRender/eMultimedia for speakers, eCapture/eCommunications for calls.
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(microphone ? 1 : 0, microphone ? 2 : 1, out device));
                Guid id = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref id, 23, IntPtr.Zero, out endpoint));
                action((IAudioEndpointVolume)endpoint);
            }
            finally
            {
                if (endpoint != null && Marshal.IsComObject(endpoint)) Marshal.ReleaseComObject(endpoint);
                if (device != null && Marshal.IsComObject(device)) Marshal.ReleaseComObject(device);
                if (enumerator != null && Marshal.IsComObject(enumerator)) Marshal.ReleaseComObject(enumerator);
            }
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid id, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
            [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
            [PreserveSig] int GetId(out IntPtr id);
            [PreserveSig] int GetState(out uint state);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float level, IntPtr context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr context);
            [PreserveSig] int GetMasterVolumeLevel(out float level);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, IntPtr context);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr context);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, IntPtr context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
            [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
            [PreserveSig] int VolumeStepUp(IntPtr context);
            [PreserveSig] int VolumeStepDown(IntPtr context);
            [PreserveSig] int QueryHardwareSupport(out uint mask);
            [PreserveSig] int GetVolumeRange(out float min, out float max, out float increment);
        }
    }
}
