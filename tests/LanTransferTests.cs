using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CodexIsland
{
    internal static class LanTransferTests
    {
        private static int requestOrdinal;
        internal static void Run()
        {
            requestOrdinal = 0;
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.LanTransfer." + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                string first = Path.Combine(folder, "测试 & notes.txt"), other = Path.Combine(folder, "unselected.txt");
                byte[] original = Encoding.UTF8.GetBytes("hello world · 你好 🌏\n"); File.WriteAllBytes(first, original); File.WriteAllText(other, "not shared");
                DateTime clock = DateTime.UtcNow;
                using (var server = new LanTransferService(() => clock))
                {
                    Check(!server.Snapshot().Running, "server starts OFF");
                    Throws(() => server.Start(new string[0], IPAddress.Loopback, 1, ""));
                    Throws(() => server.Start(new[] { first }, IPAddress.Any, 1, ""));
                    Throws(() => server.Start(new[] { first }, IPAddress.Loopback, 0, ""));
                    Throws(() => server.Start(new[] { first }, IPAddress.Loopback, 1, "bad\r\ncode"));
                    var activities = new List<LanTransferProgress>(); server.ActivityChanged += value => { lock (activities) activities.Add(value); };
                    var address = new Uri(server.Start(new[] { first }, IPAddress.Loopback, 2, ""));
                    Check(address.IsLoopback && address.AbsolutePath.Length > 48 && server.Snapshot().Running, "real loopback listener and unguessable route");
                    var listing = Request(address, "GET", address.AbsolutePath, null, null);
                    Check(listing.Code == 200 && listing.Text.Contains("&amp;") && !listing.Text.Contains(other) && !listing.Text.Contains(folder), "phone page escapes selected names without exposing disk paths or unselected files");
                    string fileRoute = FileRoute(listing.Text);
                    var download = Request(address, "GET", fileRoute, null, null);
                    Check(download.Code == 200 && download.Body.SequenceEqual(original), "real HTTP GET preserves exact file bytes");
                    Check(download.Headers["content-disposition"].Contains("filename*=UTF-8''") && download.Headers["cache-control"] == "no-store", "UTF-8 attachment and no-cache headers");
                    Check(Request(address, "HEAD", fileRoute, null, null).Body.Length == 0, "HEAD has no body");
                    var part = Request(address, "GET", fileRoute, "Range: bytes=2-6\r\n", null);
                    Check(part.Code == 206 && part.Body.SequenceEqual(original.Skip(2).Take(5)) && part.Headers["content-range"].StartsWith("bytes 2-6/", StringComparison.Ordinal), "actual byte-range resume");
                    Check(Request(address, "GET", fileRoute, "Range: bytes=-3\r\n", null).Body.SequenceEqual(original.Skip(original.Length - 3)), "suffix range");
                    Check(Request(address, "GET", fileRoute, "Range: bytes=999999-\r\n", null).Code == 416, "out-of-bounds range rejected");
                    Check(Request(address, "GET", "/s/wrong-token/", null, null).Code == 404, "wrong token rejected");
                    Check(Request(address, "GET", address.AbsolutePath + "%2e%2e/unselected.txt", null, null).Code == 404, "encoded traversal rejected");
                    Check(Request(address, "GET", address.AbsolutePath + "../unselected.txt", null, null).Code == 404, "raw traversal rejected");
                    Check(Request(address, "GET", address.AbsolutePath + "f/not-selected", null, null).Code == 404, "only explicit immutable file IDs served");
                    Check(Request(address, "POST", fileRoute, null, new byte[0]).Code == 405, "no upload method");
                    Check(Request(address, "GET", address.AbsolutePath, "Transfer-Encoding: chunked\r\n", null).Code == 400, "chunked input is unsupported and rejected");
                    bool oversizedRejected = false;
                    try { oversizedRejected = Request(address, "GET", address.AbsolutePath, "X-Oversize: " + new string('a', 9000) + "\r\n", null).Code == 400; }
                    catch (InvalidOperationException error) { oversizedRejected = ConnectionClosed(error); }
                    Check(oversizedRejected && Request(address, "GET", address.AbsolutePath, null, null).Code == 200, "oversized header is refused without leaking a request slot");
                    File.WriteAllText(first, "changed-length");
                    Check(Request(address, "GET", fileRoute, null, null).Code == 409, "file changed after selection is not silently substituted");
                    clock = clock.AddMinutes(3); Check(!server.Snapshot().Running, "expired snapshot is immediately inactive");
                    bool expired = false; try { expired = Request(address, "GET", address.AbsolutePath, null, null).Code == 410; } catch (InvalidOperationException error) { expired = ConnectionClosed(error); }
                    Check(expired, "expired share refuses network access"); server.Stop();
                    Check(!server.Snapshot().Running, "explicit stop closes the share");
                    Check(activities.Any(item => item.State == "completed" && item.Progress == 100) && activities.All(item => item.Id.StartsWith("lan:", StringComparison.Ordinal) && item.Message.IndexOf(address.AbsolutePath, StringComparison.Ordinal) < 0), "real transfers emit terminal task updates without URL/token");
                }
                File.WriteAllBytes(first, original);
                PasswordTests(first);
                LongNameTask(folder);
                StopActiveDownload(folder);
                long start, end;
                Check(!LanTransferService.TryRange("bytes=0-1,4-5", 10, out start, out end), "multiple ranges are rejected");
                Check(!LanTransferService.TryRange("bytes=0-", 0, out start, out end), "empty file range is rejected");
                Check(!LanTransferService.ConstantTimeEqual(new byte[] { 1, 2 }, new byte[] { 1, 2, 0 }), "authentication checks length as well as bytes");
            }
            finally
            {
                string full = Path.GetFullPath(folder), temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (Directory.Exists(full) && full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) Directory.Delete(full, true);
            }
            Console.WriteLine("PASS: real loopback HTTP downloads, UTF-8 attachments, HEAD/ranges, selected-file isolation, password cookies/rate limits, bounded headers, expiry, stop and real task progress; no firewall edits, external requests or UI.");
        }
        private static void LongNameTask(string folder)
        {
            string name = new string('x', 158) + "😀.txt", path = Path.Combine(folder, name); File.WriteAllText(path, "long-name-bytes");
            var updates = new List<LanTransferProgress>();
            using (var server = new LanTransferService())
            {
                server.ActivityChanged += item => { lock (updates) updates.Add(item); };
                var endpoint = new Uri(server.Start(new[] { path }, IPAddress.Loopback, 1, ""));
                var index = Request(endpoint, "GET", endpoint.AbsolutePath, null, null);
                Check(index.Text.Contains(name), "phone page retains the full filename");
                var download = Request(endpoint, "GET", FileRoute(index.Text), null, null);
                Check(download.Code == 200 && download.Headers["content-disposition"].Contains(Uri.EscapeDataString(name)), "long attachment header retains the full filename");
                lock (updates)
                {
                    Check(updates.Any(item => item.State == "completed"), "long-name download emits terminal task update");
                    foreach (var update in updates) { Check(update.Title.Length <= 160 && update.Title.EndsWith("…", StringComparison.Ordinal), "task title fits hub contract"); new UTF8Encoding(false, true).GetBytes(update.Title); }
                }
                Check(LanTransferService.TaskTitle("a\r\nb") == "ab", "task metadata excludes controls without changing disk names");
            }
        }
        private static void PasswordTests(string path)
        {
            DateTime clock = DateTime.UtcNow;
            using (var server = new LanTransferService(() => clock))
            {
                var address = new Uri(server.Start(new[] { path }, IPAddress.Loopback, 2, "口令+ test")); string root = address.AbsolutePath;
                var login = Request(address, "GET", root, null, null);
                Check(login.Code == 200 && login.Text.Contains("type=\"password\"") && !login.Text.Contains("口令+ test") && !login.Text.Contains("/f/"), "password gate does not disclose code or file links");
                var wrong = Request(address, "POST", root + "unlock", null, Encoding.UTF8.GetBytes("p=wrong")); Check(wrong.Code == 403, "wrong code cannot authenticate");
                var correct = Request(address, "POST", root + "unlock", null, Encoding.UTF8.GetBytes("p=" + Uri.EscapeDataString("口令+ test")));
                Check(correct.Code == 303 && correct.Headers["set-cookie"].Contains("HttpOnly") && correct.Headers["set-cookie"].Contains("SameSite=Strict"), "correct code gets scoped HTTP-only cookie");
                string cookie = correct.Headers["set-cookie"].Split(';')[0];
                var listing = Request(address, "GET", root, "Cookie: " + cookie + "\r\n", null); string fileRoute = FileRoute(listing.Text);
                Check(Request(address, "GET", fileRoute, "Cookie: " + cookie + "\r\n", null).Code == 200, "authenticated file download succeeds");
                Check(Request(address, "GET", fileRoute, "Cookie: sideisland=wrong\r\n", null).Text.Contains("type=\"password\""), "forged cookie cannot retrieve bytes");
                for (int attempt = 0; attempt < 5; attempt++) Check(Request(address, "POST", root + "unlock", null, Encoding.ASCII.GetBytes("p=bad")).Code == 403, "failed code is rejected");
                Check(Request(address, "POST", root + "unlock", null, Encoding.ASCII.GetBytes("p=bad")).Code == 429, "brute-force attempts are bounded");
                clock = clock.AddSeconds(31);
                Check(Request(address, "POST", root + "unlock", null, Encoding.UTF8.GetBytes("p=" + Uri.EscapeDataString("口令+ test"))).Code == 303, "temporary lockout expires");
                var replacement = new Uri(server.Start(new[] { path }, IPAddress.Loopback, 1, ""));
                Check(replacement.AbsolutePath != root && Request(replacement, "GET", root, null, null).Code == 404, "restarting rotates secret URL and invalidates old cookie scope");
            }
        }
        private static void StopActiveDownload(string folder)
        {
            string path = Path.Combine(folder, "large-selected.bin"); using (var output = File.Create(path)) output.SetLength(128L * 1024 * 1024);
            using (var server = new LanTransferService())
            {
                int cancelled = 0; server.ActivityChanged += item => { if (item.State == "cancelled") Interlocked.Exchange(ref cancelled, 1); };
                var address = new Uri(server.Start(new[] { path }, IPAddress.Loopback, 1, "")); string route = FileRoute(Request(address, "GET", address.AbsolutePath, null, null).Text);
                using (var client = new TcpClient())
                {
                    client.ReceiveBufferSize = 1024; client.Connect(address.Host, address.Port); client.ReceiveTimeout = 3000;
                    var stream = client.GetStream(); byte[] request = Encoding.ASCII.GetBytes("GET " + route + " HTTP/1.1\r\nHost: localhost\r\n\r\n"); stream.Write(request, 0, request.Length);
                    var watch = Stopwatch.StartNew(); while (server.Snapshot().ActiveDownloads == 0 && watch.ElapsedMilliseconds < 3000) Thread.Sleep(5);
                    Check(server.Snapshot().ActiveDownloads == 1, "large slow-reader transfer really started"); server.Stop();
                    watch.Restart(); while (Interlocked.CompareExchange(ref cancelled, 0, 0) == 0 && watch.ElapsedMilliseconds < 3000) Thread.Sleep(5);
                    Check(!server.Snapshot().Running && Interlocked.CompareExchange(ref cancelled, 0, 0) == 1, "stop interrupts active connection and emits cancellation");
                }
            }
        }
        private sealed class Response
        {
            internal int Code; internal Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); internal byte[] Body;
            internal string Text { get { return Encoding.UTF8.GetString(Body); } }
        }
        private static Response Request(Uri endpoint, string method, string path, string headers, byte[] body)
        {
          int ordinal = ++requestOrdinal;
          try
          {
            using (var client = new TcpClient())
            {
                client.Connect(endpoint.Host, endpoint.Port); client.ReceiveTimeout = client.SendTimeout = 5000;
                using (var stream = client.GetStream())
                {
                    byte[] prefix = Encoding.ASCII.GetBytes(method + " " + path + " HTTP/1.1\r\nHost: " + endpoint.Host + "\r\n" + (headers ?? "") + (body == null ? "" : "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n") + "\r\n"); stream.Write(prefix, 0, prefix.Length); if (body != null && body.Length > 0) stream.Write(body, 0, body.Length);
                    var output = new MemoryStream(); byte[] buffer = new byte[8192]; int count; while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, count);
                    byte[] data = output.ToArray(); int split = -1;
                    for (int index = 0; index + 3 < data.Length; index++) if (data[index] == 13 && data[index + 1] == 10 && data[index + 2] == 13 && data[index + 3] == 10) { split = index; break; }
                    Check(split >= 0, "HTTP response header complete"); string[] lines = Encoding.ASCII.GetString(data, 0, split).Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var result = new Response { Code = Int32.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture), Body = data.Skip(split + 4).ToArray() };
                    foreach (string line in lines.Skip(1)) { int colon = line.IndexOf(':'); if (colon > 0) result.Headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim(); }
                    return result;
                }
            }
          }
          catch (Exception error) { throw new InvalidOperationException("LAN HTTP test request #" + ordinal + " failed.", error); }
        }
        private static string FileRoute(string html) { var match = Regex.Match(html, "href=\"([^\"]+/f/[a-f0-9]+)\""); Check(match.Success, "selected-file link exists"); return match.Groups[1].Value; }
        private static bool ConnectionClosed(Exception error) { for (var current = error; current != null; current = current.InnerException) if (current is IOException || current is SocketException) return true; return false; }
        private static void Throws(Action action) { bool thrown = false; try { action(); } catch (ArgumentException) { thrown = true; } Check(thrown, "invalid input rejected"); }
        private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException("LAN: " + label); }
    }
}
