#nullable enable
using System.Collections;
using System.Security.Cryptography;
using System.Text;
using CodexIsland;
using SideIsland.Core;

namespace SideIsland.Tests;

internal static class BatchHashTests
{
    private static int checks;
    internal static void Run()
    {
        checks = 0; string root = Path.Combine(Path.GetTempPath(), "side-island-batch-hash-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            RealFiles(root); Verification(root); ControlledReads(root); Cancellation(root); Export(root); Bounds(root);
            Console.WriteLine("PASS: " + checks + " batch checksum checks; real owned SHA256/SHA512 files, GNU verification, per-file failures, frozen partial results, cancellation, byte progress, same-length stamp changes, path replacement and bounded growing streams. Native Unix execution is not implied by injected stream fixtures.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
    private static string Digest(byte[] bytes, string algorithm = "SHA256") => Convert.ToHexString(algorithm == "SHA512" ? SHA512.HashData(bytes) : SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Write(string root, string name, byte[] bytes) { string path = Path.Combine(root, name); File.WriteAllBytes(path, bytes); return path; }
    private sealed class Updates : IProgress<BatchHashProgress>
    {
        internal readonly List<BatchHashProgress> Values = [];
        internal Action<BatchHashProgress>? OnReport;
        public void Report(BatchHashProgress value) { Values.Add(value); OnReport?.Invoke(value); }
    }
    private static void RealFiles(string root)
    {
        byte[] content = Enumerable.Range(0, 256 * 1024 + 3).Select(index => (byte)(index % 251)).ToArray();
        string first = Write(root, "real 中文😀.bin", content), empty = Write(root, "empty.bin", []), missing = Path.Combine(root, "missing.bin");
        foreach (string algorithm in new[] { "SHA256", "SHA512" })
        {
            var updates = new Updates(); var callbacks = new List<BatchHashItem>();
            var result = PortableBatchHash.Run([first, missing, empty, root], algorithm, "", default, updates, callbacks.Add);
            Check(result.Items.Count == 4 && result.Items[0].Digest == Digest(content, algorithm) && result.Items[0].Status == BatchHashStatus.Completed, "real portable native-stamped checksum");
            Check(result.Items[1].Status == BatchHashStatus.Failed && result.Items[1].ErrorCode == "file-missing", "missing file stays visible");
            Check(result.Items[2].Digest == Digest([], algorithm) && result.Items[2].Status == BatchHashStatus.Completed, "read error does not drop later empty-file result");
            Check(result.Items[3].Status == BatchHashStatus.Failed && result.Items[3].ErrorCode == "not-regular", "directory selection is rejected per file");
            Check(callbacks.Count == result.Items.Count && callbacks.Zip(result.Items).All(pair => ReferenceEquals(pair.First, pair.Second)), "completed callback and final list share immutable item snapshots");
            Check(result.TotalBytesRead == content.Length && updates.Values.Last().TotalBytesRead == content.Length, "byte progress accounts for real bytes");
            Check(updates.Values.Zip(updates.Values.Skip(1)).All(pair => pair.First.TotalBytesRead <= pair.Second.TotalBytesRead), "aggregate bytes never move backwards");
            Check(result.Items is IList list && list.IsReadOnly, "result list is genuinely read-only");
            Check(!BatchHashTools.TryCreateManifest(result, out _, out string error) && error == "incomplete", "failed list cannot masquerade as complete exported manifest");
            if (OperatingSystem.IsWindows())
            {
                var native = BatchHashTools.Run([first, empty], algorithm, "", default);
                Check(native.Items.All(item => item.Status == BatchHashStatus.Completed) && native.Items[0].Digest == result.Items[0].Digest, "C#5 Windows default provider agrees with portable adapter");
            }
        }
        using (var held = new FileStream(first, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            if (OperatingSystem.IsWindows())
            {
                var blocked = PortableBatchHash.Run([first, empty], "SHA256", "", default);
                Check(blocked.Items[0].Status == BatchHashStatus.Failed && blocked.Items[1].Status == BatchHashStatus.Completed, "real existing writable handle cannot yield an unprotected Windows read");
            }
        }
        string link = Path.Combine(root, "hash-link.bin"); File.CreateSymbolicLink(link, first);
        var linked = PortableBatchHash.Run([link], "SHA256", "", default);
        Check(linked.Items[0].ErrorCode == "not-regular" && File.ReadAllBytes(first).SequenceEqual(content), "symbolic selection is rejected without modifying its target");
    }
    private static void Verification(string root)
    {
        string a = Write(root, "verify-a.txt", Encoding.UTF8.GetBytes("first")), b = Write(root, "verify-b.txt", Encoding.UTF8.GetBytes("second"));
        string ah = Digest(File.ReadAllBytes(a)), bh = Digest(File.ReadAllBytes(b));
        string manifest = FileHashTools.CreateManifest(ah, "SHA256", a) + FileHashTools.CreateManifest(bh, "SHA256", b);
        var valid = PortableBatchHash.Run([a, b], "SHA256", manifest, default);
        Check(valid.Items.All(item => item.Verification == BatchHashVerification.Matched), "manifest matches each exact selected basename");
        var mismatch = PortableBatchHash.Run([a, b], "SHA256", FileHashTools.CreateManifest(bh, "SHA256", a) + FileHashTools.CreateManifest(bh, "SHA256", b), default);
        Check(mismatch.Items[0].Verification == BatchHashVerification.Mismatched && mismatch.Items[0].Digest == ah && mismatch.Items[1].Verification == BatchHashVerification.Matched, "mismatch preserves computed digest and other verification");
        foreach (string bare in new[] { ah, "\uFEFF" + ah })
        {
            var result = PortableBatchHash.Run([a, b], "SHA256", bare, default);
            Check(result.Items.All(item => item.Status == BatchHashStatus.Completed && item.Verification == BatchHashVerification.InvalidExpected && item.ExpectedError == "manifest-required"), "single digest cannot silently verify multiple selected files");
        }
        Check(PortableBatchHash.Run([a], "SHA256", ah, default).Items[0].Verification == BatchHashVerification.Matched, "single-file raw checksum remains useful");
        var absent = PortableBatchHash.Run([a, b], "SHA256", FileHashTools.CreateManifest(ah, "SHA256", a), default);
        Check(absent.Items[1].ExpectedError == "file-missing" && absent.Items[1].Digest == bh, "missing manifest entry does not erase computed digest");
        foreach (string bad in new[] { "bad manifest", new string(' ', FileHashTools.MaximumManifestLength + 1), manifest + "nonsense", manifest + ah + " *other/verify-a.txt\n" })
        {
            var result = PortableBatchHash.Run([a, b], "SHA256", bad, default);
            Check(result.Items[0].Status == BatchHashStatus.Completed && result.Items[0].Verification == BatchHashVerification.InvalidExpected, "invalid/oversized/conflicting manifest never becomes successful verification");
        }
        string other = Path.Combine(root, "other"); Directory.CreateDirectory(other); string duplicate = Write(other, "verify-a.txt", Encoding.UTF8.GetBytes("first"));
        var duplicates = PortableBatchHash.Run([a, duplicate], "SHA256", FileHashTools.CreateManifest(ah, "SHA256", a), default);
        Check(duplicates.Items.All(item => item.ExpectedError == "ambiguous-selection" && item.Digest == ah), "same basename in different directories cannot pretend to be distinct manifest entries");
        Check(!BatchHashTools.TryCreateManifest(duplicates, out _, out string error) && error == "ambiguous-selection", "same basename export refuses ambiguity even with identical digests");
        var repeated = PortableBatchHash.Run([a, a], "SHA256", "", default);
        Check(repeated.Items.Count == 2 && !BatchHashTools.TryCreateManifest(repeated, out _, out _), "duplicate selections remain visible and cannot become duplicate export entries");
    }
    private sealed class ProbeStream : MemoryStream
    {
        internal Action<ProbeStream>? AfterRead;
        internal int MaximumRequested, Calls;
        internal bool Disposed;
        internal ProbeStream(byte[] bytes) { Write(bytes); Position = 0; }
        public override int Read(byte[] buffer, int offset, int count)
        { MaximumRequested = Math.Max(MaximumRequested, count); Calls++; int value = base.Read(buffer, offset, count); AfterRead?.Invoke(this); return value; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class ProbeSource : BatchHashSource
    {
        internal readonly ProbeStream Data;
        internal BatchHashStamp Stamp;
        internal BatchHashStamp? PathStamp;
        internal Action? OnDispose;
        internal ProbeSource(byte[] bytes) { Data = new(bytes); Stamp = new("owned-probe", bytes.Length, 1, 0); }
        public override Stream Stream => Data;
        public override BatchHashStamp ReadStamp() => Stamp;
        public override BatchHashStamp ReadPathStamp() => PathStamp ?? Stamp;
        public override void Dispose() { Data.Dispose(); OnDispose?.Invoke(); }
    }
    private sealed class ProbeProvider(Func<string, ProbeSource> open) : IBatchHashProvider
    {
        internal int Active, MaximumActive;
        internal readonly List<string> Opened = [];
        internal readonly List<ProbeSource> Sources = [];
        public BatchHashSource Open(string path)
        {
            var source = open(path); Opened.Add(path); Sources.Add(source); Active++; MaximumActive = Math.Max(MaximumActive, Active);
            source.OnDispose = () => Active--; return source;
        }
    }
    private static void ControlledReads(string root)
    {
        string path = Path.Combine(root, "probe.bin"); byte[] bytes = new byte[131072];
        foreach (string kind in new[] { "same-length", "replaced-path", "shrunk", "growing" })
        {
            var source = new ProbeSource(bytes); bool once = false;
            source.Data.AfterRead = stream =>
            {
                if (once) return; once = true;
                if (kind == "same-length") { long offset = stream.Position; stream.Position = 0; stream.WriteByte(42); stream.Position = offset; source.Stamp = new("owned-probe", bytes.Length, 2, 0); }
                else if (kind == "replaced-path") source.PathStamp = new("different-file", bytes.Length, 1, 0);
                else if (kind == "shrunk") stream.SetLength(65536);
                else { long offset = stream.Position; stream.Position = stream.Length; stream.Write(new byte[2 * 1024 * 1024]); stream.Position = offset; }
            };
            var provider = new ProbeProvider(_ => source); var result = BatchHashTools.Run([path], "SHA256", "", default, provider: provider);
            Check(result.Items[0].Status == BatchHashStatus.Failed && result.Items[0].ErrorCode == "changed" && result.Items[0].Digest is null, "changed source rejected: " + kind);
            Check(source.Data.MaximumRequested <= 65536 && result.TotalBytesRead <= bytes.Length + 1, "source reads remain bounded by initial length plus EOF byte: " + kind);
            Check(source.Data.Disposed && provider.Active == 0, "changed stream is disposed: " + kind);
        }
        var requests = new List<string> { Path.Combine(root, "first-probe"), Path.Combine(root, "second-probe") };
        var frozen = new ProbeProvider(_ => new ProbeSource([1, 2, 3]));
        var resultFrozen = BatchHashTools.Run(requests, "SHA256", "", default, itemCompleted: item => { if (item.Index == 0) requests[1] = Path.Combine(root, "replacement"); }, provider: frozen);
        Check(resultFrozen.Items[1].Path.EndsWith("second-probe") && frozen.Opened[1].EndsWith("second-probe"), "later caller list edits do not change frozen selections");
        Check(frozen.MaximumActive == 1 && frozen.Active == 0, "sequential engine owns at most one active file stream");
        var failing = new ProbeProvider(file => { if (file.EndsWith("error")) throw new IOException("owned read failure"); return new ProbeSource([7]); });
        var continued = BatchHashTools.Run([path, Path.Combine(root, "error"), path], "SHA256", "", default, provider: failing);
        Check(continued.Items.Select(item => item.Status).SequenceEqual(new[] { BatchHashStatus.Completed, BatchHashStatus.Failed, BatchHashStatus.Completed }), "one read failure preserves earlier and later completed results");
        var denied = new ProbeProvider(_ => throw new UnauthorizedAccessException("owned denial"));
        Check(BatchHashTools.Run([path], "SHA256", "", default, provider: denied).Items[0].ErrorCode == "access", "access denial has localized stable error code");
    }
    private static void Cancellation(string root)
    {
        var selections = new[] { Path.Combine(root, "cancel-a"), Path.Combine(root, "cancel-b"), Path.Combine(root, "cancel-c") };
        using (var token = new CancellationTokenSource())
        {
            token.Cancel(); var provider = new ProbeProvider(_ => new ProbeSource([1])); var result = BatchHashTools.Run(selections, "SHA256", "", token.Token, provider: provider);
            Check(result.Cancelled && result.Items.All(item => item.Status == BatchHashStatus.NotStarted) && provider.Opened.Count == 0, "cancellation before start preserves all selections without IO");
        }
        using (var token = new CancellationTokenSource())
        {
            var provider = new ProbeProvider(_ => new ProbeSource([1]));
            var result = BatchHashTools.Run(selections, "SHA256", "", token.Token, itemCompleted: item => { if (item.Index == 0) token.Cancel(); }, provider: provider);
            Check(result.Cancelled && result.Items[0].Status == BatchHashStatus.Completed && result.Items.Skip(1).All(item => item.Status == BatchHashStatus.NotStarted), "cancel between files preserves completed checksum");
            Check(provider.Opened.Count == 1 && provider.Active == 0 && result.Items[0].Digest == Digest([1]), "no later file is opened after cancellation");
        }
        using (var token = new CancellationTokenSource())
        {
            var source = new ProbeSource(new byte[131072]); source.Data.AfterRead = _ => token.Cancel();
            var provider = new ProbeProvider(_ => source); var updates = new Updates();
            var result = BatchHashTools.Run(selections, "SHA256", "", token.Token, updates, provider: provider);
            Check(result.Cancelled && result.Items[0].Status == BatchHashStatus.Cancelled && result.Items[0].Digest is null && result.Items.Skip(1).All(item => item.Status == BatchHashStatus.NotStarted), "cancel during a read never publishes partial digest");
            Check(result.TotalBytesRead == 65536 && updates.Values.Last().TotalBytesRead == 65536 && source.Data.Disposed, "cancel accounts for real read bytes and disposes the current stream");
        }
        using (var token = new CancellationTokenSource())
        {
            var source = new ProbeSource([5]); var progress = new Updates { OnReport = _ => token.Cancel() };
            var result = BatchHashTools.Run(selections, "SHA256", "", token.Token, progress, provider: new ProbeProvider(_ => source));
            Check(result.Items[0].Status == BatchHashStatus.Cancelled && source.Data.Calls == 0, "cancel from initial progress stops before first read");
        }
    }
    private static void Export(string root)
    {
        string a = Write(root, "export-a.txt", [1, 2]), b = Write(root, "export-b.txt", [3, 4]);
        var result = PortableBatchHash.Run([a, b], "SHA512", "", default);
        Check(BatchHashTools.TryCreateManifest(result, out string manifest, out _) && manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2, "complete batch exports one GNU entry per ordered file");
        foreach (var item in result.Items)
            Check(FileHashTools.TryResolveExpected(manifest, "SHA512", item.Path, out string expected, out _) && item.Digest == expected, "batch GNU export resolves to each exact checksum");
        string digest = Digest([8]); string escaped = Path.Combine(root, "name\nwith\rline.txt");
        var itemEscaped = new BatchHashItem(0, escaped, Path.GetFileName(escaped), BatchHashStatus.Completed, digest, 1, 1, BatchHashVerification.None, null, null, null, null);
        var escapedResult = new BatchHashResult("SHA256", [itemEscaped], false, 1);
        Check(BatchHashTools.TryCreateManifest(escapedResult, out string escapedManifest, out _) && escapedManifest.StartsWith('\\') && escapedManifest.Contains("\\nwith\\r"), "GNU escape syntax preserves newline and carriage return filenames");
        Check(FileHashTools.TryResolveExpected(escapedManifest, "SHA256", escaped, out string resolved, out _) && resolved == digest, "escaped GNU entry roundtrip");
        string longName = new('x', 900); var many = Enumerable.Range(0, 200).Select(index => new BatchHashItem(index, Path.Combine(root, index + longName), index + longName, BatchHashStatus.Completed, digest, 1, 1, BatchHashVerification.None, null, null, null, null)).ToList();
        Check(!BatchHashTools.TryCreateManifest(new BatchHashResult("SHA256", many, false, 200), out _, out string tooLarge) && tooLarge == "too-large", "aggregate manifest cap applies after combining valid individual entries");
        if (!OperatingSystem.IsWindows())
        {
            string spaced = Write(root, "literal\\name ", [9]); var literal = PortableBatchHash.Run([spaced], "SHA256", "", default);
            Check(BatchHashTools.TryCreateManifest(literal, out string text, out _) && text.Contains("literal\\\\name "), "Unix literal backslash and trailing filename space retained");
        }
    }
    private static void Bounds(string root)
    {
        var provider = new ProbeProvider(_ => new ProbeSource([])); string path = Path.Combine(root, "bounds");
        foreach (IList<string> invalid in new IList<string>[] { [], Enumerable.Repeat(path, 201).ToArray() })
        {
            bool rejected = false; try { BatchHashTools.Run(invalid, "SHA256", "", default, provider: provider); } catch (ArgumentException) { rejected = true; }
            Check(rejected && provider.Opened.Count == 0, "invalid selection count rejected before IO");
        }
        bool algorithm = false; try { BatchHashTools.Run([path], "MD5", "", default, provider: provider); } catch (ArgumentException) { algorithm = true; }
        Check(algorithm && provider.Opened.Count == 0, "unsupported algorithm rejected before IO");
        var paths = BatchHashTools.Run([path, "bad\0path", new string('x', BatchHashTools.MaximumPathLength + 1), path], "SHA256", "", default, provider: provider);
        Check(paths.Items.Count == 4 && paths.Items[1].ErrorCode == "invalid-path" && paths.Items[2].ErrorCode == "invalid-path" && paths.Items[3].Status == BatchHashStatus.Completed, "invalid individual paths stay visible without dropping valid selections");
        var maximum = BatchHashTools.Run(Enumerable.Range(0, 200).Select(index => Path.Combine(root, "max" + index)).ToArray(), "SHA256", "", default, provider: new ProbeProvider(_ => new ProbeSource([])));
        Check(maximum.Items.Count == 200 && maximum.Items.All(item => item.Status == BatchHashStatus.Completed), "maximum allowed selection is fully processed");
    }
}
