using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CodexIsland
{
    // Never follows redirects, reads response bodies or supplies credentials.
    // Timed-out native operations stay tracked until they really finish, so a
    // blocked DNS lookup or UNC metadata call cannot create unlimited retries.
    internal sealed class ServiceReadyNativeProbe : IServiceReadyProbe
    {
        private sealed class Pending
        {
            internal Task<ServiceReadyObservation> Task;
            internal Action Abort;
            internal int AbortQueued;
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>(StringComparer.Ordinal);
        private readonly Func<ServiceReadyRequest, Task<ServiceReadyObservation>> syntheticTransport;
        private readonly Action<ServiceReadyRequest> syntheticAbort;
        private bool disposed;
        internal ServiceReadyNativeProbe() : this(null, null) { }
        internal ServiceReadyNativeProbe(Func<ServiceReadyRequest, Task<ServiceReadyObservation>> start, Action<ServiceReadyRequest> abort)
        { syntheticTransport = start; syntheticAbort = abort; }
        internal static HttpWebRequest CreateHttpRequest(ServiceReadyRequest value)
        {
            var request = (HttpWebRequest)WebRequest.Create(value.Target);
            request.AllowAutoRedirect = false; request.MaximumAutomaticRedirections = 1;
            request.Method = "GET"; request.KeepAlive = false; request.Proxy = null;
            request.UseDefaultCredentials = false; request.PreAuthenticate = false; request.Credentials = null;
            request.Timeout = value.TimeoutMilliseconds; request.ReadWriteTimeout = value.TimeoutMilliseconds;
            request.MaximumResponseHeadersLength = 32; request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            request.UserAgent = "SideIsland-ServiceReady"; return request;
        }
        public async Task<ServiceReadyObservation> ProbeAsync(ServiceReadyRequest value, CancellationToken cancellation)
        {
            var request = value.Copy(); request.Validate(); cancellation.ThrowIfCancellationRequested(); Pending operation;
            string key = request.Kind + ":" + request.Target + (request.Kind == "tcp" ? ":" + request.Port : "");
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("ServiceReadyNativeProbe");
                foreach (string old in pending.Where(pair => pair.Value.Task.IsCompleted).Select(pair => pair.Key).ToArray()) pending.Remove(old);
                if (!pending.TryGetValue(key, out operation))
                {
                    if (pending.Count >= 8) return ServiceReadyObservation.Waiting("后台检查繁忙，等待上次连接结束。");
                    operation = Begin(request); pending.Add(key, operation);
                    ObserveFaults(operation.Task);
                }
            }
            using (var delay = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (cancellation.Register(() => Abort(operation)))
            {
                Task timeout = Task.Delay(request.TimeoutMilliseconds, delay.Token);
                Task winner = await Task.WhenAny(operation.Task, timeout).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (winner != operation.Task) { Abort(operation); return ServiceReadyObservation.Waiting("连接或文件检查超时，稍后重试。"); }
                delay.Cancel();
                try { return await operation.Task.ConfigureAwait(false); }
                catch { return ServiceReadyObservation.Waiting("暂时无法连接或读取，稍后重试。"); }
            }
        }
        private static void Abort(Pending operation)
        {
            if (Interlocked.Exchange(ref operation.AbortQueued, 1) != 0) return;
            // Cancellation is often requested from a UI button. Some network
            // providers do work while closing, so cleanup must stay off that UI.
            ThreadPool.QueueUserWorkItem(ignored => { try { if (operation.Abort != null) operation.Abort(); } catch { } });
        }
        private static void ObserveFaults(Task<ServiceReadyObservation> pendingTask)
        { pendingTask.ContinueWith(task => { var ignored = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); }
        private Pending Begin(ServiceReadyRequest request)
        {
            if (syntheticTransport != null) return new Pending { Task = syntheticTransport(request.Copy()), Abort = () => { if (syntheticAbort != null) syntheticAbort(request); } };
            if (request.Kind == "tcp")
            {
                var client = new TcpClient();
                return new Pending { Abort = () => client.Close(), Task = ConnectAsync(client, request.Target, request.Port) };
            }
            if (request.Kind == "http")
            {
                var http = CreateHttpRequest(request);
                return new Pending { Abort = http.Abort, Task = ReadHttpAsync(http) };
            }
            return new Pending { Abort = () => { }, Task = Task.Run(() => {
                try
                {
                    var file = new FileInfo(request.Target); file.Refresh();
                    if (!file.Exists) return ServiceReadyObservation.Waiting("文件尚不存在或不可读取。");
                    return new ServiceReadyObservation { FileExists = true, FileLength = file.Length, FileWriteUtc = file.LastWriteTimeUtc, Status = "文件存在，等待大小和修改时间保持稳定。" };
                }
                catch { return ServiceReadyObservation.Waiting("文件暂不可读取，稍后重试。"); }
            }) };
        }
        private static async Task<ServiceReadyObservation> ConnectAsync(TcpClient client, string host, int port)
        {
            try { await client.ConnectAsync(host, port).ConfigureAwait(false); return new ServiceReadyObservation { Ready = client.Connected, Status = "TCP 端口已接受连接" }; }
            catch { return ServiceReadyObservation.Waiting("TCP 端口暂未接受连接。"); }
            finally { client.Close(); }
        }
        private static async Task<ServiceReadyObservation> ReadHttpAsync(HttpWebRequest request)
        {
            HttpWebResponse response = null;
            try
            {
                // GetResponseAsync completes when headers arrive. Closing here
                // deliberately avoids reading even a single response-body byte.
                response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false); return HttpStatus((int)response.StatusCode);
            }
            catch (WebException error)
            {
                response = error.Response as HttpWebResponse;
                if (response != null) return HttpStatus((int)response.StatusCode);
                return ServiceReadyObservation.Waiting("HTTP 服务暂不可用，稍后重试。");
            }
            catch { return ServiceReadyObservation.Waiting("HTTP 服务暂不可用，稍后重试。"); }
            finally
            {
                // Aborting before disposal also prevents connection reuse from
                // draining a large response behind this headers-only probe.
                try { request.Abort(); } catch { }
                if (response != null) try { response.Close(); } catch { }
            }
        }
        internal static ServiceReadyObservation HttpStatus(int status)
        {
            return new ServiceReadyObservation { Ready = status >= 200 && status < 300,
                Status = status >= 300 && status < 400 ? "HTTP 返回重定向，未跟随。" : status >= 200 && status < 300 ? "HTTP 状态正常" : "HTTP 状态暂未正常" };
        }
        public void Dispose()
        {
            Pending[] operations; lock (gate) { if (disposed) return; disposed = true; operations = pending.Values.ToArray(); }
            foreach (var operation in operations) Abort(operation);
        }
    }
}
