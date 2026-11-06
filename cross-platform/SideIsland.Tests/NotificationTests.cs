#nullable enable
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SideIsland.Core;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Tests;

/// <summary>Fixtures and synthetic loopback requests only; no personal notification reads.</summary>
internal static class NotificationTests
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "side-island-notification-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            StateTests(directory); PersistenceTests(directory); BoundsTests(directory); NotificationPersistenceTests.Run(); DbusFixtureTests(); MacClassifierFixtures();
            HttpFixtureTests(directory).GetAwaiter().GetResult();
            Console.WriteLine("PASS: notification model, persistence, D-Bus image-data fixtures and real localhost HTTP feed using synthetic messages only; macOS AX and Linux desktop notifications were not exercised.");
        }
        finally
        {
            string target = Path.GetFullPath(directory), temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (target.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(target).StartsWith("side-island-notification-fixture-", StringComparison.Ordinal)) Directory.Delete(target, true);
        }
    }
    private static void StateTests(string directory)
    {
        DateTimeOffset now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var center = new NotificationCenter(Path.Combine(directory, "model.json"), () => now); int changes = 0; center.Changed += () => changes++;
        Check(!center.Publish(new("", "", "empty source", "", now)), "empty source rejected");
        Check(center.Publish(new("fixture.a", "Fixture A", "任务完成", "合成测试消息", now)), "first fixture accepted");
        string first = center.Items.Single().Id;
        Check(center.UnreadCount == 1 && center.Applications.Single().Priority == 0 && center.Filter(NotificationFilter.Important).Count == 0, "new applications default to normal priority");
        center.MarkRead(first); Check(center.Filter(NotificationFilter.Unread).Count == 0, "mark read updates unread filter");
        now += TimeSpan.FromSeconds(10); center.Publish(new("fixture.a", "Fixture A", "任务完成", "合成测试消息", now));
        Check(center.Items.Count == 1 && center.Items[0].Id == first && center.Items[0].Count == 2 && !center.Items[0].Read, "30 second duplicate window merges and reopens unread");
        now += TimeSpan.FromSeconds(31); center.Publish(new("fixture.a", "Fixture A", "任务完成", "合成测试消息", now));
        Check(center.Items.Count == 2, "later duplicate creates new item");
        center.Publish(new("fixture.b", "Fixture B", "important", "synthetic", now, "native:1"));
        center.SetApplicationPriority("fixture.b", 1);
        Check(center.Items[0].SourceId == "fixture.b" && center.Filter(NotificationFilter.Important).Count == 1, "important priority is included and sorted first");
        center.SetApplicationPriority("fixture.b", 2); Check(center.Items[0].Priority == 2, "highest priority persists in existing items");
        center.MarkRead(center.Items[0].Id); int before = changes;
        Check(!center.Publish(new("fixture.b", "Fixture B", "important", "synthetic", now, "native:1")) && center.Items[0].Read && changes == before, "native repeated polling does not reset read state or emit changes");
        center.Publish(new("fixture.b", "Fixture B", "updated", "changed", now - TimeSpan.FromSeconds(5), "native:1"));
        Check(center.Items[0].Title == "updated" && center.Items[0].Count == 1 && center.Items[0].UpdatedAt >= center.Items[0].CreatedAt, "native update replaces contents and guards backwards clocks");
        center.MarkAllRead(); Check(center.UnreadCount == 0, "mark all read");
        center.SetApplicationEnabled("fixture.a", false);
        Check(center.Items.All(item => item.SourceId != "fixture.a") && !center.Publish(new("fixture.a", "Fixture A", "blocked", "synthetic", now)), "application disabling removes and rejects messages");
        center.Publish(new("internal.fixture", "Side Island", "内部提醒", "Synthetic internal fixture", now, Internal: true));
        Check(center.Items.Any(item => item.Internal), "internal reminders share the center");
        center.Clear(); Check(center.Items.Count == 0 && center.Applications.Count == 3 && !center.Applications.Single(app => app.SourceId == "fixture.a").Enabled, "clear preserves application policies");
        Check(!center.Publish(new("fixture.b", "Fixture B", "updated", "changed", now, "native:1")), "clear cannot resurrect an unchanged visible native card");
    }
    private static void PersistenceTests(string directory)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow; string path = Path.Combine(directory, "persistence.json");
        var center = new NotificationCenter(path);
        const string secret = "FIXTURE_PRIVATE_BODY_6bc62f8d";
        center.Publish(new("synthetic", "Synthetic fixture", "Private fixture", secret, now, "synthetic-1"));
        Check(File.Exists(path) && !File.ReadAllText(path).Contains(secret, StringComparison.Ordinal), "history is absent on disk by default");
        center.SetApplicationPriority("synthetic", 1); center.SetPersistHistory(true); center.MarkAllRead();
        var restored = new NotificationCenter(path);
        Check(restored.PersistHistory && restored.Items.Count == 1 && restored.Items[0].Body == secret && restored.Items[0].Read && restored.Items[0].Priority == 1 && restored.LastError is null, "opt-in history and policy roundtrip");
        restored.SetPersistHistory(false);
        Check(!File.ReadAllText(path).Contains(secret, StringComparison.Ordinal) && new NotificationCenter(path).Items.Count == 0, "history disabling removes message contents from state file");
        restored.SetApplicationEnabled("synthetic", false); restored.Clear();
        Check(!new NotificationCenter(path).Applications.Single().Enabled, "clear and restart retain disabled applications");
        string corrupt = Path.Combine(directory, "corrupt.json"); File.WriteAllText(corrupt, "{invalid JSON fixture");
        var recovered = new NotificationCenter(corrupt); Check(recovered.LastError is not null && recovered.Items.Count == 0, "corrupt state reports load failure");
        recovered.Save(); Check(recovered.LastError is not null && File.ReadAllText(corrupt) == "{invalid JSON fixture", "ordinary save cannot replace an unreadable fallback");
        Check(recovered.RecoverAndSave() && recovered.LastError is null && Directory.GetFiles(directory, "corrupt.json.unreadable-*.bak").Length == 1, "explicit recovery preserves corrupt state before committing current data");
        string malformed = Path.Combine(directory, "malformed.json"); File.WriteAllText(malformed, "{\"Version\":1,\"PersistHistory\":true,\"Applications\":null,\"Items\":[]}");
        Check(new NotificationCenter(malformed).LastError is not null, "null collection rejected without crashing");
        string oversized = Path.Combine(directory, "oversized.json"); File.WriteAllText(oversized, new string(' ', 1024 * 1024 + 1));
        Check(new NotificationCenter(oversized).LastError is not null, "oversized persisted state is bounded");
        string blocked = Path.Combine(directory, "directory-instead-of-file.json"); Directory.CreateDirectory(blocked);
        var failed = new NotificationCenter(blocked); failed.SetPersistHistory(true);
        Check(failed.LastError is not null && !failed.PersistHistory, "failed disk save does not falsely enable history persistence");
    }
    private static void BoundsTests(string directory)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow; var center = new NotificationCenter(Path.Combine(directory, "bounds.json"), () => now);
        for (int index = 0; index < NotificationCenter.MaximumItems + 7; index++) center.Publish(new("fixture", "Fixture", "Synthetic " + index.ToString(CultureInfo.InvariantCulture), "generated body", now + TimeSpan.FromMilliseconds(index)));
        Check(center.Items.Count == NotificationCenter.MaximumItems && center.Items.All(item => item.Title != "Synthetic 0"), "oldest messages evicted at bounded capacity");
        now += TimeSpan.FromSeconds(1);
        center.Publish(new("emoji", "中文 Fixture", new string('a', 511) + "😀", string.Concat(Enumerable.Repeat("中😀", 2500)) + "\0\uD800", now));
        var item = center.Items.First(row => row.SourceId == "emoji");
        Check(item.Title.Length <= 512 && item.Body.Length <= 6000 && !char.IsHighSurrogate(item.Title[^1]) && !char.IsHighSurrogate(item.Body[^1]) && !item.Body.Contains('\0'), "length limits preserve surrogate pairs and sanitize invalid text");
    }
    private const string NotifyFixture = """
method call time=1234.000 sender=:1.42 -> destination=org.freedesktop.Notifications serial=17 path=/org/freedesktop/Notifications; interface=org.freedesktop.Notifications; member=Notify
   string "Fixture 消息"
   uint32 42
   string "fixture-icon"
   string "Build \"ready\""
   string "<b>合成通知</b> &amp; safe\nsecond line"
   array [
      string "default"
      string "Open"
   ]
   array [
      dict entry(
         string "image-data"
         variant             struct {
               int32 1
               int32 1
               int32 4
               boolean true
               int32 8
               int32 4
               array of bytes [ 00 00 00 ff ]
            }
      )
      dict entry(
         string "desktop-entry"
         variant             string "org.sideisland.SyntheticFixture"
      )
   ]
   int32 -1
""";
    private static void DbusFixtureTests()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow; var parser = new NotificationBridge.DbusParser(); NotificationInput? output = null;
        foreach (string line in NotifyFixture.Split('\n'))
        {
            var parsed = parser.Push(line.TrimEnd('\r'), now);
            if (!line.StartsWith("   int32 ", StringComparison.Ordinal)) Check(parsed is null, "nested image-data int32 cannot complete a message");
            if (parsed is not null) { Check(output is null, "one D-Bus result per record"); output = parsed; }
        }
        Check(output is not null && output.SourceName == "Fixture 消息" && output.Title == "Build \"ready\"" && output.Body == "合成通知 & safe\nsecond line" && output.NativeId == "replacement:42", "structured D-Bus fixture decoded");
        var renamed = NotificationBridge.TryParseDbusNotify(NotifyFixture.Replace("Fixture 消息", "Renamed fixture", StringComparison.Ordinal), now);
        Check(renamed?.SourceId == output!.SourceId, "desktop-entry after nested image data establishes stable identity");
        Check(NotificationBridge.TryParseDbusNotify(NotifyFixture.Replace("   int32 -1", "", StringComparison.Ordinal), now) is null && NotificationBridge.TryParseDbusNotify(NotifyFixture.Replace("member=Notify", "member=CloseNotification", StringComparison.Ordinal), now) is null, "partial and unrelated records ignored");
        var noReplace = NotificationBridge.TryParseDbusNotify(NotifyFixture.Replace("   uint32 42", "   uint32 0", StringComparison.Ordinal), now);
        Check(noReplace?.NativeId is null, "unreturned server ID is not invented");
        var escaped = NotificationBridge.TryParseDbusNotify(NotifyFixture.Replace("Fixture 消息", @"Fixture \344\270\255", StringComparison.Ordinal), now);
        Check(escaped?.SourceName == "Fixture 中", "octal-escaped UTF-8 bytes decode as Unicode, not Latin-1");
        Check(NotificationBridge.TryParseDbusNotify(NotifyFixture.Replace("Fixture 消息", @"Fixture \377", StringComparison.Ordinal), now) is null, "invalid UTF-8 escape sequence is rejected");
    }
    private static void MacClassifierFixtures()
    {
        Check(NotificationBridge.IsMacNotificationCard("AXGroup", "com.apple.notificationcenterui.NotificationEntry", "notification", 3), "synthetic single card with NotificationCenter bundle prefix is retained");
        Check(NotificationBridge.IsMacNotificationCard("AXButton", "com.apple.notificationcenter.NotificationCard", "通知", 2), "synthetic Chinese card is recognized");
        Check(!NotificationBridge.IsMacNotificationCard("AXWindow", "com.apple.notificationcenterui.NotificationEntry", "notification", 3), "AXWindow is a container, not a card");
        Check(!NotificationBridge.IsMacNotificationCard("AXGroup", "com.apple.notificationcenterui", "group", 3) && !NotificationBridge.IsMacNotificationCard("AXGroup", "NotificationCenter AXGroup", "group", 3), "explicit root identifiers excluded without prefix exclusion");
        Check(!NotificationBridge.IsMacNotificationCard("AXGroup", "NotificationViewController", "notification", 3) && !NotificationBridge.IsMacNotificationCard("AXGroup", "notificationEntry", "notification", 1), "controller roots and insufficient text are excluded");
    }
    private static async Task HttpFixtureTests(string directory)
    {
        var center = new NotificationCenter(Path.Combine(directory, "http.json")); using var bridge = new NotificationBridge(center);
        Check(!bridge.IsHttpEnabled && bridge.Token.Length == 0, "local feed starts disabled without a token");
        Check(await bridge.SetHttpAsync(true, 0, default) is null, "synthetic loopback test bridge starts");
        var endpoint = new Uri(bridge.Url); string access = bridge.Token;
        string body = JsonSerializer.Serialize(new { sourceId = "synthetic.test", sourceName = "Synthetic localhost fixture", title = "Generated HTTP fixture", body = "No personal notifications were read", nativeId = "http-fixture-1" });
        Check((await Post(endpoint, null, body, "", false)).Status == 401 && center.Items.Count == 0, "missing token is rejected");
        Check((await Post(endpoint, access, body)).Text.Contains("accepted", StringComparison.Ordinal) && center.Items.Single().SourceId == "http:synthetic.test", "real TCP/HTTP request reaches production notification model");
        center.MarkAllRead(); Check((await Post(endpoint, access, body)).Text.Contains("ignored", StringComparison.Ordinal) && center.Items.Count == 1 && center.UnreadCount == 0, "feed native duplicate does not create another unread message");
        center.SetApplicationEnabled("http:synthetic.test", false);
        Check((await Post(endpoint, access, body.Replace("http-fixture-1", "http-fixture-2", StringComparison.Ordinal))).Text.Contains("ignored", StringComparison.Ordinal) && center.Items.Count == 0, "disabled application cannot enter via HTTP");
        Check((await Post(endpoint, access, body, "Origin: https://example.invalid\r\n", false)).Status == 400, "browser-origin requests are rejected");
        Check((await Post(endpoint, access, body, "", false, 16001)).Status == 400, "oversized body rejected before reading it");
        Check((await Post(endpoint, access, "{invalid fixture")).Status == 400, "malformed JSON rejected");
        Check((await Post(endpoint, access, body, "Content-Length: 1\r\n", false)).Status == 400, "duplicate content-length rejected");
        Check(await bridge.SetHttpAsync(false, 0, default) is null && !bridge.IsHttpEnabled && bridge.Token.Length == 0 && bridge.Url.Length == 0, "pause invalidates token and endpoint");
        int stoppedCount = center.Items.Count;
        Check(await Refused(endpoint), "old listener refuses or does not establish a connection within the test deadline");
        Check(center.Items.Count == stoppedCount, "stopped feed has not published a message");
        // Rebind+listen proves the old listener released the endpoint, independently
        // of WFP/firewall DROP behavior. Keep it occupied to force a different port.
        using var oldPortReservation = ReserveReleased(endpoint);
        Check(await bridge.SetHttpAsync(true, 0, default) is null, "local feed can be enabled again");
        var newEndpoint = new Uri(bridge.Url); string newAccess = bridge.Token;
        Check(newEndpoint.Port != endpoint.Port && newAccess != access && (await Post(newEndpoint, access, body, "", false)).Status == 401, "old token is invalid on a new endpoint");
        center.SetApplicationEnabled("http:synthetic.test", true);
        string next = body.Replace("http-fixture-1", "http-fixture-3", StringComparison.Ordinal);
        Check((await Post(newEndpoint, newAccess, next)).Text.Contains("accepted", StringComparison.Ordinal), "new token works after restart");
        bridge.Dispose(); Check(!bridge.IsHttpEnabled && bridge.Token.Length == 0 && bridge.Url.Length == 0 && await Refused(newEndpoint), "disposing stops owned HTTP listener");
        using var disposedPortReservation = ReserveReleased(newEndpoint);
    }
    private static async Task<(int Status, string Text)> Post(Uri endpoint, string? token, string body, string extra = "", bool sendBody = true, int? declaredLength = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port, deadline.Token); using var stream = client.GetStream(); byte[] payload = Encoding.UTF8.GetBytes(body);
        string headers = "POST /notifications HTTP/1.1\r\nHost: 127.0.0.1:" + endpoint.Port.ToString(CultureInfo.InvariantCulture) + "\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + (declaredLength ?? payload.Length).ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n" + (token is null ? "" : "Authorization: Bearer " + token + "\r\n") + extra + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), deadline.Token); if (sendBody) await stream.WriteAsync(payload, deadline.Token);
        using var response = new MemoryStream(); var buffer = new byte[1024];
        try { while (response.Length < 8192) { int count = await stream.ReadAsync(buffer, deadline.Token); if (count == 0) break; response.Write(buffer, 0, count); } }
        catch (IOException) when (response.Length > 0) { }
        string text = Encoding.UTF8.GetString(response.ToArray()); string[] words = text.Split(' ', 3);
        Check(words.Length >= 2 && int.TryParse(words[1], out _), "feed returns an HTTP status line"); return (int.Parse(words[1], CultureInfo.InvariantCulture), text);
    }
    private static async Task<bool> Refused(Uri endpoint)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2)); using var client = new TcpClient();
        try { await client.ConnectAsync(IPAddress.Loopback, endpoint.Port, deadline.Token); return false; }
        catch (SocketException) { return true; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return true; }
    }
    private sealed class PortReservation(TcpListener listener) : IDisposable { public void Dispose() => listener.Stop(); }
    private static IDisposable ReserveReleased(Uri endpoint)
    {
        var reservation = new TcpListener(IPAddress.Loopback, endpoint.Port);
        // On Unix TIME_WAIT sockets may remain after legitimate disconnects;
        // REUSEADDR still cannot listen alongside a live listener without REUSEPORT.
        if (OperatingSystem.IsWindows()) reservation.ExclusiveAddressUse = true;
        else reservation.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try { reservation.Start(1); return new PortReservation(reservation); }
        catch { reservation.Stop(); throw new InvalidOperationException("Notification fixture: stopped bridge did not release its listening endpoint."); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Notification fixture: " + message); }
}
