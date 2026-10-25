using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace CodexIsland
{
    internal sealed class ClipboardHistoryWorker : IDisposable
    {
        private readonly object gate = new object();
        private readonly AutoResetEvent signal;
        private readonly IClipboardHistoryAdapter adapter;
        private readonly int timeoutMilliseconds;
        private readonly TaskCompletionSource<bool> exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<ReadOutcome> pending;
        private bool stopped;
        private sealed class ReadOutcome
        {
            internal ClipboardHistoryPayload Payload;
            internal Exception Error;
        }
        internal Task Completion { get { return exited.Task; } }
        internal ClipboardHistoryWorker(IClipboardHistoryAdapter reader, int readTimeoutMilliseconds = 2000)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            if (readTimeoutMilliseconds < 1 || readTimeoutMilliseconds > 10000) throw new ArgumentOutOfRangeException("readTimeoutMilliseconds");
            adapter = reader; timeoutMilliseconds = readTimeoutMilliseconds; signal = new AutoResetEvent(false);
            try
            {
                var thread = new Thread(Run) { IsBackground = true, Name = "SideIsland Clipboard STA" }; thread.SetApartmentState(ApartmentState.STA); thread.Start();
            }
            catch { signal.Dispose(); throw; }
        }
        internal async Task<ClipboardHistoryPayload> ReadAsync()
        {
            Task<ReadOutcome> work;
            lock (gate)
            {
                if (stopped) return null;
                if (pending == null) { pending = new TaskCompletionSource<ReadOutcome>(TaskCreationOptions.RunContinuationsAsynchronously); signal.Set(); }
                work = pending.Task;
            }
            var winner = await Task.WhenAny(work, Task.Delay(timeoutMilliseconds)).ConfigureAwait(false);
            if (winner != work) return null;
            var result = await work.ConfigureAwait(false);
            // The shared promise carries an outcome rather than a fault. A driver
            // failing after every caller timed out cannot leave an unobserved task.
            if (result != null && result.Error != null) ExceptionDispatchInfo.Capture(result.Error).Throw();
            return result == null ? null : result.Payload;
        }
        private void Run()
        {
            try
            {
                while (true)
                {
                    signal.WaitOne(); TaskCompletionSource<ReadOutcome> job;
                    lock (gate) { if (stopped) return; job = pending; }
                    if (job == null) continue;
                    var result = new ReadOutcome();
                    try { result.Payload = adapter.Read(); } catch (Exception error) { result.Error = error; }
                    lock (gate)
                    {
                        if (ReferenceEquals(job, pending)) pending = null;
                        // Clear the in-flight slot before publishing completion:
                        // a continuation requesting another read receives fresh data.
                        job.TrySetResult(stopped ? null : result);
                        if (stopped) return;
                    }
                }
            }
            finally
            {
                lock (gate)
                {
                    stopped = true;
                    if (pending != null) { pending.TrySetResult(null); pending = null; }
                    signal.Dispose();
                }
                exited.TrySetResult(true);
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (stopped) return;
                stopped = true;
                if (pending != null) { pending.TrySetResult(null); pending = null; }
                signal.Set();
            }
        }
    }
}
