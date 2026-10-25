using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace CodexIsland
{
    // Pure state, fake transports and unstarted request configuration only.
    // No sockets, HTTP requests, user files, UI, clipboard or global input.
    internal static class ServiceReadyTests
    {
        private sealed class FakeProbe : IServiceReadyProbe
        {
            internal Func<ServiceReadyRequest, CancellationToken, Task<ServiceReadyObservation>> Read;
            internal int Calls; internal bool Disposed;
            public Task<ServiceReadyObservation> ProbeAsync(ServiceReadyRequest request, CancellationToken cancellation) { Interlocked.Increment(ref Calls); return Read(request, cancellation); }
            public void Dispose() { Disposed = true; }
        }
        internal static void Run()
        {
            ValidateTargets(); FileStability(); HttpConfiguration(); OneShotAndFairness(); CancelRemoveDispose(); DemoNeverProbes(); NativeTimeoutDoesNotOverlap(); NativePendingLimit();
            Console.WriteLine("PASS: readiness validation, HTTP no-redirect/body-free configuration, stable-file timing, one-shot/fair polling, cancellation and bounded late operations (fake transports only).");
        }
        private static ServiceReadyRequest Tcp(string name = "fixture") { return new ServiceReadyRequest { Kind = "tcp", Target = "localhost", Port = 3000, Name = name }; }
        private static Task<ServiceReadyObservation> ReadyResult() { return Task.FromResult(new ServiceReadyObservation { Ready = true }); }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception("Service readiness: " + message); }
        private static void Invalid(ServiceReadyRequest request) { try { request.Validate(); } catch (ArgumentException) { return; } throw new Exception("Service readiness: invalid target accepted"); }
        private static void ValidateTargets()
        {
            Tcp().Validate(); new ServiceReadyRequest { Kind = "tcp", Target = "[::1]", Port = 65535 }.Validate();
            new ServiceReadyRequest { Kind = "http", Target = "https://example.invalid/health" }.Validate();
            new ServiceReadyRequest { Kind = "file", Target = @"C:\synthetic\artifact.zip", StableSeconds = 1 }.Validate();
            Invalid(new ServiceReadyRequest { Kind = "tcp", Target = "http://localhost", Port = 3000 }); Invalid(new ServiceReadyRequest { Kind = "tcp", Target = "localhost", Port = 0 });
            Invalid(new ServiceReadyRequest { Kind = "http", Target = "file:///C:/fixture" }); Invalid(new ServiceReadyRequest { Kind = "http", Target = "https://user:secret@example.invalid/" });
            Invalid(new ServiceReadyRequest { Kind = "file", Target = "relative.zip" }); Invalid(new ServiceReadyRequest { Kind = "file", Target = @"C:\fixture.zip", StableSeconds = 0 });
            Invalid(new ServiceReadyRequest { Kind = "file", Target = @"C:\fixture.zip", StableSeconds = 3601 }); Invalid(new ServiceReadyRequest { Target = "localhost", TimeoutMilliseconds = 0 });
        }
        private static void FileStability()
        {
            var state = new ServiceReadyStability(); var file = new ServiceReadyObservation { FileExists = true, FileLength = 10, FileWriteUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            Check(!state.Observe(file, 5, 0) && !state.Observe(file, 5, 4.9) && state.Observe(file, 5, 5), "file requires full stable duration");
            file.FileLength++; Check(!state.Observe(file, 5, 6) && !state.Observe(file, 5, 10), "size change resets stable interval");
            file.FileWriteUtc = file.FileWriteUtc.AddSeconds(1); Check(!state.Observe(file, 5, 11) && state.Observe(file, 5, 16), "modification time change resets stable interval");
            Check(!state.Observe(ServiceReadyObservation.Waiting("missing"), 5, 20) && !state.Observe(file, 5, 30) && !state.Observe(file, 5, 34), "absence or unreadable file resets interval");
            Check(!state.Observe(file, 5, 1) && !state.Observe(file, 5, 5) && state.Observe(file, 5, 6), "backward clock resets interval");
        }
        private static void HttpConfiguration()
        {
            var request = ServiceReadyNativeProbe.CreateHttpRequest(new ServiceReadyRequest { Kind = "http", Target = "https://example.invalid/health", TimeoutMilliseconds = 2500 });
            try
            {
                Check(!request.AllowAutoRedirect && !request.KeepAlive && request.Proxy == null && request.Credentials == null && !request.UseDefaultCredentials, "unstarted HTTP request cannot redirect, reuse connections or send Windows credentials");
                Check(request.Method == "GET" && request.MaximumResponseHeadersLength == 32 && request.Timeout == 2500 && request.ReadWriteTimeout == 2500, "HTTP request limits and timeouts configured without starting it");
                Check(ServiceReadyNativeProbe.HttpStatus(200).Ready && ServiceReadyNativeProbe.HttpStatus(204).Ready && !ServiceReadyNativeProbe.HttpStatus(302).Ready && !ServiceReadyNativeProbe.HttpStatus(404).Ready && !ServiceReadyNativeProbe.HttpStatus(503).Ready, "only 2xx is healthy; redirects do not count as readiness");
            }
            finally { request.Abort(); }
        }
        private static void OneShotAndFairness()
        {
            var fake = new FakeProbe { Read = (request, token) => ReadyResult() }; int notifications = 0;
            using (var monitor = new ServiceReadyMonitor(false, fake))
            {
                monitor.Ready += watch => notifications++; monitor.Add(Tcp()); Check(fake.Calls == 0, "adding a task does not itself perform network I/O");
                monitor.PollAsync().GetAwaiter().GetResult(); monitor.PollAsync().GetAwaiter().GetResult();
                Check(notifications == 1 && fake.Calls == 1 && monitor.Snapshot[0].State == "ready", "completed watch notifies once and stops polling");
            }
            var names = new HashSet<string>(); fake = new FakeProbe { Read = (request, token) => { lock (names) names.Add(request.Name); return Task.FromResult(ServiceReadyObservation.Waiting("wait")); } };
            using (var monitor = new ServiceReadyMonitor(false, fake))
            {
                for (int i = 0; i < 20; i++) monitor.Add(Tcp("job" + i));
                for (int i = 0; i < 5; i++) monitor.PollAsync().GetAwaiter().GetResult();
                Check(names.Count == 20 && fake.Calls == 20, "four-item batches rotate fairly through all tasks");
                try { monitor.Add(Tcp("too many")); throw new Exception("limit ignored"); } catch (ArgumentException) { }
            }
        }
        private static void CancelRemoveDispose()
        {
            var gate = new TaskCompletionSource<ServiceReadyObservation>(); var started = new TaskCompletionSource<bool>();
            var fake = new FakeProbe { Read = (request, token) => { started.TrySetResult(true); return gate.Task; } }; int notifications = 0;
            using (var monitor = new ServiceReadyMonitor(false, fake))
            {
                monitor.Ready += watch => notifications++; string id = monitor.Add(Tcp()); Task first = monitor.PollAsync(); Check(started.Task.Wait(1500), "fake first poll started");
                Check(monitor.PollAsync().IsCompleted && fake.Calls == 1, "second poll cannot overlap first"); monitor.Cancel(id); gate.SetResult(new ServiceReadyObservation { Ready = true }); first.GetAwaiter().GetResult();
                Check(notifications == 0 && monitor.Snapshot[0].State == "canceled", "late completion cannot alert after cancel");
                gate = new TaskCompletionSource<ServiceReadyObservation>(); started = new TaskCompletionSource<bool>(); id = monitor.Add(Tcp("removed")); first = monitor.PollAsync(); Check(started.Task.Wait(1500), "fake removed poll started"); monitor.Remove(id); gate.SetResult(new ServiceReadyObservation { Ready = true }); first.GetAwaiter().GetResult();
                Check(notifications == 0 && monitor.Snapshot.Count == 1, "late completion cannot resurrect removed task");
            }
            gate = new TaskCompletionSource<ServiceReadyObservation>(); started = new TaskCompletionSource<bool>(); fake = new FakeProbe { Read = (request, token) => { started.TrySetResult(true); return gate.Task; } };
            var disposed = new ServiceReadyMonitor(false, fake); disposed.Ready += watch => notifications++; disposed.Add(Tcp()); Task pending = disposed.PollAsync(); Check(started.Task.Wait(1500), "fake disposal poll started"); disposed.Dispose(); gate.SetResult(new ServiceReadyObservation { Ready = true }); pending.GetAwaiter().GetResult();
            Check(notifications == 0 && fake.Disposed, "disposal cancels observers and suppresses late alerts");
        }
        private static void DemoNeverProbes()
        {
            var fake = new FakeProbe { Read = (request, token) => { throw new Exception("demo must not probe"); } };
            using (var monitor = new ServiceReadyMonitor(true, fake)) { monitor.Add(Tcp()); monitor.PollAsync().GetAwaiter().GetResult(); Check(fake.Calls == 0, "demo never probes even if a task is added"); }
        }
        private static void NativeTimeoutDoesNotOverlap()
        {
            int starts = 0, aborts = 0; var late = new TaskCompletionSource<ServiceReadyObservation>();
            using (var probe = new ServiceReadyNativeProbe(request => Interlocked.Increment(ref starts) == 1 ? late.Task : ReadyResult(), request => Interlocked.Increment(ref aborts)))
            {
                var request = Tcp(); request.TimeoutMilliseconds = 250;
                Check(!probe.ProbeAsync(request, CancellationToken.None).GetAwaiter().GetResult().Ready, "fake native operation times out");
                Check(!probe.ProbeAsync(request, CancellationToken.None).GetAwaiter().GetResult().Ready && starts == 1 && aborts >= 1, "late native operation remains tracked instead of spawning overlapping retries");
                late.SetException(new IOException("synthetic late failure"));
                Check(probe.ProbeAsync(request, CancellationToken.None).GetAwaiter().GetResult().Ready && starts == 2, "completed late operation can be retried, with its fault observed");
            }
        }
        private static void NativePendingLimit()
        {
            int starts = 0; var gate = new TaskCompletionSource<ServiceReadyObservation>();
            using (var probe = new ServiceReadyNativeProbe(request => { Interlocked.Increment(ref starts); return gate.Task; }, request => { }))
            using (var cancellation = new CancellationTokenSource())
            {
                var tasks = new List<Task<ServiceReadyObservation>>();
                for (int i = 0; i < 8; i++) tasks.Add(probe.ProbeAsync(new ServiceReadyRequest { Target = "host" + i, TimeoutMilliseconds = 10000 }, cancellation.Token));
                var ninth = probe.ProbeAsync(new ServiceReadyRequest { Target = "host9", TimeoutMilliseconds = 10000 }, cancellation.Token);
                Check(ninth.IsCompleted && !ninth.Result.Ready && starts == 8, "retired or stalled native operations have a hard global limit");
                cancellation.Cancel(); try { Task.WhenAll(tasks).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                gate.SetResult(ServiceReadyObservation.Waiting("finished"));
            }
        }
    }
}
