using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodexIsland
{
    // The mutex is owned on the startup/UI thread. Signals are ready before any
    // WPF construction, so a second launch can queue a reveal during cold start.
    internal sealed class SingleInstance : IDisposable
    {
        internal const string DefaultPrefix = "Local\\CodexIsland.Desktop.";
        private readonly string prefix;
        private readonly Mutex mutex;
        private EventWaitHandle close, show;
        private MemoryMappedFile owner;
        private bool disposed;
        public bool IsPrimary { get; private set; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(uint processId);

        internal SingleInstance(string namePrefix = DefaultPrefix)
        {
            prefix = namePrefix;
            mutex = new Mutex(false, prefix + "v1");
            TryBecomePrimary();
        }

        internal bool TryBecomePrimary()
        {
            if (disposed) return false;
            if (IsPrimary) return true;
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return false;
            IsPrimary = true;
            try
            {
                close = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "Close");
                show = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "Show");
                try
                {
                    owner = MemoryMappedFile.CreateOrOpen(prefix + "Owner", sizeof(int), MemoryMappedFileAccess.ReadWrite);
                    using (var view = owner.CreateViewAccessor(0, sizeof(int), MemoryMappedFileAccess.Write))
                        view.Write(0, Process.GetCurrentProcess().Id);
                }
                // Foreground handoff is optional; it must never prevent startup.
                catch (IOException) { if (owner != null) owner.Dispose(); owner = null; }
                catch (UnauthorizedAccessException) { if (owner != null) owner.Dispose(); owner = null; }
                return true;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal bool TakeCloseRequest() { return close != null && close.WaitOne(0); }
        internal bool TakeShowRequest() { return show != null && show.WaitOne(0); }
        internal bool SignalShow(int timeoutMilliseconds) { return Signal("Show", timeoutMilliseconds, prefix, true); }

        internal static bool Signal(string action, int timeoutMilliseconds = 1500, string namePrefix = DefaultPrefix, bool foreground = false)
        {
            if (action != "Show" && action != "Close") throw new ArgumentException("Unsupported instance signal.", "action");
            var elapsed = Stopwatch.StartNew();
            do
            {
                try
                {
                    using (var handle = EventWaitHandle.OpenExisting(namePrefix + action))
                    {
                        if (foreground) GrantForeground(namePrefix);
                        handle.Set();
                        return true;
                    }
                }
                catch (WaitHandleCannotBeOpenedException) { }
                catch (UnauthorizedAccessException) { return false; }
                int remaining = timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds;
                if (remaining <= 0) return false;
                Thread.Sleep(Math.Min(25, remaining));
            } while (true);
        }

        private static void GrantForeground(string namePrefix)
        {
            try
            {
                using (var mapping = MemoryMappedFile.OpenExisting(namePrefix + "Owner", MemoryMappedFileRights.Read))
                using (var view = mapping.CreateViewAccessor(0, sizeof(int), MemoryMappedFileAccess.Read))
                {
                    int processId = view.ReadInt32(0);
                    if (processId > 0 && processId != Process.GetCurrentProcess().Id)
                        AllowSetForegroundWindow((uint)processId);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (show != null) { show.Dispose(); show = null; }
            if (close != null) { close.Dispose(); close = null; }
            if (owner != null) { owner.Dispose(); owner = null; }
            if (IsPrimary) { IsPrimary = false; mutex.ReleaseMutex(); }
            mutex.Dispose();
        }
    }
}
