using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodexIsland
{
    internal interface IKeepAwakeNative
    {
        uint SetState(uint state);
    }
    internal sealed class KeepAwakeNative : IKeepAwakeNative
    {
        public uint SetState(uint state) { return SetThreadExecutionState(state); }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SetThreadExecutionState(uint state);
    }
    internal sealed class KeepAwakeDemoNative : IKeepAwakeNative
    {
        public uint SetState(uint state) { return 0x80000000; }
    }
    internal sealed class KeepAwakeSession : IDisposable
    {
        internal const uint Continuous = 0x80000000, SystemRequired = 0x00000001;
        private readonly IKeepAwakeNative native;
        private readonly Func<TimeSpan> clock;
        private readonly int ownerThread;
        private TimeSpan deadline;
        private bool disposed;
        public bool IsRunning { get; private set; }
        public TimeSpan Remaining { get { return IsRunning ? Max(TimeSpan.Zero, deadline - clock()) : TimeSpan.Zero; } }
        public KeepAwakeSession(IKeepAwakeNative adapter, Func<TimeSpan> elapsedClock)
        {
            if (adapter == null || elapsedClock == null) throw new ArgumentNullException();
            native = adapter; clock = elapsedClock; ownerThread = Thread.CurrentThread.ManagedThreadId;
        }
        public static KeepAwakeSession Create(bool demo)
        {
            var stopwatch = Stopwatch.StartNew();
            return new KeepAwakeSession(demo ? (IKeepAwakeNative)new KeepAwakeDemoNative() : new KeepAwakeNative(), () => stopwatch.Elapsed);
        }
        public void Start(int minutes)
        {
            CheckThread(); if (disposed) throw new ObjectDisposedException("KeepAwakeSession");
            if (minutes != 30 && minutes != 60 && minutes != 120) throw new ArgumentException("请选择 30、60 或 120 分钟。");
            if (native.SetState(Continuous | SystemRequired) == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "系统未接受保持唤醒请求。");
            deadline = clock().Add(TimeSpan.FromMinutes(minutes)); IsRunning = true;
        }
        public void Stop()
        {
            CheckThread();
            if (!IsRunning) return;
            if (native.SetState(Continuous) == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "释放保持唤醒请求失败，请重试停止。");
            IsRunning = false;
        }
        public bool Poll()
        {
            CheckThread();
            if (IsRunning && Remaining <= TimeSpan.Zero) { Stop(); return true; }
            return false;
        }
        private void CheckThread() { if (Thread.CurrentThread.ManagedThreadId != ownerThread) throw new InvalidOperationException("保持唤醒必须在发起请求的同一线程上停止。"); }
        private static TimeSpan Max(TimeSpan a, TimeSpan b) { return a > b ? a : b; }
        public void Dispose() { CheckThread(); if (disposed) return; Stop(); disposed = true; }
    }
}
