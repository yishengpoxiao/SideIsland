using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodexIsland
{
    internal static class SystemUtilityTests
    {
        private static int checks;
        public static void Run()
        {
            PortMonitorPresentationTests.Run(); PortTests(); TaskWatchTests(); AwakeTests();
            Console.WriteLine("PASS: " + checks + " port ownership, task watch and injected keep-awake checks.");
        }
        private static void PortTests()
        {
            IntPtr scopedRow = Marshal.AllocHGlobal(56);
            try
            {
                byte[] address = new byte[16]; address[0] = 0xfe; address[1] = 0x80; address[15] = 1;
                Marshal.Copy(address, 0, scopedRow, address.Length); Marshal.WriteInt32(scopedRow, 16, 12);
                Check(PortMonitor.ReadLocalAddress(scopedRow, true, true).ScopeId == 12 && PortMonitor.ReadLocalAddress(scopedRow, true, true).ToString() == "fe80::1%12", "IPv6 TCP nonzero native scope is not byte-reversed");
                Check(PortMonitor.ReadLocalAddress(scopedRow, false, true).ScopeId == 12, "IPv6 UDP nonzero native scope is preserved");
                Marshal.WriteInt32(scopedRow, 16, unchecked((int)0x8000000c));
                Check(PortMonitor.ReadLocalAddress(scopedRow, true, true).ScopeId == 0x8000000cL, "IPv6 native scope DWORD remains unsigned");
            }
            finally { Marshal.FreeHGlobal(scopedRow); }
            Check(PortMonitor.ParsePorts("3000、8080，443 3000").SequenceEqual(new[] { 443, 3000, 8080 }), "Port delimiters, ordering, deduplication");
            foreach (string invalid in new[] { "", "0", "65536", "-1", "3000.0", "8080oops", "1-10" }) Throws(() => PortMonitor.ParsePorts(invalid), "Invalid port rejected");
            var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
            try
            {
                using (var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                {
                    int tcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port, udpPort = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
                    var result = PortMonitor.Read(new[] { tcpPort, udpPort }); int ownPid = Process.GetCurrentProcess().Id;
                    Check(result.Errors.Count == 0, "System endpoint tables read successfully");
                    var ownTcp = result.Owners.FirstOrDefault(x => x.Port == tcpPort && x.Protocol == "TCP IPv4" && x.Pid == ownPid);
                    Check(ownTcp != null && ownTcp.Address == "127.0.0.1", "Own IPv4 listener PID/address");
                    Check(PortMonitor.TcpStateName(2) == "LISTEN" && PortMonitor.TcpStateName(5) == "ESTABLISHED" && PortMonitor.TcpStateName(0) == "未知", "Native TCP state values mapped, including unavailable state");
                    Check(ownTcp != null && File.Exists(ownTcp.Path) && !String.IsNullOrWhiteSpace(ownTcp.Program), "Own process full executable path resolved");
                    Check(result.Owners.Any(x => x.Port == udpPort && x.Protocol == "UDP IPv4" && x.Pid == ownPid), "Own IPv4 UDP owner");
                }
            }
            finally { tcp.Stop(); }
            if (Socket.OSSupportsIPv6)
            {
                var tcp6 = new TcpListener(IPAddress.IPv6Loopback, 0); tcp6.Start();
                try
                {
                    using (var udp6 = new UdpClient(AddressFamily.InterNetworkV6))
                    {
                        udp6.Client.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
                        int port = ((IPEndPoint)tcp6.LocalEndpoint).Port, udpPort = ((IPEndPoint)udp6.Client.LocalEndPoint).Port;
                        var result = PortMonitor.Read(new[] { port, udpPort }); int ownPid = Process.GetCurrentProcess().Id;
                        Check(result.Owners.Any(x => x.Port == port && x.Protocol == "TCP IPv6" && x.Pid == ownPid && x.Address == "::1"), "Own IPv6 TCP owner and byte offsets");
                        Check(result.Owners.Any(x => x.Port == udpPort && x.Protocol == "UDP IPv6" && x.Pid == ownPid && x.Address == "::1"), "Own IPv6 UDP owner and byte offsets");
                    }
                }
                finally { tcp6.Stop(); }
            }
        }
        private static void TaskWatchTests()
        {
            string directory = Path.Combine(Path.GetTempPath(), "side-island-watch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            try
            {
                string pending = Path.Combine(directory, "result.txt"); var book = new TaskWatchBook();
                Check(book.AddFile(pending).Contains("已开始"), "Missing file starts watch");
                Check(book.Poll().Count == 0 && !book.Items[0].Completed, "Missing file does not notify");
                Throws(() => book.AddFile(pending), "Duplicate file watch rejected");
                File.WriteAllText(pending, "own fixture");
                var notice = book.Poll(); Check(notice.Count == 1 && notice[0].Body == pending && book.Items[0].Completed, "Own generated file notifies once");
                Check(book.Poll().Count == 0, "Completed file does not repeat");
                book.Remove(book.Items[0].Id); Check(book.Items.Count == 0, "Completed watch removable");
                Check(book.AddFile(pending).Contains("已经存在") && book.Items.Count == 0, "Initially existing file does not generate stale notification");
                Throws(() => book.AddFile(directory), "Directory cannot be file target");
                Throws(() => book.AddFile("relative.txt"), "Relative file path rejected");
                bool exists = false; long identity = 10; bool readDenied = false;
                var fake = new TaskWatchBook(path => exists, pid => { if (readDenied) throw new Win32Exception(5); return identity == 0 ? null : new WatchedProcess { Pid = pid, Name = "own-worker", StartUtcTicks = identity }; });
                fake.AddProcess(42, 10); readDenied = true;
                Check(fake.Poll().Count == 0 && !fake.Items[0].Completed && fake.Items[0].Status.Contains("无法检查"), "Read denial does not masquerade as process exit");
                readDenied = false; identity = 11; Check(fake.Poll().Count == 1, "PID reuse completes original process watch");
                Check(fake.Poll().Count == 0, "Process completion only once");
                Check(fake.AddProcess(42, 10).Contains("PID 已被") && fake.Items.Count == 1, "Stale process-list selection rejected");
                identity = 0; Check(fake.AddProcess(43, null).Contains("已经退出") && fake.Items.Count == 1, "Already exited process does not notify");
                fake.AddFile(pending + ".next"); string id = fake.Items.Last().Id; fake.Remove(id); exists = true;
                Check(fake.Poll().Count == 0, "Cancelled file watch stays silent");
                int ownPid = Process.GetCurrentProcess().Id; var current = TaskWatchBook.ReadProcess(ownPid);
                Check(current != null && current.Pid == ownPid && current.StartUtcTicks > 0, "Own running process identity readable");
                var ownBook = new TaskWatchBook(); ownBook.AddProcess(ownPid, current.StartUtcTicks);
                Check(ownBook.Poll().Count == 0 && !ownBook.Items[0].Completed, "Live own process stays pending");
                ownBook.Clear(); Check(ownBook.Items.Count == 0, "Disposal cancellation clears all pending state");
            }
            finally { Directory.Delete(directory, true); }
        }
        private static void AwakeTests()
        {
            TimeSpan elapsed = TimeSpan.Zero; var native = new FakeAwake(); var session = new KeepAwakeSession(native, () => elapsed);
            session.Start(30); Check(session.IsRunning && session.Remaining == TimeSpan.FromMinutes(30), "30 minute awake duration starts");
            Check(native.Calls.SequenceEqual(new[] { KeepAwakeSession.Continuous | KeepAwakeSession.SystemRequired }), "Request system wake without display or policy changes");
            elapsed += TimeSpan.FromMinutes(5); Check(session.Remaining == TimeSpan.FromMinutes(25), "Monotonic remaining duration");
            session.Start(60); Check(session.Remaining == TimeSpan.FromMinutes(60), "New duration replaces deadline");
            elapsed += TimeSpan.FromMinutes(60); Check(session.Poll() && !session.IsRunning && native.Calls.Last() == KeepAwakeSession.Continuous, "Expiry releases same-thread request");
            int released = native.Calls.Count; Check(!session.Poll() && native.Calls.Count == released, "Expired session does not repeatedly call native release");
            session.Start(120); session.Stop(); Check(!session.IsRunning && native.Threads.Distinct().Count() == 1, "Explicit stop on initiating thread");
            session.Start(30); Exception wrongThread = null; var worker = new Thread(() => { try { session.Stop(); } catch (Exception error) { wrongThread = error; } }); worker.Start(); worker.Join();
            Check(wrongThread is InvalidOperationException && session.IsRunning, "Wrong-thread release blocked");
            session.Dispose(); Check(!session.IsRunning && native.Calls.Last() == KeepAwakeSession.Continuous, "Dispose releases outstanding request");
            Throws(() => session.Start(30), "Disposed session cannot restart");
            var failedNative = new FakeAwake { Fail = true }; var failed = new KeepAwakeSession(failedNative, () => elapsed);
            Throws(() => failed.Start(30), "Failed native request is reported"); Check(!failed.IsRunning, "Failed native request never appears active");
            failed.Dispose();
        }
        private sealed class FakeAwake : IKeepAwakeNative
        {
            public readonly List<uint> Calls = new List<uint>(); public readonly List<int> Threads = new List<int>(); public bool Fail;
            public uint SetState(uint state) { Calls.Add(state); Threads.Add(Thread.CurrentThread.ManagedThreadId); return Fail ? 0 : KeepAwakeSession.Continuous; }
        }
        private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
        private static void Throws(Action action, string message) { bool threw = false; try { action(); } catch (Exception error) { if (error is ArgumentException || error is Win32Exception || error is ObjectDisposedException) threw = true; else throw; } Check(threw, message); }
    }
}
