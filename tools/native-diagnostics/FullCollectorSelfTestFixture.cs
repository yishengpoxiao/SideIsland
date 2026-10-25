using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

// Diagnostic-only, no WPF/window/input APIs. Exercises owned process discovery.
internal static class FullCollectorSelfTestFixture
{
    private static int Main(string[] args)
    {
        if(args.Length>0 && args[0]=="--leaf") { Console.WriteLine("NONUI fixture leaf stdout"); Console.Error.WriteLine("NONUI fixture leaf stderr");Thread.Sleep(300);return 0; }
        if(args.Length>0 && args[0]=="--child") { Console.WriteLine("NONUI fixture child begin");int code=Spawn("--leaf");Thread.Sleep(200);Console.WriteLine("NONUI fixture child end");return code; }
        Console.WriteLine("CONTROLLED NONUI COORDINATOR BEGIN; no real regression/UI");
        int first=Spawn("--child"),second=Spawn("--leaf");
        Console.WriteLine("CONTROLLED NONUI COORDINATOR END; exits="+first+","+second);return first==0 && second==0?0:1;
    }
    private static int Spawn(string argument)
    {
        using(var child=Process.Start(new ProcessStartInfo {FileName=Assembly.GetExecutingAssembly().Location,Arguments=argument,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}))
        { string stdout=child.StandardOutput.ReadToEnd(),stderr=child.StandardError.ReadToEnd();child.WaitForExit();Console.Write(stdout);Console.Error.Write(stderr);return child.ExitCode; }
    }
}
