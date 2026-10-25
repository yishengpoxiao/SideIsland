using System;
using System.Diagnostics;
using System.Threading;

namespace CodexIsland
{
    internal static class SingleInstanceTests
    {
        public static void Run()
        {
            QueuedRequests();
            ConcurrentClaims();
            StartupRetry();
            Takeover();
            AbandonedOwner();
            Console.WriteLine("PASS: pre-window launch requests, single-instance contention, delayed signal readiness and owner-exit takeover with isolated IPC names.");
        }

        private static string Name() { return "Local\\SideIsland.InstanceTests." + Guid.NewGuid().ToString("N") + "."; }
        private static void QueuedRequests()
        {
            string prefix = Name();
            using (var primary = new SingleInstance(prefix))
            {
                Check(primary.IsPrimary, "The first instance owns its mutex");
                Exception failure = null;
                var secondary = new Thread(() => {
                    try
                    {
                        using (var other = new SingleInstance(prefix))
                        {
                            Check(!other.IsPrimary, "A second startup cannot create another primary");
                            Check(other.SignalShow(100), "A launch request can queue before a window has been built");
                        }
                    }
                    catch (Exception error) { failure = error; }
                });
                secondary.Start(); Check(secondary.Join(1500), "Secondary launch finishes promptly"); if (failure != null) throw failure;
                Check(primary.TakeShowRequest() && !primary.TakeShowRequest(), "A queued launch is delivered exactly once after the UI becomes ready");
                Check(SingleInstance.Signal("Close", 0, prefix) && primary.TakeCloseRequest() && !primary.TakeCloseRequest(), "Graceful close requests retain one-shot delivery");
            }
            Check(!SingleInstance.Signal("Show", 0, prefix), "Signal handles are removed with their owning instance");
        }

        private static void ConcurrentClaims()
        {
            string prefix = Name(); int primaryCount = 0; Exception failure = null;
            var threads = new Thread[6];
            using (var ready = new CountdownEvent(threads.Length))
            using (var release = new ManualResetEvent(false))
            {
                try
                {
                    for (int i = 0; i < threads.Length; i++)
                    {
                        threads[i] = new Thread(() => {
                            try
                            {
                                using (var instance = new SingleInstance(prefix))
                                {
                                    if (instance.IsPrimary) Interlocked.Increment(ref primaryCount);
                                    ready.Signal(); release.WaitOne(3000);
                                }
                            }
                            catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
                        });
                        threads[i].Start();
                    }
                    Check(ready.Wait(2500), "Concurrent startup attempts all reach a decision");
                    Check(primaryCount == 1, "Exactly one of six concurrent startups becomes primary");
                }
                finally { release.Set(); foreach (var thread in threads) if (thread != null) thread.Join(1500); }
            }
            if (failure != null) throw failure;
        }

        private static void StartupRetry()
        {
            string prefix = Name(); bool delivered = false; Exception failure = null;
            using (var entered = new ManualResetEvent(false))
            {
                var secondary = new Thread(() => { try { entered.Set(); delivered = SingleInstance.Signal("Show", 1000, prefix); } catch (Exception error) { failure = error; } });
                secondary.Start(); Check(entered.WaitOne(1000), "Delayed-readiness sender started");
                Thread.Sleep(80);
                using (var receiver = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "Show"))
                {
                    Check(secondary.Join(1500), "Delayed-readiness retry remains bounded"); if (failure != null) throw failure;
                    Check(delivered && receiver.WaitOne(0), "A signal retries when a legacy owner's event is not ready yet");
                }
            }
            var elapsed = Stopwatch.StartNew();
            Check(!SingleInstance.Signal("Show", 80, Name()), "A missing receiver is reported instead of silently claiming success");
            Check(elapsed.ElapsedMilliseconds < 1000, "Retry never blocks startup indefinitely");
        }

        private static void Takeover()
        {
            string prefix = Name(); Exception failure = null;
            using (var legacyOwner = new Mutex(true, prefix + "v1"))
            using (var started = new ManualResetEvent(false))
            using (var released = new ManualResetEvent(false))
            {
                var secondary = new Thread(() => {
                    try
                    {
                        using (var instance = new SingleInstance(prefix))
                        {
                            Check(!instance.IsPrimary, "A live legacy owner retains the instance lock"); started.Set();
                            Check(released.WaitOne(1500), "Owner release arrives");
                            Check(instance.TryBecomePrimary(), "A startup can take over after the prior owner exits before revealing");
                            Check(SingleInstance.Signal("Show", 0, prefix) && instance.TakeShowRequest(), "The replacement owner publishes fresh signal handles");
                        }
                    }
                    catch (Exception error) { failure = error; started.Set(); }
                });
                secondary.Start(); Check(started.WaitOne(1000), "Replacement startup was waiting on the live owner");
                legacyOwner.ReleaseMutex(); released.Set();
                Check(secondary.Join(2000), "Replacement startup finishes"); if (failure != null) throw failure;
            }
        }

        private static void AbandonedOwner()
        {
            string prefix = Name(); Mutex abandoned = null;
            var crashedOwner = new Thread(() => { abandoned = new Mutex(true, prefix + "v1"); });
            crashedOwner.Start(); Check(crashedOwner.Join(1000), "The simulated failed owner has ended");
            try
            {
                using (var replacement = new SingleInstance(prefix))
                    Check(replacement.IsPrimary && SingleInstance.Signal("Show", 0, prefix) && replacement.TakeShowRequest(), "An abandoned startup mutex is recovered after a crashed owner");
            }
            finally { if (abandoned != null) abandoned.Dispose(); }
        }

        private static void Check(bool value, string reason) { if (!value) throw new Exception("Single instance: " + reason); }
    }
}
