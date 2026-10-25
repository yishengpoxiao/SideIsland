using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace CodexIsland
{
    internal sealed class StartupDiagnosticData
    {
        public string Version = typeof(StartupDiagnosticData).Assembly.GetName().Version.ToString(), StartedUtc = "", Stage = "尚无启动记录", Windows = "";
        public long ElapsedMilliseconds;
        public bool Ready;
        public List<string> Errors = new List<string>();
        public string PreviousStage = "";
        public bool PreviousReady;
        public long PreviousElapsedMilliseconds;
        public List<string> PreviousErrors = new List<string>();
    }
    internal static class StartupDiagnostics
    {
        private static readonly object Gate = new object();
        private static StartupDiagnosticData data;
        private static Stopwatch watch;
        private static LocalDataFile<StartupDiagnosticData> file;
        private static bool writing, historyPending;
        private static int revision;
        internal static void Begin(bool demo)
        {
            lock (Gate) { file = new LocalDataFile<StartupDiagnosticData>("startup-diagnostics.json", demo, 32768); historyPending = !demo; watch = Stopwatch.StartNew(); data = new StartupDiagnosticData { StartedUtc = DateTime.UtcNow.ToString("O"), Stage = "启动中", Windows = Environment.OSVersion.Version.ToString() }; }
            Mark("应用初始化", false);
        }
        internal static void Mark(string stage, bool ready)
        {
            lock (Gate) { if (data == null || (data.Ready && ready)) return; data.Stage = stage; data.Ready = ready; data.ElapsedMilliseconds = watch.ElapsedMilliseconds; revision++; QueueWrite(); }
        }
        internal static void Failure(Exception error)
        {
            lock (Gate)
            {
                if (data == null) return;
                data.Errors.Add(error.GetType().Name + " · 0x" + error.HResult.ToString("X8"));
                data.Errors = data.Errors.Skip(Math.Max(0, data.Errors.Count - 10)).ToList(); revision++; QueueWrite();
            }
        }
        internal static StartupDiagnosticData Snapshot()
        {
            lock (Gate)
            {
                if (data == null) return new LocalDataFile<StartupDiagnosticData>("startup-diagnostics.json", false, 32768).Load(new StartupDiagnosticData());
                return Copy(data);
            }
        }
        private static StartupDiagnosticData Copy(StartupDiagnosticData value)
        { return new StartupDiagnosticData { Version = value.Version, StartedUtc = value.StartedUtc, Stage = value.Stage, Ready = value.Ready, ElapsedMilliseconds = value.ElapsedMilliseconds, Windows = value.Windows, Errors = new List<string>(value.Errors ?? new List<string>()), PreviousStage = value.PreviousStage, PreviousReady = value.PreviousReady, PreviousElapsedMilliseconds = value.PreviousElapsedMilliseconds, PreviousErrors = new List<string>(value.PreviousErrors ?? new List<string>()) }; }
        private static void QueueWrite()
        {
            if (writing) return; writing = true;
            Task.Run(delegate {
                while (true)
                {
                    StartupDiagnosticData snapshot; int ticket; LocalDataFile<StartupDiagnosticData> target; bool load;
                    lock (Gate) { target = file; load = historyPending; historyPending = false; }
                    if (load)
                    {
                        var previous = target.Load(new StartupDiagnosticData());
                        lock (Gate) if (ReferenceEquals(file, target))
                        {
                            data.PreviousStage = new[] { "启动中", "应用初始化", "窗口已创建", "窗口已加载", "界面已显示", "启动失败" }.Contains(previous.Stage) ? previous.Stage : "";
                            data.PreviousReady = previous.Ready; data.PreviousElapsedMilliseconds = Math.Max(0, previous.ElapsedMilliseconds);
                            data.PreviousErrors = (previous.Errors ?? new List<string>()).Concat(previous.PreviousErrors ?? new List<string>()).Where(x => x != null && System.Text.RegularExpressions.Regex.IsMatch(x, @"\A[A-Za-z][A-Za-z0-9.]{0,100} · 0x[0-9A-F]{8}\z")).Distinct().Take(10).ToList();
                        }
                    }
                    lock (Gate) { if (!ReferenceEquals(file, target)) continue; snapshot = Copy(data); ticket = revision; }
                    try { target.Save(snapshot); } catch (Exception) { }
                    lock (Gate) { if (ticket == revision) { writing = false; return; } }
                }
            });
        }
    }
}
