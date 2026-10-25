using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CodexIsland
{
    internal static class FileHashTools
    {
        public static string ComputeFile(string path, string algorithm, CancellationToken cancellation, IProgress<int> progress = null)
        {
            cancellation.ThrowIfCancellationRequested();
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("请先选择一个文件。");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
                return Compute(stream, algorithm, cancellation, progress);
        }

        internal static string Compute(Stream stream, string algorithm, CancellationToken cancellation, IProgress<int> progress = null)
        {
            if (algorithm != "SHA256" && algorithm != "SHA512") throw new ArgumentException("请选择 SHA-256 或 SHA-512。");
            cancellation.ThrowIfCancellationRequested();
            using (HashAlgorithm hash = algorithm == "SHA256" ? (HashAlgorithm)SHA256.Create() : SHA512.Create())
            {
                long length = stream.Length, processed = 0; byte[] buffer = new byte[65536]; var elapsed = Stopwatch.StartNew();
                if (progress != null) progress.Report(0);
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    hash.TransformBlock(buffer, 0, count, buffer, 0); processed += count;
                    if (progress != null && elapsed.ElapsedMilliseconds >= 100) { progress.Report(length == 0 ? 0 : (int)Math.Min(99, processed * 100.0 / length)); elapsed.Restart(); }
                }
                cancellation.ThrowIfCancellationRequested();
                if (stream.Length != length || processed != length) throw new IOException("文件在计算期间发生变化，请重试。");
                hash.TransformFinalBlock(new byte[0], 0, 0);
                var text = new StringBuilder(hash.Hash.Length * 2); foreach (byte value in hash.Hash) text.Append(value.ToString("x2"));
                if (progress != null) progress.Report(100); return text.ToString();
            }
        }

        public static bool TryNormalizeExpected(string input, string algorithm, out string expected)
        {
            expected = (input ?? "").Trim();
            int length = algorithm == "SHA256" ? 64 : algorithm == "SHA512" ? 128 : 0;
            if (length == 0 || expected.Length != length) return false;
            foreach (char value in expected) if (!((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f') || (value >= 'A' && value <= 'F'))) return false;
            expected = expected.ToLowerInvariant(); return true;
        }
    }
}
