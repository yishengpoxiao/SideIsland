#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SideIsland.Core;

public enum NotificationFilter { All, Unread, Important }
public sealed record NotificationInput(string SourceId, string SourceName, string Title, string Body,
    DateTimeOffset ReceivedAt, string? NativeId = null, bool Internal = false);
public sealed record NotificationItem(string Id, string SourceId, string SourceName, string Title, string Body,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Count, bool Read, int Priority, bool Internal, string? NativeId = null);
public sealed record NotificationApplication(string SourceId, string Name, bool Enabled, int Priority);

/// <summary>Bounded local notification state. No platform reads or network calls.</summary>
public sealed class NotificationCenter
{
    public const int MaximumItems = 200;
    private const int MaximumBytes = 1024 * 1024;
    private readonly object gate = new();
    private readonly PortableStateFile stateFile;
    private readonly Func<DateTimeOffset> clock;
    private readonly Action<string, byte[]> publishDraft;
    private readonly List<NotificationItem> messages = new();
    private readonly Dictionary<string, NotificationApplication> applications = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Fingerprint, DateTimeOffset Seen)> nativeSeen = new(StringComparer.Ordinal);
    private bool persistHistory;
    private int pendingHistory = -1;
    private bool? pendingPersistHistory { get { int value = Volatile.Read(ref pendingHistory); return value < 0 ? null : value == 1; } set { Volatile.Write(ref pendingHistory, value is null ? -1 : value.Value ? 1 : 0); } }
    private string? lastError;
    private string? lastDraftBackupPath;
    private FilePublicationException? lastDraftBackupFailure;
    public event Action? Changed;
    public NotificationCenter(string storagePath, Func<DateTimeOffset>? clock = null) : this(storagePath, clock, null) { }
    internal NotificationCenter(string storagePath, Func<DateTimeOffset>? clock, PortableStateFile? persistence, Action<string, byte[]>? publishDraft = null)
    {
        stateFile = persistence ?? new PortableStateFile(Path.GetFullPath(storagePath ?? throw new ArgumentNullException(nameof(storagePath))), MaximumBytes);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.publishDraft = publishDraft ?? ((destination, bytes) => PortableFilePublication.WriteNew(destination, bytes));
        Reload();
    }
    public IReadOnlyList<NotificationItem> Items { get { lock (gate) return Ordered().ToArray(); } }
    public IReadOnlyList<NotificationApplication> Applications { get { lock (gate) return applications.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(); } }
    public int UnreadCount { get { lock (gate) return messages.Count(item => !item.Read); } }
    public bool PersistHistory { get { lock (gate) return persistHistory; } }
    public bool? PendingPersistHistory => pendingPersistHistory;
    // This immutable snapshot never waits behind a notification or filesystem operation.
    public StateFileStatus PersistenceStatus => stateFile.Status;
    public string? LastDraftBackupPath => Volatile.Read(ref lastDraftBackupPath);
    // Belongs to the latest reload attempt. Kept separate from the readable
    // state message so callers can show an unconfirmed publication's paths.
    public FilePublicationException? LastDraftBackupFailure => Volatile.Read(ref lastDraftBackupFailure);
    public string? LastError { get { lock (gate) return lastError; } }

    public bool Publish(NotificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string source = Text(input.SourceId, 160, false), name = Text(input.SourceName, 160, false);
        string title = Text(input.Title, 512, false), body = Text(input.Body, 6000, true);
        if (source.Length == 0 || (title.Length == 0 && body.Length == 0)) return false;
        if (name.Length == 0) name = source;
        if (title.Length == 0) title = Text(body, 512, false);
        string? native = Text(input.NativeId ?? "", 256, false); if (native.Length == 0) native = null;
        bool accepted = false;
        lock (gate)
        {
            DateTimeOffset now = clock(), received = input.ReceivedAt;
            if (received > now.AddMinutes(5) || received < now.AddYears(-1)) received = now;
            if (!applications.ContainsKey(source) && applications.Count >= 512) { lastError = "Notification application limit reached (512)."; return false; }
            bool applicationChanged = !applications.TryGetValue(source, out var app) || app.Name != name;
            app ??= new(source, name, true, 0);
            applications[source] = app with { Name = name };
            if (!app.Enabled) return false;
            string fingerprint = Fingerprint(source, title, body), nativeKey = source + "\n" + native;
            if (native is not null)
            {
                if (nativeSeen.TryGetValue(nativeKey, out var seen) && seen.Fingerprint == fingerprint)
                { nativeSeen[nativeKey] = (fingerprint, now); return false; }
                nativeSeen[nativeKey] = (fingerprint, now);
                foreach (string stale in nativeSeen.Where(pair => now - pair.Value.Seen > TimeSpan.FromHours(6)).Select(pair => pair.Key).ToArray()) nativeSeen.Remove(stale);
                while (nativeSeen.Count > 1024) nativeSeen.Remove(nativeSeen.OrderBy(pair => pair.Value.Seen).First().Key);
            }
            int existing = native is null ? -1 : messages.FindIndex(item => item.SourceId == source && item.NativeId == native);
            if (existing >= 0)
            {
                var previous = messages[existing];
                if (received < previous.CreatedAt) received = previous.CreatedAt;
                messages[existing] = previous with { SourceName = name, Title = title, Body = body, UpdatedAt = received, Read = false, Priority = app.Priority };
            }
            else
            {
                int duplicate = messages.FindIndex(item => item.SourceId == source && item.Title == title && item.Body == body &&
                    received >= item.UpdatedAt && received - item.UpdatedAt <= TimeSpan.FromSeconds(30));
                if (duplicate >= 0)
                {
                    var previous = messages[duplicate];
                    messages[duplicate] = previous with { UpdatedAt = received, Count = Math.Min(10000, previous.Count + 1), Read = false, Priority = app.Priority };
                }
                else messages.Add(new(Guid.NewGuid().ToString("N"), source, name, title, body, received, received, 1, false, app.Priority, input.Internal, native));
                while (messages.Count > MaximumItems) messages.Remove(messages.MinBy(item => item.UpdatedAt)!);
            }
            if (persistHistory || applicationChanged) SaveLocked(); accepted = true;
        }
        if (accepted) Changed?.Invoke();
        return accepted;
    }
    public void SetApplicationEnabled(string id, bool enabled)
    {
        lock (gate)
        {
            if (!applications.TryGetValue(id, out var app)) return;
            applications[id] = app with { Enabled = enabled };
            if (!enabled) messages.RemoveAll(item => item.SourceId == id);
            SaveLocked();
        }
        Changed?.Invoke();
    }
    public void SetApplicationPriority(string id, int priority)
    {
        if (priority is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(priority));
        lock (gate)
        {
            if (!applications.TryGetValue(id, out var app)) return;
            applications[id] = app with { Priority = priority };
            for (int index = 0; index < messages.Count; index++) if (messages[index].SourceId == id) messages[index] = messages[index] with { Priority = priority };
            SaveLocked();
        }
        Changed?.Invoke();
    }
    public void MarkRead(string id, bool read = true)
    {
        lock (gate) { int index = messages.FindIndex(item => item.Id == id); if (index < 0) return; messages[index] = messages[index] with { Read = read }; SaveLocked(); }
        Changed?.Invoke();
    }
    public void MarkAllRead()
    {
        lock (gate) { for (int index = 0; index < messages.Count; index++) messages[index] = messages[index] with { Read = true }; SaveLocked(); }
        Changed?.Invoke();
    }
    public void Clear() { lock (gate) { messages.Clear(); SaveLocked(); } Changed?.Invoke(); }
    public IReadOnlyList<NotificationItem> Filter(NotificationFilter filter)
    { lock (gate) return Ordered().Where(item => filter == NotificationFilter.All || (filter == NotificationFilter.Unread ? !item.Read : item.Priority >= 1)).ToArray(); }
    public void SetPersistHistory(bool enabled)
    {
        lock (gate)
        {
            pendingPersistHistory = enabled;
            // A failed enable must not start recording private bodies. A request
            // to disable takes effect immediately for future automatic writes,
            // even when removing the previous disk history still needs retry.
            if (!enabled) persistHistory = false;
            SaveLocked();
        }
        Changed?.Invoke();
    }
    public void Save() { lock (gate) SaveLocked(); }
    public bool RetrySave() { bool saved; lock (gate) saved = SaveLocked(); Changed?.Invoke(); return saved; }
    public bool RecoverAndSave() { bool saved; lock (gate) saved = SaveLocked(true); Changed?.Invoke(); return saved; }
    private IEnumerable<NotificationItem> Ordered() => messages.OrderByDescending(item => item.Priority).ThenByDescending(item => item.UpdatedAt);
    private sealed class Storage
    {
        public Storage() { }
        public int Version { get; set; } = 1;
        public bool PersistHistory { get; set; }
        public List<NotificationApplication> Applications { get; set; } = new();
        public List<NotificationItem> Items { get; set; } = new();
    }
    public bool Reload() => Reload(false);
    public bool Reload(bool backUpUnsaved)
    {
        bool loaded;
        lock (gate) loaded = ReloadLocked(backUpUnsaved);
        Changed?.Invoke(); return loaded;
    }
    private bool ReloadLocked(bool backUpUnsaved)
    {
        // Even a clean reload with no backup may fail while reading. Never let
        // that result inherit a previous attempt's publication uncertainty.
        Volatile.Write(ref lastDraftBackupFailure, null);
        if (backUpUnsaved && (stateFile.Status.Dirty || pendingPersistHistory is not null) && !BackUpDraftLocked()) return false;
        // Decode into a separate validated model. A failed explicit reload
        // must keep the current messages, rules, and pending privacy intent.
        Storage state = stateFile.Load(Decode, () => new Storage());
        bool loaded = !stateFile.Status.LoadFailed;
        if (loaded)
        {
            applications.Clear(); messages.Clear(); nativeSeen.Clear();
            foreach (var app in state.Applications) applications.Add(app.SourceId, app);
            persistHistory = state.PersistHistory; pendingPersistHistory = null;
            if (persistHistory) foreach (var item in state.Items)
            {
                if (!applications.TryGetValue(item.SourceId, out var app) || !app.Enabled) continue;
                messages.Add(item with { Priority = app.Priority });
                if (item.NativeId is not null) nativeSeen[item.SourceId + "\n" + item.NativeId] = (Fingerprint(item.SourceId, item.Title, item.Body), clock());
            }
            lastError = null;
        }
        else lastError = "Notification state could not be loaded; current edits remain in memory: " + stateFile.Status.Message;
        return loaded;
    }
    private bool BackUpDraftLocked()
    {
        try
        {
            // Backup does not activate a failed opt-in. Pending enablement is
            // still only an in-memory request until an actual save succeeds.
            bool history = persistHistory && pendingPersistHistory != false;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Storage { PersistHistory = history, Applications = applications.Values.ToList(), Items = history ? messages.ToList() : new() });
            // Draft rescue is not automatically loaded. It also rescues a bounded
            // in-memory history that is too large for the normal 1 MiB state file.
            if (bytes.Length > 8 * MaximumBytes) throw new InvalidDataException("Notification draft exceeds its backup limit.");
            if ((File.GetAttributes(Path.GetDirectoryName(stateFile.FilePath)!) & FileAttributes.ReparsePoint) != 0) throw new IOException("Notification draft storage may not be a symbolic link.");
            string destination = stateFile.FilePath + ".draft-" + Guid.NewGuid().ToString("N") + ".json";
            publishDraft(destination, bytes);
            Volatile.Write(ref lastDraftBackupPath, destination); return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            if (error is FilePublicationException publicationFailure) Volatile.Write(ref lastDraftBackupFailure, publicationFailure);
            string detail = error is FilePublicationException publication && publication.State == FilePublicationState.OutcomeUnknown
                ? "Notification draft backup could not be confirmed. Inspect: " + publication.TargetPath + (publication.RetainedTemporaryPath is null ? "" : " | " + publication.RetainedTemporaryPath)
                : "Notification draft backup failed; current edits were retained: " + error.Message;
            stateFile.ReportFailure(StateFileProblemCode.Io, detail); lastError = detail; return false;
        }
    }
    private static Storage Decode(byte[] bytes)
    {
        int offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        string json = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
        var state = JsonSerializer.Deserialize<Storage>(json) ?? throw new InvalidDataException("Invalid notification state.");
        if (state.Version != 1 || state.Applications is null || state.Items is null || state.Applications.Count > 512 || state.Items.Count > MaximumItems) throw new InvalidDataException("Invalid notification state limits.");
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in state.Applications)
            if (app is null || app.SourceId != Text(app.SourceId, 160, false) || app.SourceId.Length == 0 || app.Name != Text(app.Name, 160, false) || app.Priority is < 0 or > 2 || !sources.Add(app.SourceId)) throw new InvalidDataException("Invalid notification application.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in state.Items)
            if (item is null || !Guid.TryParseExact(item.Id, "N", out _) || item.Title != Text(item.Title, 512, false) || item.Title.Length == 0 || item.Body != Text(item.Body, 6000, true) || item.SourceId != Text(item.SourceId, 160, false) || item.SourceName != Text(item.SourceName, 160, false) || item.Count is < 1 or > 10000 || item.Priority is < 0 or > 2 || (item.NativeId is not null && (item.NativeId != Text(item.NativeId, 256, false) || item.NativeId.Length == 0)) || item.UpdatedAt < item.CreatedAt || !ids.Add(item.Id)) throw new InvalidDataException("Invalid notification item.");
        return state;
    }
    private bool SaveLocked(bool recover = false)
    {
        bool desiredHistory = pendingPersistHistory ?? persistHistory;
        try
        {
            if (applications.Count > 512) throw new InvalidDataException("Notification application limit reached (512).");
            var state = new Storage { PersistHistory = desiredHistory, Applications = applications.Values.ToList(), Items = desiredHistory ? messages.ToList() : new() };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state);
            if (bytes.Length > MaximumBytes)
            {
                const string message = "Notification history exceeds 1 MiB. Clear messages or turn off saved history.";
                stateFile.ReportFailure(StateFileProblemCode.FileTooLarge, message);
                lastError = "Notification changes remain in memory; saving failed: " + message; return false;
            }
            if (recover) stateFile.RecoverAndSave(bytes); else stateFile.Save(bytes);
            persistHistory = desiredHistory; pendingPersistHistory = null; lastError = null; return true;
        }
        catch (StateFileException error)
        {
            if (error.DraftCommitted) { persistHistory = desiredHistory; pendingPersistHistory = null; lastError = "Notification changes were saved; verify the reported recovery copy: " + error.Message; return true; }
            lastError = "Notification changes remain in memory; saving failed: " + error.Message;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { stateFile.ReportFailure(error is JsonException or ArgumentException or InvalidDataException ? StateFileProblemCode.InvalidData : StateFileProblemCode.Io, error.Message); lastError = "Notification changes remain in memory; saving failed: " + error.Message; }
        return false;
    }
    private static string Fingerprint(string source, string title, string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\n" + title + "\n" + body)));
    private static string Text(string? value, int limit, bool multiline)
    {
        value = value ?? ""; var builder = new StringBuilder(Math.Min(value.Length, limit));
        for (int index = 0; index < value.Length && builder.Length < limit; index++)
        {
            char character = value[index];
            if (char.IsHighSurrogate(character)) { if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]) && builder.Length + 2 <= limit) { builder.Append(character).Append(value[++index]); } continue; }
            if (char.IsLowSurrogate(character) || character == '\0') continue;
            if (char.IsControl(character) && !(multiline && character is '\n' or '\r' or '\t')) { if (!multiline && char.IsWhiteSpace(character)) builder.Append(' '); continue; }
            builder.Append(character);
        }
        string result = builder.ToString().Trim(); if (result.Length > 0 && char.IsHighSurrogate(result[^1])) result = result[..^1]; return result;
    }
}
