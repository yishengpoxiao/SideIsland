using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodexIsland
{
    internal static class TaskActivityTests
    {
        internal static void Run()
        {
            TaskActivitySourceTests.Run();
            TaskActivityCompositionTests.Run();
            string originalLanguage = UiLocalization.Language;
            try
            {
                UiLocalization.Configure("en");
                var internalTask = new TaskActivity { Id = "watch:fixture", Title = "Keep · Edit", Source = "任务守望", Message = "等待的文件已出现" };
                Check(TaskActivityHub.DisplayTitle(internalTask) == "Keep · Edit" && TaskActivityHub.DisplaySource(internalTask) != "任务守望" && TaskActivityHub.DisplayMessage(internalTask) != "等待的文件已出现", "built-in task labels translate while user titles stay exact");
                var externalTask = new TaskActivity { Id = "api:fixture", Title = "Keep · Edit", Source = "User · Edit", Message = "等待的文件已出现" };
                Check(TaskActivityHub.DisplayTitle(externalTask) == externalTask.Title && TaskActivityHub.DisplaySource(externalTask) == externalTask.Source && TaskActivityHub.DisplayMessage(externalTask) == externalTask.Message, "external task metadata is never translated as UI text");
            }
            finally { UiLocalization.Configure(originalLanguage); }
            var hub = new TaskActivityHub(); DateTime start = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
            hub.Publish(new TaskActivityUpdate { Id = "build", Title = "构建😀", Progress = null }, start);
            Check(hub.Current.Active && hub.Current.Progress == null, "unknown progress remains indeterminate");
            hub.Publish(new TaskActivityUpdate { Id = "build", Title = "构建😀", Progress = 50 }, start.AddSeconds(30));
            Check(hub.Snapshot().Count == 1 && hub.Current.StartedUtc == start && TaskActivityHub.Elapsed(hub.Current, start.AddSeconds(30)) == "0:30", "stable identity and elapsed time");
            hub.Publish(new TaskActivityUpdate { Id = "build", Title = "构建😀", State = "completed", Progress = 50 }, start.AddSeconds(40));
            Check(!hub.Current.Active && hub.Current.Progress == 100 && TaskActivityHub.Elapsed(hub.Current, start.AddDays(1)) == "0:40", "completed elapsed freezes and progress completes");
            Throws(() => hub.Publish(new TaskActivityUpdate { Id = "bad", Title = "x", Progress = Double.NaN }), "NaN progress rejected");
            Throws(() => hub.Publish(new TaskActivityUpdate { Id = "../bad", Title = "x" }), "path-like id rejected");
            for (int i = 0; i < 80; i++) hub.Publish(new TaskActivityUpdate { Id = "active" + i, Title = "Task " + i }, start.AddMinutes(i + 1));
            Check(hub.Snapshot().Count == 80 && hub.Snapshot().All(x => x.Active), "old completed record is evicted before active tasks");
            Throws(() => hub.Publish(new TaskActivityUpdate { Id = "overflow", Title = "x" }), "active tasks never silently evicted");
            hub.ClearFinished(); Check(hub.Snapshot().Count == 80, "clear finished retains active tasks");
            var external = new TaskActivityHub();
            using (var api = new TaskActivityApi(external))
            {
                api.Start(0); string address = api.Url, token = api.Token;
                Check(api.Port > 0 && token.Length == 48 && address.StartsWith("http://127.0.0.1:"), "actual loopback endpoint and random token");
                Check(Post(address, "wrong", "{\"id\":\"one\",\"title\":\"x\"}") == 401 && external.Snapshot().Count == 0, "real unauthorized HTTP does not publish");
                Check(Post(address, token, "{\"id\":\"one\",\"title\":\"Build\",\"progress\":25}") == 200 && external.Current.Id == "api:one" && external.Current.Progress == 25, "real authenticated HTTP publishes numeric progress in external namespace");
                Check(Post(address, token, "{\"id\":\"one\",\"title\":\"Build\",\"progress\":\"NaN\"}") == 400 && external.Current.Progress == 25, "invalid HTTP payload preserves existing task");
                using (var stalled = new TcpClient())
                {
                    stalled.Connect(IPAddress.Loopback, api.Port);
                    byte[] partial = Encoding.ASCII.GetBytes("POST /api/tasks HTTP/1.1\r\nAuthorization: Bearer " + token + "\r\nContent-Length: 100\r\n\r\n{");
                    stalled.GetStream().Write(partial, 0, partial.Length);
                    api.Stop(); Check(!api.Running && api.Token == "" && api.Port == 0, "stop invalidates token and endpoint");
                    stalled.ReceiveTimeout = 1500; bool closed = false;
                    try { closed = stalled.GetStream().ReadByte() == -1; } catch (IOException) { closed = true; } catch (ObjectDisposedException) { closed = true; }
                    Check(closed, "stop closes accepted partial requests, not only the listener");
                }
                api.Start(0); Check(api.Token != token, "restart rotates the access token");
                Check(Post(api.Url, token, "{\"id\":\"two\",\"title\":\"x\"}") == 401, "old token cannot publish after restart");
                Check(external.Snapshot().Count == 1, "stopped incomplete request never publishes later");
            }
            var widget = CompactWidgetState.Create("activities", new CompactWidgetData { Language = "en", ActivityTitle = "Build", ActivityProgress = 25, ActivityRunning = true });
            Check(widget.Key == "activities" && widget.Value == "25%" && widget.Fraction == .25, "task compact component uses actual progress");
            Console.WriteLine("PASS: task identity/progress/state/retention and actual loopback HTTP auth, invalid payload, stop, partial-request cancellation and token rotation; no external network.");
        }
        private static int Post(string url, string token, string json)
        {
            var request = (HttpWebRequest)WebRequest.Create(url); request.Proxy = null; request.Method = "POST"; request.Timeout = request.ReadWriteTimeout = 4000;
            request.Headers["Authorization"] = "Bearer " + token; request.ContentType = "application/json"; byte[] body = Encoding.UTF8.GetBytes(json); request.ContentLength = body.Length;
            using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
            try { using (var response = (HttpWebResponse)request.GetResponse()) return (int)response.StatusCode; }
            catch (WebException error) { using (var response = error.Response as HttpWebResponse) { if (response == null) throw; return (int)response.StatusCode; } }
        }
        private static void Throws(Action action, string label) { bool caught = false; try { action(); } catch (ArgumentException) { caught = true; } catch (InvalidOperationException) { caught = true; } Check(caught, label); }
        private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    }
}
