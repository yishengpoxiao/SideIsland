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
        public const int MaximumManifestLength = 131072;

        // Read text only: filenames in a checksum list are never opened. Match
        // the selected basename exactly so a different download is not verified.
        public static bool TryResolveExpected(string input, string algorithm, string fileName, out string expected, out string error)
        {
            expected = null; error = null;
            string text = input ?? "";
            if (text.Length > MaximumManifestLength) { error = "too-large"; return false; }
            if (text.StartsWith("\uFEFF", StringComparison.Ordinal)) text = text.Substring(1);
            string digest;
            // Count physical lines before normalization, including blank lines.
            int physicalLines = text.Length == 0 ? 0 : 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')))
                    if (i + 1 < text.Length && ++physicalLines > 1024) { error = "too-large"; return false; }
            }
            if (TryNormalizeExpected(text, algorithm, out digest)) { expected = digest; return true; }
            if (text.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) < 0) { error = "digest"; return false; }
            int length = algorithm == "SHA256" ? 64 : algorithm == "SHA512" ? 128 : 0;
            if (length == 0) { error = "digest"; return false; }
            string selected = Basename(fileName ?? "") ?? "";
            string matchedName = null, matchedHash = null;
            using (var reader = new StringReader(text))
            {
                string line; int count = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    if (++count > 1024) { error = "too-large"; return false; }
                    if (String.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    bool escaped = line[0] == '\\';
                    if (escaped) line = line.Substring(1);
                    string name = null, hash = null;
                    if (line.Length > length + 2 && line[length] == ' ' && (line[length + 1] == ' ' || line[length + 1] == '*'))
                    { hash = line.Substring(0, length); name = line.Substring(length + 2); }
                    else if (line.StartsWith(algorithm + " (", StringComparison.Ordinal))
                    {
                        int end = line.LastIndexOf(") = ", StringComparison.Ordinal);
                        if (end > algorithm.Length + 2) { name = line.Substring(algorithm.Length + 2, end - algorithm.Length - 2); hash = line.Substring(end + 4); }
                    }
                    if (escaped && !TryUnescapeFilename(name, out name)) { error = "manifest"; return false; }
                    if (String.IsNullOrEmpty(name) || name.IndexOf('\0') >= 0 || !TryNormalizeExpected(hash, algorithm, out digest)) { error = "manifest"; return false; }
                    if (!String.Equals(Basename(name), selected, StringComparison.Ordinal)) continue;
                    if (matchedName != null && (!String.Equals(name, matchedName, StringComparison.Ordinal) || digest != matchedHash))
                    { error = "ambiguous"; return false; }
                    matchedName = name; matchedHash = digest;
                }
            }
            if (selected.Length == 0) { error = "file-required"; return false; }
            if (matchedHash == null) { error = "file-missing"; return false; }
            expected = matchedHash; return true;
        }

        private static string Basename(string path)
        {
            // Backslashes are valid filename characters on Unix. Only the
            // current platform's actual separators may split a selected path.
            // This parses text, not a native path: keep GNU escaped CR/LF names
            // intact even on platforms that cannot create those filenames.
            if (path == null || path.IndexOf('\0') >= 0) return null;
            int last = Math.Max(path.LastIndexOf(Path.DirectorySeparatorChar), path.LastIndexOf(Path.AltDirectorySeparatorChar));
            if (Path.DirectorySeparatorChar == '\\' && last < 1 && path.Length >= 2 && path[1] == ':') last = 1;
            return path.Substring(last + 1);
        }

        private static bool TryUnescapeFilename(string value, out string name)
        {
            name = null;
            if (value == null) return false;
            var result = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char next = value[i];
                if (next == '\\')
                {
                    if (++i == value.Length) return false;
                    next = value[i];
                    if (next == 'n') next = '\n';
                    else if (next == 'r') next = '\r';
                    else if (next != '\\') return false;
                }
                result.Append(next);
            }
            name = result.ToString(); return true;
        }

        // GNU binary untagged format; no source directories or absolute paths
        // are exported. Preserve filename whitespace and use LF on every OS.
        public static string CreateManifest(string digest, string algorithm, string sourcePath)
        {
            string normalized;
            if (!TryNormalizeExpected(digest, algorithm, out normalized)) throw new ArgumentException("Invalid checksum.", "digest");
            string name = Basename(sourcePath);
            if (String.IsNullOrEmpty(name) || name == "." || name == "..") throw new ArgumentException("A filename is required.", "sourcePath");
            bool escaped = name.IndexOfAny(new[] { '\\', '\n', '\r' }) >= 0;
            if (escaped) name = name.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r");
            string manifest = (escaped ? "\\" : "") + normalized + " *" + name + "\n";
            if (manifest.Length > MaximumManifestLength) throw new ArgumentException("Checksum list is too large.", "sourcePath");
            new UTF8Encoding(false, true).GetByteCount(manifest); // Reject malformed UTF-16; never replace filename bytes.
            return manifest;
        }

        // The default path is the Windows/.NET Framework implementation. Unix
        // callers must provide a native no-replace publisher: File.Move's Unix
        // check/rename optimization cannot enforce exclusive publication.
        public static void SaveManifestNew(string destination, string manifest)
        { SaveManifestNew(destination, manifest, null); }
        public static void SaveManifestNew(string destination, string manifest, Action<string, byte[]> publisher)
        {
            if (String.IsNullOrEmpty(manifest) || manifest.Length > MaximumManifestLength) throw new ArgumentException("Invalid checksum list.", "manifest");
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(manifest);
            string target = Path.GetFullPath(destination);
            if (publisher != null) { publisher(target, bytes); return; }
            if (Path.DirectorySeparatorChar != '\\') throw new PlatformNotSupportedException("Checksum export on Unix requires a native exclusive file publisher.");
            bool exists;
            try { File.GetAttributes(target); exists = true; }
            catch (FileNotFoundException) { exists = false; }
            catch (DirectoryNotFoundException) { exists = false; }
            if (exists) throw new IOException("The destination already exists. Choose a new filename.");
            string temporary = Path.Combine(Path.GetDirectoryName(target), ".side-island-checksum-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool owned = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    owned = true;
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                File.Move(temporary, target); // Windows-only; native MoveFile refuses existing entries.
                owned = false;
            }
            finally { if (owned) { try { File.Delete(temporary); } catch { } } }
        }

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
