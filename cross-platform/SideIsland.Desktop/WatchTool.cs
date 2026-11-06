using Avalonia.Controls;
using SideIsland.Core;
using System.Globalization;
using Avalonia.Threading;

namespace SideIsland.Desktop;

public sealed partial class IslandWindow
{
    private readonly WatchBook watchBook = new();
    private StackPanel? watchRows;
    private WatchEntry[]? renderedWatches;
    private Button? clearWatches;
    private Button? watchAddFile, watchAddProcess;
    private bool addingWatch;
    private TextBlock? watchQueue;
    private WatchProbeScheduler? watchScheduler;
    private DateTime nextWatch;
    private void RenderWatch()
    {
        var path = Input("watch-file"); path.Name = "watch-file";
        var pid = Input("watch-pid"); pid.Name = "watch-pid"; pid.MaxLength = 10;
        body.Children.Add(Hint(T("等待文件出现：绝对路径", "Wait for a file: absolute path"))); body.Children.Add(path);
        watchAddFile = AsyncButton(T("守望文件", "Watch file"), () => AddWatchAsync(path.Text ?? "", false), () => !addingWatch && !closing); watchAddFile.Name = "watch-add-file";
        body.Children.Add(Actions(watchAddFile));
        body.Children.Add(Hint(T("等待进程退出：PID", "Wait for a process to exit: PID"))); body.Children.Add(pid);
        watchAddProcess = AsyncButton(T("守望进程", "Watch process"), () => AddWatchAsync(pid.Text ?? "", true), () => !addingWatch && !closing); watchAddProcess.Name = "watch-add-process";
        body.Children.Add(Actions(watchAddProcess));
        body.Children.Add(Hint(T("最多同时守望 24 项，保留最近 24 条完成记录。已有文件不会补发提醒；取消守望不会终止进程。", "Up to 24 active watches and 24 finished records. Existing files do not trigger alerts. Canceling a watch never stops its process.")));
        clearWatches = Button(T("清除已完成", "Clear finished"), () => { if (closing) return; foreach (var item in watchBook.RemoveFinished()) SettleWatchActivity(item); RefreshWatchRows(); }); clearWatches.Name = "watch-clear-finished";
        body.Children.Add(Actions(clearWatches)); watchQueue = Hint(""); watchQueue.Name = "watch-queue"; watchQueue.IsVisible = false; body.Children.Add(watchQueue);
        watchRows = new StackPanel { Name = "watch-rows", Spacing = 4 }; body.Children.Add(watchRows); renderedWatches = null; RefreshWatchRows();
        body.Children.Add(Hint(T("后台每秒检查，退出应用即停止。进程身份暂不可读时会重试；原 PID 被复用时视为原进程退出。文件出现不代表写入已完成。", "Checks every second in the background while the app runs. Unreadable process identities are retried; PID reuse means the original process exited. File appearance does not guarantee writing is complete.")));
    }
    private async Task AddWatchAsync(string value, bool process)
    {
        if (closing || addingWatch) return; addingWatch = true; UpdateWatchAdding(); Report(T("正在检查守望目标…", "Checking watch target…"));
        try
        {
            int pid = 0;
            if (process && (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid <= 0)) throw new WatchProblem("pid");
            var entry = await Task.Run(() => process ? watchBook.AddProcess(pid, lifetime.Token) : watchBook.AddFile(value, lifetime.Token), lifetime.Token);
            if (closing) return;
            // A completion can race addition's continuation; use the current
            // model state so a completed task is never reverted to running.
            var current = watchBook.Snapshot.FirstOrDefault(item => item.Id == entry.Id);
            if (current is null) return;
            if (current.Completed) SettleWatchActivity(current); else PublishWatch(current, "running");
            Report(current.Completed ? T("守望已完成。", "Watch completed.") : T("已开始守望。", "Watch started.")); RefreshWatchRows();
        }
        catch (WatchProblem problem) { if (!closing) Report(WatchProblemMessage(problem.Code)); }
        finally { addingWatch = false; UpdateWatchAdding(); }
    }
    private void UpdateWatchAdding()
    {
        if (watchAddFile is not null) watchAddFile.IsEnabled = !closing && !addingWatch;
        if (watchAddProcess is not null) watchAddProcess.IsEnabled = !closing && !addingWatch;
    }
    private string WatchProblemMessage(string code) => code switch
    {
        "path" => T("请输入文件的绝对路径。", "Enter an absolute file path."),
        "directory" => T("请选择文件路径，不能选择文件夹。", "Choose a file path, not a folder."),
        "exists" => T("文件已经存在，未启动守望。", "The file already exists; no watch was started."),
        "pid" => T("请输入大于 0 的 PID。", "Enter a PID greater than zero."),
        "exited" => T("该进程已经退出，未启动守望。", "The process has exited; no watch was started."),
        "duplicate" => T("这个目标已经在守望中。", "This target is already being watched."),
        "capacity" => T("最多同时守望 24 项，请先取消不需要的任务。", "Up to 24 active watches; cancel an unneeded task first."),
        _ => T("无法添加守望。", "Could not add the watch.")
    };
    private void PublishWatch(WatchEntry item, string state)
    {
        var clean = new System.Text.StringBuilder();
        for (int index = 0; index < item.Target.Length; index++)
        {
            char value = item.Target[index]; if (char.IsControl(value)) continue;
            if (char.IsHighSurrogate(value)) { if (index + 1 < item.Target.Length && char.IsLowSurrogate(item.Target[index + 1])) clean.Append(value).Append(item.Target[++index]); }
            else if (!char.IsLowSurrogate(value)) clean.Append(value);
        }
        string title = clean.ToString().Trim(); if (title.Length == 0) title = T("任务守望", "Task watch");
        if (title.Length > 150) { int length = char.IsHighSurrogate(title[149]) ? 149 : 150; title = title[..length]; }
        try { tasks.Publish(new() { Id = item.Id, Title = title, State = state, Source = T("任务守望", "Task watch") }); }
        catch (InvalidOperationException error) { if (!testMode) DiagnosticLog.Write("watch-activity-capacity", error); }
    }
    private void RemoveWatch(string id)
    {
        if (closing) return; var removed = watchBook.Remove(id);
        if (removed is not null) { if (removed.Completed) SettleWatchActivity(removed); else PublishWatch(removed, "cancelled"); }
        RefreshWatchRows();
    }
    private void SettleWatchActivity(WatchEntry item)
    {
        // Explicit history cleanup must not recreate a task that the user
        // already cleared in Activities. A still-running task does need closure.
        if (tasks.Snapshot().Any(task => task.Id == item.Id && task.Active)) PublishWatch(item, "completed");
    }
    private void RefreshWatchRows()
    {
        if (watchRows is null || closing) return;
        var snapshot = watchBook.Snapshot;
        if (watchQueue is not null)
        {
            int waiting = watchScheduler?.WaitingForCapacity ?? 0; watchQueue.IsVisible = waiting > 0;
            watchQueue.Text = waiting > 0 ? T("部分检查响应较慢，", "Some checks are taking longer; ") + waiting + T(" 项正在排队。", " watches are queued.") : "";
        }
        if (clearWatches is not null) clearWatches.IsEnabled = snapshot.Any(item => item.Completed);
        if (renderedWatches is not null && renderedWatches.SequenceEqual(snapshot)) return;
        renderedWatches = snapshot; watchRows.Children.Clear();
        if (snapshot.Length == 0) { watchRows.Children.Add(Hint(T("还没有守望任务。", "No watches yet."))); return; }
        foreach (var item in snapshot)
        {
            var card = new StackPanel { Spacing = 4 };
            card.Children.Add(Text(item.Target + (item.IsProcess ? " · PID " + item.Pid : ""), 12));
            card.Children.Add(Hint(item.Completed ? T("已完成", "Completed") : item.Error.Length > 0 ? T("暂时无法检查，将自动重试。", "Temporarily unavailable; retrying.") : item.IsProcess ? T("等待进程退出", "Waiting for process exit") : T("等待文件出现", "Waiting for file")));
            var remove = Button(item.Completed ? T("移除记录", "Remove record") : T("取消守望", "Cancel watch"), () => RemoveWatch(item.Id)); remove.Name = "watch-remove-" + item.Id;
            card.Children.Add(Actions(remove)); watchRows.Children.Add(Card(card));
        }
    }
    private void PollWatches()
    {
        if (testMode || closing || DateTime.UtcNow < nextWatch) return;
        nextWatch = DateTime.UtcNow.AddSeconds(1); _ = PollWatchBookAsync();
    }
    internal async Task PollWatchBookAsync()
    {
        if (closing) return;
        try
        {
            if (watchScheduler is null)
            {
                watchScheduler = new(watchBook);
                watchScheduler.Probed += result => Dispatcher.UIThread.Post(() => ApplyWatchProbe(result));
                watchScheduler.Failed += error => Dispatcher.UIThread.Post(() => { if (!closing) Report(T("检查守望失败：", "Watch check failed: ") + error.Message); });
            }
            Task work = watchScheduler.PollAsync(lifetime.Token); RefreshWatchRows(); await work;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closing) Report(T("检查守望失败：", "Watch check failed: ") + error.Message); }
    }
    private void ApplyWatchProbe(WatchProbeResult? result)
    {
        if (closing) return;
        if (result?.CompletedNow == true)
        {
            // Completion is a terminal fact even if its retained row was cleared
            // or aged out before this callback. Only a retained row may notify.
            bool retained = watchBook.Snapshot.Any(item => item.Id == result.Entry.Id && item.Completed);
            if (retained) PublishWatch(result.Entry, "completed"); else SettleWatchActivity(result.Entry);
            if (retained) Notice(T("守望已完成", "Watch completed"), result.Entry.Target);
        }
        RefreshWatchRows();
    }
}
