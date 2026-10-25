using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace CodexIsland
{
    // Diagnostic entry point only. No windows, simulated input, private player data,
    // credentials, artwork bytes or lyric text are collected by this helper.
    internal static class LiveMediaProbe
    {
        private sealed class Options
        {
            public string SourceId { get; set; }
            public int Samples { get; set; }
            public int IntervalSeconds { get; set; }
            public string OutputPath { get; set; }
            public bool OnlineLyrics { get; set; }
            public string Command { get; set; }
            public int TimeoutSeconds { get; set; }
            public string ProjectRoot { get; set; }
        }

        private sealed class ReadResult
        {
            internal MediaSnapshot Snapshot;
            internal Dictionary<string, object> Data;
        }

        private static readonly SemaphoreSlim RawWorkers = new SemaphoreSlim(8, 8);
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 40 };
        private static Task<AudioSnapshot> pendingAudio;
        private static Task<MediaSnapshot> pendingMedia;
        private static DateTime pendingMediaStartedAtUtc;

        [STAThread]
        public static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                if (args.Length != 2 || args[0] != "--options-file") throw new ArgumentException("Expected --options-file.");
                var options = Json.Deserialize<Options>(File.ReadAllText(args[1], Encoding.UTF8));
                Validate(options);
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds)))
                    return Run(options, timeout.Token).GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Probe failed: " + Error(error));
                return 2;
            }
        }

        private static void Validate(Options options)
        {
            if (options == null || options.Samples < 1 || options.Samples > 10 || options.IntervalSeconds < 1 || options.IntervalSeconds > 5 || options.TimeoutSeconds < 10 || options.TimeoutSeconds > 600)
                throw new ArgumentException("Invalid probe limits.");
            options.SourceId = options.SourceId ?? "";
            options.Command = options.Command ?? "";
            if (options.SourceId.Length > 512 || options.SourceId.IndexOf('\0') >= 0) throw new ArgumentException("Invalid SourceId.");
            if (options.Command.Length > 0 && (String.IsNullOrWhiteSpace(options.SourceId) || (options.Command != "toggle" && options.Command != "previous" && options.Command != "next")))
                throw new ArgumentException("Command requires an exact nonempty SourceId and toggle, previous or next.");
            string root = Path.GetFullPath(options.ProjectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            options.OutputPath = Path.GetFullPath(options.OutputPath);
            if (!options.OutputPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !String.Equals(Path.GetExtension(options.OutputPath), ".json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("JSON output must be inside this project.");
        }

        private static async Task<int> Run(Options options, CancellationToken cancellation)
        {
            var samples = new List<object>();
            var report = Map("SchemaVersion", 1, "StartedAtUtc", Utc(), "ReadOnly", options.Command.Length == 0,
                "RequestedSourceId", options.SourceId, "RequestedCommand", options.Command, "CommandsAttempted", 0,
                "OnlineLyricsEnabled", options.OnlineLyrics, "ContainsLyricText", false, "Samples", samples,
                "Completed", false, "Outcome", "in_progress");
            Save(options.OutputPath, report);
            var lyrics = options.OnlineLyrics ? new OnlineMediaLyrics() : null;
            using (var service = new MediaService())
            {
                service.PreferredSourceId = options.SourceId;
                try
                {
                    ReadResult baseline = null;
                    for (int index = 0; index < options.Samples; index++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var captured = await Capture(service, lyrics, "sample-" + (index + 1), cancellation).ConfigureAwait(false);
                        samples.Add(captured.Data); baseline = captured;
                        Save(options.OutputPath, report);
                        Console.WriteLine("Sample " + (index + 1) + ": " + (captured.Snapshot == null ? "read incomplete" : captured.Snapshot.Available ? captured.Snapshot.Source + " / " + captured.Snapshot.Title : captured.Snapshot.Status));
                        if (index + 1 < options.Samples) await Task.Delay(options.IntervalSeconds * 1000, cancellation).ConfigureAwait(false);
                    }
                    if (options.Command.Length > 0)
                    {
                        Save(Sidecar(options.OutputPath, "baseline"), baseline.Data);
                        var control = Map("Command", options.Command, "SourceId", options.SourceId, "Attempted", false,
                            "ReturnValueIsPlaybackProof", false, "AutomaticRetry", false);
                        report["Control"] = control;
                        if (baseline.Snapshot == null || !baseline.Snapshot.Available || !String.Equals(baseline.Snapshot.Source, options.SourceId, StringComparison.Ordinal))
                        {
                            control["Outcome"] = "refused_no_exact_available_source";
                            report["Outcome"] = "command_refused";
                        }
                        else
                        {
                            // Only this explicitly opted-in path sends a command. Never retry a
                            // toggle/skip: a timeout leaves its outcome unknown.
                            cancellation.ThrowIfCancellationRequested();
                            control["Attempted"] = true; control["StartedAtUtc"] = Utc();
                            report["CommandsAttempted"] = 1;
                            Save(options.OutputPath, report);
                            var sent = await Bound(Task.Run(async delegate {
                                cancellation.ThrowIfCancellationRequested();
                                return await service.SendAsync(options.Command).ConfigureAwait(false);
                            }, cancellation), 5500, cancellation).ConfigureAwait(false);
                            control["CompletedAtUtc"] = Utc();
                            string commandOutcome = ControlOutcome(sent.Item1, sent.Item2);
                            control["Outcome"] = commandOutcome;
                            control["ReturnedWithinTimeout"] = sent.Item1;
                            if (sent.Item1) control["ServiceResponse"] = sent.Item2;
                            var after = Stopwatch.StartNew();
                            foreach (int delay in new[] { 700, 1800, 3000 })
                            {
                                int remaining = delay - (int)after.ElapsedMilliseconds;
                                if (remaining > 0) await Task.Delay(remaining, cancellation).ConfigureAwait(false);
                                double actual = after.Elapsed.TotalMilliseconds;
                                var captured = await Capture(service, null, "after-" + delay + "ms", cancellation).ConfigureAwait(false);
                                captured.Data["RequestedDelayAfterCommandReturnMs"] = delay;
                                captured.Data["ActualCaptureStartAfterCommandReturnMs"] = Math.Round(actual, 1);
                                captured.Data["ComparedToBaseline"] = Compare(baseline.Snapshot, captured.Snapshot);
                                samples.Add(captured.Data);
                                Save(Sidecar(options.OutputPath, "after-" + delay.ToString("0000", CultureInfo.InvariantCulture) + "ms"), captured.Data);
                                Save(options.OutputPath, report);
                            }
                            report["Outcome"] = commandOutcome == "accepted_by_smtc_not_playback_proof" ? "command_observed_inspect_state_and_track_changes" :
                                commandOutcome.IndexOf("unknown", StringComparison.Ordinal) >= 0 ? "command_timeout_or_error_outcome_unknown" : "command_not_accepted";
                        }
                    }
                    else report["Outcome"] = samples.Any(item => HasAvailable((Dictionary<string, object>)item)) ? "observations_completed" : "completed_no_available_production_media";
                    report["Completed"] = true;
                }
                catch (OperationCanceledException) { report["Outcome"] = "overall_timeout"; }
                catch (Exception error) { report["Outcome"] = "probe_error"; report["Error"] = Error(error); }
                report["FinishedAtUtc"] = Utc();
                Save(options.OutputPath, report);
            }
            Console.WriteLine("JSON: " + options.OutputPath);
            string outcome = (string)report["Outcome"];
            return !(bool)report["Completed"] ? 2 : outcome == "command_refused" ? 3 : outcome == "command_timeout_or_error_outcome_unknown" ? 4 : outcome == "command_not_accepted" ? 5 : 0;
        }

        private static bool HasAvailable(Dictionary<string, object> sample)
        {
            object value; var media = sample["ProductionMedia"] as Dictionary<string, object>;
            return media != null && media.TryGetValue("Available", out value) && value is bool && (bool)value;
        }

        private static async Task<ReadResult> Capture(MediaService service, OnlineMediaLyrics lyrics, string label, CancellationToken cancellation)
        {
            var data = Map("Label", label, "StartedAtUtc", Utc());
            Task<Dictionary<string, object>> raw = Raw(cancellation);
            Task<ReadResult> media = Production(service, cancellation);
            Task<Dictionary<string, object>> audio = Audio(cancellation);
            await Task.WhenAll(raw, media, audio).ConfigureAwait(false);
            var result = media.Result;
            data["RawSmtc"] = raw.Result; data["ProductionMedia"] = result.Data; data["Audio"] = audio.Result;
            if (lyrics != null)
            {
                if (result.Snapshot == null || !result.Snapshot.Available) data["OnlineLyrics"] = Map("Attempted", false, "Reason", "no_available_snapshot");
                else
                {
                    using (var token = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                    {
                        token.CancelAfter(6000);
                        var found = await Bound(Task.Run(() => lyrics.FindAsync(result.Snapshot, token.Token)), 6500, cancellation).ConfigureAwait(false);
                        data["OnlineLyrics"] = Map("Attempted", true, "Completed", found.Item1,
                            "HasLyrics", found.Item1 && found.Item2 != null && found.Item2.Count > 0,
                            "LineCount", found.Item1 && found.Item2 != null ? found.Item2.Count : 0,
                            "NoResultMayIncludeProviderErrorOrNoMatch", true, "ContainsLyricText", false);
                    }
                }
            }
            data["FinishedAtUtc"] = Utc(); result.Data = data; return result;
        }

        private static async Task<ReadResult> Production(MediaService service, CancellationToken cancellation)
        {
            var attempts = new List<object>(); MediaSnapshot snapshot = null;
            for (int index = 0; index < 2; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                bool reused = pendingMedia != null && !pendingMedia.IsCompleted;
                if (!reused)
                {
                    pendingMediaStartedAtUtc = DateTime.UtcNow;
                    pendingMedia = Task.Run(() => service.ReadAsync());
                }
                var observed = await Bound(pendingMedia, 6500, cancellation).ConfigureAwait(false);
                attempts.Add(Map("Attempt", index + 1, "Completed", observed.Item1, "Available", observed.Item1 && observed.Item2 != null && observed.Item2.Available,
                    "ReusedPendingRead", reused, "ReadStartedAtUtc", pendingMediaStartedAtUtc.ToString("o")));
                if (!observed.Item1) break; // Do not overlap a still-pending production read.
                snapshot = observed.Item2;
                if (snapshot != null && snapshot.Available) break;
                if (index == 0) await Task.Delay(200, cancellation).ConfigureAwait(false);
            }
            var data = Map("Attempts", attempts, "Completed", snapshot != null);
            if (snapshot != null)
            {
                data["Available"] = snapshot.Available; data["Title"] = Clip(snapshot.Title); data["Artist"] = Clip(snapshot.Artist);
                data["Album"] = Clip(snapshot.Album); data["Source"] = snapshot.Source; data["PreferredSourceId"] = snapshot.PreferredSourceId;
                data["Playing"] = snapshot.Playing; data["Status"] = snapshot.Status;
                data["CanToggle"] = snapshot.CanToggle; data["CanPrevious"] = snapshot.CanPrevious; data["CanNext"] = snapshot.CanNext;
                data["PositionSeconds"] = Number(snapshot.Position.TotalSeconds); data["DurationSeconds"] = Number(snapshot.Duration.TotalSeconds);
                data["EstimatedPositionSeconds"] = Number(snapshot.EstimatePosition(DateTime.UtcNow).TotalSeconds);
                data["TimelineSampledAtUtc"] = snapshot.SampledAtUtc.ToString("o"); data["PlaybackRate"] = Number(snapshot.PlaybackRate);
                data["Artwork"] = Map("Present", snapshot.Artwork != null, "PixelWidth", snapshot.Artwork == null ? 0 : snapshot.Artwork.PixelWidth,
                    "PixelHeight", snapshot.Artwork == null ? 0 : snapshot.Artwork.PixelHeight);
                if (snapshot.Artwork != null)
                {
                    var artwork = (Dictionary<string, object>)data["Artwork"];
                    artwork["PixelFormat"] = snapshot.Artwork.Format.ToString();
                    artwork["Sha256PixelFormat"] = "Bgra32";
                    artwork["Sha256Definition"] = "UTF8(width x height : Bgra32 LF) followed by decoded canonical BGRA32 pixels; includes indexed palette colors";
                    try
                    {
                        var canonical = new System.Windows.Media.Imaging.FormatConvertedBitmap(snapshot.Artwork, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                        canonical.Freeze();
                        int stride = checked(canonical.PixelWidth * 4);
                        int length = checked(stride * snapshot.Artwork.PixelHeight);
                        if (length <= 0 || length >= 4 * 1024 * 1024) artwork["Sha256Error"] = "decoded_pixel_buffer_outside_limit";
                        else
                        {
                            var pixels = new byte[length]; canonical.CopyPixels(pixels, stride, 0);
                            byte[] header = Encoding.UTF8.GetBytes(canonical.PixelWidth + "x" + canonical.PixelHeight + ":Bgra32\n");
                            using (var hash = SHA256.Create())
                            {
                                hash.TransformBlock(header, 0, header.Length, header, 0);
                                hash.TransformFinalBlock(pixels, 0, pixels.Length);
                                artwork["Sha256"] = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                            }
                        }
                    }
                    catch (Exception error) { artwork["Sha256Error"] = Error(error); }
                }
                data["Players"] = snapshot.Players.Select(player => Map("SourceId", player.SourceId, "Playing", player.Playing)).ToArray();
            }
            return new ReadResult { Snapshot = snapshot, Data = data };
        }

        private static async Task<Dictionary<string, object>> Audio(CancellationToken cancellation)
        {
            if (pendingAudio == null || pendingAudio.IsCompleted) pendingAudio = Task.Run(() => AudioService.Read());
            var read = await Bound(pendingAudio, 2500, cancellation).ConfigureAwait(false);
            if (!read.Item1 || read.Item2 == null) return Map("Completed", false);
            var audio = read.Item2;
            return Map("Completed", true, "OutputAvailable", audio.OutputAvailable, "OutputMuted", audio.OutputMuted,
                "VolumePercent", Number(audio.Volume), "OutputStatus", audio.OutputStatus,
                "MicrophoneAvailable", audio.MicrophoneAvailable, "MicrophoneMuted", audio.MicrophoneMuted, "MicrophoneStatus", audio.MicrophoneStatus);
        }

        private static async Task<Dictionary<string, object>> Raw(CancellationToken cancellation)
        {
            using (var token = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                token.CancelAfter(9000);
                var read = await Bound(Task.Run(() => RawCore(token.Token)), 9500, cancellation).ConfigureAwait(false);
                return read.Item1 ? read.Item2 : Map("Completed", false, "Outcome", "raw_smtc_timeout");
            }
        }

        private static async Task<Dictionary<string, object>> RawCore(CancellationToken cancellation)
        {
            try
            {
                var manager = await Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), cancellation).ConfigureAwait(false);
                var current = manager.GetCurrentSession();
                string currentSourceId = null, currentError = null;
                if (current != null)
                    try { currentSourceId = current.SourceAppUserModelId; } catch (Exception error) { currentError = Error(error); }
                var native = manager.GetSessions().Take(64).ToArray();
                var rows = new List<object>();
                var tasks = native.Select(session => RawSession(session, Object.Equals(session, current), cancellation)).ToArray();
                foreach (var task in tasks)
                {
                    var finished = await Bound(task, 6500, cancellation).ConfigureAwait(false);
                    rows.Add(finished.Item1 ? finished.Item2 : Map("Completed", false, "Outcome", "session_timeout"));
                }
                return Map("Completed", true, "CurrentSessionPresent", current != null, "CurrentSourceId", currentSourceId,
                    "CurrentSessionNote", current == null ? "GetCurrentSession returned null; Windows has no current SMTC session" : "",
                    "CurrentSourceReadError", currentError, "SessionCount", native.Length, "MaximumEnumeratedSessions", 64, "Sessions", rows);
            }
            catch (Exception error) { return Map("Completed", false, "Error", Error(error)); }
        }

        private static async Task<Dictionary<string, object>> RawSession(GlobalSystemMediaTransportControlsSession session, bool current, CancellationToken cancellation)
        {
            bool entered = false; var row = Map("CurrentSession", current, "Completed", false);
            try
            {
                await RawWorkers.WaitAsync(cancellation).ConfigureAwait(false); entered = true;
                row["SourceAppUserModelId"] = session.SourceAppUserModelId;
                var info = session.GetPlaybackInfo();
                row["PlaybackStatus"] = info.PlaybackStatus.ToString(); row["PlaybackRate"] = info.PlaybackRate.HasValue ? (object)Number(info.PlaybackRate.Value) : null;
                var controls = new Dictionary<string, object>();
                foreach (var property in typeof(GlobalSystemMediaTransportControlsSessionPlaybackControls).GetProperties(BindingFlags.Instance | BindingFlags.Public))
                    if (property.PropertyType == typeof(bool) && property.GetIndexParameters().Length == 0)
                        try { controls[property.Name] = property.GetValue(info.Controls, null); } catch (Exception error) { controls[property.Name] = Error(error); }
                row["Controls"] = controls;
                var timeline = session.GetTimelineProperties();
                row["Timeline"] = Map("StartSeconds", Number(timeline.StartTime.TotalSeconds), "EndSeconds", Number(timeline.EndTime.TotalSeconds),
                    "PositionSeconds", Number(timeline.Position.TotalSeconds), "MinSeekSeconds", Number(timeline.MinSeekTime.TotalSeconds),
                    "MaxSeekSeconds", Number(timeline.MaxSeekTime.TotalSeconds), "LastUpdatedTimeUtc", timeline.LastUpdatedTime.UtcDateTime.ToString("o"));
                var properties = await Await(session.TryGetMediaPropertiesAsync(), cancellation).ConfigureAwait(false);
                row["Title"] = Clip(properties.Title); row["Artist"] = Clip(properties.Artist); row["Album"] = Clip(properties.AlbumTitle);
                row["ThumbnailPresent"] = properties.Thumbnail != null;
                if (properties.Thumbnail != null)
                {
                    using (var thumbnailTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                    {
                        thumbnailTimeout.CancelAfter(1500);
                        try
                        {
                            using (var stream = await Await(properties.Thumbnail.OpenReadAsync(), thumbnailTimeout.Token).ConfigureAwait(false))
                            {
                                ulong length = stream.Size; row["ThumbnailStreamBytes"] = length;
                                bool permitted = length > 0 && length < 4UL * 1024 * 1024;
                                row["ThumbnailBelow4MiB"] = permitted;
                                if (permitted)
                                {
                                    using (var reader = new DataReader(stream))
                                    {
                                        uint loaded = await Await(reader.LoadAsync((uint)Math.Min(length, 4096UL)), thumbnailTimeout.Token).ConfigureAwait(false);
                                        row["ThumbnailPrefixBytesRead"] = loaded;
                                        // No pixel bytes are persisted; production ReadAsync separately decodes artwork.
                                    }
                                }
                            }
                        }
                        catch (Exception error) { row["ThumbnailError"] = Error(error); }
                    }
                }
                row["Completed"] = true;
            }
            catch (Exception error) { row["Error"] = Error(error); }
            finally { if (entered) RawWorkers.Release(); }
            return row;
        }

        private static async Task<T> Await<T>(IAsyncOperation<T> operation, CancellationToken cancellation)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(4000);
                try
                {
                    while (operation.Status == AsyncStatus.Started) await Task.Delay(40, timeout.Token).ConfigureAwait(false);
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

        private static async Task<Tuple<bool, T>> Bound<T>(Task<T> task, int milliseconds, CancellationToken cancellation)
        {
            var winner = await Task.WhenAny(task, Task.Delay(milliseconds, cancellation)).ConfigureAwait(false);
            if (winner == task)
            {
                try { return Tuple.Create(true, await task.ConfigureAwait(false)); }
                catch (OperationCanceledException) { cancellation.ThrowIfCancellationRequested(); return Tuple.Create(false, default(T)); }
                catch (Exception error) { Console.Error.WriteLine("Read stage: " + Error(error)); return Tuple.Create(false, default(T)); }
            }
            Observe(task); cancellation.ThrowIfCancellationRequested(); return Tuple.Create(false, default(T));
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(faulted => { var observed = faulted.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        private static string ControlOutcome(bool returned, string response)
        {
            if (!returned || response == "媒体控制响应超时") return "timeout_or_error_outcome_unknown";
            if (response == "已发送媒体控制") return "accepted_by_smtc_not_playback_proof";
            if (response == "应用未接受此控制") return "rejected_by_player";
            if (response == "当前应用不支持此控制") return "unsupported_control";
            if (response == "没有可控制的媒体") return "no_controllable_session";
            if (response != null && response.StartsWith("播放器选择已更改", StringComparison.Ordinal)) return "source_selection_changed";
            if (response == "媒体会话已改变，请稍后重试") return "session_changed_or_error_outcome_unknown";
            return "unclassified_response_outcome_unknown";
        }
        private static Dictionary<string, object> Compare(MediaSnapshot before, MediaSnapshot after)
        {
            if (before == null || after == null || !after.Available) return Map("Comparable", false);
            return Map("Comparable", true, "SameExactSource", String.Equals(before.Source, after.Source, StringComparison.Ordinal),
                "PlayingChanged", before.Playing != after.Playing,
                "TrackMetadataChanged", before.Title != after.Title || before.Artist != after.Artist || before.Album != after.Album,
                "PositionDeltaSeconds", Number(after.Position.TotalSeconds - before.Position.TotalSeconds),
                "BeforePlaying", before.Playing, "AfterPlaying", after.Playing,
                "BeforeTitle", Clip(before.Title), "AfterTitle", Clip(after.Title), "PlaybackNotProvenByCommandReturn", true);
        }
        private static Dictionary<string, object> Map(params object[] values)
        {
            var result = new Dictionary<string, object>();
            for (int index = 0; index < values.Length; index += 2) result.Add((string)values[index], values[index + 1]);
            return result;
        }
        private static string Clip(string value) { return value == null ? "" : value.Length > 2048 ? value.Substring(0, 2048) : value; }
        private static object Number(double value) { return Double.IsNaN(value) || Double.IsInfinity(value) ? null : (object)Math.Round(value, 4); }
        private static string Utc() { return DateTime.UtcNow.ToString("o"); }
        private static string Error(Exception error) { return error.GetType().Name + " (0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture) + ")"; }
        private static string Sidecar(string path, string suffix) { return Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "." + suffix + ".json"); }
        private static void Save(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, Json.Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
    }
}
