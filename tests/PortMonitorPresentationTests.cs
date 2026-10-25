using System;
using System.Linq;

namespace CodexIsland
{
    internal static class PortMonitorPresentationTests
    {
        public static void Run()
        {
            var server = new PortMonitorResult();
            for (int i = 0; i < 5000; i++) server.Owners.Add(Owner(3000, 42, "TCP IPv4", "127.0.0.1", "ESTABLISHED"));
            server.Owners.Add(Owner(3000, 42, "TCP IPv4", "127.0.0.1", "LISTEN"));
            server.Owners.Add(Owner(3000, 43, "TCP IPv4", "127.0.0.1", "ESTABLISHED"));
            server.Owners.Add(Owner(3000, 42, "UDP IPv4", "127.0.0.1", "已绑定"));
            server.Owners.Add(Owner(3000, 42, "TCP IPv4", "0.0.0.0", "LISTEN"));
            var grouped = PortMonitorPresentation.Build(server, new[] { 3000, 8080 });
            Check(grouped.TotalBindings == 5004 && grouped.TotalGroups == 4 && grouped.Rows.Count == 5, "Connection-heavy server creates owner cards, preserving protocol/address/PID distinctions");
            var own = grouped.Rows.Single(x => x.Owner != null && x.Owner.Pid == 42 && x.Owner.Protocol == "TCP IPv4" && x.Owner.Address == "127.0.0.1");
            Check(own.Connections == 5001 && own.Owner.State == "LISTEN" && own.Owner.Program == "worker" && own.Owner.Path == @"D:\Own Fixture\worker.exe", "Merged owner preserves program/path and prioritizes listening state");
            Check(grouped.Rows.Single(x => x.Port == 8080).Owner == null, "Unoccupied requested port keeps an explicit row");
            Check(server.Owners[0].State == "ESTABLISHED", "Presentation aggregation never mutates the native snapshot");
            var reversed = new PortMonitorResult(); reversed.Owners.AddRange(server.Owners.AsEnumerable().Reverse());
            Check(grouped.Signature == PortMonitorPresentation.Build(reversed, new[] { 8080, 3000 }).Signature, "Unchanged native results in a different enumeration order keep the same render signature");
            server.Owners.Add(Owner(3000, 42, "TCP IPv4", "127.0.0.1", "ESTABLISHED"));
            Check(grouped.Signature != PortMonitorPresentation.Build(server, new[] { 3000, 8080 }).Signature, "Connection-count changes invalidate the visible presentation");
            var crowded = new PortMonitorResult();
            for (int i = 0; i < 250; i++) crowded.Owners.Add(Owner(3000, i + 1, "TCP IPv4", "127.0.0.1", "ESTABLISHED"));
            var capped = PortMonitorPresentation.Build(crowded, new[] { 3000, 8080 });
            Check(capped.Rows.Count == PortMonitorPresentation.MaximumCards && capped.OmittedRows == 51 && capped.TotalGroups == 250, "A global card cap includes missing-port rows and reports all omitted rows");
            var empty = new PortMonitorResult(); var complete = PortMonitorPresentation.Build(empty, new[] { 8080, 8080 }); empty.Errors.Add("TCP IPv6 denied"); var partial = PortMonitorPresentation.Build(empty, new[] { 8080 });
            Check(complete.Rows.Count == 1 && partial.Signature != complete.Signature && partial.Errors.Count == 1, "Duplicate requested ports do not create duplicate empty cards, and incomplete reads change their presentation");
            Console.WriteLine("PASS: port owner grouping, LISTEN priority, global card cap and stable render signature fixtures.");
        }
        private static PortOwner Owner(int port, int pid, string protocol, string address, string state) { return new PortOwner { Port = port, Pid = pid, Protocol = protocol, Address = address, State = state, Program = "worker", Path = @"D:\Own Fixture\worker.exe" }; }
        private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
    }
}
