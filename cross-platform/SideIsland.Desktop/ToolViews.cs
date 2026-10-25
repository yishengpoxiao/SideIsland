using System.Globalization;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
using CodexIsland;
using SideIsland.Core;

namespace SideIsland.Desktop;
public sealed partial class IslandWindow
{
    private void RenderTool()
    {
        switch (selected)
        {
            case "focus": RenderFocus(); break;
            case "media": RenderMedia(); break;
            case "quota": RenderQuota(); break;
            case "ports": RenderPorts(); break;
            case "notes": RenderNotes(); break;
            case "clipboard": RenderClipboard(); break;
            case "diff": RenderDiff(); break;
            case "text": RenderText(); break;
            case "qr": RenderQr(); break;
            case "calculator": RenderCalculator(); break;
            case "random": RenderRandom(); break;
            case "hash": RenderHash(); break;
            case "screen-pin": RenderScreenPin(); break;
            case "batch-files": RenderBatchFiles(); break;
            case "batch-images": RenderBatchImages(); break;
            case "notifications": RenderNotificationSettings(); break;
            case "watch": RenderWatch(); break;
            case "awake": RenderAwake(); break;
            case "volume": RenderVolume(); break;
            case "activities": RenderActivities(); break;
            default: RenderSettings(); break;
        }
    }
    private void RenderFocus()
    {
        focusTime = Text(FocusTimer.Format(focus.Value, false), 42); focusTime.FontWeight = FontWeight.SemiBold; focusTime.HorizontalAlignment = HorizontalAlignment.Center; focusTime.Margin = new(0, 10);
        var panel = new StackPanel(); panel.Children.Add(focusTime);
        var minutes = new NumericUpDown { Minimum = 1, Maximum = 480, Value = prefs.FocusMinutes, Increment = 5, FormatString = "0", HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(Hint(T("自定义分钟数；正在计时时修改会在重置后生效。", "Custom minutes; reset to apply a change during a session."))); panel.Children.Add(minutes);
        minutes.ValueChanged += (_, _) => { prefs.FocusMinutes = (int)(minutes.Value ?? 25); QueueSave(); if (!focus.IsRunning) focus.SetCountdown(TimeSpan.FromMinutes(prefs.FocusMinutes), "Focus", true); };
        panel.Children.Add(Actions(Button(T("开始 / 暂停", "Start / pause"), () => QuickAction("focus")), Button(T("重置", "Reset"), () => { focus.SetCountdown(TimeSpan.FromMinutes(prefs.FocusMinutes), "Focus", true); RefreshCompact(); focusTime.Text = FocusTimer.Format(focus.Value, false); }), Button(T("秒表", "Stopwatch"), () => { focus.SetStopwatch(); focusTime.Text = "0:00"; })));
        panel.Children.Add(Hint(T("收起后点击专注组件即可开始、暂停或继续。", "Tap the collapsed focus widget to start, pause or resume."))); body.Children.Add(Card(panel));
    }
    private void RenderMedia()
    {
        body.Children.Add(Hint(OperatingSystem.IsMacOS() ? T("Apple Music 通过公开 Apple Events 连接，首次操作可能需要你在系统中允许自动化。其他播放器需各自接口。", "Apple Music uses public Apple Events. macOS may ask you to allow Automation. Other players require their own integration.") : T("Ubuntu 使用 MPRIS / playerctl，支持发布 MPRIS 的播放器。没有会话时不显示虚假进度。", "Ubuntu uses MPRIS / playerctl for supported players. Missing timelines are not estimated.")));
        mediaTitle = Text(media?.Available == true ? media.Title : T("尚未连接播放器", "Player is not connected"), 20); mediaArtist = Hint(media?.Artist ?? ""); body.Children.Add(Card(new StackPanel { Spacing = 5, Children = { mediaTitle, mediaArtist } }));
        body.Children.Add(Actions(AsyncButton(T("连接 / 刷新", "Connect / refresh"), async () => { prefs.MediaEnabled = true; Save(); await ReadMedia(); }), Button(T("断开", "Disconnect"), () => { prefs.MediaEnabled = false; media = null; Save(); RenderPage(); RefreshCompact(); })));
        body.Children.Add(Actions(AsyncButton("⏮", () => MediaAction("previous")), AsyncButton("▶ / Ⅱ", () => MediaAction("play-pause")), AsyncButton("⏭", () => MediaAction("next"))));
    }
    private void RenderQuota()
    {
        quotaText = Text(QuotaValue(false) + "\n\n" + QuotaValue(true), 20); body.Children.Add(Card(quotaText));
        body.Children.Add(Hint(T("使用本机已登录的 Codex app-server 只读接口，不复制账户凭据。需要你主动连接。", "Uses the local, signed-in Codex app-server read-only interface. Credentials are not copied. Connect explicitly.")));
        var executable = Input("codex-path", prefs.CodexPath); executable.PlaceholderText = T("codex 可执行文件路径；留空自动查找", "Codex executable; leave empty to discover");
        var home = Input("codex-home", prefs.CodexHome); home.PlaceholderText = "CODEX_HOME"; body.Children.Add(executable); body.Children.Add(home);
        body.Children.Add(Actions(AsyncButton(T("连接 / 刷新", "Connect / refresh"), async () => { codex?.Dispose(); codex = null; prefs.CodexPath = executable.Text ?? ""; prefs.CodexHome = home.Text ?? ""; prefs.CodexEnabled = true; Save(); await ReadQuota(); }), Button(T("断开", "Disconnect"), () => { prefs.CodexEnabled = false; codex?.Dispose(); codex = null; quota = null; Save(); RenderPage(); RefreshCompact(); })));
    }
    private void RenderPorts()
    {
        var input = Input("ports", "3000, 8080"); var results = new StackPanel { Spacing = 6 }; body.Children.Add(input);
        body.Children.Add(AsyncButton(T("查看占用程序", "Find owners"), async () => {
            var ports = (input.Text ?? "").Split([',', '，', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).Distinct().ToArray();
            if (ports.Length is < 1 or > 32 || ports.Any(p => p is < 1 or > 65535)) throw new ArgumentException(T("输入 1—65535 的端口，最多 32 个。", "Enter up to 32 ports between 1 and 65535."));
            var items = testMode ? [] : await platform.ReadPortsAsync(ports, lifetime.Token); results.Children.Clear();
            foreach (var item in items) results.Children.Add(Card(new StackPanel { Spacing = 3, Children = { Text(item.Port + " · " + item.Protocol + " · " + item.Name, 14), Hint("PID " + (item.Pid?.ToString() ?? "—")), Hint(item.Path) } }));
            if (items.Count == 0) results.Children.Add(Hint(T("未发现可读取的占用记录。", "No readable owner records found.")));
        })); body.Children.Add(results); body.Children.Add(Hint(T("Mac 使用 lsof，Ubuntu 使用 ss；受保护进程可能无法读取 PID 或程序路径。", "Uses lsof on Mac and ss on Ubuntu. Protected process IDs or executable paths may be unavailable.")));
    }
    private void RenderNotes()
    {
        var notes = Input("notes", prefs.Notes, true); notes.MinHeight = 250; notes.MaxLength = 60000; notes.TextChanged += (_, _) => { prefs.Notes = notes.Text ?? ""; QueueSave(); }; body.Children.Add(notes);
        body.Children.Add(Actions(AsyncButton(T("复制", "Copy"), async () => { if (Clipboard is not null && !testMode) await Clipboard.SetTextAsync(notes.Text ?? ""); }), Button(T("保存", "Save"), Save)));
        body.Children.Add(Hint(T("仅保存在本机跨平台版本的数据目录，不覆盖 Windows 版资料。", "Stored locally in the portable app's data folder, separate from the Windows edition.")));
    }
    private void RenderClipboard()
    {
        var text = Input("clipboard", "", true); text.MinHeight = 230; body.Children.Add(text);
        body.Children.Add(Actions(AsyncButton(T("读取文字", "Read text"), async () => { if (Clipboard is not null && !testMode) text.Text = await Clipboard.TryGetTextAsync() ?? ""; }), AsyncButton(T("复制文字", "Copy text"), async () => { if (Clipboard is not null && !testMode) await Clipboard.SetTextAsync(text.Text ?? ""); })));
        body.Children.Add(Hint(T("此预览版提供主动读写。Wayland 后台剪贴板历史取决于桌面权限，不会把手动读取伪装成全局监听。", "This preview provides explicit clipboard actions. Wayland background history depends on desktop support and permissions.")));
    }
    private void RenderDiff()
    {
        var left = Input("diff-left", "", true); var right = Input("diff-right", "", true); var rows = new StackPanel { Spacing = 3 };
        body.Children.Add(Hint(T("原文", "Original"))); body.Children.Add(left); body.Children.Add(Hint(T("新文", "Revised"))); body.Children.Add(right);
        body.Children.Add(AsyncButton(T("比较差异", "Compare"), async () => { var a = left.Text ?? ""; var b = right.Text ?? ""; var result = await Task.Run(() => TextDiff.Compare(a, b, lifetime.Token), lifetime.Token); rows.Children.Clear();
            rows.Children.Add(Hint(T($"新增 {result.Added} · 删除 {result.Removed} · 修改 {result.Modified}", $"Added {result.Added} · Removed {result.Removed} · Changed {result.Modified}")));
            foreach (var line in result.Rows.Take(1600)) { var label = Text((line.LeftNumber?.ToString() ?? "+") + " │ " + line.Left + (line.Kind == TextDiffKind.Modified ? "\n→ " + line.Right : line.Kind == TextDiffKind.Added ? line.Right : ""), 12); var card = Card(label); card.Background = new SolidColorBrush(Color.Parse(line.Kind == TextDiffKind.Added ? prefs.LightTheme ? "#E5F4E9" : "#253D32" : line.Kind == TextDiffKind.Removed ? prefs.LightTheme ? "#FCE9EA" : "#472A2E" : line.Kind == TextDiffKind.Modified ? prefs.LightTheme ? "#EBE8FA" : "#3B354D" : prefs.LightTheme ? "#F2F2F7" : "#232326")); rows.Children.Add(card); }
            if (result.Rows.Count > 1600) rows.Children.Add(Hint(T("差异已完整计算，页面只显示前 1,600 项。", "The full difference was computed; this preview shows the first 1,600 rows.")));
        })); body.Children.Add(rows);
    }
    private void RenderText()
    {
        var input = Input("text-input", "", true); var output = Input("text-output", "", true); output.IsReadOnly = true;
        var ids = new[] { "json-pretty", "json-compact", "base64-encode", "base64-decode", "url-encode", "url-decode", "sha256", "upper", "lower", "deduplicate", "nonempty", "trim" };
        var choice = new ComboBox { ItemsSource = ids, SelectedIndex = 0, Margin = new(0, 0, 0, 6) }; body.Children.Add(input); body.Children.Add(choice);
        body.Children.Add(Actions(Button(T("处理", "Transform"), () => output.Text = TextTools.Transform(ids[choice.SelectedIndex], input.Text ?? "")), AsyncButton(T("复制结果", "Copy result"), async () => { if (Clipboard is not null && !testMode) await Clipboard.SetTextAsync(output.Text ?? ""); }))); body.Children.Add(output);
    }
    private void RenderQr()
    {
        var input = Input("qr", "", true); input.MaxLength = 10000; var qr = new QrCanvas { Width = 230, Height = 230, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false, Margin = new(0, 8) }; body.Children.Add(input);
        body.Children.Add(Actions(Button(T("生成二维码", "Generate QR"), () => { qr.Code = Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(input.Text ?? "", Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Low); qr.IsVisible = true; }), AsyncButton(T("保存 PNG", "Save PNG"), async () => { if (qr.Code is null || testMode) return; var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = T("保存二维码", "Save QR"), SuggestedFileName = "side-island-qr.png", DefaultExtension = "png", FileTypeChoices = [new("PNG") { Patterns = ["*.png"] }] }); if (file is null) return; using var bitmap = qr.Bitmap(); await using var target = await file.OpenWriteAsync(); bitmap.Save(target, PngBitmapEncoderOptions.Default); })));
        body.Children.Add(qr); body.Children.Add(Hint(T("单张使用标准二维码最大容量；中文和 emoji 的 UTF-8 字节会占更多容量。", "Uses the standard single-code maximum capacity. UTF-8 Chinese and emoji consume more bytes.")));
    }
    private void RenderCalculator()
    {
        var input = Input("calculator", ""); var result = Text("—", 30); body.Children.Add(input); body.Children.Add(Button(T("计算", "Calculate"), () => result.Text = Calculator.Evaluate(input.Text ?? "").ToString("G15", CultureInfo.InvariantCulture))); body.Children.Add(Card(result));
        body.Children.Add(Hint(T("支持括号、加减乘除和乘方。", "Supports parentheses, arithmetic and exponentiation.")));
    }
    private void RenderRandom()
    {
        var length = new NumericUpDown { Minimum = 8, Maximum = 128, Value = 20, FormatString = "0" }; var result = Input("random-result", "", true); result.IsReadOnly = true; body.Children.Add(Hint(T("密码长度", "Password length"))); body.Children.Add(length);
        body.Children.Add(Actions(Button(T("生成密码", "Generate password"), () => result.Text = RandomTools.Password(new PasswordOptions { Length = (int)(length.Value ?? 20), Uppercase = true, Lowercase = true, Digits = true, Symbols = true })), AsyncButton(T("复制", "Copy"), async () => { if (Clipboard is not null && !testMode) await Clipboard.SetTextAsync(result.Text ?? ""); }))); body.Children.Add(result);
    }
    private void RenderHash()
    {
        var path = Input("hash-path"); var result = Input("hash-result", "", true); result.IsReadOnly = true; body.Children.Add(path);
        body.Children.Add(Actions(AsyncButton(T("选择文件", "Choose file"), async () => { if (testMode) return; var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, Title = T("选择校验文件", "Choose a file") }); if (files.Count > 0) path.Text = files[0].TryGetLocalPath() ?? ""; }), AsyncButton("SHA-256", async () => { var value = path.Text ?? ""; result.Text = await Task.Run(() => FileHashTools.ComputeFile(value, "SHA256", lifetime.Token), lifetime.Token); })));
        body.Children.Add(result);
    }
    private readonly List<(string Id, string Target, bool Process, int Pid, DateTime? Start)> watches = new();
    private readonly HashSet<string> watchedDone = new();
    private void RenderWatch()
    {
        var path = Input("watch-file"); var pid = Input("watch-pid"); body.Children.Add(Hint(T("等待文件出现：绝对路径", "Wait for a file: absolute path"))); body.Children.Add(path);
        body.Children.Add(Button(T("守望文件", "Watch file"), () => { if (!Path.IsPathFullyQualified(path.Text ?? "")) throw new ArgumentException(T("需要绝对路径。", "An absolute path is required.")); AddWatch(path.Text!, false, 0, null); }));
        body.Children.Add(Hint(T("等待进程退出：PID", "Wait for a process to exit: PID"))); body.Children.Add(pid);
        body.Children.Add(Button(T("守望进程", "Watch process"), () => { var id = int.Parse(pid.Text ?? "", CultureInfo.InvariantCulture); using var process = Process.GetProcessById(id); AddWatch(process.ProcessName, true, id, process.StartTime.ToUniversalTime()); }));
        body.Children.Add(Hint(T("运行期间检查，不会启动或终止目标程序。目标 PID 被复用时仍会视为原进程已经退出。", "Checked while the island runs. Targets are never started or killed; a reused PID counts as the original process exiting.")));
    }
    private void AddWatch(string value, bool process, int pid, DateTime? start)
    {
        if (watches.Count >= 24) throw new ArgumentException(T("最多同时守望 24 项。", "Up to 24 watches are allowed.")); var id = "watch:" + Guid.NewGuid().ToString("N"); watches.Add((id, value, process, pid, start));
        tasks.Publish(new() { Id = id, Title = value.Length > 150 ? value[..150] : value, State = "running", Source = T("任务守望", "Task watch") }); Report(T("已开始守望。", "Watch started."));
    }
    private DateTime nextWatch;
    private void PollWatches()
    {
        if (testMode || DateTime.UtcNow < nextWatch) return; nextWatch = DateTime.UtcNow.AddSeconds(1);
        foreach (var item in watches.ToArray())
        {
            if (watchedDone.Contains(item.Id)) continue; bool complete;
            if (!item.Process) complete = File.Exists(item.Target);
            else try { using var p = Process.GetProcessById(item.Pid); complete = p.HasExited || p.StartTime.ToUniversalTime() != item.Start; } catch (ArgumentException) { complete = true; } catch { continue; }
            if (!complete) continue; watchedDone.Add(item.Id); tasks.Publish(new() { Id = item.Id, Title = item.Target.Length > 150 ? item.Target[..150] : item.Target, State = "completed", Source = T("任务守望", "Task watch") }); Notice(T("守望已完成", "Watch completed"), item.Target);
        }
    }
    private void RenderAwake()
    {
        body.Children.Add(Hint(T("只在所选时长内阻止系统自动休眠，退出岛后结束。", "Temporarily inhibits automatic sleep; quitting the island releases it.")));
        body.Children.Add(Actions(new[] { 30, 60, 120 }.Select(minutes => AsyncButton(minutes + T(" 分钟", " min"), async () => { if (testMode) return; var error = await platform.SetAwakeAsync(true, TimeSpan.FromMinutes(minutes), lifetime.Token); if (error is not null) throw new InvalidOperationException(error); awakeUntil = DateTime.UtcNow.AddMinutes(minutes); Report(T("已开始保持唤醒。", "Keeping awake.")); })).Cast<Control>().ToArray()));
        body.Children.Add(AsyncButton(T("结束保持唤醒", "Stop keeping awake"), async () => { if (!testMode) { var error = await platform.SetAwakeAsync(false, TimeSpan.Zero, lifetime.Token); if (error is not null) throw new InvalidOperationException(error); } awakeUntil = default; RefreshCompact(); }));
    }
    private void RenderVolume()
    {
        var level = new Slider { Minimum = 0, Maximum = 100, Value = 50, Margin = new(0, 12) }; var muted = new CheckBox { Content = T("静音", "Mute") }; var state = Hint(T("点击读取后查看当前音量。", "Read to inspect current volume.")); body.Children.Add(level); body.Children.Add(muted); body.Children.Add(state);
        body.Children.Add(Actions(AsyncButton(T("读取音量", "Read volume"), async () => { if (testMode) return; var value = await platform.ReadVolumeAsync(lifetime.Token); if (!value.Available) throw new InvalidOperationException(value.Error); level.Value = value.Percent; muted.IsChecked = value.Muted; state.Text = value.Percent + "%"; }), AsyncButton(T("应用", "Apply"), async () => { if (testMode) return; var error = await platform.SetVolumeAsync((int)level.Value, muted.IsChecked == true, lifetime.Token); if (error is not null) throw new InvalidOperationException(error); })));
        body.Children.Add(Hint(T("此预览版提供系统音量。Mac 通用分应用音量不能直接照搬 Windows Core Audio；没有安装虚拟音频驱动。", "This preview controls system volume. Mac per-app volume needs separate support; no virtual audio driver is installed.")));
    }
    private void RenderActivities()
    {
        taskRows = new StackPanel { Spacing = 3 }; body.Children.Add(taskRows); RenderTaskRows();
        body.Children.Add(Actions(Button(T("开启本地接入", "Start local input"), () => { if (testMode) return; taskApi.Start(0); Report(taskApi.Url); }), Button(T("停止接入", "Stop input"), () => { taskApi.Stop(); Report(T("接入已停止，旧令牌失效。", "Input stopped; old token expired.")); })));
        body.Children.Add(Actions(AsyncButton(T("复制请求示例", "Copy example"), async () => { if (!taskApi.Running) throw new InvalidOperationException(T("先开启本地接入。", "Start input first.")); if (Clipboard is not null && !testMode) await Clipboard.SetTextAsync("curl -X POST '" + taskApi.Url + "' -H 'Authorization: Bearer " + taskApi.Token + "' -H 'Content-Type: application/json' --data '{\"id\":\"build\",\"title\":\"Build\",\"progress\":25}'"); }), Button(T("清除已完成", "Clear finished"), tasks.ClearFinished)));
        body.Children.Add(Hint(T("仅本机 127.0.0.1，可接收脚本状态但不执行命令、不读取文件；访问令牌仅在内存中。", "Loopback 127.0.0.1 only. Reports script status without executing commands or reading files; token stays in memory.")));
    }
    private void RenderTaskRows()
    {
        if (taskRows is null) return; taskRows.Children.Clear(); foreach (var task in tasks.Snapshot()) { var panel = new StackPanel { Children = { Text(task.Title), Hint(UiLocalization.Localize(TaskActivityHub.StateLabel(task.State)) + (task.Progress is double p ? " · " + p.ToString("0") + "%" : "")) } }; taskRows.Children.Add(Card(panel)); }
        if (tasks.Snapshot().Count == 0) taskRows.Children.Add(Hint(T("还没有任务动态。", "No task activities.")));
    }
    private void RenderMessages()
    {
        RenderNotificationCenter();
    }
    private void RenderStatus()
    {
        body.Children.Add(Text(T("本机状态", "Status"), 18)); body.Children.Add(Card(new StackPanel { Spacing = 7, Children = { Text(System.Runtime.InteropServices.RuntimeInformation.OSDescription), Hint(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()), Text(QuotaValue(false)), Text(QuotaValue(true)), Hint(realNotch ? T("已使用真实刘海区域。", "Using the detected physical notch area.") : T("当前使用屏幕边缘布局。", "Using screen-edge placement.")) } }));
        body.Children.Add(Button(T("打开额度连接", "Connect usage"), () => { selected = "quota"; page = "tools"; RenderPage(); }));
    }
}

internal sealed class QrCanvas : Control
{
    private Net.Codecrete.QrCodeGenerator.QrCode? code;
    public Net.Codecrete.QrCodeGenerator.QrCode? Code { get => code; set { code = value; InvalidateVisual(); } }
    public override void Render(DrawingContext context)
    {
        if (code is null) return; var scale = Math.Min(Bounds.Width, Bounds.Height) / (code.Size + 8); context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        for (int y = 0; y < code.Size; y++) for (int x = 0; x < code.Size; x++) if (code.GetModule(x, y)) context.FillRectangle(Brushes.Black, new Rect((x + 4) * scale, (y + 4) * scale, scale + .02, scale + .02));
    }
    public RenderTargetBitmap Bitmap()
    {
        if (code is null) throw new InvalidOperationException("No QR code"); var size = (code.Size + 8) * 6; var canvas = new QrCanvas { Code = code, Width = size, Height = size }; canvas.Measure(new(size, size)); canvas.Arrange(new Rect(0, 0, size, size)); var bitmap = new RenderTargetBitmap(new(size, size), new(96, 96)); bitmap.Render(canvas); return bitmap;
    }
}
