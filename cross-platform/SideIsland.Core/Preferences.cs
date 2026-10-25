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
        if (!Enum.TryParse<DockEdge>(Dock, out _)) Dock = "Auto";
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
        try { if (new FileInfo(FilePath).Length > 256 * 1024) return new(); var value = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new(); value.Normalize(); return value; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new(); }
    }
    public void Save(Preferences value)
    {
        value.Normalize(); Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
