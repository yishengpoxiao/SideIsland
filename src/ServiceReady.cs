using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace CodexIsland
{
    internal sealed class ServiceReadyRequest
    {
        internal string Name = "", Kind = "tcp", Target = "localhost";
        internal int Port = 3000, StableSeconds = 5, TimeoutMilliseconds = 2500;
        internal ServiceReadyRequest Copy() { return (ServiceReadyRequest)MemberwiseClone(); }
        internal void Validate()
        {
            Name = (Name ?? "").Trim(); Target = (Target ?? "").Trim();
            if (Name.Length > 60 || Name.Any(Char.IsControl) || Target.Length == 0 || Target.Length > 2048 || Target.Any(Char.IsControl)) throw new ArgumentException("请输入有效名称和监测目标。");
            if (TimeoutMilliseconds < 250 || TimeoutMilliseconds > 10000) throw new ArgumentException("连接超时应为 250 至 10000 毫秒。");
            if (Kind == "tcp")
            {
                string host = Target.Trim('[', ']');
                if (Uri.CheckHostName(host) == UriHostNameType.Unknown || Port < 1 || Port > 65535) throw new ArgumentException("请输入有效主机名称或 IP，以及 1 至 65535 的端口。");
                Target = host;
            }
            else if (Kind == "http")
            {
                Uri uri;
                if (!Uri.TryCreate(Target, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https") || String.IsNullOrEmpty(uri.Host) || !String.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("请输入完整 HTTP／HTTPS 地址，不要在地址中包含用户名或密码。");
            }
            else if (Kind == "file")
            {
                string root;
                try { root = Path.GetPathRoot(Target); }
                catch { throw new ArgumentException("请使用文件的完整绝对路径。"); }
                if (!Path.IsPathRooted(Target) || String.IsNullOrEmpty(root) || (!root.StartsWith("\\\\", StringComparison.Ordinal) && (root.Length < 3 || root[1] != ':'))) throw new ArgumentException("请使用文件的完整绝对路径。");
                if (StableSeconds < 1 || StableSeconds > 3600) throw new ArgumentException("文件稳定时长应为 1 至 3600 秒。");
            }
            else throw new ArgumentException("请选择 TCP、HTTP 或文件稳定监测。");
            if (Name.Length == 0) Name = Kind == "tcp" ? Target + ":" + Port : Kind == "http" ? "HTTP · " + new Uri(Target).Authority : Path.GetFileName(Target);
            if (String.IsNullOrWhiteSpace(Name)) Name = "文件稳定";
            if (Name.Length > 60) Name = Name.Substring(0, 60);
        }
    }
    internal sealed class ServiceReadyObservation
    {
        internal bool Ready, FileExists;
        internal long FileLength;
        internal DateTime FileWriteUtc;
        internal string Status = "等待检查";
        internal static ServiceReadyObservation Waiting(string text) { return new ServiceReadyObservation { Status = text }; }
    }
    internal interface IServiceReadyProbe : IDisposable
    {
        Task<ServiceReadyObservation> ProbeAsync(ServiceReadyRequest request, CancellationToken cancellation);
    }
    internal sealed class ServiceReadyStability
    {
        private long length;
        private DateTime modified;
        private double stableSince = Double.NaN;
        internal bool Observe(ServiceReadyObservation observation, int seconds, double now)
        {
            if (observation == null || !observation.FileExists || observation.FileLength < 0 || Double.IsNaN(now) || Double.IsInfinity(now)) { stableSince = Double.NaN; return false; }
            if (Double.IsNaN(stableSince) || length != observation.FileLength || modified != observation.FileWriteUtc || now < stableSince)
            { length = observation.FileLength; modified = observation.FileWriteUtc; stableSince = now; return false; }
            return now - stableSince >= seconds;
        }
    }
    internal sealed class ServiceReadyWatch
    {
        internal string Id, State, Status;
        internal ServiceReadyRequest Request;
        internal bool Running { get { return State == "waiting" || State == "checking"; } }
        internal ServiceReadyWatch Copy() { return new ServiceReadyWatch { Id = Id, State = State, Status = Status, Request = Request.Copy() }; }
    }
    internal sealed class ServiceReadyMonitor : IDisposable
    {
        internal const int MaximumTasks = 20;
        private sealed class Entry
        {
            internal ServiceReadyWatch Watch;
            internal ServiceReadyStability Stability = new ServiceReadyStability();
            internal CancellationTokenSource Cancellation = new CancellationTokenSource();
        }
        private readonly object gate = new object();
        private readonly List<Entry> entries = new List<Entry>();
        private readonly IServiceReadyProbe probe;
        private readonly bool demo;
        private readonly Func<double> clock;
        private bool busy, disposed;
        private int nextIndex;
        internal event Action Changed;
        internal event Action<ServiceReadyWatch> Ready;
        internal ServiceReadyMonitor(bool demoMode, IServiceReadyProbe adapter = null, Func<double> monotonicSeconds = null)
        {
            demo = demoMode; probe = adapter ?? new ServiceReadyNativeProbe(); clock = monotonicSeconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        }
        internal List<ServiceReadyWatch> Snapshot { get { lock (gate) return entries.Select(entry => entry.Watch.Copy()).ToList(); } }
        internal string Add(ServiceReadyRequest value)
        {
            if (value == null) throw new ArgumentNullException("value"); var request = value.Copy(); request.Validate(); string id;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("ServiceReadyMonitor");
                if (entries.Count >= MaximumTasks) throw new ArgumentException("最多保留 20 项就绪提醒，请先移除旧任务。");
                id = Guid.NewGuid().ToString("N"); entries.Add(new Entry { Watch = new ServiceReadyWatch { Id = id, Request = request, State = "waiting", Status = demo ? "演示模式不连接网络或读取文件。" : "等待检查" } });
            }
            AnnounceChanged(); return id;
        }
        internal void Cancel(string id)
        {
            Entry found;
            lock (gate) { found = entries.FirstOrDefault(entry => entry.Watch.Id == id); if (found == null || !found.Watch.Running) return; found.Watch.State = "canceled"; found.Watch.Status = "已取消"; }
            found.Cancellation.Cancel(); AnnounceChanged();
        }
        internal void Remove(string id) { RemoveForActivity(id); }
        internal ServiceReadyWatch RemoveForActivity(string id)
        {
            Entry found; ServiceReadyWatch removed;
            lock (gate) { found = entries.FirstOrDefault(entry => entry.Watch.Id == id); if (found == null) return null; removed = found.Watch.Copy(); found.Watch.State = "canceled"; entries.Remove(found); }
            found.Cancellation.Cancel(); AnnounceChanged(); return removed;
        }
        internal async Task PollAsync()
        {
            Entry[] batch;
            lock (gate)
            {
                if (disposed || demo || busy) return;
                var active = entries.Where(entry => entry.Watch.Running).ToArray(); if (active.Length == 0) return;
                busy = true; int start = nextIndex % active.Length; batch = active.Skip(start).Concat(active.Take(start)).Take(4).ToArray(); nextIndex = (start + batch.Length) % active.Length;
                foreach (var entry in batch) { entry.Watch.State = "checking"; entry.Watch.Status = "正在检查…"; }
            }
            AnnounceChanged();
            try { await Task.WhenAll(batch.Select(CheckAsync)).ConfigureAwait(false); }
            finally { lock (gate) busy = false; AnnounceChanged(); }
        }
        private async Task CheckAsync(Entry entry)
        {
            ServiceReadyObservation observed;
            try { observed = await Task.Run(() => probe.ProbeAsync(entry.Watch.Request.Copy(), entry.Cancellation.Token)).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch { observed = ServiceReadyObservation.Waiting("暂时无法检查，稍后重试。"); }
            ServiceReadyWatch completed = null;
            lock (gate)
            {
                if (disposed || !entries.Contains(entry) || !entry.Watch.Running || entry.Cancellation.IsCancellationRequested) return;
                bool ready = observed != null && (entry.Watch.Request.Kind == "file" ? entry.Stability.Observe(observed, entry.Watch.Request.StableSeconds, clock()) : observed.Ready);
                entry.Watch.State = ready ? "ready" : "waiting";
                entry.Watch.Status = ready ? "已就绪，已提醒一次" : observed == null ? "暂时无法检查，稍后重试。" : entry.Watch.Request.Kind == "file" && observed.FileExists ? "文件存在，等待大小和修改时间保持稳定。" : observed.Status;
                if (ready) completed = entry.Watch.Copy();
            }
            if (completed != null) { var handler = Ready; if (handler != null) handler(completed); }
        }
        private void AnnounceChanged() { Action handler; lock (gate) handler = disposed ? null : Changed; if (handler != null) handler(); }
        public void Dispose()
        {
            Entry[] values; lock (gate) { if (disposed) return; disposed = true; values = entries.ToArray(); foreach (var value in values) value.Watch.State = "canceled"; }
            foreach (var value in values) value.Cancellation.Cancel(); probe.Dispose();
        }
    }
}
