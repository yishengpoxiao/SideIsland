using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace CodexIsland
{
    public enum NotificationAccessState
    {
        Unsupported,
        NeedsPackage,
        NotGranted,
        Denied,
        Allowed,
        Error
    }

    /// <summary>
    /// Reads only notifications published to the Windows notification platform.
    /// No chat process, database, network protocol, or private popup is inspected.
    /// </summary>
    public sealed class NotificationListenerService
    {
        private const int AppModelErrorNoPackage = 15700;
        private const int ErrorInsufficientBuffer = 122;
        // A timed-out WinRT read may still be stuck inside cancellation/cleanup.
        // Do not start another OS operation until that cleanup actually finishes.
        private static int readInFlight;
        private string lastError = "";

        // Diagnostics deliberately contain no notification text or application data.
        public string LastError { get { return lastError; } }
        public bool LastReadSucceeded { get; private set; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);

        public NotificationAccessState GetStatus()
        {
            try
            {
                if (!ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener"))
                    return NotificationAccessState.Unsupported;

                uint length = 0;
                int packageResult = GetCurrentPackageFullName(ref length, IntPtr.Zero);
                if (packageResult == AppModelErrorNoPackage)
                    return NotificationAccessState.NeedsPackage;
                if (packageResult != ErrorInsufficientBuffer && packageResult != 0)
                {
                    lastError = "无法检查 Windows 应用身份（" + packageResult.ToString(CultureInfo.InvariantCulture) + "）。";
                    return NotificationAccessState.Error;
                }

                return MapAccess(UserNotificationListener.Current.GetAccessStatus());
            }
            catch (EntryPointNotFoundException) { return NotificationAccessState.Unsupported; }
            catch (DllNotFoundException) { return NotificationAccessState.Unsupported; }
            catch (TypeLoadException) { return NotificationAccessState.Unsupported; }
            catch (Exception error)
            {
                SetError("无法检查通知权限", error);
                return NotificationAccessState.Error;
            }
        }

        /// <summary>
        /// Call only from an explicit user action on the WPF UI thread.
        /// Polling and normal application startup must never request consent.
        /// </summary>
        public async Task<bool> RequestAccessAsync()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                lastError = "请从应用界面的通知授权按钮开启权限。";
                return false;
            }

            try
            {
                UserNotificationListenerAccessStatus result = await ToTask(UserNotificationListener.Current.RequestAccessAsync());
                lastError = "";
                return result == UserNotificationListenerAccessStatus.Allowed;
            }
            catch (Exception error)
            {
                SetError("通知授权未完成", error);
                return false;
            }
        }

        // Invoked only by NotificationSyncRunner's worker. Return a self-contained
        // snapshot so UI presentation never needs a synchronous permission query.
        internal async Task<NotificationSnapshot> ReadSnapshotAsync()
        {
            NotificationAccessState before = GetStatus();
            if (before != NotificationAccessState.Allowed)
                return new NotificationSnapshot { Access = before, Error = before == NotificationAccessState.Error ? LastError : null };
            var messages = await ReadAsync().ConfigureAwait(false);
            bool succeeded = LastReadSucceeded; string error = LastError;
            NotificationAccessState after = GetStatus();
            if (after != NotificationAccessState.Allowed)
                return new NotificationSnapshot { Access = after, Error = after == NotificationAccessState.Error ? LastError : null };
            return new NotificationSnapshot { Access = after, ReadSucceeded = succeeded, Error = succeeded ? null : error, Messages = messages };
        }

        /// <summary>
        /// Returns a snapshot of currently available Windows toast notifications.
        /// Caller owns unread state and deduplication. No content is persisted here.
        /// </summary>
        public async Task<List<IslandNotification>> ReadAsync()
        {
            LastReadSucceeded = false;
            lastError = "";
            var result = new List<IslandNotification>();
            // Permission can be revoked while the application remains running.
            if (GetStatus() != NotificationAccessState.Allowed) return result;
            try
            {
                IReadOnlyList<UserNotification> notifications = await ToTaskWithTimeout(
                    () => UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast), 10000);

                foreach (UserNotification notification in notifications)
                {
                    try
                    {
                        var app = notification.AppInfo;
                        string appId = app == null ? "" : Clean(app.AppUserModelId, 1024);
                        string appName = app == null ? "应用通知" : Clean(app.DisplayInfo.DisplayName, 256);
                        if (String.IsNullOrWhiteSpace(appName)) appName = String.IsNullOrWhiteSpace(appId) ? "应用通知" : appId;

                        var content = notification.Notification;
                        var visual = content == null ? null : content.Visual;
                        if (visual == null) continue;
                        NotificationBinding binding = visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                        // Older Windows toast templates may use another binding name.
                        if (binding == null) binding = visual.Bindings.FirstOrDefault();
                        if (binding == null) continue;

                        var elements = binding.GetTextElements();
                        var text = new List<string>();
                        foreach (AdaptiveNotificationText element in elements.Take(16))
                        {
                            string value = Clean(element.Text, 4096);
                            if (!String.IsNullOrWhiteSpace(value)) text.Add(value);
                        }
                        result.Add(new IslandNotification
                        {
                            Id = notification.Id.ToString(CultureInfo.InvariantCulture),
                            AppId = appId,
                            AppName = appName,
                            Title = text.Count == 0 ? "新通知" : text[0],
                            Body = Clean(String.Join("\n", text.Skip(1)), 8192),
                            CreatedAt = notification.CreationTime.UtcDateTime,
                            IsRead = false,
                            IsDemo = false
                        });
                    }
                    // A broken notification must not hide the other applications' notifications.
                    catch (Exception) { }
                }
                var snapshot = result.OrderByDescending(item => item.CreatedAt).Take(200).ToList();
                lastError = "";
                LastReadSucceeded = true;
                return snapshot;
            }
            catch (NotificationReadPendingException)
            {
                lastError = "上次通知读取仍在结束处理中，已暂缓重试；稍后会自动恢复。";
                return result;
            }
            catch (TimeoutException error)
            {
                SetError("读取 Windows 通知超时，将自动重试", error);
                return result;
            }
            catch (Exception error)
            {
                SetError("读取 Windows 通知失败", error);
                return result;
            }
        }

        private static NotificationAccessState MapAccess(UserNotificationListenerAccessStatus state)
        {
            if (state == UserNotificationListenerAccessStatus.Allowed) return NotificationAccessState.Allowed;
            if (state == UserNotificationListenerAccessStatus.Denied) return NotificationAccessState.Denied;
            return NotificationAccessState.NotGranted;
        }

        private void SetError(string action, Exception error)
        {
            lastError = action + "（0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture) + "）。";
        }

        private static string Clean(string value, int maximum)
        {
            if (value == null) return "";
            string text = value.Replace("\0", "").Trim();
            return text.Length <= maximum ? text : text.Substring(0, maximum);
        }

        private sealed class NotificationReadPendingException : InvalidOperationException { }

        private static async Task<T> ToTaskWithTimeout<T>(Func<IAsyncOperation<T>> startOperation, int milliseconds)
        {
            if (milliseconds <= 0) throw new ArgumentOutOfRangeException("milliseconds");
            if (Interlocked.CompareExchange(ref readInFlight, 1, 0) != 0) throw new NotificationReadPendingException();
            OperationCleanup<T> cleanup = null;
            try
            {
                IAsyncOperation<T> operation = startOperation();
                if (operation == null) throw new InvalidOperationException("Windows returned no notification operation.");
                cleanup = new OperationCleanup<T>(operation, () => Interlocked.Exchange(ref readInFlight, 0));
                Task<T> task = ToTask(operation, cleanup);
                using (var deadline = new CancellationTokenSource())
                {
                    if (await Task.WhenAny(task, Task.Delay(milliseconds, deadline.Token)) == task)
                    {
                        deadline.Cancel();
                        return await task;
                    }

                    // Cleanup can itself block in COM. It must not delay the timeout
                    // or publish a late result; the occupied slot also bounds retries.
                    ObserveLateFailure(task);
                    cleanup.Schedule(true);
                    throw new TimeoutException("Windows notification read timed out.");
                }
            }
            catch
            {
                if (cleanup == null) Interlocked.Exchange(ref readInFlight, 0);
                else cleanup.Schedule(true);
                throw;
            }
        }

        private static async void ObserveLateFailure<T>(Task<T> task)
        {
            try { await task.ConfigureAwait(false); } catch (Exception) { }
        }

        // Use the documented IAsyncOperation completion contract directly. This keeps
        // the app buildable with Windows' existing .NET Framework compiler and WinMD
        // metadata, without requiring a Windows SDK or a NuGet runtime projection.
        private static Task<T> ToTask<T>(IAsyncOperation<T> operation)
        {
            var cleanup = new OperationCleanup<T>(operation, null);
            try { return ToTask(operation, cleanup); }
            catch { cleanup.Schedule(true); throw; }
        }

        private static Task<T> ToTask<T>(IAsyncOperation<T> operation, OperationCleanup<T> cleanup)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            operation.Completed = (finished, state) =>
            {
                T value = default(T); Exception failure = null;
                try
                {
                    if (state == AsyncStatus.Completed) value = finished.GetResults();
                    else if (state != AsyncStatus.Canceled) failure = finished.ErrorCode ?? new InvalidOperationException("Windows notification operation failed.");
                }
                catch (Exception error) { failure = error; }
                // Register cleanup before completing the managed task, but never
                // call Cancel/Close inline on WinRT's callback or Dispatcher thread.
                finally { cleanup.Schedule(false); }
                if (failure != null) completion.TrySetException(failure);
                else if (state == AsyncStatus.Canceled) completion.TrySetCanceled();
                else completion.TrySetResult(value);
            };
            return completion.Task;
        }

        private sealed class OperationCleanup<T>
        {
            private readonly IAsyncOperation<T> operation;
            private readonly Action release;
            private int scheduled;
            public OperationCleanup(IAsyncOperation<T> value, Action onReleased) { operation = value; release = onReleased; }
            public void Schedule(bool cancel)
            {
                if (Interlocked.CompareExchange(ref scheduled, 1, 0) != 0) return;
                Task.Run(delegate
                {
                    try
                    {
                        if (cancel) { try { operation.Cancel(); } catch (Exception) { } }
                        try { operation.Close(); } catch (Exception) { }
                    }
                    finally { if (release != null) release(); }
                });
            }
        }
    }
}
