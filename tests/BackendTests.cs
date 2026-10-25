using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexIsland
{
    public static class BackendTests
    {
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Backend test failed: " + message);
        }

        public static void Run()
        {
            ParseTests();
            string folder = Path.Combine(Path.GetTempPath(), "codex-island-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string executable = BuildFakeServer(folder);
                TransportTests(folder, executable);
                DiscoveryTests(folder, executable);
            }
            finally
            {
                // This is the exact fresh, test-owned temporary directory.
                // Windows can briefly retain the executable's image mapping
                // after the test-owned child reports exit. Retry only deletion
                // of this fresh fixture directory, with a bounded deadline.
                var cleanup = Stopwatch.StartNew();
                while (Directory.Exists(folder))
                {
                    try { Directory.Delete(folder, true); }
                    catch (IOException) { if (cleanup.ElapsedMilliseconds >= 2000) throw; Thread.Sleep(40); }
                    catch (UnauthorizedAccessException) { if (cleanup.ElapsedMilliseconds >= 2000) throw; Thread.Sleep(40); }
                }
            }
        }

        private static void ParseTests()
        {
            UsageSnapshot multiple = CodexClient.ParseRateLimits(@"{""rateLimits"":{""primary"":{""usedPercent"":99}},""rateLimitsByLimitId"":{""fast"":{""limitName"":""Fast models"",""primary"":{""usedPercent"":12.5}},""codex"":{""planType"":""plus"",""primary"":{""usedPercent"":23,""windowDurationMins"":300,""resetsAt"":1900000000},""secondary"":{""usedPercent"":60,""windowDurationMins"":10080}}}}");
            Check(multiple.Buckets.Count == 2, "per-limit buckets override legacy data");
            Check(multiple.Buckets[0].LimitId == "codex", "main bucket sorts first");
            Check(multiple.Buckets[0].Primary.Remaining == 77 && multiple.Buckets[0].Secondary.Remaining == 40, "used percentages are inverted");
            Check(multiple.Buckets[1].Primary.Remaining == 87.5, "fractional usage is retained");
            Check(multiple.Buckets[0].Primary.ResetsAt == 1900000000 && multiple.PlanType == "plus", "reset and plan are parsed");
            UsageSnapshot unknown = CodexClient.ParseRateLimits(@"{""rateLimits"":{""primary"":{""usedPercent"":null},""secondary"":null}}");
            Check(unknown.Buckets[0].Primary.Remaining == null && unknown.Buckets[0].Secondary == null, "missing values remain unknown");
            UsageSnapshot invalid = CodexClient.ParseRateLimits(@"{""rateLimits"":{""primary"":{""usedPercent"":true,""windowDurationMins"":-1,""resetsAt"":""later""}}}");
            Check(invalid.Buckets[0].Primary.Remaining == null && invalid.Buckets[0].Primary.WindowMinutes == null && invalid.Buckets[0].Primary.ResetsAt == null, "invalid data is not displayed as real usage");
            Check(new QuotaWindow { UsedPercent = 125 }.Remaining == 0 && new QuotaWindow { UsedPercent = -25 }.Remaining == 100, "remaining is clamped");
            Check(new QuotaWindow { UsedPercent = Double.NaN }.Remaining == null, "nonfinite usage is unknown");
            UsageSnapshot fallback = CodexClient.ParseRateLimits(@"{""rateLimitsByLimitId"":{},""rateLimits"":{""primary"":{""usedPercent"":0}}}");
            Check(fallback.Buckets[0].Primary.Remaining == 100, "empty buckets fall back to legacy response");
            UsageSnapshot envelope = CodexClient.ParseRateLimits(@"{""id"":2,""result"":{""rateLimits"":{""primary"":{""usedPercent"":10}}}}");
            Check(envelope.Buckets[0].Primary.Remaining == 90, "full JSON-RPC envelope is accepted");
            Check(CodexClient.ParseRateLimits("{}").Buckets.Count == 0, "no data is not fabricated");
            bool failed = false;
            try { CodexClient.ParseRateLimits("[]"); } catch (CodexClientException) { failed = true; }
            Check(failed, "wrong response shape is rejected");
        }

        private static void DiscoveryTests(string folder, string executable)
        {
            Check(CodexClient.DiscoverCodexCommand("\"" + executable + "\"") == executable, "quoted native path with spaces");
            Check(CodexClient.DiscoverCodexHome(folder) == folder, "explicit home wins");
            string wrapper = Path.Combine(folder, "codex.ps1");
            File.WriteAllText(wrapper, "# fixture only");
            string architecture = Environment.Is64BitOperatingSystem ? "x64" : "ia32";
            string machine = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
            if (machine.IndexOf("ARM64", StringComparison.OrdinalIgnoreCase) >= 0) architecture = "arm64";
            string native = Path.Combine(folder, "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-" + architecture, "vendor", "fixture-triple", "bin", "codex.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(native));
            File.WriteAllText(native, "fixture");
            Check(CodexClient.DiscoverCodexCommand(wrapper) == native, "npm shim resolves native executable without a shell");
        }

        private static void SetMode(string folder, string mode)
        {
            File.WriteAllText(Path.Combine(folder, "mode.txt"), mode);
        }

        private static void TransportTests(string folder, string executable)
        {
            string log = Path.Combine(folder, "requests.txt");
            SetMode(folder, "ok");
            using (CodexClient client = new CodexClient(executable, folder))
            {
                Check(client.ReadLimits(3000).Buckets[0].Primary.Remaining == 72, "first response and notification handling");
                Check(client.ReadLimits(3000).Buckets[0].Primary.Remaining == 72, "persistent second response");
                string[] requests = File.ReadAllLines(log);
                Check(String.Join(",", requests) == "initialize,initialized,account/rateLimits/read,account/rateLimits/read", "only expected read-only methods are called once per connection");
            }
            SetMode(folder, "timeout");
            using (CodexClient client = new CodexClient(executable, folder))
            {
                Stopwatch elapsed = Stopwatch.StartNew();
                bool failed = false;
                try { client.ReadLimits(400); } catch (CodexClientException) { failed = true; }
                Check(failed && elapsed.ElapsedMilliseconds < 3000, "timeout is bounded");
                SetMode(folder, "ok");
                Check(client.ReadLimits(3000).Buckets[0].Primary.Remaining == 72, "timeout reconnects on the next call");
            }
            SetMode(folder, "error");
            using (CodexClient client = new CodexClient(executable, folder))
            {
                bool failed = false;
                try { client.ReadLimits(3000); }
                catch (CodexClientException error)
                {
                    failed = true;
                    Check(!error.ToString().Contains("secret-fixture"), "RPC errors never expose raw sensitive response details");
                }
                Check(failed, "server error is reported");
                SetMode(folder, "ok");
                Check(client.ReadLimits(3000).Buckets[0].Primary.Remaining == 72, "server error reconnects on the next call");
            }
            SetMode(folder, "exit");
            using (CodexClient client = new CodexClient(executable, folder))
            {
                bool failed = false;
                try { client.ReadLimits(3000); } catch (CodexClientException) { failed = true; }
                Check(failed, "process exit is reported");
            }
            File.WriteAllText(log, "");
            SetMode(folder, "timeout");
            using (CodexClient client = new CodexClient(executable, folder))
            {
                Task request = Task.Factory.StartNew(delegate()
                {
                    try { client.ReadLimits(25000); }
                    catch (CodexClientException) { }
                    catch (ObjectDisposedException) { }
                });
                Stopwatch startup = Stopwatch.StartNew();
                bool reading = false;
                while (startup.ElapsedMilliseconds < 3000)
                {
                    try
                    {
                        // This trace is being appended by the fake peer. A
                        // normal ReadAllText share mode can deny its write and
                        // crash the fixture instead of exercising cancellation.
                        using (var trace = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var reader = new StreamReader(trace)) reading = reader.ReadToEnd().Contains("account/rateLimits/read");
                    }
                    catch (IOException) { }
                    if (reading) break;
                    Thread.Sleep(10);
                }
                Check(reading, "shutdown fixture entered an in-flight request");
                int childPid = Int32.Parse(File.ReadAllText(Path.Combine(folder, "pid.txt")));
                Stopwatch elapsed = Stopwatch.StartNew();
                client.Abort();
                Check(elapsed.ElapsedMilliseconds < 2000, "Abort does not wait for the request timeout");
                Check(request.Wait(3000), "Abort releases the blocked reader");
                bool alive = false;
                try { using (Process child = Process.GetProcessById(childPid)) alive = !child.HasExited; } catch (ArgumentException) { }
                Check(!alive, "Abort leaves no app-server child process");
            }
        }

        private static string BuildFakeServer(string folder)
        {
            string source = Path.Combine(folder, "Fake Codex.cs");
            string executable = Path.Combine(folder, "Fake Codex.exe");
            File.WriteAllText(source, FakeServerSource, Encoding.UTF8);
            string compiler = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "csc.exe");
            ProcessStartInfo start = new ProcessStartInfo(compiler, "/nologo /target:exe /reference:System.Web.Extensions.dll /out:\"" + executable + "\" \"" + source + "\"");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using (Process compilation = Process.Start(start))
            {
                string output = compilation.StandardOutput.ReadToEnd();
                string error = compilation.StandardError.ReadToEnd();
                Check(compilation.WaitForExit(10000) && compilation.ExitCode == 0, "fixture compiler: " + output + error);
            }
            return executable;
        }

        private const string FakeServerSource = @"
using System;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Web.Script.Serialization;
class FakeCodex {
  static void Main() {
    var parser = new JavaScriptSerializer();
    string folder = Environment.GetEnvironmentVariable(""CODEX_HOME"");
    string mode = File.ReadAllText(Path.Combine(folder, ""mode.txt""));
    string log = Path.Combine(folder, ""requests.txt"");
    File.WriteAllText(Path.Combine(folder, ""pid.txt""), Process.GetCurrentProcess().Id.ToString());
    string line;
    while ((line = Console.ReadLine()) != null) {
      var request = (Dictionary<string,object>)parser.DeserializeObject(line);
      string method = (string)request[""method""];
      File.AppendAllText(log, method + ""\n"");
      if (!request.ContainsKey(""id"")) continue;
      string id = request[""id""].ToString();
      if (method == ""initialize"") Console.WriteLine(""{\""id\"":""+id+"",\""result\"":{}}"");
      else if (mode == ""timeout"") Thread.Sleep(20000);
      else if (mode == ""exit"") return;
      else if (mode == ""error"") Console.WriteLine(""{\""id\"":""+id+"",\""error\"":{\""message\"":\""unauthorized secret-fixture-must-not-leak\""}}"");
      else {
        Console.WriteLine(""{\""method\"":\""account/rateLimits/updated\"",\""params\"":{}}"");
        Console.WriteLine(""{\""id\"":""+id+"",\""result\"":{\""rateLimits\"":{\""primary\"":{\""usedPercent\"":28}}}}"");
      }
    }
  }
}";
    }
}
