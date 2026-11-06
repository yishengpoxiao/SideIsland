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
    // Current preference fields are value types and immutable strings.
    public Preferences Copy() => (Preferences)MemberwiseClone();
    public void CopyFrom(Preferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Language = value.Language; LightTheme = value.LightTheme; Dock = value.Dock; PreferNotch = value.PreferNotch;
        HoverExpand = value.HoverExpand; HoverDelayMs = value.HoverDelayMs; AlwaysOnTop = value.AlwaysOnTop;
        AcrossSpaces = value.AcrossSpaces; OverFullscreen = value.OverFullscreen; ExpandedWidth = value.ExpandedWidth;
        ExpandedHeight = value.ExpandedHeight; AlongEdge = value.AlongEdge; FocusMinutes = value.FocusMinutes;
        Slot1 = value.Slot1; Slot2 = value.Slot2; CodexPath = value.CodexPath; CodexHome = value.CodexHome;
        CodexEnabled = value.CodexEnabled; MediaEnabled = value.MediaEnabled; Notes = value.Notes; LastTool = value.LastTool;
    }
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
    private readonly PortableStateFile state;
    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "preferences.json");
    public StateFileStatus PersistenceStatus => state.Status;
    public PreferenceStore(string? explicitDirectory = null)
        : this(explicitDirectory, null) { }
    internal PreferenceStore(string? explicitDirectory, PortableStateFile? stateFile)
    {
        DirectoryPath = Path.GetFullPath(explicitDirectory ?? Environment.GetEnvironmentVariable("SIDE_ISLAND_DATA_DIR") ??
            (OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "SideIsland") :
             OperatingSystem.IsLinux() ? Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "side-island") :
             Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideIsland.PortablePreview")));
        state = stateFile ?? new PortableStateFile(FilePath, MaximumBytes);
        if (!StringComparer.Ordinal.Equals(Path.GetFullPath(state.FilePath), Path.GetFullPath(FilePath))) throw new ArgumentException("The supplied state file does not belong to this preference store.", nameof(stateFile));
    }
    public Preferences Load() => state.Load(Decode, () => new Preferences());
    public Preferences Reload(Preferences current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return state.Load(Decode, () => current, () => new Preferences());
    }
    public void MarkDirty() => state.MarkDirty();
    public void Save(Preferences value) => Save(value, false);
    public void RecoverAndSave(Preferences value) => Save(value, true);
    private void Save(Preferences value, bool recovery)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            var copy = value.Copy(); copy.Normalize();
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(copy, new JsonSerializerOptions { WriteIndented = true });
            if (!state.IsLoaded) Load();
            if (recovery) state.RecoverAndSave(bytes); else state.Save(bytes);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            state.ReportFailure(StateFileProblemCode.InvalidData, error.Message);
            throw new StateFileException(StateFileProblemCode.InvalidData, FilePath, "Preferences could not be serialized; the current draft and original file were retained.", error);
        }
    }
    private static Preferences Decode(byte[] bytes)
    {
        // Preserve the former stream reader's UTF-8 BOM compatibility. Only a
        // single complete leading signature is allowed; JSON stays strict.
        int offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        var value = JsonSerializer.Deserialize<Preferences>(bytes.AsSpan(offset)) ?? throw new JsonException("Invalid preferences.");
        value.Normalize(); return value;
    }
}
