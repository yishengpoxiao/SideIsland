using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CodexIsland
{
    internal enum BatchHashStatus { Completed, Failed, Cancelled, NotStarted }
    internal enum BatchHashVerification { None, Matched, Mismatched, InvalidExpected }

    internal sealed class BatchHashStamp : IEquatable<BatchHashStamp>
    {
        public readonly string Identity;
        public readonly long Length, ModifiedSeconds, ModifiedFraction;
        public BatchHashStamp(string identity, long length, long modifiedSeconds, long modifiedFraction)
        { Identity = identity; Length = length; ModifiedSeconds = modifiedSeconds; ModifiedFraction = modifiedFraction; }
        public bool Equals(BatchHashStamp other)
        { return other != null && Identity == other.Identity && Length == other.Length && ModifiedSeconds == other.ModifiedSeconds && ModifiedFraction == other.ModifiedFraction; }
        public override bool Equals(object other) { return Equals(other as BatchHashStamp); }
        public override int GetHashCode() { return (Identity ?? "").GetHashCode() ^ Length.GetHashCode() ^ ModifiedSeconds.GetHashCode() ^ ModifiedFraction.GetHashCode(); }
    }
    internal abstract class BatchHashSource : IDisposable
    {
        public abstract Stream Stream { get; }
        public abstract BatchHashStamp ReadStamp();
        public abstract BatchHashStamp ReadPathStamp();
        public abstract void Dispose();
    }
    internal interface IBatchHashProvider { BatchHashSource Open(string path); }
    internal sealed class BatchHashReadException : IOException
    {
        public readonly string Code;
        public BatchHashReadException(string code, string message) : base(message) { Code = code; }
    }
    internal sealed class BatchHashProgress
    {
        public readonly int Index, FileCount;
        public readonly long FileBytesRead, FileLength, TotalBytesRead;
        internal BatchHashProgress(int index, int fileCount, long fileBytesRead, long fileLength, long totalBytesRead)
        { Index = index; FileCount = fileCount; FileBytesRead = fileBytesRead; FileLength = fileLength; TotalBytesRead = totalBytesRead; }
    }
    internal sealed class BatchHashItem
    {
        public readonly int Index;
        public readonly string Path, FileName, Digest, ExpectedDigest, ExpectedError, ErrorCode, ErrorMessage;
        public readonly long BytesRead, Length;
        public readonly BatchHashStatus Status;
        public readonly BatchHashVerification Verification;
        internal BatchHashItem(int index, string path, string fileName, BatchHashStatus status, string digest, long bytesRead, long length,
            BatchHashVerification verification, string expectedDigest, string expectedError, string errorCode, string errorMessage)
        {
            Index = index; Path = path; FileName = fileName; Status = status; Digest = digest; BytesRead = bytesRead; Length = length;
            Verification = verification; ExpectedDigest = expectedDigest; ExpectedError = expectedError; ErrorCode = errorCode; ErrorMessage = errorMessage;
        }
    }
    internal sealed class BatchHashResult
    {
        public readonly string Algorithm;
        public readonly ReadOnlyCollection<BatchHashItem> Items;
        public readonly bool Cancelled;
        public readonly long TotalBytesRead;
        internal BatchHashResult(string algorithm, IList<BatchHashItem> items, bool cancelled, long totalBytesRead)
        { Algorithm = algorithm; Items = new ReadOnlyCollection<BatchHashItem>(new List<BatchHashItem>(items)); Cancelled = cancelled; TotalBytesRead = totalBytesRead; }
    }
    internal static class BatchHashTools
    {
        public const int MaximumFiles = 200;
        public const int MaximumPathLength = 32768;
        private sealed class Input
        {
            internal string Path, Name, ErrorCode, ErrorMessage;
        }
        public static BatchHashResult Run(IList<string> paths, string algorithm, string expectedManifest, CancellationToken cancellation,
            IProgress<BatchHashProgress> progress = null, Action<BatchHashItem> itemCompleted = null, IBatchHashProvider provider = null)
        {
            if (paths == null || paths.Count == 0 || paths.Count > MaximumFiles) throw new ArgumentException("Choose between 1 and 200 files.", "paths");
            if (algorithm != "SHA256" && algorithm != "SHA512") throw new ArgumentException("Choose SHA256 or SHA512.", "algorithm");
            string expected = expectedManifest ?? "";
            bool expectedTooLarge = expected.Length > FileHashTools.MaximumManifestLength;
            bool hasExpected = expectedTooLarge || !String.IsNullOrWhiteSpace(expected);
            string bareDigest;
            bool bareExpected = !expectedTooLarge && FileHashTools.TryNormalizeExpected(expected.StartsWith("\uFEFF", StringComparison.Ordinal) ? expected.Substring(1) : expected, algorithm, out bareDigest);
            // Freeze every selection before opening any file. The sequence and
            // duplicate entries are visible, never silently dropped or merged.
            var inputs = new List<Input>(); var names = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < paths.Count; index++)
            {
                string path = paths[index]; var input = new Input { Path = path ?? "", Name = "" };
                try
                {
                    if (String.IsNullOrEmpty(path) || path.Length > MaximumPathLength || path.IndexOf('\0') >= 0) throw new ArgumentException("Invalid file path.");
                    input.Path = System.IO.Path.GetFullPath(path); input.Name = System.IO.Path.GetFileName(input.Path);
                    if (String.IsNullOrEmpty(input.Name)) throw new ArgumentException("Choose a file, not a root directory.");
                    int count; names.TryGetValue(input.Name, out count); names[input.Name] = count + 1;
                }
                catch (Exception error) { if (!Expected(error)) throw; input.ErrorCode = "invalid-path"; input.ErrorMessage = error.Message; }
                inputs.Add(input);
            }
            provider = provider ?? new WindowsProvider();
            var items = new List<BatchHashItem>(); long total = 0; bool cancelled = false;
            for (int index = 0; index < inputs.Count; index++)
            {
                if (cancellation.IsCancellationRequested) { cancelled = true; break; }
                Input input = inputs[index]; long read = 0, length = -1; string digest = null, code = input.ErrorCode, message = input.ErrorMessage;
                BatchHashStatus status = BatchHashStatus.Completed;
                if (code != null) status = BatchHashStatus.Failed;
                else
                {
                    var elapsed = Stopwatch.StartNew();
                    Action<bool> report = force => { if (progress != null && (force || elapsed.ElapsedMilliseconds >= 100)) { progress.Report(new BatchHashProgress(index, inputs.Count, read, length, total)); elapsed.Restart(); } };
                    try
                    {
                        using (BatchHashSource source = provider.Open(input.Path))
                        {
                            cancellation.ThrowIfCancellationRequested();
                            BatchHashStamp before = source.ReadStamp();
                            if (before == null || String.IsNullOrEmpty(before.Identity) || before.Length < 0) throw new BatchHashReadException("not-regular", "A stable ordinary file is required.");
                            length = before.Length; report(true);
                            using (HashAlgorithm hash = algorithm == "SHA256" ? (HashAlgorithm)SHA256.Create() : SHA512.Create())
                            {
                                byte[] buffer = new byte[65536];
                                // Bound reads to the observed length plus one EOF
                                // byte, even if another process keeps appending.
                                while (read < length)
                                {
                                    cancellation.ThrowIfCancellationRequested();
                                    int count = source.Stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - read));
                                    if (count == 0) throw Changed();
                                    read += count; total = SaturatingAdd(total, count); cancellation.ThrowIfCancellationRequested();
                                    hash.TransformBlock(buffer, 0, count, buffer, 0); report(false);
                                }
                                cancellation.ThrowIfCancellationRequested();
                                int extra = source.Stream.Read(buffer, 0, 1);
                                if (extra != 0) { read += extra; total = SaturatingAdd(total, extra); throw Changed(); }
                                if (!before.Equals(source.ReadStamp()) || !before.Equals(source.ReadPathStamp())) throw Changed();
                                cancellation.ThrowIfCancellationRequested();
                                hash.TransformFinalBlock(new byte[0], 0, 0);
                                var text = new StringBuilder(hash.Hash.Length * 2); foreach (byte value in hash.Hash) text.Append(value.ToString("x2"));
                                digest = text.ToString();
                            }
                        }
                    }
                    catch (OperationCanceledException error)
                    {
                        if (cancellation.IsCancellationRequested) { status = BatchHashStatus.Cancelled; cancelled = true; code = "cancelled"; }
                        else { status = BatchHashStatus.Failed; code = "read"; }
                        message = error.Message; digest = null;
                    }
                    catch (Exception error)
                    { if (!Expected(error)) throw; status = BatchHashStatus.Failed; code = ErrorCode(error); message = error.Message; digest = null; }
                    report(true);
                }
                string resolved = null, expectedError = null; BatchHashVerification verification = BatchHashVerification.None;
                if (status == BatchHashStatus.Completed && hasExpected)
                {
                    if (expectedTooLarge) expectedError = "too-large";
                    else if (inputs.Count > 1 && bareExpected) expectedError = "manifest-required";
                    else if (names[input.Name] > 1) expectedError = "ambiguous-selection";
                    else FileHashTools.TryResolveExpected(expected, algorithm, input.Path, out resolved, out expectedError);
                    if (expectedError != null) verification = BatchHashVerification.InvalidExpected;
                    else verification = digest == resolved ? BatchHashVerification.Matched : BatchHashVerification.Mismatched;
                }
                var item = new BatchHashItem(index, input.Path, input.Name, status, digest, read, length, verification, resolved, expectedError, code, message);
                items.Add(item); if (itemCompleted != null) itemCompleted(item);
                if (cancelled) break;
            }
            for (int index = items.Count; index < inputs.Count; index++)
            {
                var input = inputs[index]; var item = new BatchHashItem(index, input.Path, input.Name, BatchHashStatus.NotStarted, null, 0, -1, BatchHashVerification.None, null, null, "cancelled", null);
                items.Add(item); if (itemCompleted != null) itemCompleted(item);
            }
            return new BatchHashResult(algorithm, items, cancelled, total);
        }
        public static bool TryCreateManifest(BatchHashResult result, out string manifest, out string error)
        {
            manifest = null; error = null;
            if (result == null || result.Items.Count == 0 || result.Items.Count > MaximumFiles) { error = "incomplete"; return false; }
            var names = new HashSet<string>(StringComparer.Ordinal); var text = new StringBuilder();
            foreach (BatchHashItem item in result.Items)
            {
                if (item.Status != BatchHashStatus.Completed || item.Digest == null) { error = "incomplete"; return false; }
                if (!names.Add(item.FileName)) { error = "ambiguous-selection"; return false; }
                try { text.Append(FileHashTools.CreateManifest(item.Digest, result.Algorithm, item.Path)); }
                catch (ArgumentException) { error = "filename"; return false; }
                if (text.Length > FileHashTools.MaximumManifestLength) { error = "too-large"; return false; }
            }
            manifest = text.ToString(); return true;
        }
        private static long SaturatingAdd(long value, int added) { return value > Int64.MaxValue - added ? Int64.MaxValue : value + added; }
        private static BatchHashReadException Changed() { return new BatchHashReadException("changed", "The file changed while its checksum was being read."); }
        private static string ErrorCode(Exception error)
        {
            var known = error as BatchHashReadException;
            if (known != null) return known.Code;
            if (error is FileNotFoundException || error is DirectoryNotFoundException) return "file-missing";
            if (error is UnauthorizedAccessException || error is System.Security.SecurityException) return "access";
            if (error is ArgumentException || error is PathTooLongException) return "invalid-path";
            if (error is NotSupportedException) return "unsupported";
            return "read";
        }
        private static bool Expected(Exception error)
        { return error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException || error is ArgumentException || error is NotSupportedException || error is Win32Exception || error is CryptographicException || error is ObjectDisposedException; }
        private sealed class WindowsProvider : IBatchHashProvider
        {
            public BatchHashSource Open(string path)
            {
                if (System.IO.Path.DirectorySeparatorChar != '\\') throw new PlatformNotSupportedException("A portable native file provider is required.");
                return new WindowsSource(path);
            }
        }
        private sealed class WindowsSource : BatchHashSource
        {
            private readonly string path;
            private readonly FileStream stream;
            internal WindowsSource(string path) { this.path = path; stream = OpenRegular(path); }
            public override Stream Stream { get { return stream; } }
            public override BatchHashStamp ReadStamp() { return Stamp(stream.SafeFileHandle); }
            public override BatchHashStamp ReadPathStamp() { using (var current = OpenRegular(path)) return Stamp(current.SafeFileHandle); }
            public override void Dispose() { stream.Dispose(); }
            private static FileStream OpenRegular(string path)
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new BatchHashReadException("not-regular", "Choose an ordinary file, not a directory or symbolic link.");
                var value = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                try { Stamp(value.SafeFileHandle); return value; } catch { value.Dispose(); throw; }
            }
            private static BatchHashStamp Stamp(SafeFileHandle handle)
            {
                WinInfo info; if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((info.Attributes & 0x410) != 0) throw new BatchHashReadException("not-regular", "Choose an ordinary file.");
                return new BatchHashStamp(info.Volume.ToString("X8") + ":" + info.IndexHigh.ToString("X8") + info.IndexLow.ToString("X8"),
                    ((long)info.SizeHigh << 32) | info.SizeLow, unchecked((long)(((ulong)info.Written.High << 32) | info.Written.Low)), 0);
            }
            [StructLayout(LayoutKind.Sequential)] private struct WinTime { internal uint Low, High; }
            [StructLayout(LayoutKind.Sequential)] private struct WinInfo { internal uint Attributes; internal WinTime Created, Accessed, Written; internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
            [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out WinInfo info);
        }
    }
}
