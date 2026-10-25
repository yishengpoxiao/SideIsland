using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace CodexIsland
{
    // Synthetic operations only: never reads notifications or requests permission.
    internal static class NotificationListenerTests
    {
        public static void Run()
        {
            SuccessfulAndFailedReads();
            TimeoutAndLateCompletion();
            BlockingCleanup(true);
            BlockingCleanup(false);
            SuccessfulReadWithBlockedClose();
            StartupFailures();
            Console.WriteLine("PASS: notification deadlines survive blocked Cancel/Close, bound retries, discard late results and release normal operations.");
        }

        private static void SuccessfulAndFailedReads()
        {
            using (var completed = new FakeOperation { Status = AsyncStatus.Completed })
            {
                Check(Convert(() => completed, 1000).GetAwaiter().GetResult() == 42, "completed reads return their result");
                WaitIdle(); Check(completed.Closed && completed.CloseCount == 1 && completed.CancelCount == 0, "successful reads close exactly once without canceling");
            }
            using (var failed = new FakeOperation { Status = AsyncStatus.Error })
            {
                Expect<UnauthorizedAccessException>(Convert(() => failed, 1000), "operation failure preserves original error");
                WaitIdle(); Check(failed.Closed && failed.CloseCount == 1, "failed reads also close exactly once");
            }
        }

        private static void TimeoutAndLateCompletion()
        {
            using (var hanging = new FakeOperation { Status = AsyncStatus.Started })
            {
                var elapsed = Stopwatch.StartNew(); Expect<TimeoutException>(Convert(() => hanging, 80), "hanging read times out"); WaitIdle();
                Check(hanging.CancelCount == 1 && hanging.CloseCount == 1 && elapsed.ElapsedMilliseconds < 2000, "ordinary timeout promptly cancels and closes once");
            }
            using (var late = new FakeOperation { Status = AsyncStatus.Started, CompleteOnCancel = false })
            {
                Task<int> request = Convert(() => late, 80); Expect<TimeoutException>(request, "late result fixture times out first"); WaitIdle();
                late.Complete(99);
                Expect<TimeoutException>(request, "late successful completion cannot replace the previously timed-out result");
                Check(late.CloseCount == 1 && late.CancelCount == 1, "late callback never schedules duplicate cleanup");
            }
            using (var lateFailure = new FakeOperation { Status = AsyncStatus.Started, CompleteOnCancel = false })
            {
                Task<int> request = Convert(() => lateFailure, 80); Expect<TimeoutException>(request, "late failure fixture times out first"); WaitIdle();
                lateFailure.Fail(); Expect<TimeoutException>(request, "late failure stays isolated from the already completed deadline");
                Check(lateFailure.CloseCount == 1, "late failure does not close an operation twice");
            }
        }

        private static void BlockingCleanup(bool blockCancel)
        {
            using (var operation = new FakeOperation { Status = AsyncStatus.Started })
            {
                if (blockCancel) operation.AllowCancel.Reset(); else operation.AllowClose.Reset();
                var elapsed = Stopwatch.StartNew(); Task<int> request = Convert(() => operation, 80);
                try
                {
                    Check((blockCancel ? operation.CancelEntered : operation.CloseEntered).Wait(2000), "fixture enters the selected blocking cleanup call");
                    Expect<TimeoutException>(request, "timeout completes even while " + (blockCancel ? "Cancel" : "Close") + " never returns");
                    Check(elapsed.ElapsedMilliseconds < 2000 && !operation.Closed, "cleanup does not extend the caller's deadline");
                    VerifyRetriesAreBounded();
                    Check(operation.CancelCount == 1 && operation.CloseCount == (blockCancel ? 0 : 1), "retries do not schedule more cleanup workers for blocked operation");
                }
                finally { operation.AllowCancel.Set(); operation.AllowClose.Set(); WaitIdle(); }
                Check(operation.Closed && operation.CloseCount == 1, "releasing blocked cleanup eventually closes its operation");
                using (var recovered = new FakeOperation { Status = AsyncStatus.Completed, Result = 73 })
                {
                    Check(Convert(() => recovered, 1000).GetAwaiter().GetResult() == 73, "cleanup completion allows the next read to recover"); WaitIdle();
                }
            }
        }

        private static void SuccessfulReadWithBlockedClose()
        {
            using (var operation = new FakeOperation { Status = AsyncStatus.Completed })
            {
                operation.AllowClose.Reset();
                try
                {
                    Task<int> result = Convert(() => operation, 1000);
                    Check(operation.CloseEntered.Wait(2000), "successful read entered cleanup");
                    WaitCompleted(result); Check(result.GetAwaiter().GetResult() == 42, "successful result is delivered even when Close blocks inside an already-completed callback");
                    VerifyRetriesAreBounded(); Check(operation.CancelCount == 0 && operation.CloseCount == 1, "successful cleanup is neither canceled nor duplicated");
                }
                finally { operation.AllowClose.Set(); WaitIdle(); }
            }
        }

        private static void VerifyRetriesAreBounded()
        {
            int created = 0;
            for (int retry = 0; retry < 12; retry++)
            {
                Task<int> request = Convert(() => { created++; return new FakeOperation { Status = AsyncStatus.Completed }; }, 80);
                Expect<InvalidOperationException>(request, "retry is postponed while old OS operation still owns the read slot");
            }
            Check(created == 0, "blocked retries never invoke the Windows operation factory or accumulate OS reads");
        }

        private static void StartupFailures()
        {
            Expect<UnauthorizedAccessException>(Convert(() => { throw new UnauthorizedAccessException(); }, 1000), "operation creation error is preserved"); WaitIdle();
            Expect<InvalidOperationException>(Convert(() => null, 1000), "null operation is rejected without retaining slot"); WaitIdle();
            using (var broken = new FakeOperation { Status = AsyncStatus.Started, ThrowOnRegister = true })
            {
                Expect<InvalidOperationException>(Convert(() => broken, 1000), "completion registration failure is contained"); WaitIdle();
                Check(broken.CancelCount == 1 && broken.CloseCount == 1, "registration failure cleans the operation and releases the slot");
            }
            using (var recovered = new FakeOperation { Status = AsyncStatus.Completed })
            {
                Check(Convert(() => recovered, 1000).GetAwaiter().GetResult() == 42, "startup failures do not poison subsequent reads"); WaitIdle();
            }
        }

        private static Task<int> Convert(Func<IAsyncOperation<int>> startOperation, int milliseconds)
        {
            MethodInfo method = typeof(NotificationListenerService).GetMethod("ToTaskWithTimeout", BindingFlags.NonPublic | BindingFlags.Static);
            return (Task<int>)method.MakeGenericMethod(typeof(int)).Invoke(null, new object[] { startOperation, milliseconds });
        }
        private static void WaitCompleted(Task task)
        {
            var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.ElapsedMilliseconds < 2000) Thread.Sleep(5);
            Check(task.IsCompleted, "managed notification task completes within test deadline");
        }
        private static void WaitIdle()
        {
            FieldInfo field = typeof(NotificationListenerService).GetField("readInFlight", BindingFlags.NonPublic | BindingFlags.Static);
            var watch = Stopwatch.StartNew(); while ((int)field.GetValue(null) != 0 && watch.ElapsedMilliseconds < 3000) Thread.Sleep(5);
            Check((int)field.GetValue(null) == 0, "notification cleanup releases the shared read slot");
        }
        private static void Expect<T>(Task task, string message) where T : Exception
        {
            WaitCompleted(task); try { task.GetAwaiter().GetResult(); } catch (T) { return; } throw new Exception("Notification listener: " + message);
        }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception("Notification listener: " + message); }

        private sealed class FakeOperation : IAsyncOperation<int>, IDisposable
        {
            public readonly ManualResetEventSlim AllowCancel = new ManualResetEventSlim(true), AllowClose = new ManualResetEventSlim(true), CancelEntered = new ManualResetEventSlim(), CloseEntered = new ManualResetEventSlim();
            public volatile bool Closed;
            public int CancelCount, CloseCount, Result = 42;
            public bool CompleteOnCancel = true, ThrowOnRegister;
            public AsyncStatus Status { get; set; }
            public uint Id { get { return 1; } }
            public Exception ErrorCode { get { return new UnauthorizedAccessException(); } }
            private AsyncOperationCompletedHandler<int> handler;
            public AsyncOperationCompletedHandler<int> Completed
            {
                get { return handler; }
                set { if (ThrowOnRegister) throw new InvalidOperationException("fixture registration failure"); handler = value; if (Status != AsyncStatus.Started) handler(this, Status); }
            }
            public void Cancel()
            {
                Interlocked.Increment(ref CancelCount); CancelEntered.Set(); AllowCancel.Wait();
                if (CompleteOnCancel) { Status = AsyncStatus.Canceled; var callback = handler; if (callback != null) callback(this, Status); }
            }
            public void Close() { Interlocked.Increment(ref CloseCount); CloseEntered.Set(); AllowClose.Wait(); Closed = true; }
            public int GetResults() { return Result; }
            public void Complete(int value) { Result = value; Status = AsyncStatus.Completed; var callback = handler; if (callback != null) callback(this, Status); }
            public void Fail() { Status = AsyncStatus.Error; var callback = handler; if (callback != null) callback(this, Status); }
            public void Dispose() { AllowCancel.Dispose(); AllowClose.Dispose(); CancelEntered.Dispose(); CloseEntered.Dispose(); }
        }
    }
}
