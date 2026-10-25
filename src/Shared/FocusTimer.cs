using System;
using System.Diagnostics;

namespace CodexIsland
{
    internal interface IFocusClock
    {
        DateTime UtcNow { get; }
        TimeSpan Elapsed { get; }
    }

    internal sealed class SystemFocusClock : IFocusClock
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        public DateTime UtcNow { get { return DateTime.UtcNow; } }
        public TimeSpan Elapsed { get { return clock.Elapsed; } }
    }

    // Countdown deadlines survive suspend/resume. Stopwatch uses a monotonic clock
    // so clock synchronization and manual wall-clock edits cannot change its result.
    internal sealed class FocusTimer
    {
        private readonly IFocusClock clock;
        private TimeSpan duration, remaining, accumulated, startedAt;
        private DateTime deadline;
        private bool focusSession;
        public bool IsRunning { get; private set; }
        public bool IsStopwatch { get; private set; }
        public bool IsCompleted { get; private set; }
        public bool IsFocusSession { get { return !IsStopwatch && focusSession; } }
        public string Label { get; private set; }
        public int CompletedFocusSessions { get; private set; }
        public event Action Completed;

        public FocusTimer() : this(new SystemFocusClock()) { }
        internal FocusTimer(IFocusClock clock)
        {
            if (clock == null) throw new ArgumentNullException("clock");
            this.clock = clock;
            SetCountdown(TimeSpan.FromMinutes(25), "专注", true);
        }

        public TimeSpan Value
        {
            get
            {
                if (IsStopwatch)
                {
                    TimeSpan delta = IsRunning ? clock.Elapsed - startedAt : TimeSpan.Zero;
                    return accumulated + (delta < TimeSpan.Zero ? TimeSpan.Zero : delta);
                }
                TimeSpan value = IsRunning ? deadline - clock.UtcNow : remaining;
                return value < TimeSpan.Zero ? TimeSpan.Zero : value;
            }
        }

        public void SetCountdown(TimeSpan time, string label, bool isFocus)
        {
            if (time <= TimeSpan.Zero || time > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException("time");
            IsRunning = false; IsStopwatch = false; IsCompleted = false;
            duration = remaining = time; accumulated = TimeSpan.Zero;
            Label = label ?? "计时"; focusSession = isFocus;
        }

        public void SetStopwatch()
        {
            IsRunning = false; IsStopwatch = true; IsCompleted = false;
            accumulated = TimeSpan.Zero; Label = "秒表"; focusSession = false;
        }

        public void Start()
        {
            if (IsRunning) return;
            if (IsStopwatch) startedAt = clock.Elapsed;
            else
            {
                if (IsCompleted || remaining <= TimeSpan.Zero) remaining = duration;
                deadline = clock.UtcNow.Add(remaining);
            }
            IsCompleted = false; IsRunning = true;
        }

        public void Pause()
        {
            Poll();
            if (!IsRunning) return;
            if (IsStopwatch) accumulated = Value;
            else remaining = Value;
            IsRunning = false;
        }

        public void Reset()
        {
            IsRunning = false; IsCompleted = false;
            remaining = duration; accumulated = TimeSpan.Zero;
        }

        public void Poll()
        {
            if (!IsRunning || IsStopwatch || clock.UtcNow < deadline) return;
            IsRunning = false; IsCompleted = true; remaining = TimeSpan.Zero;
            if (focusSession) CompletedFocusSessions++;
            Action handler = Completed;
            if (handler != null) handler();
        }

        public static string Format(TimeSpan value, bool tenths)
        {
            if (value < TimeSpan.Zero) value = TimeSpan.Zero;
            long seconds = tenths ? (long)value.TotalSeconds : (long)Math.Ceiling(value.TotalSeconds);
            string result = seconds >= 3600
                ? String.Format("{0:00}:{1:00}:{2:00}", seconds / 3600, seconds / 60 % 60, seconds % 60)
                : String.Format("{0:00}:{1:00}", seconds / 60, seconds % 60);
            return tenths ? result + "." + (value.Milliseconds / 100) : result;
        }
    }
}
