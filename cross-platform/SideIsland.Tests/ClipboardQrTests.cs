using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SideIsland.Desktop;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Tests;

internal static class ClipboardQrTests
{
    private static readonly (string Name, Action Test)[] Cases =
    [
        ("clipboard-edit", ClipboardEdit), ("clipboard-rebuild", ClipboardRebuild), ("clipboard-close", ClipboardClose),
        ("qr-edit", QrEdit), ("qr-picker-edit", QrPickerEdit), ("qr-picker-rebuild", QrPickerRebuild),
        ("qr-existing-file", QrExistingFile), ("qr-new-file", QrNewFile), ("notification-search", NotificationSearch),
        ("clipboard-latest", ClipboardLatest), ("clipboard-copy", ClipboardCopy), ("clipboard-errors", ClipboardErrors),
        ("qr-picker-close", QrPickerClose), ("qr-picker-cancel", QrPickerCancel), ("qr-capacity", QrCapacity), ("qr-atomic-publication", QrAtomicPublication)
    ];
    internal static void Run()
    {
        foreach (var item in Cases) item.Test();
        Console.WriteLine("PASS: delayed synthetic clipboard/QR picker lifecycle, exact draft preservation, new-file-only PNG publication and same-turn notification search rebuild; no real clipboard, picker or notification reads.");
    }
    internal static void RecordBaseline(string directory)
    {
        Directory.CreateDirectory(directory); var observations = new List<object>();
        foreach (var item in Cases)
            try { item.Test(); observations.Add(new { item.Name, Passed = true, Error = "" }); }
            catch (Exception error) { observations.Add(new { item.Name, Passed = false, Error = error.ToString() }); }
        string json = JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, "observations.json"), json); Console.WriteLine(json);
    }
    private static void ClipboardEdit()
    {
        using var fixture = new Fixture(); var window = fixture.Window; window.SelectToolForTest("clipboard"); Flush(window);
        var input = Multiline(window); input.Text = "before-read"; Click(window, "读取文字", "Read text"); Task read = window.LatestClipboardRead;
        input.Text = "manual edit wins"; fixture.Reads.Single().SetResult("late synthetic clipboard"); Await(window, read);
        Check(input.Text == "manual edit wins", "late clipboard read cannot overwrite a manual edit");
    }
    private static void ClipboardRebuild()
    {
        using var fixture = new Fixture(); var window = fixture.Window; window.SelectToolForTest("clipboard"); Flush(window);
        Click(window, "读取文字", "Read text"); Task first = window.LatestClipboardRead;
        window.SelectToolForTest("notes"); window.SelectToolForTest("clipboard"); Flush(window);
        Click(window, "读取文字", "Read text"); Task latest = window.LatestClipboardRead;
        fixture.Reads[1].SetResult("newest synthetic clipboard"); Await(window, latest);
        fixture.Reads[0].SetResult("stale synthetic clipboard"); Await(window, first);
        window.SelectToolForTest("notes"); window.SelectToolForTest("clipboard"); Flush(window);
        Check(Multiline(window).Text == "newest synthetic clipboard", "detached old read cannot replace newer clipboard draft after rebuild");
    }
    private static void ClipboardClose()
    {
        using var fixture = new Fixture(); var window = fixture.Window; window.SelectToolForTest("clipboard"); Flush(window);
        var input = Multiline(window); input.Text = "closed draft"; Click(window, "读取文字", "Read text"); Task read = window.LatestClipboardRead;
        window.Close(); fixture.Reads.Single().SetResult("late private fixture"); Await(window, read);
        Check(input.Text == "closed draft", "closed view never imports late clipboard text");
    }
    private static void ClipboardLatest()
    {
        using var fixture = new Fixture(); var window = fixture.Window; window.SelectToolForTest("clipboard"); Flush(window);
        Click(window, "读取文字", "Read text"); Task old = window.LatestClipboardRead;
        Multiline(window).Text = "explicit edit";
        Check(fixture.ReadTokens[0].IsCancellationRequested && Action(window, "读取文字", "Read text").IsEnabled, "input change cancels the old request and allows a new explicit read");
        Click(window, "读取文字", "Read text"); Task latest = window.LatestClipboardRead;
        fixture.Reads[0].SetResult("old callback ignores cancellation"); Await(window, old);
        Check(!Action(window, "读取文字", "Read text").IsEnabled, "old request completion cannot re-enable read while the newer read is pending");
        fixture.Reads[1].SetResult("new read wins"); Await(window, latest);
        Check(Multiline(window).Text == "new read wins", "newer explicit clipboard read wins in the same view");
    }
    private static void ClipboardCopy()
    {
        using var fixture = new Fixture(); var window = fixture.Window; window.SelectToolForTest("clipboard"); Flush(window);
        fixture.CopyGate = new(TaskCreationOptions.RunContinuationsAsynchronously); Multiline(window).Text = "captured copy fixture";
        Click(window, "复制文字", "Copy text"); Task copy = window.LatestClipboardWrite;
        Multiline(window).Text = "new private draft"; fixture.CopyGate.SetResult(); Await(window, copy);
        Check(fixture.Copies.SequenceEqual(["captured copy fixture"]) && Multiline(window).Text == "new private draft", "copy uses the text captured at click time without replacing edits");
        var status = window.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == "clipboard-status");
        Check(status.Text == "" && Action(window, "复制文字", "Copy text").IsEnabled, "late clipboard copy cannot replace current status and restores its control");
        window.Close();
        Check(!Directory.GetFiles(fixture.Data, "*.json", SearchOption.AllDirectories).Any(path => File.ReadAllText(path).Contains("new private draft", StringComparison.Ordinal)), "clipboard draft is not persisted to app JSON files");
    }
    private static void ClipboardErrors()
    {
        using var fixture = new Fixture(); var window = fixture.Window; window.SelectToolForTest("clipboard"); Flush(window);
        Multiline(window).Text = "keep on reader error"; Click(window, "读取文字", "Read text"); Task read = window.LatestClipboardRead;
        fixture.Reads.Single().SetException(new IOException("synthetic provider error")); Await(window, read);
        Check(Multiline(window).Text == "keep on reader error" && Action(window, "读取文字", "Read text").IsEnabled, "provider failure preserves text and permits retry");
        Click(window, "读取文字", "Read text"); read = window.LatestClipboardRead; fixture.Reads[1].SetResult(null); Await(window, read);
        Check(Multiline(window).Text == "", "successful empty clipboard read is distinct from a failed read");
    }
    private static void QrEdit()
    {
        using var fixture = new Fixture(); var window = fixture.Window; Generate(window, "original QR fixture");
        Multiline(window).Text = "edited QR fixture";
        var qr = window.GetVisualDescendants().OfType<QrCanvas>().Single();
        Check(qr.Code is null && !qr.IsVisible && !Action(window, "保存 PNG", "Save PNG").IsEnabled, "input edit immediately invalidates the visible QR and save action");
    }
    private static void QrPickerEdit()
    {
        using var fixture = new Fixture(); var window = fixture.Window; Generate(window, "first QR fixture");
        Click(window, "保存 PNG", "Save PNG"); Task save = window.LatestQrSave;
        Multiline(window).Text = "second QR fixture"; Click(window, "生成二维码", "Generate QR");
        string destination = fixture.Path("stale-edit.png"); fixture.Pickers.Single().SetResult(destination); Await(window, save);
        Check(!File.Exists(destination), "late picker result cannot save changed QR contents under the old request");
    }
    private static void QrPickerRebuild()
    {
        using var fixture = new Fixture(); var window = fixture.Window; Generate(window, "detached QR fixture");
        Click(window, "保存 PNG", "Save PNG"); Task save = window.LatestQrSave; window.SelectToolForTest("notes");
        string destination = fixture.Path("stale-view.png"); fixture.Pickers.Single().SetResult(destination); Await(window, save);
        Check(!File.Exists(destination), "detached QR view cannot save after a delayed picker returns");
    }
    private static void QrPickerClose()
    {
        using var fixture = new Fixture(); var window = fixture.Window; Generate(window, "closed QR fixture");
        Click(window, "保存 PNG", "Save PNG"); Task save = window.LatestQrSave; window.Close();
        string destination = fixture.Path("closed.png"); fixture.Pickers.Single().SetResult(destination); Await(window, save);
        Check(!File.Exists(destination), "closing the window suppresses a delayed QR picker result");
    }
    private static void QrPickerCancel()
    {
        using var fixture = new Fixture(); var window = fixture.Window; Generate(window, "cancelled QR fixture");
        Click(window, "保存 PNG", "Save PNG"); Task save = window.LatestQrSave; Click(window, "保存 PNG", "Save PNG");
        Check(fixture.Pickers.Count == 1, "repeated save clicks open only one QR picker");
        fixture.Pickers.Single().SetResult(null); Await(window, save);
        Check(Action(window, "保存 PNG", "Save PNG").IsEnabled, "picker cancellation restores saving of the unchanged QR");
        Click(window, "保存 PNG", "Save PNG"); save = window.LatestQrSave;
        fixture.Pickers[1].SetResult(fixture.Path("missing-folder/output.png")); Await(window, save);
        Check(Action(window, "保存 PNG", "Save PNG").IsEnabled && !Directory.Exists(fixture.Path("missing-folder")), "save IO failure restores action and does not create unexpected directories");
    }
    private static void QrCapacity()
    {
        using var fixture = new Fixture(); var window = fixture.Window; Generate(window, new string('x', 2953));
        Check(window.GetVisualDescendants().OfType<QrCanvas>().Single().Code?.Size == 177, "full version-40 byte-mode single QR capacity remains available");
        Multiline(window).Text = new string('x', 2954); Click(window, "生成二维码", "Generate QR");
        Check(window.GetVisualDescendants().OfType<QrCanvas>().Single().Code is null && !Action(window, "保存 PNG", "Save PNG").IsEnabled, "over-capacity generation cannot retain the previous QR or save action");
    }
    private static void QrAtomicPublication()
    {
        using var fixture = new Fixture(); var window = fixture.Window; Generate(window, "atomic QR fixture");
        using var bitmap = window.GetVisualDescendants().OfType<QrCanvas>().Single().Bitmap(); using var encoded = new MemoryStream();
        bitmap.Save(encoded, PngBitmapEncoderOptions.Default); byte[] png = encoded.ToArray();
        string destination = fixture.Path("complete-qr.png"); ScreenPinImageFiles.SaveNew(png, destination, default);
        Check(File.ReadAllBytes(destination).SequenceEqual(png), "shared byte snapshot publisher preserves the exact generated QR PNG");
        string interrupted = fixture.Path("interrupted-qr.png"); bool rejected = false;
        try { ScreenPinImageFiles.PublishNew(interrupted, stream => { stream.Write(png, 0, png.Length / 2); throw new IOException("synthetic interrupted PNG write"); }, default); }
        catch (IOException) { rejected = true; }
        Check(rejected && !File.Exists(interrupted), "interrupted PNG writer never leaves an incomplete final QR file");
        using var cancellation = new CancellationTokenSource(); string cancelled = fixture.Path("cancelled-qr.png"); rejected = false;
        try { ScreenPinImageFiles.PublishNew(cancelled, stream => { stream.Write(png); cancellation.Cancel(); }, cancellation.Token); }
        catch (OperationCanceledException) { rejected = true; }
        Check(rejected && !File.Exists(cancelled), "cancellation after staging a complete PNG suppresses final publication");
        Check(Directory.GetFiles(System.IO.Path.GetDirectoryName(destination)!, ".side-island-publish-*.tmp").Length == 0, "QR shared publisher removes owned staging files after failed or cancelled writes");
    }
    private static void QrExistingFile()
    {
        using var fixture = new Fixture(); var window = fixture.Window; Generate(window, "owned QR fixture");
        string destination = fixture.Path("existing-owned.bin"); byte[] original = Enumerable.Repeat((byte)0x7a, 65536).ToArray(); File.WriteAllBytes(destination, original);
        Click(window, "保存 PNG", "Save PNG"); Task save = window.LatestQrSave; fixture.Pickers.Single().SetResult(destination); Await(window, save);
        Check(File.ReadAllBytes(destination).SequenceEqual(original), "QR save never overwrites or leaves mixed trailing bytes in an existing file");
    }
    private static void QrNewFile()
    {
        using var fixture = new Fixture(); var window = fixture.Window; Generate(window, "实际 PNG fixture 中文");
        var qr = window.GetVisualDescendants().OfType<QrCanvas>().Single(); int size = (qr.Code!.Size + 8) * 6;
        string destination = fixture.Path("new.png"); Click(window, "保存 PNG", "Save PNG"); Task save = window.LatestQrSave;
        fixture.Pickers.Single().SetResult(destination); Await(window, save);
        using var decoded = new Bitmap(destination); Check(decoded.PixelSize == new PixelSize(size, size), "saved new file is an actual decodable PNG at the generated QR dimensions");
    }
    private static void NotificationSearch()
    {
        using var fixture = new Fixture(); var window = fixture.Window; SelectPage(window, "messages"); Flush(window);
        var input = Body(window).Children.OfType<TextBox>().Single(); input.Text = "search survives same-turn rebuild";
        SelectPage(window, "status"); SelectPage(window, "messages"); Flush(window);
        Check(Body(window).Children.OfType<TextBox>().Single().Text == "search survives same-turn rebuild", "notification search must be captured before a same-turn page rebuild");
    }
    private static void Generate(IslandWindow window, string text)
    { window.SelectToolForTest("qr"); Flush(window); Multiline(window).Text = text; Click(window, "生成二维码", "Generate QR"); Flush(window); }
    private static StackPanel Body(IslandWindow window) => (StackPanel)typeof(IslandWindow).GetField("body", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static TextBox Multiline(IslandWindow window) => Body(window).GetVisualDescendants().OfType<TextBox>().Single(box => box.AcceptsReturn);
    private static Button Action(IslandWindow window, string zh, string en) => Body(window).GetVisualDescendants().OfType<Button>().Single(button => button.Content is TextBlock text && (text.Text == zh || text.Text == en));
    private static void Click(IslandWindow window, string zh, string en) => Action(window, zh, en).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void SelectPage(IslandWindow window, string page) => typeof(IslandWindow).GetMethod("SelectPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [page]);
    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Await(Window window, Task task)
    {
        var elapsed = Stopwatch.StartNew(); while (!task.IsCompleted && elapsed.ElapsedMilliseconds < 10000) { Flush(window); Thread.Sleep(2); }
        Flush(window); Check(task.IsCompleted, "async fixture completes before deadline"); task.GetAwaiter().GetResult();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Clipboard/QR: " + message); }
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "side-island-clipboard-qr-" + Guid.NewGuid().ToString("N"));
        internal readonly List<TaskCompletionSource<string?>> Reads = [], Pickers = [];
        internal readonly List<CancellationToken> ReadTokens = [];
        internal readonly List<string> Copies = [];
        internal TaskCompletionSource? CopyGate;
        internal string Data => System.IO.Path.Combine(directory, "data");
        internal IslandWindow Window { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(directory);
            var sources = new IntegrationSources(_ => Task.FromResult(new MediaSnapshot(false, "", "", "", null, null, null, false, null)), (_, _) => Task.FromResult<string?>(null), (_, _) => throw new InvalidOperationException("Unused quota fixture"))
            {
                ReadClipboardText = token => { ReadTokens.Add(token); var task = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously); Reads.Add(task); return task.Task; },
                WriteClipboardText = text => { Copies.Add(text); return CopyGate?.Task ?? Task.CompletedTask; },
                ChooseQrDestination = _ => { var task = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously); Pickers.Add(task); return task.Task; }
            };
            Window = new IslandWindow(true, System.IO.Path.Combine(directory, "data"), sources); Window.Show(); Window.Expand(); Flush(Window);
        }
        internal string Path(string name) => System.IO.Path.Combine(directory, name);
        public void Dispose()
        {
            Window.Close(); foreach (var read in Reads) read.TrySetResult(null); foreach (var picker in Pickers) picker.TrySetResult(null); CopyGate?.TrySetResult();
            Await(Window, Window.LatestClipboardRead); Await(Window, Window.LatestClipboardWrite); Await(Window, Window.LatestQrSave);
            string full = System.IO.Path.GetFullPath(directory), parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            if (System.IO.Path.GetDirectoryName(full) != parent || !System.IO.Path.GetFileName(full).StartsWith("side-island-clipboard-qr-", StringComparison.Ordinal)) throw new InvalidOperationException("Fixture cleanup escaped its owned temp directory.");
            Directory.Delete(full, true);
        }
    }
}
