using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class FileHashExportTests
    {
        private static int checks;
        public static void Run()
        {
            checks = 0;
            string directory = Path.Combine(Path.GetTempPath(), "island-hash-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { FormatTests(); SaveTests(directory); GnuTests(directory); PanelTests(directory); DeferredSaveTests(directory); UnrepresentableNameTests(directory); }
            finally { Directory.Delete(directory, true); }
            Console.WriteLine("PASS: " + checks + " checksum export checks; GNU format, strict UTF8, new-file-only publication, real temporary saves, responsive worker saves, duplicate/reentrant export exclusion, stale/disposed completion suppression and digest preservation when filename export is unavailable.");
        }

        private static void FormatTests()
        {
            string digest = new string('a', 64), expected, error;
            Check(FileHashTools.CreateManifest(digest.ToUpperInvariant(), "SHA256", "C:\\private\\release 资料.zip") == digest + " *release 资料.zip\n", "export strips source directory and normalizes only digest");
            Check(FileHashTools.CreateManifest(digest, "SHA256", "C:release.zip") == digest + " *release.zip\n", "Windows drive-relative prefix is not exported as part of basename");
            Check(FileHashTools.CreateManifest(digest, "SHA256", "/private/app.zip ") == digest + " *app.zip \n", "export preserves filename trailing space");
            string odd = "line\nreturn\rend.bin";
            string escaped = "\\" + digest + " *line\\nreturn\\rend.bin\n";
            Check(FileHashTools.CreateManifest(digest, "SHA256", odd) == escaped, "GNU CR/LF escapes and leading marker");
            Check(FileHashTools.TryResolveExpected(escaped, "SHA256", odd, out expected, out error) && expected == digest, "escaped names round-trip exactly as text on Windows");
            Check(FileHashTools.TryResolveExpected("\\" + digest + " *folder\\\\app.zip\n", "SHA256", "app.zip", out expected, out error), "escaped backslash decodes before platform path handling");
            Check(FileHashTools.TryResolveExpected("\\SHA256 (line\\nreturn\\rend.bin) = " + digest, "SHA256", odd, out expected, out error), "GNU escaped tagged names also accepted");
            Check(FileHashTools.TryResolveExpected("\\" + digest + " *plain.bin\n", "SHA256", "plain.bin", out expected, out error), "escape marker without special characters remains legal");
            foreach (string suffix in new[] { "bad\\q", "bad\\t", "bad\\0", "bad\\", "bad\0name" })
                Check(!FileHashTools.TryResolveExpected("\\" + digest + " *" + suffix, "SHA256", "plain.bin", out expected, out error) && error == "manifest", "malformed escape or NUL rejected: " + suffix.Replace("\0", "NUL"));
            Check(!FileHashTools.TryResolveExpected(escaped, "SHA256", "line\\nreturn\\rend.bin", out expected, out error), "decoded controls never match literal escape letters");
            Check(!FileHashTools.TryResolveExpected(escaped + "\\" + digest + " *bad\\q\n", "SHA256", odd, out expected, out error) && error == "manifest", "later invalid escape invalidates earlier matched row");
            Check(!FileHashTools.TryResolveExpected(escaped + new string('\n', 1025), "SHA256", odd, out expected, out error) && error == "too-large", "escaped input retains physical line bound");
            Check(!FileHashTools.TryResolveExpected(escaped + new string(' ', FileHashTools.MaximumManifestLength), "SHA256", odd, out expected, out error) && error == "too-large", "escaped input retains raw size bound");
            Reject<ArgumentException>(() => FileHashTools.CreateManifest(digest, "SHA512", "file"), "reject mismatched algorithm");
            Reject<ArgumentException>(() => FileHashTools.CreateManifest(digest, "SHA256", "/folder/"), "reject missing basename");
            Reject<ArgumentException>(() => FileHashTools.CreateManifest(digest, "SHA256", "a\0b"), "reject invalid NUL source name");
            Reject<EncoderFallbackException>(() => FileHashTools.CreateManifest(digest, "SHA256", "bad\ud800"), "never replace malformed filename Unicode");
            Reject<ArgumentException>(() => FileHashTools.CreateManifest(digest, "SHA256", new string('\n', FileHashTools.MaximumManifestLength)), "expanded filename cannot exceed manifest limit");
        }

        private static void SaveTests(string directory)
        {
            string digest = new string('a', 64), manifest = FileHashTools.CreateManifest(digest, "SHA256", "发布 包.zip");
            string target = Path.Combine(directory, "result.sha256");
            FileHashTools.SaveManifestNew(target, manifest);
            Check(File.ReadAllBytes(target).SequenceEqual(new UTF8Encoding(false, true).GetBytes(manifest)), "real saved bytes are strict UTF8 without BOM and with LF");
            byte[] original = File.ReadAllBytes(target);
            Reject<IOException>(() => FileHashTools.SaveManifestNew(target, "different"), "existing manifest never overwritten");
            Check(File.ReadAllBytes(target).SequenceEqual(original), "refused overwrite preserves exact existing bytes");
            Reject<IOException>(() => FileHashTools.SaveManifestNew(directory, manifest), "existing directory cannot be replaced");
            string invalid = Path.Combine(directory, "invalid.sha256");
            Reject<EncoderFallbackException>(() => FileHashTools.SaveManifestNew(invalid, "bad\ud800"), "strict UTF8 refuses invalid text before IO");
            Check(!File.Exists(invalid), "invalid Unicode does not create output");
            Reject<DirectoryNotFoundException>(() => FileHashTools.SaveManifestNew(Path.Combine(directory, "missing", "result.sha256"), manifest), "missing directory fails without creating folders");
            string contested = Path.Combine(directory, "contested.sha256"); int success = 0, refused = 0;
            Task.WaitAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => {
                try { FileHashTools.SaveManifestNew(contested, manifest); Interlocked.Increment(ref success); }
                catch (IOException) { Interlocked.Increment(ref refused); }
            })).ToArray());
            Check(success == 1 && refused == 7 && File.ReadAllText(contested, new UTF8Encoding(false, true)) == manifest, "concurrent publication has exactly one complete winner");
            Check(Directory.GetFiles(directory, ".side-island-checksum-*.tmp").Length == 0, "all owned staging files cleaned after success/failure/race");
        }

        private static void GnuTests(string directory)
        {
            string tools = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin");
            if (!File.Exists(Path.Combine(tools, "sha256sum.exe"))) { Console.WriteLine("SKIP: GNU interoperability executable is not installed."); return; }
            string file = Path.Combine(directory, "gnu-fixture.bin"); File.WriteAllBytes(file, Encoding.UTF8.GetBytes("GNU interoperability\n第二行\r\n"));
            foreach (string algorithm in new[] { "SHA256", "SHA512" })
            {
                string tool = Path.Combine(tools, algorithm.ToLowerInvariant() + "sum.exe");
                string digest = FileHashTools.ComputeFile(file, algorithm, CancellationToken.None);
                string manifest = FileHashTools.CreateManifest(digest, algorithm, file);
                string output; int code = Run(tool, "--binary -- gnu-fixture.bin", directory, out output);
                Check(code == 0 && output.Replace("\r\n", "\n") == manifest, algorithm + " exported bytes match actual GNU binary output");
                string saved = "interop." + algorithm.ToLowerInvariant(); FileHashTools.SaveManifestNew(Path.Combine(directory, saved), manifest);
                code = Run(tool, "--check --strict -- " + saved, directory, out output);
                Check(code == 0 && output.Contains("OK"), algorithm + " real GNU verifies saved manifest");
                string marked = "escaped." + algorithm.ToLowerInvariant(); FileHashTools.SaveManifestNew(Path.Combine(directory, marked), "\\" + manifest);
                code = Run(tool, "--check --strict -- " + marked, directory, out output);
                Check(code == 0, algorithm + " real GNU accepts escaped-record marker without special name");
                code = Run(tool, "--binary -- .\\gnu-fixture.bin", directory, out output);
                string expected, error;
                Check(code == 0 && output.StartsWith("\\", StringComparison.Ordinal) && FileHashTools.TryResolveExpected(output, algorithm, file, out expected, out error) && expected == digest, algorithm + " actual GNU backslash-escaped filename parses to selected file");
                string escapedPath = "escaped-path." + algorithm.ToLowerInvariant(); FileHashTools.SaveManifestNew(Path.Combine(directory, escapedPath), output);
                code = Run(tool, "--check --strict -- " + escapedPath, directory, out output);
                Check(code == 0, algorithm + " actual GNU verifies backslash-escaped path");
            }
        }

        private static int Run(string executable, string arguments, string directory, out string output)
        {
            using (var process = Process.Start(new ProcessStartInfo(executable, arguments) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 }))
            {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10000)) { try { process.Kill(); } catch { } throw new Exception("GNU checksum process timed out."); }
                output = stdout.GetAwaiter().GetResult(); string errors = stderr.GetAwaiter().GetResult(); Console.WriteLine("GNU " + Path.GetFileName(executable) + " " + arguments + ": exit=" + process.ExitCode + " " + output.Trim() + errors.Trim()); return process.ExitCode;
            }
        }

        private static void PanelTests(string directory)
        {
            var oldContext = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            string source = Path.Combine(directory, "panel-source.bin"); byte[] bytes = Encoding.UTF8.GetBytes("panel export snapshot\n"); File.WriteAllBytes(source, bytes);
            string destination = Path.Combine(directory, "panel.sha256"), clipboard = null; int dialogs = 0, writes = 0; Action onDialog = null;
            using (var panel = new FileHashPanel(false, (name, extension) => { dialogs++; Check(name == "panel-source.bin" + extension, "save suggestion includes basename and current algorithm extension"); if (onDialog != null) onDialog(); return destination; }, value => { writes++; clipboard = value; }))
            {
                try
                {
                    Check(!Field<Button>(panel, "copyManifest").IsEnabled && !Field<Button>(panel, "saveManifest").IsEnabled, "no export before computation");
                    panel.AcceptExternalFile(source); Complete(panel.Calculate());
                    string expected = FileHashTools.CreateManifest(Field<string>(panel, "result"), "SHA256", source);
                    Check(Field<Button>(panel, "copyManifest").IsEnabled && Field<Button>(panel, "saveManifest").IsEnabled, "completed result enables both export actions");
                    Invoke(panel, "CopyManifest"); Check(writes == 1 && clipboard == expected, "copy uses complete captured manifest without accessing real clipboard");
                    Invoke(panel, "SaveManifest"); Check(dialogs == 1 && File.ReadAllText(destination) == expected, "actual panel save writes captured manifest");
                    Invoke(panel, "SaveManifest"); Check(File.ReadAllText(destination) == expected && Field<TextBlock>(panel, "status").Text.Contains("可能已存在"), "panel refuses existing destination with clear feedback");
                    destination = source; Invoke(panel, "SaveManifest"); Check(File.ReadAllBytes(source).SequenceEqual(bytes) && Field<TextBlock>(panel, "status").Text.Contains("不能覆盖源文件"), "panel refuses original source path");
                    onDialog = () => File.Delete(source); Invoke(panel, "SaveManifest"); Check(!File.Exists(source) && Field<TextBlock>(panel, "status").Text.Contains("不能覆盖源文件"), "source path stays protected if original disappears during modal dialog"); File.WriteAllBytes(source, bytes);
                    destination = Path.Combine(directory, "changed.sha256"); onDialog = () => Field<ComboBox>(panel, "algorithm").SelectedIndex = 1;
                    Invoke(panel, "SaveManifest"); Check(!File.Exists(destination) && Field<TextBlock>(panel, "status").Text.Contains("结果已更新"), "algorithm change during modal dialog rejects stale save");
                    Check(!Field<Button>(panel, "copyManifest").IsEnabled && !Field<Button>(panel, "saveManifest").IsEnabled, "algorithm change invalidates both export actions");
                    onDialog = null; Complete(panel.Calculate()); destination = Path.Combine(directory, "panel.sha512"); Invoke(panel, "SaveManifest");
                    Check(File.ReadAllText(destination) == FileHashTools.CreateManifest(Field<string>(panel, "result"), "SHA512", source), "new SHA512 snapshot exports its own algorithm and digest");
                    destination = Path.Combine(directory, "reselected.sha512"); onDialog = () => panel.AcceptExternalFile(source); Invoke(panel, "SaveManifest");
                    Check(!File.Exists(destination) && !Field<Button>(panel, "saveManifest").IsEnabled, "even same-path reselection invalidates dialog snapshot identity");
                    onDialog = null; Complete(panel.Calculate()); destination = Path.Combine(directory, "recomputed.sha512"); onDialog = () => Complete(panel.Calculate()); Invoke(panel, "SaveManifest");
                    Check(!File.Exists(destination), "new computation during modal dialog rejects old snapshot even with equal digest");
                    onDialog = null; destination = null; Invoke(panel, "SaveManifest"); Check(Field<Button>(panel, "saveManifest").IsEnabled, "dialog cancellation retains computed result");
                    destination = Path.Combine(directory, "closed.sha512"); onDialog = panel.Dispose; Invoke(panel, "SaveManifest"); Check(!File.Exists(destination) && !Field<Button>(panel, "saveManifest").IsEnabled && !Field<Button>(panel, "copyManifest").IsEnabled, "dispose during native dialog prevents late save and disables actions");
                    int oldWrites = writes; Invoke(panel, "CopyManifest"); Invoke(panel, "Copy"); Check(writes == oldWrites, "disposed panel cannot copy either representation");
                }
                finally { SynchronizationContext.SetSynchronizationContext(oldContext); }
            }
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                int oldDialogs = dialogs, oldWrites = writes;
                using (var demo = new FileHashPanel(true, (name, extension) => { dialogs++; return Path.Combine(directory, "demo.sha256"); }, value => writes++))
                {
                    Complete(demo.Calculate()); Invoke(demo, "CopyManifest"); Invoke(demo, "SaveManifest");
                    Check(dialogs == oldDialogs && writes == oldWrites && !File.Exists(Path.Combine(directory, "demo.sha256")), "demo invokes no dialog, filesystem writer or real/injected clipboard");
                }
            }
            finally { SynchronizationContext.SetSynchronizationContext(oldContext); }
        }

        private static void DeferredSaveTests(string directory)
        {
            var oldContext = SynchronizationContext.Current; var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                string source = Path.Combine(directory, "deferred-source.bin"), nextSource = Path.Combine(directory, "deferred-next.bin");
                File.WriteAllText(source, "owned deferred save"); File.WriteAllText(nextSource, "new selection");
                foreach (string scenario in new[] { "success", "changed", "recomputed", "disposed", "error", "changed-error", "disposed-error" })
                using (var entered = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                {
                    string destination = Path.Combine(directory, "deferred-" + scenario + ".sha256");
                    int dialogs = 0, saves = 0, copies = 0; bool workerOnUi = true, nestedChecked = false; FileHashPanel panel = null; Task saving = null;
                    panel = new FileHashPanel(false, (name, extension) => {
                        dialogs++;
                        if (!nestedChecked) { nestedChecked = true; Check(InvokeTask(panel, "SaveManifest").IsCompleted, "reentrant modal export returns without opening another dialog"); }
                        return destination;
                    }, value => copies++, (path, manifest) => {
                        workerOnUi = dispatcher.CheckAccess(); Interlocked.Increment(ref saves); entered.Set();
                        if (!release.Wait(5000)) throw new TimeoutException("Bounded deferred saver release.");
                        if (scenario.EndsWith("error", StringComparison.Ordinal)) throw new IOException("Owned delayed save failure.");
                        FileHashTools.SaveManifestNew(path, manifest);
                    });
                    try
                    {
                        panel.AcceptExternalFile(source); Complete(panel.Calculate()); string manifest = FileHashTools.CreateManifest(Field<string>(panel, "result"), "SHA256", source);
                        saving = InvokeTask(panel, "SaveManifest"); Check(entered.Wait(5000) && !saving.IsCompleted && !workerOnUi, "pending native write runs on a worker while caller returns a pending task: " + scenario);
                        var heartbeat = new TaskCompletionSource<bool>(); dispatcher.BeginInvoke(new Action(() => heartbeat.SetResult(true))); Complete(heartbeat.Task);
                        Check(!saving.IsCompleted, "dispatcher handles heartbeat before blocked disk writer is released: " + scenario);
                        Check(!Field<Button>(panel, "saveManifest").IsEnabled && !Field<Button>(panel, "copyManifest").IsEnabled && !Field<Button>(panel, "copy").IsEnabled, "all export actions remain disabled during save: " + scenario);
                        Check(InvokeTask(panel, "SaveManifest").IsCompleted && dialogs == 1 && saves == 1, "repeat and modal reentrant attempts cannot launch another save: " + scenario);
                        Invoke(panel, "Copy"); Invoke(panel, "CopyManifest"); Check(copies == 0, "disabled exports also reject direct invocation: " + scenario);
                        if (scenario.StartsWith("changed", StringComparison.Ordinal)) panel.AcceptExternalFile(nextSource);
                        else if (scenario.StartsWith("disposed", StringComparison.Ordinal)) panel.Dispose();
                        else if (scenario == "recomputed") Complete(panel.Calculate());
                        string statusBeforeCompletion = Field<TextBlock>(panel, "status").Text;
                        release.Set(); Complete(saving);
                        bool failed = scenario.EndsWith("error", StringComparison.Ordinal);
                        Check(failed ? !File.Exists(destination) : File.ReadAllText(destination) == manifest, "deferred completion preserves captured manifest and failure creates no export: " + scenario);
                        Check(File.ReadAllText(source) == "owned deferred save" && File.ReadAllText(nextSource) == "new selection", "deferred export preserves both source files: " + scenario);
                        if (scenario == "success") Check(Field<TextBlock>(panel, "status").Text == "校验清单已保存。" && Field<Button>(panel, "saveManifest").IsEnabled, "successful completion restores current export actions");
                        else if (scenario == "error") Check(Field<TextBlock>(panel, "status").Text.Contains("可能已存在") && Field<Button>(panel, "saveManifest").IsEnabled, "current failed save reports error and restores export actions");
                        else Check(Field<TextBlock>(panel, "status").Text == statusBeforeCompletion, "late success/failure cannot overwrite changed, recomputed or disposed state: " + scenario);
                        if (scenario.StartsWith("disposed", StringComparison.Ordinal)) Check(!Field<Button>(panel, "saveManifest").IsEnabled && !Field<Button>(panel, "copy").IsEnabled, "disposed controls stay disabled after worker completion");
                    }
                    finally { release.Set(); if (saving != null) Complete(saving); panel.Dispose(); }
                }
            }
            finally { SynchronizationContext.SetSynchronizationContext(oldContext); }
        }

        private static void UnrepresentableNameTests(string directory)
        {
            string source = Path.Combine(directory, "invalid-\ud800.bin"); byte[] bytes = Encoding.UTF8.GetBytes("readable filename, strict export unavailable");
            try { File.WriteAllBytes(source, bytes); }
            catch (Exception error) { if (!(error is ArgumentException) && !(error is IOException)) throw; Console.WriteLine("SKIP: filesystem rejects unpaired-surrogate filename: " + error.GetType().Name); return; }
            var oldContext = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                string copied = null; int dialogs = 0, saves = 0;
                using (var panel = new FileHashPanel(false, (name, extension) => { dialogs++; return Path.Combine(directory, "invalid-name.sha256"); }, value => copied = value, (path, manifest) => saves++))
                {
                    panel.AcceptExternalFile(source); Complete(panel.Calculate());
                    string digest = FileHashTools.ComputeFile(source, "SHA256", CancellationToken.None); Field<TextBox>(panel, "expected").Text = digest;
                    Check(Field<TextBox>(panel, "output").Text == digest && Field<string>(panel, "result") == digest && Field<TextBlock>(panel, "comparison").Text.Contains("校验值一致"), "actual readable NTFS filename with unpaired surrogate retains digest and comparison");
                    Check(Field<Button>(panel, "copy").IsEnabled && !Field<Button>(panel, "copyManifest").IsEnabled && !Field<Button>(panel, "saveManifest").IsEnabled && Field<TextBlock>(panel, "status").Text.Contains("UTF-8"), "only manifest export is disabled with specific filename feedback");
                    Invoke(panel, "Copy"); Invoke(panel, "CopyManifest"); Invoke(panel, "SaveManifest");
                    Check(copied == digest && dialogs == 0 && saves == 0 && File.ReadAllBytes(source).SequenceEqual(bytes), "invalid export encoding never opens a save dialog or writes, while digest copying still works");
                }
            }
            finally { SynchronizationContext.SetSynchronizationContext(oldContext); }
        }

        private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        private static Task InvokeTask(object target, string name) { return (Task)target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null); }
        private static void Invoke(object target, string name) { var task = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null) as Task; if (task != null) Complete(task); }
        private static void Complete(Task task)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) }; var frame = new DispatcherFrame(); var elapsed = Stopwatch.StartNew();
            timer.Tick += (s, e) => { if (task.IsCompleted || elapsed.ElapsedMilliseconds > 15000) frame.Continue = false; };
            if (!task.IsCompleted) { timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); } }
            Check(task.IsCompleted, "actual async calculation completes"); task.GetAwaiter().GetResult();
        }
        private static void Reject<T>(Action action, string message) where T : Exception { bool rejected = false; try { action(); } catch (T) { rejected = true; } Check(rejected, message); }
        private static void Check(bool value, string message) { checks++; if (!value) throw new Exception("Checksum export: " + message); }
    }
}
