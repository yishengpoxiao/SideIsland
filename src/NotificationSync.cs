using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodexIsland
{
    internal sealed class NotificationSnapshot
    {
        public NotificationAccessState? Access;
        public bool ReadSucceeded;
        public string Error;
        public List<IslandNotification> Messages = new List<IslandNotification>();
        public static NotificationSnapshot Failed(string error) { return new NotificationSnapshot { Error = error }; }
    }

    // Owns the entire permission/read/recheck operation, including synchronous COM
    // startup. A deadline frees the UI waiter, not the still-running worker slot.
    internal sealed class NotificationSyncRunner : IDisposable
    {
        private readonly Func<Task<NotificationSnapshot>> read;
        private readonly int timeoutMilliseconds;
        private readonly object sync = new object();
        private readonly TaskCompletionSource<bool> stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task<NotificationSnapshot> pending;
        private bool disposed;
        public NotificationSyncRunner(Func<Task<NotificationSnapshot>> readSnapshot, int timeout = 12000)
        {
            if (readSnapshot == null) throw new ArgumentNullException("readSnapshot");
            if (timeout <= 0) throw new ArgumentOutOfRangeException("timeout");
            read = readSnapshot; timeoutMilliseconds = timeout;
        }
        public async Task<NotificationSnapshot> SynchronizeAsync()
        {
            Task<NotificationSnapshot> work;
            lock (sync)
            {
                if (disposed) return NotificationSnapshot.Failed("通知同步已停止。");
                if (pending != null && !pending.IsCompleted) return NotificationSnapshot.Failed("上次通知连接仍未结束，已暂缓重试；其他工具可继续使用。");
                // The factory itself must run on the worker: GetStatus and WinRT
                // activation can block before their first asynchronous return.
                work = pending = Task.Run(read);
            }
            try
            {
                using (var deadline = new CancellationTokenSource())
                {
                    Task winner = await Task.WhenAny(work, Task.Delay(timeoutMilliseconds, deadline.Token), stopped.Task).ConfigureAwait(false);
                    deadline.Cancel();
                    if (winner != work)
                    {
                        ObserveLateResult(work);
                        return NotificationSnapshot.Failed(winner == stopped.Task ? "通知同步已停止。" : "连接 Windows 通知超时，将自动重试；其他工具可继续使用。");
                    }
                    var result = await work.ConfigureAwait(false);
                    lock (sync) if (disposed) return NotificationSnapshot.Failed("通知同步已停止。");
                    return result ?? NotificationSnapshot.Failed("Windows 未返回通知状态，将自动重试。");
                }
            }
            catch (Exception) { return NotificationSnapshot.Failed("通知连接暂不可用，将自动重试；也可检查系统通知访问权限。"); }
            finally
            {
                lock (sync) if (ReferenceEquals(pending, work) && work.IsCompleted) pending = null;
            }
        }
        private static async void ObserveLateResult(Task task) { try { await task.ConfigureAwait(false); } catch (Exception) { } }
        public void Dispose()
        {
            lock (sync) { if (disposed) return; disposed = true; }
            stopped.TrySetResult(true);
        }
    }
}
