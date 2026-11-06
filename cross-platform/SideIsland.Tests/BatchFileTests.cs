using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SideIsland.Core;
using SideIsland.Desktop;

namespace SideIsland.Tests;

internal static class BatchFileTests
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("Batch files: " + message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or OperationCanceledException or OverflowException or System.ComponentModel.Win32Exception) { checks++; return; }
        throw new InvalidOperationException("Batch files accepted: " + message);
    }
    internal static void Run()
    {
        PortableBatchFiles.VerifyNativeAbiContract(); Check(true, "Linux statx and Apple Silicon stat byte fixtures");
        using var fixture = new Fixture();
        var a = fixture.File("basic/report.txt", "one"); var b = fixture.File("basic/参数.json", "two");
        using var engine = new PortableBatchFiles(fixture.Data);
        var plan = engine.Preview([a, b], new() { Prefix = "pre-", Suffix = "-end", Template = "{name}_{n}", Find = "report", Replace = "readme", Start = 7, Padding = 2 });
        Check(Path.GetFileName(plan.Items[0].DestinationPath) == "pre-readme_07-end.txt", "prefix, suffix, replace, number and original extension");
        Check(Path.GetFileName(plan.Items[1].DestinationPath) == "pre-参数_08-end.json", "Unicode and sequence");
        Check(File.Exists(a) && !File.Exists(plan.Items[0].DestinationPath), "preview must not rename");
        Check(plan.Items[0].Source.Sha256 == Convert.ToHexString(SHA256.HashData("one"u8)), "source SHA manifest");
        engine.Execute(plan); Check(engine.CanUndo && !File.Exists(a), "commit and session undo");
        Check(File.ReadAllText(plan.Items[0].DestinationPath) == "one", "contents unchanged after rename");
        var journal = File.ReadAllText(engine.LastJournalPath);
        Check(journal.Contains("\"manifest\"") && journal.Contains("\"completed\"") && journal.Contains(plan.Items[0].Source.Sha256), "durable JSONL contains source hashes and final state");
        engine.Undo(); Check(File.ReadAllText(a) == "one" && File.ReadAllText(b) == "two" && !engine.CanUndo, "safe undo");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(a)!, ".side-island-rename-*").Any(), "no stale staging files after successful undo");
        Reject(() => engine.Undo(), "second undo");
        Reject(() => new PortableBatchFiles(fixture.Data).Execute(plan), "plan execution by another session");
        Reject(() => new PortableBatchFiles(fixture.Data).Undo(), "old journals do not enable cross-session undo");

        var dot = fixture.File("special/.profile", "dot");
        Check(Path.GetFileName(engine.Preview([dot], new() { Prefix = "x-" }).Items[0].DestinationPath) == "x-.profile", "dotfiles keep their entire stem");
        var braces = fixture.File("special/{n}.txt", "braces");
        Check(Path.GetFileName(engine.Preview([braces], new() { Template = "{name}-{n}" }).Items[0].DestinationPath) == "{n}-001.txt", "literal template token in filename is not substituted twice");
        Reject(() => engine.Preview([a], new() { Template = "{bad}" }), "unknown template token");
        Reject(() => engine.Preview([a], new() { Prefix = "../" }), "directory traversal output");
        Reject(() => engine.Preview([a], new() { Padding = 0 }), "invalid padding");
        Reject(() => engine.Preview([a, b], new() { Start = int.MaxValue, Template = "{n}" }), "overflow invalid sequence");
        Reject(() => engine.Preview([a, a], new()), "duplicate source identity");
        Reject(() => engine.Preview([], new()), "empty batch");
        Reject(() => engine.Preview(["relative.txt"], new()), "relative source path");
        Reject(() => PortableBatchFiles.ValidateName(new string('界', 100)), "UTF-8 filesystem byte limit");
        using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); Reject(() => engine.Preview([a], new(), cancelled.Token), "preview cancellation"); }

        var collision1 = fixture.File("collision/a.txt", "a"); var collision2 = fixture.File("collision/b.txt", "b");
        Reject(() => engine.Preview([collision1, collision2], new() { Template = "same" }), "duplicate generated destination");
        fixture.File("collision/taken.txt", "outside");
        Reject(() => engine.Preview([collision1], new() { Template = "taken" }), "preexisting destination");
        var changedPlan = engine.Preview([collision1], new() { Prefix = "new-" }); File.WriteAllText(collision1, "changed");
        Reject(() => engine.Execute(changedPlan), "source modified after preview"); Check(File.ReadAllText(collision1) == "changed", "modified source preserved");
        var latePlan = engine.Preview([collision1], new() { Prefix = "late-" }); File.WriteAllText(latePlan.Items[0].DestinationPath, "external");
        Reject(() => engine.Execute(latePlan), "destination appeared after preview"); Check(File.ReadAllText(latePlan.Items[0].DestinationPath) == "external", "external target preserved");

        var undoSource = fixture.File("undo/source.txt", "undo-data"); var undoPlan = engine.Preview([undoSource], new() { Prefix = "moved-" }); engine.Execute(undoPlan);
        File.WriteAllText(undoSource, "external-original-path"); Reject(() => engine.Undo(), "undo destination occupied externally");
        Check(File.ReadAllText(undoSource) == "external-original-path" && !engine.CanUndo, "undo does not overwrite new data and consumes the undo attempt"); File.Delete(undoSource);
        var undoTarget = undoPlan.Items[0].DestinationPath; File.Move(undoTarget, undoSource);
        undoPlan = engine.Preview([undoSource], new() { Prefix = "moved-" }); engine.Execute(undoPlan);
        File.AppendAllText(undoTarget, "modified"); Reject(() => engine.Undo(), "undo target content changed");
        Check(File.ReadAllText(undoTarget).EndsWith("modified", StringComparison.Ordinal), "modified target preserved");

        var replacedSource = fixture.File("identity/source.txt", "identical-data"); var replacementPlan = engine.Preview([replacedSource], new() { Prefix = "moved-" }); engine.Execute(replacementPlan);
        var target = replacementPlan.Items[0].DestinationPath; var timestamp = File.GetLastWriteTimeUtc(target); File.Delete(target); File.WriteAllText(target, "identical-data"); File.SetLastWriteTimeUtc(target, timestamp);
        Check(PortableBatchFiles.ReadLocalSnapshot(target, 100).Source.Identity != replacementPlan.Items[0].Source.Identity, "retained identity anchor prevents deleted inode/file-id recycling");
        Reject(() => engine.Undo(), "identical-byte external replacement must fail identity check");
        Check(File.ReadAllText(target) == "identical-data", "external same-byte replacement survives undo rejection");

        var c1 = fixture.File("cycle/n2.txt", "two"); var c2 = fixture.File("cycle/n1.txt", "one");
        var cycle = engine.Preview([c1, c2], new() { Template = "n{n}", Start = 1, Padding = 1 }); engine.Execute(cycle);
        Check(File.ReadAllText(c1) == "one" && File.ReadAllText(c2) == "two", "two-phase swap"); engine.Undo();
        Check(File.ReadAllText(c1) == "two" && File.ReadAllText(c2) == "one", "swap undo");
        var caseSource = fixture.File("case/alpha.txt", "case"); var casePlan = engine.Preview([caseSource], new() { Find = "alpha", Replace = "ALPHA" }); engine.Execute(casePlan);
        Check(Directory.EnumerateFiles(Path.GetDirectoryName(caseSource)!).Any(p => Path.GetFileName(p) == "ALPHA.txt"), "case-only rename"); engine.Undo();
        Check(Directory.EnumerateFiles(Path.GetDirectoryName(caseSource)!).Any(p => Path.GetFileName(p) == "alpha.txt"), "case-only undo");

        var cancel1 = fixture.File("cancel/first.txt", "first"); var cancel2 = fixture.File("cancel/second.txt", "second"); var cancelPlan = engine.Preview([cancel1, cancel2], new() { Prefix = "after-" });
        using (var cancellation = new CancellationTokenSource())
        {
            Reject(() => engine.Execute(cancelPlan, cancellation.Token, new InlineProgress(value => { if (value.Stage == "renaming" && value.Completed == 1) cancellation.Cancel(); })), "cancellation after first committed move");
        }
        Check(File.ReadAllText(cancel1) == "first" && File.ReadAllText(cancel2) == "second" && cancelPlan.Items.All(i => !File.Exists(i.DestinationPath)), "mid-commit cancellation restores all names");
        Check(File.ReadAllText(engine.LastJournalPath).Contains("rolled-back", StringComparison.Ordinal), "rollback journal retained");
        var failSource = fixture.File("callback/source.txt", "keep"); var failPlan = engine.Preview([failSource], new() { Prefix = "after-" });
        Reject(() => engine.Execute(failPlan, default, new InlineProgress(_ => throw new IOException("test progress interruption"))), "unexpected callback exception rolls back");
        Check(File.Exists(failSource) && !File.Exists(failPlan.Items[0].DestinationPath), "callback fault restored filename");

        var symlink = fixture.Path("links/linked.txt"); var linkTarget = fixture.File("links/real.txt", "link-target");
        try
        {
            File.CreateSymbolicLink(symlink, linkTarget); fixture.TrackLink(symlink);
            Reject(() => engine.Preview([symlink], new() { Prefix = "after-" }), "symbolic-link source");
            Reject(() => PortableBatchFiles.ReadLocalSnapshot(symlink, 1000), "symbolic-link image source");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException) { Console.WriteLine("SKIP: symbolic-link creation unavailable: " + error.GetType().Name); }

        var outputDirectory = fixture.Directory("outputs");
        var first = PortableBatchFiles.WriteNewOutput(outputDirectory, "result.txt", "first"u8.ToArray());
        var second = PortableBatchFiles.WriteNewOutput(outputDirectory, "result.txt", "second"u8.ToArray());
        Check(first != second && File.ReadAllText(first) == "first" && File.ReadAllText(second) == "second", "new output allocation never replaces an existing file");
        var boundaryName = new string('中', 83) + "ab.txt";
        Check(Encoding.UTF8.GetByteCount(boundaryName) == 255, "owned collision fixture reaches the UTF-8 component limit");
        var allocated = Enumerable.Range(0, 12).Select(index => PortableBatchFiles.WriteNewOutput(outputDirectory, boundaryName, Encoding.UTF8.GetBytes("owned-" + index))).ToArray();
        Check(allocated.Distinct(StringComparer.Ordinal).Count() == 12 && allocated.Select((path, index) => File.ReadAllText(path) == "owned-" + index).All(value => value), "near-limit collisions allocate unique names through two-digit suffixes without changing prior outputs");
        Check(allocated.All(path => Encoding.UTF8.GetByteCount(Path.GetFileName(path)) <= 255 && Path.GetFileName(path).EndsWith(".txt", StringComparison.Ordinal)), "collision names retain extension and stay within the byte limit");
        var emojiName = string.Concat(Enumerable.Repeat("😀", 63)) + ".x";
        var emojiFirst = PortableBatchFiles.WriteNewOutput(outputDirectory, emojiName, "emoji-first"u8.ToArray());
        var emojiSecond = PortableBatchFiles.WriteNewOutput(outputDirectory, emojiName, "emoji-second"u8.ToArray());
        Check(Path.GetFileName(emojiSecond) == string.Concat(Enumerable.Repeat("😀", 62)) + "-1.x" && File.ReadAllText(emojiFirst) == "emoji-first", "UTF-8 collision fitting never splits a surrogate pair or replaces the first output");
        Check(PortableBatchFiles.FitOutputName(new string('a', 237), "-12", ".txt").Length == 240, "generated names also respect the UTF-16 component limit");
        Reject(() => PortableBatchFiles.FitOutputName(new string('中', 85) + "/invalid", "-1", ".txt"), "invalid path characters cannot be hidden by output truncation");
        Reject(() => PortableBatchFiles.FitOutputName(new string('中', 85) + "\ud800", "-1", ".txt"), "invalid Unicode cannot be hidden by output truncation");
        Reject(() => PortableBatchFiles.FitOutputName("name", "", "." + new string('x', 255)), "extension cannot be silently truncated");
        Reject(() => PortableBatchFiles.WriteNewOutput(outputDirectory, new string('中', 85) + ".txt", []), "explicit over-limit output names remain rejected");
        var parsePaths = typeof(IslandWindow).GetMethod("BatchPaths", BindingFlags.Static | BindingFlags.NonPublic)!;
        var literal = fixture.Path("literal-trailing.txt ");
        var parsed = (string[])parsePaths.Invoke(null, ["\r\n" + literal + "\r\n \t \n" + a + "\n"])!;
        Check(parsed.SequenceEqual([literal, a]), "batch path parsing preserves literal spaces and ignores only blank lines");
        if (!OperatingSystem.IsWindows())
        {
            File.WriteAllText(literal, "literal-space-source"); var trimmed = literal.TrimEnd(); File.WriteAllText(trimmed, "different-file");
            var literalPlan = engine.Preview([parsed[0]], new() { Prefix = "moved-" }); engine.Execute(literalPlan);
            Check(File.ReadAllText(literalPlan.Items[0].DestinationPath) == "literal-space-source" && File.ReadAllText(trimmed) == "different-file", "real Unix trailing-space path addresses only the exact selected file");
            engine.Undo(); Check(File.ReadAllText(literal) == "literal-space-source", "Unix trailing-space source restored exactly by undo");
        }
        else Console.WriteLine("INFO: trailing-space path preservation tested as a parser contract on Windows; real Unix filename fixture runs only on Unix.");
        var snapshot = PortableBatchFiles.ReadLocalSnapshot(first, 20); Check(snapshot.Source.Length == 5 && snapshot.Bytes.SequenceEqual("first"u8.ToArray()), "bounded strong-identity snapshot");
        Reject(() => PortableBatchFiles.ReadLocalSnapshot(first, 2), "snapshot size limit before allocation");
        var disposeSource = fixture.File("dispose/source.txt", "dispose");
        var disposable = new PortableBatchFiles(fixture.Data); disposable.Execute(disposable.Preview([disposeSource], new() { Prefix = "moved-" }));
        Check(disposable.CanUndo, "identity anchor exists before disposal"); disposable.Dispose(); disposable.Dispose();
        Check(!disposable.CanUndo, "disposing releases undo anchors idempotently"); Reject(() => disposable.Preview([a], new()), "use after engine disposal");
        Console.WriteLine($"PASS: batch-file Core {checks} checks on {RuntimeInformation.OSDescription}; foreign OS stat fixtures are ABI contracts, not native execution.");
    }

    internal static void RunImages()
    {
        using var fixture = new Fixture(); var output = fixture.Directory("output");
        var source = fixture.Path("source.png");
        using (var bitmap = new WriteableBitmap(new PixelSize(16, 8), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul))
        {
            using (var frame = bitmap.Lock()) { var row = new byte[16 * 4]; for (var x = 0; x < row.Length; x += 4) { row[x] = 0; row[x + 1] = 0; row[x + 2] = 255; row[x + 3] = 128; } for (var y = 0; y < 8; y++) Marshal.Copy(row, 0, IntPtr.Add(frame.Address, y * frame.RowBytes), row.Length); }
            bitmap.Save(source, PngBitmapEncoderOptions.Default);
        }
        var originalHash = SHA256.HashData(File.ReadAllBytes(source));
        var png = Task.Run(() => PortableBatchImages.Process([source], new(output, "PNG", 8, 8), fixture.Data)).GetAwaiter().GetResult();
        Check(png.Count == 1 && png[0].Width == 8 && png[0].Height == 4, "PNG aspect-ratio scaling on worker");
        using (var decoded = new Bitmap(png[0].OutputPath)) Check(decoded.PixelSize == new PixelSize(8, 4), "actual encoded PNG dimensions");
        var jpg = Task.Run(() => PortableBatchImages.Process([source], new(output, "JPG", 16, 8, 95), fixture.Data)).GetAwaiter().GetResult();
        using (var decoded = new Bitmap(jpg[0].OutputPath))
        using (var pixels = new WriteableBitmap(decoded.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul))
        using (var frame = pixels.Lock())
        {
            decoded.CopyPixels(frame); var pixel = new byte[4]; Marshal.Copy(frame.Address, pixel, 0, 4);
            Check(pixel[2] > 235 && pixel[1] is > 105 and < 150 && pixel[0] is > 105 and < 150 && pixel[3] == 255, "actual JPEG white alpha composition");
        }
        var repeated = PortableBatchImages.Process([source], new(output, "PNG", 8, 8), fixture.Data);
        Check(repeated[0].OutputPath != png[0].OutputPath && File.Exists(png[0].OutputPath), "image output collision uses a new name");
        Check(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "source image remains byte identical");
        var longChinese = fixture.Path(new string('中', 83) + ".png"); File.Copy(source, longChinese);
        var longEmoji = fixture.Path(string.Concat(Enumerable.Repeat("😀", 62)) + ".png"); File.Copy(source, longEmoji);
        Check(Encoding.UTF8.GetByteCount(Path.GetFileName(longChinese)) == 253 && Encoding.UTF8.GetByteCount(Path.GetFileName(longEmoji)) == 252, "real image fixtures approach the byte limit while remaining valid source names");
        var unicodeImages = Task.Run(() => PortableBatchImages.Process([longChinese, longEmoji], new(output, "PNG", 8, 8), fixture.Data)).GetAwaiter().GetResult();
        Check(unicodeImages.Count == 2 && unicodeImages.All(result => Encoding.UTF8.GetByteCount(Path.GetFileName(result.OutputPath)) <= 255 && result.OutputPath.EndsWith("-processed.png", StringComparison.Ordinal)), "long Chinese and emoji image names preserve generated suffix and extension within filesystem limits");
        foreach (var result in unicodeImages)
        {
            using var decoded = new Bitmap(result.OutputPath); Check(decoded.PixelSize == new PixelSize(8, 4), "near-limit named output is an actual readable resized PNG");
            Check(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(result.SourcePath))), "long-name image source bytes remain unchanged");
        }
        var unicodeAgain = PortableBatchImages.Process([longChinese, longEmoji], new(output, "PNG", 8, 8), fixture.Data);
        Check(unicodeAgain.All(next => unicodeImages.All(previous => next.OutputPath != previous.OutputPath)) && unicodeImages.All(previous => File.Exists(previous.OutputPath)), "repeated long-name image output allocates new files and preserves prior files");
        var dotImage = fixture.Path(".png"); File.Copy(source, dotImage);
        var dotResult = PortableBatchImages.Process([dotImage], new(output, "PNG", 8, 8), fixture.Data);
        Check(Path.GetFileName(dotResult[0].OutputPath) == ".png-processed.png" && File.Exists(dotImage), "dotfile image keeps a nonempty literal stem");
        var oversized = new byte[24]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(oversized, 0); BinaryPrimitives.WriteInt32BigEndian(oversized.AsSpan(8, 4), 13); "IHDR"u8.CopyTo(oversized.AsSpan(12, 4)); BinaryPrimitives.WriteInt32BigEndian(oversized.AsSpan(16, 4), 10000); BinaryPrimitives.WriteInt32BigEndian(oversized.AsSpan(20, 4), 10000);
        Reject(() => PortableBatchImages.ReadDimensions(oversized), "decompression bomb rejected before decode");
        Reject(() => PortableBatchImages.ReadDimensions([0xff, 0xd8, 0xff, 0xe0, 0, 1]), "malformed JPEG header");
        var noOutputs = Directory.GetFiles(output).Length; using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Reject(() => PortableBatchImages.Process([source], new(output, "PNG"), fixture.Data, cancel.Token), "image cancellation");
        Check(Directory.GetFiles(output).Length == noOutputs, "pre-cancelled image task produces no output");
        Check(Directory.GetFiles(Path.Combine(fixture.Data, "batch-operations")).All(path => File.ReadAllText(path).Contains("image-manifest", StringComparison.Ordinal)), "image journals retained in scoped data folder");
        var uiSource = fixture.File("ui/preview.txt", "preview-state"); using var uiEngine = new PortableBatchFiles(fixture.Data);
        var uiPlan = uiEngine.Preview([uiSource], new() { Prefix = "renamed-" });
        var window = new IslandWindow(true, fixture.Data);
        try
        {
            window.Show(); window.Expand();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(IslandWindow).GetField("batchEngine", flags)!.SetValue(window, uiEngine);
            typeof(IslandWindow).GetField("batchPlan", flags)!.SetValue(window, uiPlan);
            for (var cycle = 0; cycle < 8; cycle++)
            {
                AppearanceTestActions.Set(window, cycle % 2 == 0 ? "zh" : "en", cycle % 2 == 0);
                window.SelectToolForTest("batch-files"); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Check(ReferenceEquals(typeof(IslandWindow).GetField("batchPlan", flags)!.GetValue(window), uiPlan), "queued initial TextChanged does not invalidate a preview during language/theme/page rebuild");
            }
            uiEngine.Execute(uiPlan); Check(File.Exists(uiPlan.Items[0].DestinationPath), "preserved UI preview remains executable"); uiEngine.Undo();
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
        Console.WriteLine($"PASS: batch images, cumulative {checks} Core/image checks; actual Avalonia codec worker outputs verified.");
    }
    private sealed class InlineProgress(Action<PortableBatchProgress> callback) : IProgress<PortableBatchProgress> { public void Report(PortableBatchProgress value) => callback(value); }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "side-island-batch-tests-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> links = [];
        internal Fixture() { System.IO.Directory.CreateDirectory(root); }
        internal string Data => Directory("app-data");
        internal string Path(string relative) { var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative)); if (!path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("Fixture escaped its generated root."); System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); return path; }
        internal string Directory(string relative) { var path = Path(relative); System.IO.Directory.CreateDirectory(path); return path; }
        internal string File(string relative, string text) { var path = Path(relative); System.IO.File.WriteAllText(path, text); return path; }
        internal void TrackLink(string path) => links.Add(path);
        public void Dispose()
        {
            foreach (var link in links) System.IO.File.Delete(link);
            var resolved = System.IO.Path.GetFullPath(root); var parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            if (System.IO.Path.GetDirectoryName(resolved) != parent || !System.IO.Path.GetFileName(resolved).StartsWith("side-island-batch-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing cleanup outside the generated fixture.");
            System.IO.Directory.Delete(resolved, true);
        }
    }
}
