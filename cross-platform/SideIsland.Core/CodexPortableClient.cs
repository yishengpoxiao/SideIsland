// Read-only Codex app-server transport. Compatible with .NET Framework 4.8 / C# 5.
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    public sealed class QuotaWindow
    {
        public double? UsedPercent { get; set; }
        public int? WindowMinutes { get; set; }
        public long? ResetsAt { get; set; }
        public double? Remaining
        {
            get
            {
                if (!UsedPercent.HasValue || Double.IsNaN(UsedPercent.Value) || Double.IsInfinity(UsedPercent.Value)) return null;
                return Math.Max(0, Math.Min(100, 100 - UsedPercent.Value));
            }
        }
    }

    public sealed class QuotaBucket
    {
        public string LimitId { get; set; }
        public string Name { get; set; }
        public QuotaWindow Primary { get; set; }
        public QuotaWindow Secondary { get; set; }
    }

    public sealed class UsageSnapshot
    {
        public List<QuotaBucket> Buckets { get; set; }
        public DateTime FetchedAt { get; set; }
        public string PlanType { get; set; }
        public UsageSnapshot() { Buckets = new List<QuotaBucket>(); FetchedAt = DateTime.Now; }
    }

    public sealed class CodexClientException : Exception
    {
        public CodexClientException(string message) : base(message) { }
    }

    public sealed class CodexClient : IDisposable
    {
        private readonly string explicitPath;
        private readonly string codexHome;
        private readonly object sync = new object();
        // Only process creation/publication uses this guard, never an RPC wait.
        private readonly object processSync = new object();
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private static readonly object EndOfStream = new object();
        private Process process;
        private BlockingCollection<object> messages;
        private int nextId;
        private volatile bool disposed;

        public CodexClient(string explicitPath, string codexHome)
        {
            this.explicitPath = explicitPath ?? "";
            this.codexHome = codexHome ?? "";
        }

        private static object Get(IDictionary<string, object> source, string key)
        {
            object result;
            return source != null && source.TryGetValue(key, out result) ? result : null;
        }

        private static IDictionary<string, object> AsObject(object value) { return value as IDictionary<string, object>; }

        private static double? Number(object value)
        {
            if (!(value is int || value is long || value is double || value is decimal || value is float)) return null;
            try
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return Double.IsNaN(number) || Double.IsInfinity(number) ? (double?)null : number;
            }
            catch (Exception) { return null; }
        }

        private static long? Integer(object value, long minimum, long maximum)
        {
            double? number = Number(value);
            if (!number.HasValue || number.Value < minimum || number.Value >= (double)maximum || Math.Floor(number.Value) != number.Value) return null;
            return (long)number.Value;
        }

        private static QuotaWindow ParseWindow(object value)
        {
            IDictionary<string, object> raw = AsObject(value);
            if (raw == null) return null;
            long? minutes = Integer(Get(raw, "windowDurationMins"), 1, Int32.MaxValue);
            return new QuotaWindow
            {
                UsedPercent = Number(Get(raw, "usedPercent")),
                WindowMinutes = minutes.HasValue ? (int?)minutes.Value : null,
                ResetsAt = Integer(Get(raw, "resetsAt"), 0, Int64.MaxValue)
            };
        }

        public static UsageSnapshot ParseRateLimits(string json)
        {
            IDictionary<string, object> payload;
            try { payload = AsObject(new JavaScriptSerializer().DeserializeObject(json)); }
            catch (Exception) { throw new CodexClientException("Codex 返回的额度数据格式不正确。"); }
            return ParseRateLimitsObject(payload);
        }

        private static UsageSnapshot ParseRateLimitsObject(IDictionary<string, object> payload)
        {
            if (payload == null) throw new CodexClientException("Codex 返回的额度数据格式不正确。");
            IDictionary<string, object> envelope = AsObject(Get(payload, "result"));
            if (envelope != null) payload = envelope;
            UsageSnapshot snapshot = new UsageSnapshot();
            snapshot.PlanType = Get(payload, "planType") as string;
            IDictionary<string, object> perLimit = AsObject(Get(payload, "rateLimitsByLimitId"));
            if (perLimit != null)
            {
                foreach (KeyValuePair<string, object> entry in perLimit)
                {
                    IDictionary<string, object> raw = AsObject(entry.Value);
                    if (raw != null) AddBucket(snapshot, raw, entry.Key);
                }
            }
            if (snapshot.Buckets.Count == 0)
            {
                IDictionary<string, object> legacy = AsObject(Get(payload, "rateLimits"));
                if (legacy != null) AddBucket(snapshot, legacy, "codex");
            }
            snapshot.Buckets.Sort(delegate(QuotaBucket left, QuotaBucket right)
            {
                bool leftMain = String.Equals(left.LimitId, "codex", StringComparison.OrdinalIgnoreCase);
                bool rightMain = String.Equals(right.LimitId, "codex", StringComparison.OrdinalIgnoreCase);
                if (leftMain != rightMain) return leftMain ? -1 : 1;
                return String.Compare(left.LimitId, right.LimitId, StringComparison.OrdinalIgnoreCase);
            });
            return snapshot;
        }

        private static void AddBucket(UsageSnapshot snapshot, IDictionary<string, object> raw, string fallbackId)
        {
            string id = Get(raw, "limitId") as string;
            if (String.IsNullOrWhiteSpace(id)) id = fallbackId;
            string name = Get(raw, "limitName") as string;
            if (String.IsNullOrWhiteSpace(name)) name = String.Equals(id, "codex", StringComparison.OrdinalIgnoreCase) ? "Codex" : id;
            snapshot.Buckets.Add(new QuotaBucket
            {
                LimitId = id, Name = name,
                Primary = ParseWindow(Get(raw, "primary")), Secondary = ParseWindow(Get(raw, "secondary"))
            });
            if (String.IsNullOrWhiteSpace(snapshot.PlanType)) snapshot.PlanType = Get(raw, "planType") as string;
        }

        public static string DiscoverCodexHome(string explicitHome)
        {
            string configured = String.IsNullOrWhiteSpace(explicitHome) ? Environment.GetEnvironmentVariable("CODEX_HOME") : explicitHome;
            if (!String.IsNullOrWhiteSpace(configured)) return ExpandPath(configured);
            string[] candidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "codex-home"),
                @"D:\AppData\codex-home",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            };
            foreach (string candidate in candidates) if (Directory.Exists(candidate)) return candidate;
            return null;
        }

        private static string ExpandPath(string value)
        {
            value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (value == "~" || value.StartsWith("~\\") || value.StartsWith("~/"))
                value = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + value.Substring(1);
            return Path.GetFullPath(value);
        }

        private static string ResolveNative(string path)
        {
            if (!File.Exists(path)) return null;
            if (!OperatingSystem.IsWindows()) return Path.GetFullPath(path);
            if (String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(path);
            // Resolve an npm shim to its native executable, without running a shell.
            string package = Path.Combine(Path.GetDirectoryName(path), "node_modules", "@openai", "codex");
            string optionalPackages = Path.Combine(package, "node_modules", "@openai");
            string[] packageRoots = new string[] { optionalPackages, Path.Combine(Path.GetDirectoryName(path), "node_modules", "@openai") };
            string architecture = Environment.Is64BitOperatingSystem ? "x64" : "ia32";
            string machine = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
            if (machine.IndexOf("ARM64", StringComparison.OrdinalIgnoreCase) >= 0) architecture = "arm64";
            foreach (string root in packageRoots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string nativePackage in Directory.GetDirectories(root, "codex-win32-" + architecture + "*"))
                    {
                        string vendor = Path.Combine(nativePackage, "vendor");
                        if (!Directory.Exists(vendor)) continue;
                        foreach (string triple in Directory.GetDirectories(vendor))
                        {
                            string executable = Path.Combine(triple, "bin", "codex.exe");
                            if (File.Exists(executable)) return executable;
                        }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        public static string DiscoverCodexCommand(string explicitCommand)
        {
            if (!String.IsNullOrWhiteSpace(explicitCommand))
            {
                string explicitValue = Environment.ExpandEnvironmentVariables(explicitCommand.Trim().Trim('"'));
                string resolved = ResolveNative(explicitValue);
                if (resolved != null) return resolved;
                // A short executable name can also be selected in settings.
                if (explicitValue.IndexOfAny(new char[] { '\\', '/' }) < 0)
                {
                    string found = FindOnPath(explicitValue);
                    if (found != null) return found;
                }
                throw new CodexClientException("找不到指定的 Codex 程序，请选择 codex.exe 或已安装的 npm 启动脚本。");
            }
            foreach (string name in new string[] { "codex.exe", "codex.cmd", "codex.ps1", "codex" })
            {
                string found = FindOnPath(name);
                if (found != null) return found;
            }
            if (!OperatingSystem.IsWindows()) foreach (string candidate in new[] { "/opt/homebrew/bin/codex", "/usr/local/bin/codex", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "codex"), "/Applications/Codex.app/Contents/Resources/codex" }) { string found = ResolveNative(candidate); if (found != null) return found; }
            string desktopBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            try
            {
                if (Directory.Exists(desktopBin))
                {
                    List<string> binaries = new List<string>();
                    foreach (string version in Directory.GetDirectories(desktopBin))
                    {
                        string executable = Path.Combine(version, "codex.exe");
                        if (File.Exists(executable)) binaries.Add(executable);
                    }
                    binaries.Sort(delegate(string left, string right) { return File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)); });
                    if (binaries.Count > 0) return binaries[0];
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw new CodexClientException("未找到 Codex。请先安装 Codex 桌面版或 CLI，或在设置中指定程序路径。");
        }

        private static string FindOnPath(string name)
        {
            string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string directory in pathVariable.Split(Path.PathSeparator))
            {
                if (String.IsNullOrWhiteSpace(directory)) continue;
                try
                {
                    string found = ResolveNative(Path.Combine(directory.Trim().Trim('"'), name));
                    if (found != null) return found;
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        private void Connect(Stopwatch clock, int timeoutMs)
        {
            string executable = DiscoverCodexCommand(explicitPath);
            ProcessStartInfo start = new ProcessStartInfo(executable, "app-server --listen stdio://");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = new UTF8Encoding(false);
            start.StandardErrorEncoding = new UTF8Encoding(false);
            string home = DiscoverCodexHome(codexHome);
            if (!String.IsNullOrEmpty(home)) start.EnvironmentVariables["CODEX_HOME"] = home;
            Process pending = new Process();
            pending.StartInfo = start;
            // Drain stderr so the process cannot block; never retain/log it.
            pending.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) { };
            try
            {
                lock (processSync)
                {
                    if (disposed) throw new ObjectDisposedException("CodexClient");
                    pending.Start();
                    process = pending;
                    pending.BeginErrorReadLine();
                }
            }
            catch (Exception)
            {
                CloseProcess();
                KillProcess(pending);
                throw new CodexClientException("无法启动 Codex，请在设置中检查程序路径。");
            }
            messages = new BlockingCollection<object>();
            BlockingCollection<object> queue = messages;
            StreamReader output = pending.StandardOutput;
            Thread reader = new Thread(delegate()
            {
                JavaScriptSerializer parser = new JavaScriptSerializer();
                try
                {
                    string line;
                    while ((line = output.ReadLine()) != null)
                    {
                        try
                        {
                            IDictionary<string, object> message = AsObject(parser.DeserializeObject(line));
                            if (message != null) queue.Add(message);
                        }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { }
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                finally { queue.Add(EndOfStream); }
            });
            reader.IsBackground = true;
            reader.Name = "Codex quota reader";
            reader.Start();
            Request("initialize", new Dictionary<string, object>
            {
                { "clientInfo", new Dictionary<string, object> { { "name", "codex_side_island" }, { "title", "Codex Island" }, { "version", "0.1.0" } } },
                { "capabilities", new Dictionary<string, object> { { "experimentalApi", true } } }
            }, clock, timeoutMs);
            Send(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "method", "initialized" } });
        }

        private void Send(IDictionary<string, object> message)
        {
            try
            {
                process.StandardInput.WriteLine(serializer.Serialize(message));
                process.StandardInput.Flush();
            }
            catch (Exception) { throw new CodexClientException("Codex 额度服务连接已断开，将在下次刷新时重试。"); }
        }

        private IDictionary<string, object> Request(string method, object parameters, Stopwatch clock, int timeoutMs)
        {
            int id = ++nextId;
            Send(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "method", method }, { "params", parameters } });
            while (true)
            {
                long remaining = timeoutMs - clock.ElapsedMilliseconds;
                object item;
                if (remaining <= 0 || !messages.TryTake(out item, (int)Math.Min(Int32.MaxValue, remaining)))
                    throw new CodexClientException("读取 Codex 额度超时，请检查网络或稍后重试。");
                if (Object.ReferenceEquals(item, EndOfStream))
                    throw new CodexClientException("Codex 额度服务意外退出，请检查程序路径和配置。");
                IDictionary<string, object> message = AsObject(item);
                double? responseId = Number(Get(message, "id"));
                if (!responseId.HasValue || responseId.Value != id) continue;
                if (message.ContainsKey("error")) ThrowSafeServerError(Get(message, "error"));
                IDictionary<string, object> result = AsObject(Get(message, "result"));
                if (result == null) throw new CodexClientException("Codex 返回的额度数据格式不正确。");
                return result;
            }
        }

        private static void ThrowSafeServerError(object raw)
        {
            string description = Get(AsObject(raw), "message") as string ?? "";
            string text = description.ToLowerInvariant();
            // Classification only: raw server text may contain sensitive details.
            foreach (string token in new string[] { "unauthorized", "authentication", "not authenticated", "not logged", "login", "sign in", "401" })
                if (text.Contains(token)) throw new CodexClientException("Codex 尚未登录或登录已过期，请在 Codex 中登录后刷新。");
            foreach (string token in new string[] { "not supported", "unsupported", "api key", "api-key" })
                if (text.Contains(token)) throw new CodexClientException("当前账户类型不支持读取 Codex 订阅额度，请使用 ChatGPT 账户登录。");
            throw new CodexClientException("Codex 暂时无法返回额度，请确认已登录并稍后重试。");
        }

        public UsageSnapshot ReadLimits(int timeoutMs = 25000)
        {
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException("timeoutMs");
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("CodexClient");
                Stopwatch clock = Stopwatch.StartNew();
                try
                {
                    if (process == null || process.HasExited)
                    {
                        CloseProcess();
                        Connect(clock, timeoutMs);
                    }
                    return ParseRateLimitsObject(Request("account/rateLimits/read", null, clock, timeoutMs));
                }
                catch (CodexClientException) { CloseProcess(); throw; }
                catch (Exception)
                {
                    CloseProcess();
                    throw new CodexClientException("读取 Codex 额度失败，请检查程序路径和网络后重试。");
                }
            }
        }

        private void CloseProcess()
        {
            Process previous;
            lock (processSync)
            {
                previous = process;
                process = null;
            }
            KillProcess(previous);
        }

        private static void KillProcess(Process previous)
        {
            if (previous == null) return;
            try
            {
                if (!previous.HasExited) previous.Kill();
                previous.WaitForExit(1000);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { previous.Dispose(); }
        }

        /// <summary>
        /// Stop immediately even while another thread is waiting for a quota
        /// response. No request lock is taken, so closing the app cannot wait
        /// for the network timeout or leave its app-server child running.
        /// </summary>
        public void Abort()
        {
            disposed = true;
            CloseProcess();
        }

        public void Dispose() { Abort(); }
    }
}
