using CodexIsland;

namespace SideIsland.Desktop;

public sealed partial class IslandWindow
{
    private CancellationTokenSource? mediaReadRequest, quotaReadRequest;
    private readonly HashSet<CancellationTokenSource> mediaCommands = new();
    private long mediaConnection;

    private Task ConnectMedia()
    {
        if (closing) return Task.CompletedTask;
        StopMediaRequests(); prefs.MediaEnabled = true; media = null; Save(); RefreshCompact();
        if (mediaTitle is not null) { mediaTitle.Text = T("正在连接播放器…", "Connecting to player…"); mediaArtist.Text = ""; }
        return ReadMedia();
    }
    private void DisconnectMedia()
    {
        prefs.MediaEnabled = false; StopMediaRequests(); media = null; Save(); RenderPage(); RefreshCompact();
    }
    private Task ConnectQuota(string executable, string home)
    {
        if (closing) return Task.CompletedTask;
        StopQuotaRequests(); prefs.CodexPath = executable; prefs.CodexHome = home; prefs.CodexEnabled = true; quota = null; Save(); RefreshCompact();
        if (quotaText is not null) quotaText.Text = QuotaValue(false) + "\n\n" + QuotaValue(true);
        return ReadQuota();
    }
    private void DisconnectQuota()
    {
        prefs.CodexEnabled = false; StopQuotaRequests(); quota = null; Save(); RenderPage(); RefreshCompact();
    }
    private void StopMediaRead()
    {
        var previous = mediaReadRequest; mediaReadRequest = null; readingMedia = false; CancelIntegration(previous);
    }
    private void StopMediaRequests()
    {
        mediaConnection++; StopMediaRead();
        foreach (var request in mediaCommands.ToArray()) CancelIntegration(request);
    }
    private void StopQuotaRequests()
    {
        var previous = quotaReadRequest; quotaReadRequest = null; readingQuota = false;
        var client = codex; codex = null; CancelIntegration(previous);
        try { client?.Dispose(); }
        catch (Exception error) { if (!testMode) DiagnosticLog.Write("quota-dispose-failed", error); }
    }
    private void CancelIntegration(CancellationTokenSource? request)
    {
        // Each awaiting operation owns disposal. Invalidating it first keeps a
        // synchronously resumed continuation from touching the next request.
        try { request?.Cancel(); }
        catch (AggregateException error) { if (!testMode) DiagnosticLog.Write("integration-cancel-failed", error); }
    }
    private async Task ReadMedia(bool refresh = false)
    {
        if (closing || !prefs.MediaEnabled || (testMode && !injectedIntegrations)) return;
        if (readingMedia && !refresh) return;
        if (refresh) StopMediaRead();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        mediaReadRequest = request; readingMedia = true; nextMedia = DateTime.UtcNow.AddSeconds(2);
        bool Current() => !closing && prefs.MediaEnabled && !request.IsCancellationRequested && ReferenceEquals(mediaReadRequest, request);
        try
        {
            var value = await integrations.ReadMedia(request.Token);
            if (!Current()) return;
            media = value;
            if (mediaTitle is not null) { mediaTitle.Text = media.Available ? media.Title : T("没有可读取的播放会话", "No readable player session"); mediaArtist.Text = media.Available ? media.Artist + " · " + media.Player : media.Error ?? ""; }
            RefreshCompact();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (Current()) { if (!testMode) DiagnosticLog.Write("media-read-failed", error); Report(T("读取播放信息失败，请重试连接。", "Could not read playback information. Try connecting again.")); } }
        finally { if (ReferenceEquals(mediaReadRequest, request)) { mediaReadRequest = null; readingMedia = false; } }
    }
    private async Task MediaAction(string action)
    {
        if (closing || !prefs.MediaEnabled || (testMode && !injectedIntegrations)) return;
        long connection = mediaConnection;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); mediaCommands.Add(request);
        bool Current() => !closing && prefs.MediaEnabled && connection == mediaConnection && !request.IsCancellationRequested;
        try
        {
            var error = await integrations.ControlMedia(action, request.Token);
            if (!Current()) return;
            if (error is not null) Report(error);
            // A pre-command metadata request may still be in flight. Its reply
            // must not overwrite the state read after this command completed.
            await ReadMedia(refresh: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (Current()) { if (!testMode) DiagnosticLog.Write("media-control-failed", error); Report(T("播放控制失败，请重新连接播放器。", "Playback control failed. Reconnect the player.")); } }
        finally { mediaCommands.Remove(request); }
    }
    private async Task ReadQuota()
    {
        if (readingQuota || closing || !prefs.CodexEnabled || (testMode && !injectedIntegrations)) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        quotaReadRequest = request; readingQuota = true; nextQuota = DateTime.UtcNow.AddSeconds(60);
        bool Current() => !closing && prefs.CodexEnabled && !request.IsCancellationRequested && ReferenceEquals(quotaReadRequest, request);
        try
        {
            // Capture the session before scheduling work. Reconnect replaces
            // the field, but cannot redirect this request to the new client.
            var client = codex ??= integrations.CreateQuota(prefs.CodexPath, prefs.CodexHome);
            var value = await client.ReadAsync(request.Token);
            if (!Current() || !ReferenceEquals(codex, client)) return;
            quota = value;
            if (quotaText is not null) quotaText.Text = QuotaValue(false) + "\n\n" + QuotaValue(true);
            RefreshCompact();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (Current()) Report(error is CodexClientException ? UiLocalization.Localize(error.Message) : T("读取 Codex 额度失败，请检查连接设置。", "Could not read Codex usage. Check connection settings.")); }
        finally { if (ReferenceEquals(quotaReadRequest, request)) { quotaReadRequest = null; readingQuota = false; } }
    }
}
