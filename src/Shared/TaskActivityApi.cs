using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    // Explicitly started loopback-only status input. No shell, file, network
    // proxy or application-control endpoint; requests only update task records.
    internal sealed class TaskActivityApi : IDisposable
    {
        private readonly TaskActivityHub hub;
        private readonly SemaphoreSlim clients = new SemaphoreSlim(8, 8);
        private readonly object connectionGate = new object();
        private readonly HashSet<TcpClient> activeClients = new HashSet<TcpClient>();
        private TcpListener listener; private volatile bool stopped = true;
        private int generation;
        public int Port { get; private set; }
        public string Token { get; private set; }
        public bool Running { get { return !stopped && listener != null; } }
        public string Url { get { return Running ? "http://127.0.0.1:" + Port + "/api/tasks" : ""; } }
        public TaskActivityApi(TaskActivityHub model) { hub = model; }
        public void Start(int port)
        {
            if (Running) throw new InvalidOperationException("本地任务接入已运行。");
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException("port");
            byte[] bytes = new byte[24]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            Token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            listener = new TcpListener(IPAddress.Loopback, port); listener.Start(8);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port; stopped = false;
            var current = listener; int version = ++generation; string token = Token; Task.Run(() => Accept(current, version, token));
        }
        private async Task Accept(TcpListener current, int version, string token)
        {
            while (!stopped && Object.ReferenceEquals(current, listener))
            {
                TcpClient client;
                try { client = await current.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { break; } catch (SocketException) { break; }
                if (!clients.Wait(0)) { client.Close(); continue; }
                lock (connectionGate) { if (stopped || version != generation) { client.Close(); clients.Release(); continue; } activeClients.Add(client); }
                // Each request owns its cleanup; the accept loop stays available.
                var requestWork = Task.Run(() => { try { Handle(client, version, token); } finally { lock (connectionGate) activeClients.Remove(client); client.Close(); clients.Release(); } });
            }
        }
        private void Handle(TcpClient client, int version, string token)
        {
            client.ReceiveTimeout = client.SendTimeout = 2500;
            try
            {
                var stream = client.GetStream(); var deadline = Stopwatch.StartNew(); var header = new List<byte>(); int matched = 0;
                while (header.Count < 8192)
                {
                    BoundRead(stream, deadline); int value = stream.ReadByte(); if (value < 0) return; header.Add((byte)value);
                    matched = value == (matched == 0 || matched == 2 ? 13 : 10) ? matched + 1 : value == 13 ? 1 : 0;
                    if (matched == 4) break;
                }
                if (matched != 4) { Reply(stream, 431, "header too large"); return; }
                string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] request = lines[0].Split(' '); var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                { int colon = lines[i].IndexOf(':'); if (colon > 0) { string key = lines[i].Substring(0, colon).Trim(); if (fields.ContainsKey(key)) { Reply(stream, 400, "duplicate header"); return; } fields.Add(key, lines[i].Substring(colon + 1).Trim()); } }
                string auth; if (!fields.TryGetValue("Authorization", out auth) || !Same(auth, "Bearer " + token)) { Reply(stream, 401, "unauthorized"); return; }
                if (request.Length != 3 || request[0] != "POST" || request[1] != "/api/tasks" || (request[2] != "HTTP/1.1" && request[2] != "HTTP/1.0") || fields.ContainsKey("Transfer-Encoding")) { Reply(stream, 400, "POST /api/tasks required"); return; }
                string lengthText; int length;
                if (!fields.TryGetValue("Content-Length", out lengthText) || !Int32.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 2 || length > 16384) { Reply(stream, 413, "body must be 2..16384 bytes"); return; }
                var body = new byte[length]; int read = 0;
                while (read < length) { BoundRead(stream, deadline); int count = stream.Read(body, read, length - read); if (count == 0) return; read += count; }
                try
                {
                    var serializer = new JavaScriptSerializer { MaxJsonLength = 16384, RecursionLimit = 8 };
                    var data = serializer.DeserializeObject(new UTF8Encoding(false, true).GetString(body)) as IDictionary<string, object>;
                    if (data == null) throw new ArgumentException("JSON object required");
                    var update = new TaskActivityUpdate { Id = "api:" + Value(data, "id", true), Title = Value(data, "title", true), State = Value(data, "state", false) ?? "running", Message = Value(data, "message", false) ?? "", Source = "本地接口" };
                    object progress; if (data.TryGetValue("progress", out progress) && progress != null)
                    {
                        if (!(progress is int || progress is long || progress is decimal || progress is double)) throw new ArgumentException("numeric progress required");
                        update.Progress = Convert.ToDouble(progress, CultureInfo.InvariantCulture);
                    }
                    lock (connectionGate)
                    {
                        if (stopped || version != generation) { Reply(stream, 503, "stopped"); return; }
                        hub.Publish(update);
                    }
                    Reply(stream, 200, "ok");
                }
                catch (ArgumentException) { Reply(stream, 400, "invalid task payload"); }
                catch (InvalidOperationException) { Reply(stream, 409, "task limit reached"); }
            }
            catch (IOException) { } catch (SocketException) { } catch (ObjectDisposedException) { }
        }
        private static string Value(IDictionary<string, object> data, string key, bool required)
        { object value; if (!data.TryGetValue(key, out value) || value == null) { if (required) throw new ArgumentException(key); return null; } var text = value as string; if (text == null) throw new ArgumentException(key); return text; }
        private static bool Same(string first, string second)
        { if (first == null || second == null) return false; int difference = first.Length ^ second.Length; for (int i = 0; i < Math.Min(first.Length, second.Length); i++) difference |= first[i] ^ second[i]; return difference == 0; }
        private static void Reply(Stream stream, int status, string message)
        {
            byte[] body = Encoding.UTF8.GetBytes("{\"status\":\"" + message + "\"}");
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Result\r\nContent-Type: application/json; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n\r\n");
            stream.Write(head, 0, head.Length); stream.Write(body, 0, body.Length);
        }
        private static void BoundRead(NetworkStream stream, Stopwatch clock)
        { int remaining = 5000 - (int)clock.ElapsedMilliseconds; if (remaining <= 0) throw new IOException("request deadline"); stream.ReadTimeout = Math.Min(2500, remaining); }
        public void Stop()
        {
            lock (connectionGate)
            {
                stopped = true; Interlocked.Increment(ref generation); var current = listener; listener = null; if (current != null) current.Stop();
                foreach (var client in activeClients) client.Close(); activeClients.Clear(); Token = ""; Port = 0;
            }
        }
        public void Dispose() { Stop(); }
    }
}
