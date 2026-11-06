#nullable enable
using System.Text.Json;

namespace SideIsland.Core;

public sealed class Preferences
{
    public string Language { get; set; } = "zh";
    public bool LightTheme { get; set; }
    public string Dock { get; set; } = "Auto";
    public bool PreferNotch { get; set; } = true;
    public bool HoverExpand { get; set; } = true;
    public int HoverDelayMs { get; set; } = 650;
    public bool AlwaysOnTop { get; set; } = true;
    public bool AcrossSpaces { get; set; } = true;
    public bool OverFullscreen { get; set; }
    public double ExpandedWidth { get; set; } = 480;
    public double ExpandedHeight { get; set; } = 640;
    public double AlongEdge { get; set; } = .45;
    public int FocusMinutes { get; set; } = 25;
    public string Slot1 { get; set; } = "codex-week";
    public string Slot2 { get; set; } = "focus";
    public string CodexPath { get; set; } = "";
    public string CodexHome { get; set; } = "";
    public bool CodexEnabled { get; set; }
    public bool MediaEnabled { get; set; }
    public string Notes { get; set; } = "";
    public string LastTool { get; set; } = "focus";
    public void Normalize()
    {
        Language = Language == "en" ? "en" : "zh";
        if (!Enum.GetNames<DockEdge>().Contains(Dock, StringComparer.Ordinal)) Dock = "Auto";
        HoverDelayMs = Math.Clamp(HoverDelayMs, 100, 5000);
        ExpandedWidth = double.IsFinite(ExpandedWidth) ? Math.Clamp(ExpandedWidth, 320, 900) : 480;
        ExpandedHeight = double.IsFinite(ExpandedHeight) ? Math.Clamp(ExpandedHeight, 360, 900) : 640;
        AlongEdge = double.IsFinite(AlongEdge) ? Math.Clamp(AlongEdge, 0, 1) : .45;
        FocusMinutes = Math.Clamp(FocusMinutes, 1, 480);
        if (!Widgets.Contains(Slot1)) Slot1 = "codex-week";
        if (!Widgets.Contains(Slot2)) Slot2 = "focus";
        Notes = (Notes ?? "")[..Math.Min(Notes?.Length ?? 0, 60000)];
        if (Notes.Length > 0 && char.IsHighSurrogate(Notes[^1])) Notes = Notes[..^1];
    }
    public static readonly string[] Widgets = ["codex-short", "codex-week", "focus", "clock", "music", "tasks", "awake", "unread"];
}

public sealed class PreferenceStore
{
    // JSON escapes can take six bytes per UTF-16 code unit. The notes editor
    // accepts 60,000 code units, so the previous 256 KiB read limit rejected
    // valid Chinese and emoji notes written by this store itself.
    private const int MaximumBytes = 1024 * 1024;
    private bool unreadable;
    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "preferences.json");
    public PreferenceStore(string? explicitDirectory = null)
    {
        DirectoryPath = explicitDirectory ?? Environment.GetEnvironmentVariable("SIDE_ISLAND_DATA_DIR") ??
            (OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "SideIsland") :
             OperatingSystem.IsLinux() ? Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "side-island") :
             Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideIsland.PortablePreview"));
    }
    public Preferences Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("Preferences exceed 1 MiB.");
            var value = JsonSerializer.Deserialize<Preferences>(stream) ?? throw new JsonException("Invalid preferences.");
            value.Normalize(); unreadable = false; return value;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { unreadable = true; return new(); }
    }
    public void Save(Preferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Normalize();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true });
        // Never replace a usable file with data that the next startup rejects.
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Preferences exceed 1 MiB; previous saved preferences have been retained.");
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (unreadable && File.Exists(FilePath))
            {
                File.Copy(FilePath, FilePath + ".unreadable-" + Guid.NewGuid().ToString("N") + ".bak");
                unreadable = false;
            }
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, FilePath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
