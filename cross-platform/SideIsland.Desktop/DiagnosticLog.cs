using System.Runtime.InteropServices;
using System.Text.Json;

namespace SideIsland.Desktop;
internal static class DiagnosticLog
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = new SideIsland.Core.PreferenceStore().DirectoryPath;
    public static void Write(string kind, Exception? error = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "startup.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Move(path, path + ".previous", true);
                var record = new { Utc = DateTimeOffset.UtcNow, Kind = kind, Version = typeof(Program).Assembly.GetName().Version?.ToString(), OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Process = Environment.ProcessId, Error = error?.ToString() };
                File.AppendAllText(path, JsonSerializer.Serialize(record) + Environment.NewLine);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }
}
