using System.Diagnostics;
using System.Reflection;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Tests;
internal static class ProviderTests
{
    public static void Run()
    {
        var ss = Parse("ParseSs", "tcp LISTEN 0 128 127.0.0.1:3000 0.0.0.0:* users:((\"node\",pid=12345,fd=7))\n" +
            "tcp LISTEN 0 128 [::]:8080 [::]:* users:((\"java\",pid=23456,fd=8))\n" +
            "udp UNCONN 0 0 0.0.0.0:3000 0.0.0.0:*\n" +
            "tcp LISTEN 0 128 127.0.0.1:80 0.0.0.0:* users:((\"other\",pid=34567,fd=9))\n", [3000,8080]);
        Check(ss.Count == 3 && ss.Any(p => p.Port == 3000 && p.Pid == 12345 && p.Name == "node"), "ss TCP owner");
        Check(ss.Any(p => p.Port == 8080 && p.Pid == 23456), "ss IPv6 owner");
        Check(ss.Any(p => p.Protocol == "UDP" && p.Pid is null), "permission-hidden owner is not invented");
        var lsof = Parse("ParseLsof", "p12345\0cnode app\0\nf11\0PTCP\0n127.0.0.1:3000->127.0.0.1:50000\0\nf12\0PUDP\0n*:8080\0\np23456\0cnot requested\0\nf1\0PTCP\0n*:80\0", [3000,8080]);
        Check(lsof.Count == 2 && lsof.All(p => p.Pid == 12345 && p.Name == "node app"), "lsof null-delimited process/file records");
        Check(lsof.Any(p => p.Port == 3000 && p.Protocol == "TCP") && lsof.Any(p => p.Port == 8080 && p.Protocol == "UDP"), "lsof endpoint selection and protocol");
        var start = (Process)Method("NewProcess").Invoke(null, ["/tmp/tool", new string[] { "--player", "中文 ; $(never-run)", "play-pause" }])!;
        using (start)
        {
            Check(!start.StartInfo.UseShellExecute && start.StartInfo.ArgumentList.Count == 3 && start.StartInfo.ArgumentList[1] == "中文 ; $(never-run)" && start.StartInfo.Arguments.Length == 0, "media input is a single argument without shell expansion");
        }
        using var service = new CommandPlatformService();
        Check(service.ControlMediaAsync("unknown-action", default).GetAwaiter().GetResult() is not null, "unknown actions cannot launch helpers");
        Check(service.SetVolumeAsync(101, false, default).GetAwaiter().GetResult() is not null, "invalid volume rejected before execution");
        Console.WriteLine("PASS: production ss/lsof parsers and helper argument boundaries using explicit fixtures; no desktop processes launched and no native-platform execution claim.");
    }
    private static MethodInfo Method(string name) => typeof(CommandPlatformService).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static List<PortOwner> Parse(string method, string text, int[] ports) => (List<PortOwner>)Method(method).Invoke(null, [text, ports])!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Provider: " + message); }
}
