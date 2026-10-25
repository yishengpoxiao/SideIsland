using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Web.Script.Serialization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

internal static class EtwPrivilegeProbe
{
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        string name = "CodexIsland-Minimize-Privilege-" + Guid.NewGuid().ToString("N");
        TraceEventSession session = null;
        bool enabled = false, stopped = false; string error = null;
        try
        {
            session = new TraceEventSession(name);
            session.StopOnDispose = true;
            var options = new TraceEventProviderOptions();
            options.EventIDsToEnable = new int[] { 59, 60, 61, 62 };
            session.EnableProvider(new Guid("8c416c79-d49b-4f01-a467-e56d3aa8234c"), TraceEventLevel.Informational, 0x400000, options);
            enabled = true;
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            if (session != null) { try { session.Dispose(); stopped = true; } catch (Exception exception) { error += "\nCLEANUP: " + exception; } }
        }
        string json = new JavaScriptSerializer().Serialize(new {
            SessionName = name, Enabled = enabled, Disposed = stopped,
            Elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            Error = error, NoFileLogger = true, NoUi = true, NoOtherSessionChanged = true,
            Utc = DateTime.UtcNow.ToString("o"), ProcessId = Process.GetCurrentProcess().Id
        });
        File.WriteAllText(Path.GetFullPath(args[0]), json); Console.WriteLine(json);
        return error == null ? 0 : 1;
    }
}
