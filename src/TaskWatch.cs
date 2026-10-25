using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace CodexIsland
{
    internal sealed class WatchedProcess
    {
        public int Pid;
        public long StartUtcTicks;
        public string Name;
        public override string ToString() { return Name + " · PID " + Pid; }
    }
    internal sealed class TaskWatchItem
    {
        public string Id, Target, Kind, Status;
        public int Pid;
        public long StartUtcTicks;
        public bool Completed;
        public TaskWatchItem Copy() { return (TaskWatchItem)MemberwiseClone(); }
    }
    internal sealed class TaskWatchNotice
    {
        public string Id, Title, Body;
    }
    internal sealed class TaskWatchBook
    {
        private readonly object gate = new object();
        private readonly List<TaskWatchItem> items = new List<TaskWatchItem>();
        private readonly Func<string, bool> fileExists;
        private readonly Func<int, WatchedProcess> processIdentity;
        public TaskWatchBook() : this(File.Exists, ReadProcess) { }
        internal TaskWatchBook(Func<string, bool> fileProbe, Func<int, WatchedProcess> processProbe) { fileExists = fileProbe; processIdentity = processProbe; }
        public List<TaskWatchItem> Items { get { lock (gate) return items.Select(x => x.Copy()).ToList(); } }
        public string AddFile(string rawPath)
        {
            if (String.IsNullOrWhiteSpace(rawPath)) throw new ArgumentException("请输入要等待生成的文件完整路径。");
            string path;
            try { path = Path.GetFullPath(rawPath.Trim().Trim('"')); }
            catch (Exception error) { if (!(error is ArgumentException) && !(error is NotSupportedException) && !(error is PathTooLongException)) throw; throw new ArgumentException("文件路径无效。"); }
            if (!Path.IsPathRooted(rawPath.Trim().Trim('"'))) throw new ArgumentException("请输入完整路径，例如 D:\\项目\\output.zip。");
            if (Directory.Exists(path)) throw new ArgumentException("请选择文件路径，不能选择文件夹。");
            // A network path may take time to answer. Never hold the model lock
            // while probing it: the UI reads Items while background work runs.
            bool alreadyExists = fileExists(path);
            lock (gate)
            {
                CheckCapacity();
                if (items.Any(x => !x.Completed && x.Kind == "file" && String.Equals(x.Target, path, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("这个文件已经在守望中。");
                if (alreadyExists) return "文件已经存在，未启动守望；只有启动后出现的文件才会提醒。";
                items.Add(new TaskWatchItem { Id = Guid.NewGuid().ToString("N"), Target = path, Kind = "file", Status = "等待文件出现" });
            }
            return "已开始等待文件出现。文件存在即可提醒，不代表写入已经完成。";
        }
        public string AddProcess(int pid, long? expectedStart)
        {
            if (pid <= 0) throw new ArgumentException("PID 必须是大于 0 的整数。");
            WatchedProcess process = processIdentity(pid);
            if (process == null) return "该进程已经退出，未启动守望。";
            if (expectedStart.HasValue && expectedStart.Value != process.StartUtcTicks) return "选择的进程已退出，PID 已被其他进程使用；请刷新列表。";
            lock (gate)
            {
                CheckCapacity();
                if (items.Any(x => !x.Completed && x.Kind == "process" && x.Pid == pid && x.StartUtcTicks == process.StartUtcTicks)) throw new ArgumentException("这个进程已经在守望中。");
                items.Add(new TaskWatchItem { Id = Guid.NewGuid().ToString("N"), Target = process.Name, Kind = "process", Pid = pid, StartUtcTicks = process.StartUtcTicks, Status = "等待进程退出" });
            }
            return "已开始守望 " + process.Name + "（PID " + pid + "）。";
        }
        private void CheckCapacity() { if (items.Count >= 20) throw new ArgumentException("最多保留 20 条守望，请先移除已完成的条目。"); }
        public bool Remove(string id) { return RemoveForActivity(id) != null; }
        internal TaskWatchItem RemoveForActivity(string id)
        { lock (gate) { var item = items.FirstOrDefault(x => x.Id == id); if (item == null) return null; var removed = item.Copy(); items.Remove(item); return removed; } }
        public void Clear() { lock (gate) items.Clear(); }
        public List<TaskWatchNotice> Poll()
        {
            var notices = new List<TaskWatchNotice>();
            foreach (var candidate in Items.Where(x => !x.Completed))
            {
                bool complete = false; string error = null;
                try
                {
                    if (candidate.Kind == "file") complete = fileExists(candidate.Target);
                    else { var current = processIdentity(candidate.Pid); complete = current == null || current.StartUtcTicks != candidate.StartUtcTicks; }
                }
                catch (Exception failure)
                {
                    if (!(failure is Win32Exception) && !(failure is InvalidOperationException) && !(failure is UnauthorizedAccessException) && !(failure is IOException)) throw;
                    error = "暂时无法检查：" + failure.Message;
                }
                lock (gate)
                {
                    var item = items.FirstOrDefault(x => x.Id == candidate.Id);
                    if (item == null || item.Completed) continue;
                    item.Status = error ?? (complete ? (item.Kind == "file" ? "文件已出现 · 已提醒" : "进程已退出 · 已提醒") : item.Kind == "file" ? "等待文件出现" : "等待进程退出");
                    if (complete)
                    {
                        item.Completed = true;
                        notices.Add(new TaskWatchNotice { Id = item.Id, Title = item.Kind == "file" ? "等待的文件已出现" : "守望的进程已退出", Body = item.Kind == "file" ? item.Target : item.Target + "（PID " + item.Pid + "）已结束。" });
                    }
                }
            }
            return notices;
        }
        internal static WatchedProcess ReadProcess(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    if (process.HasExited) return null;
                    // Start time identifies the original instance even if Windows later reuses its PID.
                    return new WatchedProcess { Pid = pid, Name = process.ProcessName, StartUtcTicks = process.StartTime.ToUniversalTime().Ticks };
                }
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        internal static List<WatchedProcess> ListProcesses()
        {
            var result = new List<WatchedProcess>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try { if (!process.HasExited) result.Add(new WatchedProcess { Pid = process.Id, Name = process.ProcessName, StartUtcTicks = process.StartTime.ToUniversalTime().Ticks }); }
                    catch (Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
            return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Pid).ToList();
        }
    }
}
