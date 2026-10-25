using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexIsland
{
    internal sealed class LanTransferFile
    {
        internal string Id = "", Path = "", Name = "";
        internal long Length;
    }
    internal sealed class LanTransferSnapshot
    {
        internal bool Running, PasswordRequired;
        internal string Url = "", CurrentFile = "";
        internal DateTime ExpiresUtc;
        internal long SentBytes, PlannedBytes;
        internal int CompletedDownloads, ActiveDownloads;
    }
    internal sealed class LanTransferAddress
    {
        internal IPAddress Address;
        internal string Name;
        public override string ToString() { return Name; }
    }
    internal sealed class LanTransferProgress
    {
        internal string Id, Title, State, Message;
        internal double? Progress;
    }

    // A deliberately small read-only HTTP server. There is no directory route,
    // arbitrary path parameter, upload, CORS, external service or firewall edit.
    internal sealed class LanTransferService : IDisposable
    {
        internal const int MaximumFiles = 20, MaximumHeaderBytes = 8192, MaximumBodyBytes = 2048;
        internal const long MaximumFileBytes = 20L * 1024 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly object operations = new object();
        private readonly Func<DateTime> utcNow;
        private readonly SemaphoreSlim slots = new SemaphoreSlim(4, 4);
        private readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        private readonly Dictionary<string, Attempt> failures = new Dictionary<string, Attempt>(StringComparer.Ordinal);
        private TcpListener listener;
        private CancellationTokenSource lifetime;
        private Timer expiryTimer;
        private Dictionary<string, LanTransferFile> files = new Dictionary<string, LanTransferFile>(StringComparer.Ordinal);
        private string token = "", cookie = "", url = "", currentFile = "";
        private byte[] salt, passwordHash;
        private DateTime expires;
        private long sentBytes, plannedBytes;
        private int completedDownloads, activeDownloads;
        private bool disposed;
        internal event Action<LanTransferProgress> ActivityChanged;
        private sealed class Attempt { internal int Count; internal DateTime BlockedUntil; }
        private sealed class Request
        {
            internal string Method, Target;
            internal Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal byte[] Body = new byte[0];
        }
        internal LanTransferService(Func<DateTime> clock = null) { utcNow = clock ?? (() => DateTime.UtcNow); }
        internal static LanTransferFile[] PrepareFiles(IEnumerable<string> paths)
        {
            var selected = (paths ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumFiles + 1).ToArray();
            if (selected.Length == 0 || selected.Length > MaximumFiles) throw new ArgumentException("请选择 1—20 个文件。" );
            return selected.Select(path => {
                string full = ValidateLocalFile(path); var info = new FileInfo(full);
                if (info.Length > MaximumFileBytes) throw new ArgumentException("单个分享文件不能超过 20 GB。" );
                return new LanTransferFile { Id = RandomHex(12), Path = full, Name = info.Name, Length = info.Length };
            }).ToArray();
        }
        private static string ValidateLocalFile(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) throw new ArgumentException("请选择本机普通文件，不支持网络路径或符号链接。" );
            string full = System.IO.Path.GetFullPath(path);
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(full));
            if (drive.DriveType == DriveType.Network) throw new ArgumentException("请选择本机普通文件，不支持网络路径或符号链接。" );
            if (!File.Exists(full) || Directory.Exists(full)) throw new ArgumentException("所选文件不存在或不是普通文件。" );
            for (FileSystemInfo item = new FileInfo(full); item != null; item = item is FileInfo ? (FileSystemInfo)((FileInfo)item).Directory : ((DirectoryInfo)item).Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("请选择本机普通文件，不支持网络路径或符号链接。" );
            return full;
        }
        internal static LanTransferAddress[] Addresses()
        {
            var addresses = new List<LanTransferAddress>();
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback || adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    foreach (var item in adapter.GetIPProperties().UnicastAddresses)
                        if (item.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item.Address))
                            addresses.Add(new LanTransferAddress { Address = item.Address, Name = adapter.Name + " · " + item.Address });
                }
            }
            catch (NetworkInformationException) { }
            addresses = addresses.GroupBy(item => item.Address.ToString()).Select(group => group.First()).Take(16).ToList();
            addresses.Add(new LanTransferAddress { Address = IPAddress.Loopback, Name = "127.0.0.1" }); return addresses.ToArray();
        }
        internal string Start(IEnumerable<string> selectedPaths, IPAddress bindAddress, int minutes, string password, int requestedPort = 0)
        {
            if (bindAddress == null || bindAddress.AddressFamily != AddressFamily.InterNetwork || bindAddress.Equals(IPAddress.Any) || bindAddress.Equals(IPAddress.None)) throw new ArgumentException("请选择有效的本机 IPv4 地址。" );
            if (minutes < 1 || minutes > 120) throw new ArgumentException("分享时长为 1—120 分钟。" );
            password = password ?? "";
            if (password.Length > 32 || password.Any(Char.IsControl)) throw new ArgumentException("访问口令最多 32 个字符，不能包含控制字符。" );
            if (requestedPort < 0 || requestedPort > 65535) throw new ArgumentException("端口无效。" );
            var prepared = PrepareFiles(selectedPaths);
            lock (operations)
            {
              Stop();
              lock (gate)
              {
                if (disposed) throw new ObjectDisposedException("LanTransferService");
                var next = new TcpListener(bindAddress, requestedPort); next.Server.ExclusiveAddressUse = true;
                try { next.Start(8); } catch { next.Stop(); throw; }
                listener = next; lifetime = new CancellationTokenSource(); var cancellation = lifetime.Token;
                files = prepared.ToDictionary(item => item.Id, StringComparer.Ordinal); token = RandomHex(24); cookie = RandomHex(24);
                salt = RandomBytes(24); passwordHash = password.Length == 0 ? null : HashPassword(salt, password);
                expires = utcNow().AddMinutes(minutes); sentBytes = plannedBytes = 0; completedDownloads = activeDownloads = 0; currentFile = ""; failures.Clear();
                url = "http://" + bindAddress + ":" + ((IPEndPoint)next.LocalEndpoint).Port + "/s/" + token + "/";
                expiryTimer = new Timer(delegate { StopExpected(next, true); }, null, 1000, 1000);
                Task.Run(() => AcceptLoop(next, cancellation));
                return url;
              }
            }
        }
        internal LanTransferSnapshot Snapshot()
        {
            lock (gate) return new LanTransferSnapshot { Running = listener != null && utcNow() < expires, Url = url, PasswordRequired = passwordHash != null, ExpiresUtc = expires, SentBytes = sentBytes, PlannedBytes = plannedBytes, CompletedDownloads = completedDownloads, ActiveDownloads = activeDownloads, CurrentFile = currentFile };
        }
        private async Task AcceptLoop(TcpListener server, CancellationToken cancellation)
        {
          try
          {
            while (!cancellation.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await server.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { break; } catch (SocketException) { break; }
                if (!slots.Wait(0)) { client.Close(); continue; }
                lock (gate)
                {
                    if (listener != server || cancellation.IsCancellationRequested) { client.Close(); slots.Release(); break; }
                    clients.Add(client);
                }
                var owned = client;
                // Downloads run independently and release their own connection slot.
                var requestWork = Task.Run(() => {
                    try { Handle(owned, server, cancellation); }
                    catch (Exception error) { if (!(error is IOException || error is SocketException || error is ObjectDisposedException || error is OperationCanceledException || error is ArgumentException)) { /* No request, token, path or password is logged. */ } }
                    finally { lock (gate) clients.Remove(owned); owned.Close(); slots.Release(); }
                });
            }
          }
          finally { StopExpected(server, false); }
        }
        private void StopExpected(TcpListener expected, bool onlyIfExpired)
        {
            lock (operations)
            {
                lock (gate) if (listener != expected || (onlyIfExpired && utcNow() < expires)) return;
                Stop();
            }
        }
        private void Handle(TcpClient client, TcpListener server, CancellationToken cancellation)
        {
            client.ReceiveTimeout = 5000; client.SendTimeout = 5000; client.NoDelay = true;
            using (var stream = client.GetStream())
            {
                Request request;
                try { request = ReadRequest(stream); } catch (InvalidDataException) { Reply(stream, 400, "Bad request"); return; }
                string root; byte[] currentSalt, expectedPassword; string expectedCookie; LanTransferFile[] selected;
                lock (gate)
                {
                    if (listener != server || utcNow() >= expires || cancellation.IsCancellationRequested) { Reply(stream, 410, "Share expired"); return; }
                    root = "/s/" + token; currentSalt = salt; expectedPassword = passwordHash; expectedCookie = cookie; selected = files.Values.ToArray();
                }
                string path = request.Target;
                if (path.Length > 256 || path.IndexOfAny(new[] { '%', '\\', '?', '#' }) >= 0 || path.Contains("..") || !path.StartsWith(root, StringComparison.Ordinal)) { Reply(stream, 404, "Not found"); return; }
                if (request.Method != "GET" && request.Method != "HEAD" && request.Method != "POST") { Reply(stream, 405, "Method not allowed"); return; }
                if (path == root) { Redirect(stream, root + "/", null); return; }
                if (!path.StartsWith(root + "/", StringComparison.Ordinal)) { Reply(stream, 404, "Not found"); return; }
                string remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                bool authorized = expectedPassword == null || CookieMatches(request, expectedCookie);
                if (request.Method == "POST" && path == root + "/unlock")
                {
                    if (expectedPassword == null) { Redirect(stream, root + "/", null); return; }
                    if (Blocked(remote)) { Reply(stream, 429, "Too many attempts. Try again later."); return; }
                    string provided;
                    try { provided = FormPassword(request.Body); } catch (ArgumentException) { Reply(stream, 400, "Bad request"); return; }
                    if (!ConstantTimeEqual(HashPassword(currentSalt, provided), expectedPassword)) { FailedPassword(remote); ReplyHtml(stream, 403, Login(root, true), false); return; }
                    lock (gate) failures.Remove(remote);
                    Redirect(stream, root + "/", "sideisland=" + expectedCookie + "; Path=" + root + "/; HttpOnly; SameSite=Strict"); return;
                }
                if (request.Method == "POST") { Reply(stream, 405, "Method not allowed"); return; }
                if (!authorized) { ReplyHtml(stream, 200, Login(root, false), request.Method == "HEAD"); return; }
                if (path == root + "/")
                {
                    string list = String.Join("", selected.Select(item => "<a class=\"file\" href=\"" + root + "/f/" + item.Id + "\"><span>" + WebUtility.HtmlEncode(item.Name) + "</span><small>" + FormatBytes(item.Length) + "</small></a>"));
                    ReplyHtml(stream, 200, Page("文件下载 / File download", "<p>仅列出电脑上明确选中的文件。分享停止或到期后链接失效。<br>Only the selected files are shared. Links expire automatically.</p>" + list), request.Method == "HEAD"); return;
                }
                string prefix = root + "/f/";
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) { Reply(stream, 404, "Not found"); return; }
                string fileId = path.Substring(prefix.Length); LanTransferFile asset = selected.FirstOrDefault(item => item.Id == fileId);
                if (asset == null) { Reply(stream, 404, "Not found"); return; }
                SendFile(stream, request, asset, server, cancellation);
            }
        }
        private void SendFile(NetworkStream stream, Request request, LanTransferFile asset, TcpListener server, CancellationToken cancellation)
        {
            string activityId = "lan:" + Guid.NewGuid().ToString("N");
            FileStream file;
            try
            {
                ValidateLocalFile(asset.Path); file = new FileStream(asset.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                if (!OpenedPathMatches(file, asset.Path)) { file.Dispose(); Reply(stream, 409, "File changed. Restart the share."); return; }
                if (file.Length != asset.Length) { file.Dispose(); Reply(stream, 409, "File changed. Restart the share."); return; }
            }
            catch (IOException) { Reply(stream, 404, "File unavailable"); return; }
            catch (UnauthorizedAccessException) { Reply(stream, 403, "File unavailable"); return; }
            catch (ArgumentException) { Reply(stream, 404, "File unavailable"); return; }
            using (file)
            {
                long start = 0, end = file.Length - 1; string range;
                bool partial = request.Headers.TryGetValue("Range", out range);
                if (partial && !TryRange(range, file.Length, out start, out end)) { Headers(stream, 416, "application/octet-stream", 0, "Content-Range: bytes */" + file.Length + "\r\n"); return; }
                long length = file.Length == 0 ? 0 : end - start + 1;
                string additional = "Accept-Ranges: bytes\r\nContent-Disposition: attachment; filename=\"download\"; filename*=UTF-8''" + Uri.EscapeDataString(asset.Name) + "\r\n";
                if (partial) additional += "Content-Range: bytes " + start + "-" + end + "/" + file.Length + "\r\n";
                Headers(stream, partial ? 206 : 200, "application/octet-stream", length, additional);
                if (request.Method == "HEAD") return;
                lock (gate) { if (listener != server) return; activeDownloads++; plannedBytes += length; currentFile = asset.Name; }
                bool completed = false; long transferred = 0; var progressClock = Stopwatch.StartNew(); long lastProgressTime = 0; int lastPercent = -1;
                Notify(new LanTransferProgress { Id = activityId, Title = asset.Name, State = "running", Message = "正在发送文件。", Progress = 0 });
                try
                {
                    file.Position = start; long remaining = length; byte[] buffer = new byte[65536];
                    while (remaining > 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        lock (gate) if (listener != server || utcNow() >= expires) return;
                        int count = file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining)); if (count == 0) throw new IOException("File changed.");
                        stream.Write(buffer, 0, count); remaining -= count; transferred += count; lock (gate) if (listener == server) sentBytes += count;
                        int percent = length == 0 ? 100 : (int)(transferred * 100.0 / length);
                        if (percent > lastPercent || progressClock.ElapsedMilliseconds - lastProgressTime >= 100) { lastPercent = percent; lastProgressTime = progressClock.ElapsedMilliseconds; Notify(new LanTransferProgress { Id = activityId, Title = asset.Name, State = "running", Message = "正在发送文件。", Progress = percent }); }
                    }
                    completed = true;
                }
                finally
                {
                    bool cancelled;
                    lock (gate) { cancelled = listener != server || cancellation.IsCancellationRequested || utcNow() >= expires; if (listener == server) { activeDownloads--; if (completed) completedDownloads++; if (activeDownloads == 0) currentFile = ""; } }
                    Notify(new LanTransferProgress { Id = activityId, Title = asset.Name, State = completed ? "completed" : cancelled ? "cancelled" : "failed", Message = completed ? "文件已发送。" : cancelled ? "分享已停止或到期。" : "传输中断，请重新下载。", Progress = completed ? (double?)100 : null });
                }
            }
        }
        internal static bool TryRange(string value, long length, out long start, out long end)
        {
            start = 0; end = length - 1;
            if (length <= 0 || value == null || !value.StartsWith("bytes=", StringComparison.Ordinal) || value.Contains(",")) return false;
            string[] parts = value.Substring(6).Split('-'); if (parts.Length != 2) return false;
            long first, last;
            if (parts[0].Length == 0)
            {
                if (!Int64.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out last) || last <= 0) return false;
                start = Math.Max(0, length - last); return true;
            }
            if (!Int64.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out first) || first >= length) return false;
            if (parts[1].Length == 0) last = length - 1;
            else if (!Int64.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out last) || last < first) return false;
            start = first; end = Math.Min(length - 1, last); return true;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, StringBuilder path, uint characters, uint flags);
        private static bool OpenedPathMatches(FileStream file, string expected)
        {
            var final = new StringBuilder(32768); uint count = GetFinalPathNameByHandle(file.SafeFileHandle, final, (uint)final.Capacity, 0);
            if (count == 0 || count >= final.Capacity) return false;
            string path = final.ToString(); if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) path = path.Substring(4);
            return String.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
        }
        private static Request ReadRequest(NetworkStream stream)
        {
            var bytes = new List<byte>(); int matched = 0; var deadline = Stopwatch.StartNew();
            while (bytes.Count < MaximumHeaderBytes)
            {
                if (deadline.ElapsedMilliseconds > 5000) throw new InvalidDataException();
                int next = stream.ReadByte(); if (next < 0) throw new InvalidDataException(); bytes.Add((byte)next);
                matched = next == (matched == 0 || matched == 2 ? 13 : 10) ? matched + 1 : next == 13 ? 1 : 0;
                if (matched == 4) break;
            }
            if (matched != 4 || bytes.Any(item => item > 127 || item == 0)) throw new InvalidDataException();
            string[] lines = Encoding.ASCII.GetString(bytes.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' '); if (first.Length != 3 || (first[2] != "HTTP/1.1" && first[2] != "HTTP/1.0") || !first[1].StartsWith("/", StringComparison.Ordinal)) throw new InvalidDataException();
            var request = new Request { Method = first[0], Target = first[1] };
            foreach (string line in lines.Skip(1))
            {
                if (line.Length == 0) continue; int colon = line.IndexOf(':');
                if (colon <= 0 || Char.IsWhiteSpace(line[0])) throw new InvalidDataException();
                string key = line.Substring(0, colon); if (request.Headers.ContainsKey(key)) throw new InvalidDataException(); request.Headers[key] = line.Substring(colon + 1).Trim();
            }
            if (request.Headers.ContainsKey("Transfer-Encoding")) throw new InvalidDataException();
            string contentLength; int size = 0;
            if (request.Headers.TryGetValue("Content-Length", out contentLength) && (!Int32.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < 0 || size > MaximumBodyBytes)) throw new InvalidDataException();
            if (size > 0)
            {
                request.Body = new byte[size]; int read = 0;
                while (read < size) { if (deadline.ElapsedMilliseconds > 10000) throw new InvalidDataException(); int count = stream.Read(request.Body, read, size - read); if (count == 0) throw new InvalidDataException(); read += count; }
            }
            return request;
        }
        private static string FormPassword(byte[] body)
        {
            string encoded;
            try { encoded = new UTF8Encoding(false, true).GetString(body); } catch (DecoderFallbackException) { throw new ArgumentException(); }
            if (!encoded.StartsWith("p=", StringComparison.Ordinal) || encoded.Contains("&")) throw new ArgumentException();
            string value; try { value = Uri.UnescapeDataString(encoded.Substring(2).Replace("+", " ")); } catch (UriFormatException) { throw new ArgumentException(); }
            if (value.Length > 32 || value.Any(Char.IsControl)) throw new ArgumentException(); return value;
        }
        private bool Blocked(string remote) { lock (gate) { Attempt attempt; return failures.TryGetValue(remote, out attempt) && utcNow() < attempt.BlockedUntil; } }
        private void FailedPassword(string remote)
        {
            lock (gate)
            {
                if (failures.Count >= 256 && !failures.ContainsKey(remote)) failures.Clear();
                Attempt attempt; if (!failures.TryGetValue(remote, out attempt)) failures[remote] = attempt = new Attempt();
                if (++attempt.Count >= 5) { attempt.BlockedUntil = utcNow().AddSeconds(30); attempt.Count = 0; }
            }
        }
        private static bool CookieMatches(Request request, string expected)
        {
            string value; if (!request.Headers.TryGetValue("Cookie", out value)) return false;
            foreach (string item in value.Split(';')) if (item.Trim().StartsWith("sideisland=", StringComparison.Ordinal)) return ConstantTimeEqual(Encoding.UTF8.GetBytes(item.Trim().Substring(11)), Encoding.UTF8.GetBytes(expected));
            return false;
        }
        private static byte[] HashPassword(byte[] salt, string password) { using (var hash = SHA256.Create()) return hash.ComputeHash(salt.Concat(Encoding.UTF8.GetBytes(password)).ToArray()); }
        internal static bool ConstantTimeEqual(byte[] left, byte[] right) { if (left == null || right == null) return false; int difference = left.Length ^ right.Length; for (int index = 0; index < Math.Max(left.Length, right.Length); index++) difference |= (index < left.Length ? left[index] : 0) ^ (index < right.Length ? right[index] : 0); return difference == 0; }
        private static byte[] RandomBytes(int count) { var bytes = new byte[count]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes); return bytes; }
        private static string RandomHex(int count) { return BitConverter.ToString(RandomBytes(count)).Replace("-", "").ToLowerInvariant(); }
        private static string Login(string root, bool failed) { return Page("访问口令 / Access code", (failed ? "<p>口令不正确 / Incorrect code</p>" : "") + "<form method=\"post\" action=\"" + root + "/unlock\"><label>口令 / Access code<input name=\"p\" type=\"password\" maxlength=\"32\" autocomplete=\"off\" required></label><button>查看文件 / View files</button></form>"); }
        private static string Page(string title, string body) { return "<!doctype html><html lang=\"zh\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Side Island</title><style>body{font:16px system-ui;background:#f2f2f7;color:#1c1c1e;margin:0;padding:20px}main{max-width:560px;margin:auto}h1{font-size:24px}p{color:#636366;line-height:1.6}.file,form{display:block;background:white;border-radius:16px;padding:16px;margin:10px 0;text-decoration:none;color:inherit}.file span{display:block;overflow-wrap:anywhere}small{display:block;color:#636366;margin-top:6px}input,button{box-sizing:border-box;display:block;width:100%;font:inherit;padding:12px;margin-top:12px;border:1px solid #d1d1d6;border-radius:10px}button{color:white;background:#007aff;border:0}</style><main><h1>" + title + "</h1>" + body + "</main></html>"; }
        private static void Redirect(NetworkStream stream, string location, string setCookie) { Headers(stream, 303, "text/plain; charset=utf-8", 0, "Location: " + location + "\r\n" + (setCookie == null ? "" : "Set-Cookie: " + setCookie + "\r\n")); }
        private static void Reply(NetworkStream stream, int code, string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); Headers(stream, code, "text/plain; charset=utf-8", bytes.Length, null); stream.Write(bytes, 0, bytes.Length); }
        private static void ReplyHtml(NetworkStream stream, int code, string text, bool head) { byte[] bytes = Encoding.UTF8.GetBytes(text); Headers(stream, code, "text/html; charset=utf-8", bytes.Length, null); if (!head) stream.Write(bytes, 0, bytes.Length); }
        private static void Headers(NetworkStream stream, int code, string type, long length, string extra)
        {
            string label = code == 200 ? "OK" : code == 206 ? "Partial Content" : code == 303 ? "See Other" : code == 400 ? "Bad Request" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : code == 405 ? "Method Not Allowed" : code == 409 ? "Conflict" : code == 410 ? "Gone" : code == 416 ? "Range Not Satisfiable" : "Too Many Requests";
            byte[] bytes = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + " " + label + "\r\nConnection: close\r\nContent-Type: " + type + "\r\nContent-Length: " + length.ToString(CultureInfo.InvariantCulture) + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'\r\n" + (extra ?? "") + "\r\n"); stream.Write(bytes, 0, bytes.Length);
        }
        internal static string FormatBytes(long bytes) { if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " GB"; if (bytes >= 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " MB"; return (bytes / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " KB"; }
        internal static string TaskTitle(string value)
        {
            string title = new String((value ?? "").Where(character => !Char.IsControl(character)).ToArray()).Trim();
            if (title.Length == 0) return "File transfer";
            if (title.Length <= 160) return title;
            int length = 159; if (Char.IsHighSurrogate(title[length - 1])) length--;
            return title.Substring(0, length) + "…";
        }
        private void Notify(LanTransferProgress update) { update.Title = TaskTitle(update.Title); var callback = ActivityChanged; if (callback != null) try { callback(update); } catch { } }
        public void Stop()
        {
          lock (operations)
          {
            TcpListener previous; CancellationTokenSource cancellation; Timer timer; TcpClient[] current;
            lock (gate) { previous = listener; listener = null; cancellation = lifetime; lifetime = null; timer = expiryTimer; expiryTimer = null; current = clients.ToArray(); clients.Clear(); activeDownloads = 0; currentFile = ""; }
            if (timer != null) timer.Dispose(); if (cancellation != null) cancellation.Cancel(); if (previous != null) previous.Stop(); foreach (var client in current) client.Close();
            // Accept/worker continuations hold CancellationToken values only.
            if (cancellation != null) cancellation.Dispose();
          }
        }
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; } Stop(); }
    }
}
