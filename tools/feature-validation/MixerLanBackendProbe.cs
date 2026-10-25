using System;
using System.Linq;
using System.IO;
using System.Media;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    internal static class MixerLanBackendProbe
    {
        [MTAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 1) throw new ArgumentException("Use --loopback, --lan-smoke, --read-audio or --exercise-own-session.");
                if (args[0] == "--loopback") { LanTransferTests.Run(); return 0; }
                if (args[0] == "--lan-smoke") return LanSmoke();
                if (args[0] == "--read-audio")
                {
                    var snapshot = AudioMixerService.Read("");
                    Console.WriteLine(new JavaScriptSerializer().Serialize(new { ReadOnly = true, SystemVolumeChanged = false, DefaultDeviceChanged = false, DeviceCount = snapshot.Devices.Length, SessionCount = snapshot.Sessions.Length, Error = snapshot.Error, Devices = snapshot.Devices.Select(item => new { item.Name, item.IsDefault }).ToArray(), Sessions = snapshot.Sessions.Select(item => new { item.Name, item.ProcessId, item.Volume, item.Muted, item.Active }).ToArray() }));
                    return snapshot.Devices.Length == 0 && !String.IsNullOrEmpty(snapshot.Error) ? 2 : 0;
                }
                if (args[0] == "--exercise-own-session") return ExerciseOwnSession();
                throw new ArgumentException("Unknown mode.");
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        private static int LanSmoke()
        {
            // This is a same-machine LAN-address request, not a phone/firewall
            // reachability claim. It shares only this generated fixed-byte file.
            var selected = LanTransferService.Addresses().FirstOrDefault(item => item.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item.Address));
            if (selected == null)
            {
                Console.WriteLine("SKIP: no active non-loopback IPv4 address. LAN smoke did not start a listener or issue requests; phone, firewall and remote-device access remain unverified.");
                return 0;
            }
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.LanSmoke." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                byte[] expected = Enumerable.Range(0, 4096).Select(index => (byte)((index * 17 + 3) % 256)).ToArray();
                string path = Path.Combine(folder, "LAN-smoke-测试.bin"); File.WriteAllBytes(path, expected);
                using (var server = new LanTransferService())
                {
                    Check(!server.Snapshot().Running, "LAN service must initially be stopped");
                    const string passcode = "lan-smoke+测试";
                    var endpoint = new Uri(server.Start(new[] { path }, selected.Address, 1, passcode));
                    Check(!endpoint.IsLoopback && endpoint.Host == selected.Address.ToString(), "listener must bind the selected non-loopback IPv4");
                    Console.WriteLine("LAN smoke bound selected IPv4 " + selected.Address + "; checking access using the same computer's LAN address. URL token, cookie and passcode are omitted.");
                    var gate = LanRequest(endpoint, "GET", endpoint.AbsolutePath, null, null);
                    Check(gate.Code == 200 && gate.Text.Contains("type=\"password\"") && !gate.Text.Contains("/f/") && !gate.Text.Contains(passcode), "unauthorized LAN request must see only password gate");
                    var rejected = LanRequest(endpoint, "POST", endpoint.AbsolutePath + "unlock", null, Encoding.ASCII.GetBytes("p=wrong"));
                    Check(rejected.Code == 403, "wrong LAN access code must be rejected");
                    var unlocked = LanRequest(endpoint, "POST", endpoint.AbsolutePath + "unlock", null, Encoding.UTF8.GetBytes("p=" + Uri.EscapeDataString(passcode)));
                    string setCookie;
                    Check(unlocked.Code == 303 && unlocked.Headers.TryGetValue("set-cookie", out setCookie), "correct code must return scoped authentication cookie");
                    setCookie = unlocked.Headers["set-cookie"];
                    Check(setCookie.Contains("HttpOnly") && setCookie.Contains("SameSite=Strict"), "authentication cookie must retain security attributes");
                    string cookieHeader = "Cookie: " + setCookie.Split(';')[0] + "\r\n";
                    var listing = LanRequest(endpoint, "GET", endpoint.AbsolutePath, cookieHeader, null);
                    Check(listing.Code == 200 && !listing.Text.Contains(folder), "authenticated listing must omit full disk path");
                    var match = Regex.Match(listing.Text, "href=\"([^\"]+/f/[a-f0-9]+)\"");
                    Check(match.Success, "authenticated listing must include generated selected fixture");
                    string route = match.Groups[1].Value;
                    var deniedFile = LanRequest(endpoint, "GET", route, null, null);
                    Check(deniedFile.Code == 200 && deniedFile.Text.Contains("type=\"password\""), "file route without cookie must remain gated");
                    var download = LanRequest(endpoint, "GET", route, cookieHeader, null);
                    Check(download.Code == 200 && download.Body.SequenceEqual(expected), "LAN-address download must preserve all 4096 fixed fixture bytes");
                    Check(download.Headers.ContainsKey("content-disposition") && download.Headers["content-disposition"].Contains(Uri.EscapeDataString(Path.GetFileName(path))), "download must preserve UTF-8 attachment filename");
                    var wait = Stopwatch.StartNew();
                    while (server.Snapshot().CompletedDownloads < 1 && wait.ElapsedMilliseconds < 1500) Thread.Sleep(5);
                    Check(server.Snapshot().CompletedDownloads == 1, "actual LAN download must reach completed accounting");
                    server.Stop(); Check(!server.Snapshot().Running, "stop must deactivate LAN share");
                    bool invalidated = false;
                    try { LanRequest(endpoint, "GET", route, cookieHeader, null); }
                    catch (SocketException) { invalidated = true; }
                    catch (IOException) { invalidated = true; }
                    Check(invalidated, "stopped LAN listener must no longer serve authenticated download");
                }
                Console.WriteLine("PASS: real non-loopback IPv4 bind and same-machine LAN-address password gate, wrong-code refusal, cookie authentication, exact-byte download and stop invalidation. Only a generated temporary file was shared and removed. No user file, audio, UI, firewall edit, external server or physical phone was used; remote-device reachability, firewall ingress, QR scanning and phone-browser rendering remain unverified.");
                return 0;
            }
            finally
            {
                string full = Path.GetFullPath(folder), temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (Directory.Exists(full) && full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) Directory.Delete(full, true);
            }
        }
        private sealed class LanResponse
        {
            internal int Code;
            internal Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal byte[] Body;
            internal string Text { get { return Encoding.UTF8.GetString(Body); } }
        }
        private static LanResponse LanRequest(Uri endpoint, string method, string path, string extraHeaders, byte[] body)
        {
            // Raw TCP avoids system proxies and automatic redirects/cookie state.
            using (var client = new TcpClient(AddressFamily.InterNetwork))
            {
                var connecting = client.ConnectAsync(endpoint.Host, endpoint.Port);
                var completed = System.Threading.Tasks.Task.WhenAny(connecting, System.Threading.Tasks.Task.Delay(4000)).GetAwaiter().GetResult();
                if (completed != connecting) throw new IOException("LAN smoke TCP connection deadline exceeded.");
                connecting.GetAwaiter().GetResult(); client.SendTimeout = client.ReceiveTimeout = 4000;
                using (var stream = client.GetStream())
                using (var response = new MemoryStream())
                {
                    byte[] prefix = Encoding.ASCII.GetBytes(method + " " + path + " HTTP/1.1\r\nHost: " + endpoint.Host + ":" + endpoint.Port + "\r\n" + (extraHeaders ?? "") + (body == null ? "" : "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n") + "\r\n");
                    stream.Write(prefix, 0, prefix.Length); if (body != null) stream.Write(body, 0, body.Length);
                    var watch = Stopwatch.StartNew(); byte[] buffer = new byte[8192]; int count;
                    while (true)
                    {
                        int remaining = 5000 - (int)watch.ElapsedMilliseconds;
                        if (remaining <= 0) throw new IOException("LAN smoke response deadline exceeded.");
                        stream.ReadTimeout = remaining; count = stream.Read(buffer, 0, buffer.Length); if (count == 0) break;
                        response.Write(buffer, 0, count); if (response.Length > 256 * 1024) throw new InvalidDataException("LAN smoke response exceeded fixture limit.");
                    }
                    byte[] data = response.ToArray(); int split = -1;
                    for (int index = 0; index + 3 < data.Length; index++) if (data[index] == 13 && data[index + 1] == 10 && data[index + 2] == 13 && data[index + 3] == 10) { split = index; break; }
                    Check(split >= 0, "LAN response header must be complete");
                    string[] lines = Encoding.ASCII.GetString(data, 0, split).Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var result = new LanResponse { Code = Int32.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture), Body = data.Skip(split + 4).ToArray() };
                    foreach (string line in lines.Skip(1)) { int colon = line.IndexOf(':'); if (colon > 0) result.Headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim(); }
                    string declared; long expected;
                    Check(result.Headers.TryGetValue("content-length", out declared) && Int64.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out expected) && expected == result.Body.LongLength, "LAN response must include its complete declared body");
                    return result;
                }
            }
        }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("LAN smoke: " + message); }
        private static int ExerciseOwnSession()
        {
            // The sample is PCM silence. Never selects or modifies another PID.
            var wav = new MemoryStream(); using (var writer = new BinaryWriter(wav, System.Text.Encoding.ASCII, true))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(16036); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(16000); writer.Write(new byte[16000]);
            }
            wav.Position = 0;
            using (wav) using (var player = new SoundPlayer(wav))
            {
                player.Load(); player.PlayLooping(); AudioMixerSession own = null; string device = "";
                var wait = Stopwatch.StartNew(); uint pid = (uint)Process.GetCurrentProcess().Id;
                while (own == null && wait.ElapsedMilliseconds < 5000)
                {
                    var snapshot = AudioMixerService.Read(""); device = snapshot.SelectedDeviceId;
                    own = snapshot.Sessions.FirstOrDefault(item => item.ProcessId == pid && !item.SystemSounds); if (own == null) Thread.Sleep(50);
                }
                if (own == null) { player.Stop(); throw new InvalidOperationException("Own silent playback session was not published; no audio settings were changed."); }
                bool restored = false;
                try
                {
                    AudioMixerService.ChangeSession(device, own.Id, 37, true);
                    var changed = AudioMixerService.Read(device).Sessions.FirstOrDefault(item => item.Id == own.Id && item.ProcessId == pid);
                    if (changed == null || Math.Abs(changed.Volume - 37) > .1 || !changed.Muted) throw new InvalidOperationException("Own audio session did not accept volume and mute.");
                }
                finally
                {
                    AudioMixerService.ChangeSession(device, own.Id, own.Volume, own.Muted);
                    var after = AudioMixerService.Read(device).Sessions.FirstOrDefault(item => item.Id == own.Id && item.ProcessId == pid);
                    restored = after != null && Math.Abs(after.Volume - own.Volume) < .1 && after.Muted == own.Muted;
                    player.Stop();
                }
                if (!restored) throw new InvalidOperationException("Own session restoration did not verify.");
                Console.WriteLine("PASS: real silent own-PID audio session accepted volume and mute, exact initial state restored; no master/default-device or other-app changes."); return 0;
            }
        }
    }
}
