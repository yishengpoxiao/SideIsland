using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class MediaTests
    {
        public static void Run()
        {
            Check(AudioService.ClampVolume(-20) == 0 && AudioService.ClampVolume(130) == 100, "Audio volume bounds");
            Check(AudioService.ClampVolume(42.5) == 42.5, "Audio volume preserves valid value");
            Check(AudioService.ClampVolume(Double.NaN) == 0 && AudioService.ClampVolume(Double.PositiveInfinity) == 0, "Audio nonfinite safety");
            Check(!MediaSnapshot.SupportsToggle(true, false, true, false), "Playing without pause support disabled");
            Check(!MediaSnapshot.SupportsToggle(false, false, false, true), "Stopped without play support disabled");
            Check(MediaSnapshot.SupportsToggle(true, true, false, false), "Toggle capability supported");
            Check(MediaSnapshot.SupportsToggle(true, false, false, true), "Pause fallback supported");
            Check(MediaSnapshot.SupportsToggle(false, false, true, false), "Play fallback supported");
            Check(MediaSnapshot.Progress(TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(100)) == 100, "Media progress bounded");
            Check(MediaSnapshot.Progress(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(100)) == 0, "Media progress lower bound");
            Check(MediaSnapshot.Progress(TimeSpan.FromSeconds(50), TimeSpan.Zero) == 0, "Unknown duration progress");
            Check(MediaPanel.Clock(TimeSpan.FromSeconds(81)) == "1:21", "Media clock short format");
            Check(MediaPanel.Clock(TimeSpan.FromSeconds(3661)) == "1:01:01", "Media clock long format");
            using (var panel = new MediaPanel(true)) { panel.Start(); panel.Stop(); panel.Start(); }
            SlowDeviceDoesNotBlockInput();
            ConcurrentShutdownDoesNotBlockInput();
            // Read-only hardware smoke. No test invokes volume/mute/playback setters.
            var audio = AudioService.Read();
            Check(!audio.OutputAvailable || audio.Volume >= 0 && audio.Volume <= 100, "Audio read-only snapshot");
            var media = Task.Run(async () => { using (var service = new MediaService()) return await service.ReadAsync(); }).GetAwaiter().GetResult();
            Check(media != null && !String.IsNullOrWhiteSpace(media.Status), "Read-only media status snapshot");
            Console.WriteLine("PASS: media capability, timing, slow-device responsiveness, restart freshness, concurrent shutdown and read-only audio/media checks.");
        }

        private static void SlowDeviceDoesNotBlockInput()
        {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                int uiThread = Thread.CurrentThread.ManagedThreadId, audioThread = uiThread, mediaThread = uiThread, reads = 0;
                using (var panel = new MediaPanel(false, delegate {
                    audioThread = Thread.CurrentThread.ManagedThreadId;
                    int sample = Interlocked.Increment(ref reads);
                    entered.Set();
                    if (sample == 1) release.Wait(3000);
                    return new AudioSnapshot { OutputAvailable = true, Volume = sample == 1 ? 10 : 65 };
                }, delegate {
                    mediaThread = Thread.CurrentThread.ManagedThreadId;
                    return Task.FromResult(new MediaSnapshot { Title = "isolated media", Status = "fake device" });
                }))
                {
                    try
                    {
                        var watch = Stopwatch.StartNew(); panel.Start(); watch.Stop();
                        Check(watch.ElapsedMilliseconds < 500, "Starting media sampling must return before a slow audio driver responds");
                        Check(entered.Wait(2000), "Fake slow device starts");
                        bool inputProcessed = false;
                        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => inputProcessed = true));
                        PumpUntil(() => inputProcessed, 1000);
                        Check(audioThread != uiThread, "Audio endpoint reads must not execute on Dispatcher");
                        // Closing and reopening during a pending sample must suppress its
                        // result and immediately request a fresh sample on completion.
                        panel.Stop(); panel.Stop(); panel.Start();
                        release.Set();
                        PumpUntil(() => Field<TextBlock>(panel, "title").Text == "isolated media", 1500);
                        Check(reads == 2, "Reopened panel starts one fresh read without waiting for the timer");
                        Check(Field<Slider>(panel, "volume").Value == 65, "Stopped sampling result cannot overwrite reopened panel");
                        Check(mediaThread != uiThread, "Synchronous portions of media reads must not execute on Dispatcher");
                    }
                    finally
                    {
                        release.Set(); panel.Stop();
                        PumpUntil(() => !Field<bool>(panel, "reading"), 4000);
                    }
                }
            }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        }

        private static T Field<T>(object target, string name)
        { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }

        private static void ConcurrentShutdownDoesNotBlockInput()
        {
            using (var cancellationEntered = new ManualResetEventSlim())
            using (var releaseCancellation = new ManualResetEventSlim())
            using (var service = new MediaService())
            {
                var begin = typeof(MediaService).GetMethod("TryBeginOperation", BindingFlags.Instance | BindingFlags.NonPublic);
                var end = typeof(MediaService).GetMethod("EndOperation", BindingFlags.Instance | BindingFlags.NonPublic);
                var arguments = new object[] { default(CancellationToken) };
                Check((bool)begin.Invoke(service, arguments), "Media operation owns a lifetime reference");
                bool operationActive = true;
                var cancellation = (CancellationToken)arguments[0];
                using (cancellation.Register(delegate { cancellationEntered.Set(); releaseCancellation.Wait(3000); }))
                {
                    try
                    {
                        var watch = Stopwatch.StartNew(); service.Dispose(); service.Dispose(); watch.Stop();
                        Check(watch.ElapsedMilliseconds < 500, "Stopping media must not wait for a slow cancellation callback");
                        Check(cancellationEntered.Wait(2000), "Cancellation begins on a worker");
                        Check(cancellation.IsCancellationRequested, "In-flight operation receives cancellation");
                        Check(!(bool)begin.Invoke(service, new object[] { default(CancellationToken) }), "Stopped service rejects new work");
                        var source = Field<CancellationTokenSource>(service, "lifetime");
                        Check(source.Token.IsCancellationRequested, "Token source remains valid during in-flight work");
                        end.Invoke(service, null); operationActive = false;
                        Check(!Field<bool>(service, "lifetimeDisposed"), "Cancellation callback retains the final lifetime reference");
                        releaseCancellation.Set();
                        Check(SpinWait.SpinUntil(() => Field<bool>(service, "lifetimeDisposed"), 2000), "Final background operation releases the token source");
                    }
                    finally
                    {
                        releaseCancellation.Set();
                        if (operationActive) end.Invoke(service, null);
                        service.Dispose();
                    }
                }
            }
        }

        private static void PumpUntil(Func<bool> completed, int timeoutMilliseconds)
        {
            var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (sender, args) => { if (completed() || watch.ElapsedMilliseconds >= timeoutMilliseconds) frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
            Check(completed(), "Media responsiveness check timed out");
        }

        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
