using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using SideIsland.Core;

namespace SideIsland.Desktop;

public sealed partial class IslandWindow
{
    private readonly PortableLogController logController = new();
    private TextBox? logPath, logOutput;
    private TextBlock? logState, logSummary, logDraftHint, logKeywordsHint;
    private Button? logStop, logClear, logCopy, logChoose;
    private string logStartedDraft = "", logFilter = "";
    private bool logFollow = true, choosingLog, copyingLog, invalidLogKeywords;
    private DateTime nextLog;
    internal Task LatestLogPoll { get; private set; } = Task.CompletedTask;
    internal Task LatestLogChoose { get; private set; } = Task.CompletedTask;

    private void RenderLogs()
    {
        body.Children.Add(Hint(T("选择日志文件；也可输入尚未生成的绝对路径。仅点击开始后读取。", "Choose a log file, or enter an absolute path to a future file. Reading starts only when you click Start.")));
        var path = Input("log-path"); path.Name = "log-path"; logPath = path;
        path.PlaceholderText = T("日志文件的绝对路径", "Absolute path to a log file"); body.Children.Add(path);
        var choose = AsyncButton(T("选择文件", "Choose file"), () => LatestLogChoose = ChooseLogAsync(path), () => !choosingLog && !closing); choose.Name = "log-choose"; logChoose = choose;
        var start = Button(T("开始 / 重新开始", "Start / restart"), () => StartLog(path.Text ?? "")); start.Name = "log-start";
        body.Children.Add(Actions(choose, start));
        logDraftHint = Hint(""); logDraftHint.Name = "log-draft-hint"; body.Children.Add(logDraftHint);
        path.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && ReferenceEquals(logPath, path)) RefreshLogView(); };

        body.Children.Add(Hint(T("提醒关键词（逗号分隔，不区分大小写）", "Alert keywords (comma-separated, case-insensitive)")));
        var keywords = Input("log-keywords", "error, exception, failed, 失败, 错误"); keywords.Name = "log-keywords";
        keywords.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !closing) { ApplyLogKeywords(keywords.Text ?? ""); RefreshLogView(); } };
        body.Children.Add(keywords);
        logKeywordsHint = Hint(""); logKeywordsHint.Name = "log-keywords-hint"; body.Children.Add(logKeywordsHint);

        logState = Text(""); logState.Name = "log-state";
        logSummary = Hint(""); logSummary.Name = "log-summary";
        body.Children.Add(Card(new StackPanel { Spacing = 4, Children = { logState, logSummary } }));
        var search = Input("log-search"); search.Name = "log-search";
        search.PlaceholderText = T("筛选显示内容，不影响提醒", "Filter displayed lines; alerts are unchanged"); logFilter = search.Text ?? "";
        search.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) { logFilter = search.Text ?? ""; RefreshLogView(); } }; body.Children.Add(search);
        var follow = new CheckBox { Name = "log-follow", IsChecked = logFollow, Content = Text(T("跟随最新内容", "Follow latest lines"), 12) };
        follow.IsCheckedChanged += (_, _) => { logFollow = follow.IsChecked == true; if (logFollow) ScrollLogToEnd(); }; body.Children.Add(follow);
        logOutput = new TextBox { Name = "log-output", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160, MaxHeight = 240, FontSize = 11, FontFamily = new FontFamily("Cascadia Mono, Menlo, DejaVu Sans Mono, monospace") };
        ScrollViewer.SetHorizontalScrollBarVisibility(logOutput, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(logOutput, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        body.Children.Add(logOutput);
        logStop = Button(T("停止", "Stop"), () => { if (closing) return; logController.Stop(); Report(""); RefreshLogView(); }); logStop.Name = "log-stop";
        logClear = Button(T("清除视图", "Clear view"), () => { if (closing) return; logController.Clear(); Report(""); RefreshLogView(); }); logClear.Name = "log-clear";
        logCopy = AsyncButton(T("复制显示内容", "Copy shown"), CopyLogAsync, () => !copyingLog && !closing && !string.IsNullOrEmpty(logOutput?.Text)); logCopy.Name = "log-copy";
        body.Children.Add(Actions(logStop, logClear, logCopy));
        body.Children.Add(Hint(T("每秒读取，收起或切页后继续；退出即停止。仅保留最近 1,000 行／512 Ki 字符，每行最多 4,000 字符。首次最多读取末尾 64 KiB；大量积压时跳到末尾。清除视图不修改文件。", "Checks every second, including while collapsed or on another page; exits with the app. Keeps the latest 1,000 lines / 512 Ki characters, up to 4,000 characters per line. Initial reads use the last 64 KiB; large backlogs skip to the tail. Clear view never changes the file.")));
        RefreshLogView(true);
    }

    private async Task ChooseLogAsync(TextBox target)
    {
        if (closing || choosingLog || testMode && integrations.ChooseLogPath is null) return;
        choosingLog = true; string previous = target.Text ?? "";
        try
        {
            string? local;
            if (integrations.ChooseLogPath is not null) local = await integrations.ChooseLogPath();
            else
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = T("选择日志文件", "Choose log file"), AllowMultiple = false });
                if (files.Count == 0) return;
                local = files[0].TryGetLocalPath();
                if (local is null) { if (!closing) Report(T("请选择本机文件路径。", "Choose a local file path.")); return; }
            }
            if (closing || !ReferenceEquals(logPath, target) || !target.IsAttachedToVisualTree() || (target.Text ?? "") != previous) return;
            if (local is not null) target.Text = local;
        }
        finally { choosingLog = false; RefreshLogView(); }
    }

    private void StartLog(string target)
    {
        if (closing) return;
        string keywords = drafts.GetValueOrDefault("log-keywords", "error, exception, failed, 失败, 错误");
        if (!ApplyLogKeywords(keywords)) { RefreshLogView(); return; }
        try
        {
            logController.Start(target, keywords);
            logStartedDraft = target; Report(""); RefreshLogView(); LatestLogPoll = PollLogAsync();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or System.Security.SecurityException)
        { Report(T("请输入有效的日志绝对路径。", "Enter a valid absolute log file path.")); }
    }

    private bool ApplyLogKeywords(string value)
    {
        try { logController.SetKeywords(value); invalidLogKeywords = false; return true; }
        catch (ArgumentException) { invalidLogKeywords = true; return false; }
    }

    private void PollLogs()
    {
        if (testMode || closing || !logController.Snapshot.Running || DateTime.UtcNow < nextLog) return;
        nextLog = DateTime.UtcNow.AddSeconds(1); LatestLogPoll = PollLogAsync();
    }

    internal async Task PollLogAsync()
    {
        if (closing) return;
        try
        {
            var update = await logController.PollAsync();
            if (closing || update is null || !logController.IsCurrent(update)) return;
            if (!string.IsNullOrEmpty(update.Alert)) Notice(T("日志关键词提醒", "Log keyword match"), Path.GetFileName(update.Snapshot.Path) + "\n" + update.Alert);
            RefreshLogView();
        }
        catch (Exception error) { if (!closing) Report(T("日志读取失败：", "Log read failed: ") + error.Message); }
    }

    private void RefreshLogView(bool force = false)
    {
        if (closing || logOutput is null || page != "tools" || selected != "logs" || !force && !expanded) return;
        var snapshot = logController.Snapshot;
        string[] lines = snapshot.Lines.Where(line => line.Contains(logFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        string text = string.Join(Environment.NewLine, lines);
        if (logOutput.Text != text)
        {
            var viewer = logOutput.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(); var offset = viewer?.Offset;
            logOutput.Text = text;
            if (logFollow) ScrollLogToEnd(); else if (viewer is not null && offset.HasValue) viewer.Offset = offset.Value;
        }
        logState!.Text = snapshot.State switch
        {
            PortableLogState.Watching => T("正在追踪", "Watching"),
            PortableLogState.Missing => T("等待文件出现", "Waiting for file"),
            PortableLogState.Rotated => T("文件已替换，已读取新文件末尾", "File replaced; reading the new tail"),
            PortableLogState.Truncated => T("文件已截断，已重置读取位置", "File truncated; read position reset"),
            PortableLogState.Skipped => T("积压过多，已跳到文件末尾", "Large backlog; skipped to the tail"),
            PortableLogState.Retrying => T("暂时无法读取，将自动重试", "Temporarily unreadable; retrying"),
            _ => snapshot.Path.Length == 0 ? T("尚未开始", "Not started") : T("已停止，保留最后内容", "Stopped; last lines retained")
        };
        logSummary!.Text = (snapshot.Path.Length > 0 ? snapshot.Path + "\n" : "") + T($"显示 {lines.Length} / {snapshot.Lines.Count} 行 · 已读位置 {snapshot.Offset:N0} 字节", $"Showing {lines.Length} / {snapshot.Lines.Count} lines · Read position {snapshot.Offset:N0} bytes");
        logDraftHint!.Text = snapshot.Running && logPath?.Text != logStartedDraft ? T("路径尚未应用；点击开始切换。当前文件仍在追踪。", "Path edit not applied. Start to switch; the current file is still watched.") : "";
        logDraftHint.IsVisible = logDraftHint.Text.Length > 0;
        logKeywordsHint!.Text = invalidLogKeywords
            ? T("关键词超出限制：最多 20 个、每词 100 字符。当前仍使用上一次有效设置。", "Keyword limit exceeded: up to 20 terms of 100 characters each. Previous valid settings remain active.")
            : T("只提醒读取开始后新增的匹配行，最多每 10 秒提醒一次。最多 20 个关键词、每词 100 字符；启动历史不提醒。", "Only new matching lines trigger alerts, at most once every 10 seconds. Up to 20 keywords of 100 characters each; initial history never alerts.");
        logStop!.IsEnabled = snapshot.Running; logClear!.IsEnabled = snapshot.Lines.Count > 0;
        logCopy!.IsEnabled = text.Length > 0 && !copyingLog;
        logChoose!.IsEnabled = !choosingLog;
    }

    private void ScrollLogToEnd()
    {
        if (logOutput is null) return;
        logOutput.CaretIndex = logOutput.Text?.Length ?? 0;
        logOutput.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd();
        var current = logOutput;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!closing && logFollow && ReferenceEquals(logOutput, current) && current.IsAttachedToVisualTree())
                current.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private async Task CopyLogAsync()
    {
        if (closing || copyingLog || string.IsNullOrEmpty(logOutput?.Text)) return;
        string text = logOutput.Text; copyingLog = true;
        try
        {
            if (integrations.WriteClipboardText is not null) await integrations.WriteClipboardText(text);
            else if (!testMode && Clipboard is not null) await Clipboard.SetTextAsync(text);
            else return;
        }
        finally { copyingLog = false; RefreshLogView(); }
    }
}
