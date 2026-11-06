using CodexIsland;
using SideIsland.Core;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Desktop;

internal interface IQuotaSession : IDisposable
{
    Task<UsageSnapshot> ReadAsync(CancellationToken cancellationToken);
}

internal sealed record IntegrationSources(
    Func<CancellationToken, Task<MediaSnapshot>> ReadMedia,
    Func<string, CancellationToken, Task<string?>> ControlMedia,
    Func<string, string, IQuotaSession> CreateQuota)
{
    public PreferenceStore? PreferenceData { get; init; }
    public Func<string, NotificationCenter>? CreateNotificationData { get; init; }
    public Func<string, Task>? BeforeDataOperation { get; init; }
    public Func<int[], CancellationToken, Task<PortQuerySnapshot>>? ReadPorts { get; init; }
    public Func<string, Task>? WritePortReport { get; init; }
    public Func<string, Task<string?>>? ChooseChecksumDestination { get; init; }
    public Func<string, string, CancellationToken, Task>? SaveChecksumManifest { get; init; }
    public Func<string, Task>? WriteChecksumText { get; init; }
    public Func<string, Task<string?>>? ChooseDiffDestination { get; init; }
    public Func<string, string, CancellationToken, Task<PortablePatchTarget>>? PrepareDiffPatch { get; init; }
    public Func<PortablePatchTarget, CancellationToken, Task>? SaveDiffPatch { get; init; }
    public Func<string, Task>? WriteDiffText { get; init; }
    public Func<Task<IReadOnlyList<string>>>? ChooseBatchChecksumFiles { get; init; }
    public Func<IList<string>, string, string, CancellationToken, IProgress<BatchHashProgress>?, Task<BatchHashResult>>? ComputeBatchChecksums { get; init; }
    public Func<string, Task<string?>>? ChooseBatchChecksumDestination { get; init; }
    public Func<CancellationToken, Task<string?>>? ReadClipboardText { get; init; }
    public Func<string, Task>? WriteClipboardText { get; init; }
    public Func<string, Task<string?>>? ChooseQrDestination { get; init; }
    public Func<string, byte[], CancellationToken, Task>? SaveQrPng { get; init; }
    public Func<Task<string?>>? ChooseLogPath { get; init; }
    public Func<string, Task<string?>>? ChooseLogDestination { get; init; }
    public Func<string, byte[], CancellationToken, Task>? SaveLogSnapshot { get; init; }
    public Func<IReadOnlyList<PortableWorkspaceResource>, CancellationToken, Task<WorkspaceLaunchResult>>? LaunchWorkspace { get; init; }
    public Func<PortableWorkspaceStore, PortableWorkspaceSnapshot, Task>? SaveWorkspace { get; init; }
    public Func<string, Task<string?>>? ChooseWorkspaceTarget { get; init; }
    public ScreenPinSources? ScreenPins { get; init; }
    public Func<bool, TimeSpan, CancellationToken, Task<string?>>? SetAwake { get; init; }
    public Func<AwakeSnapshot>? ReadAwake { get; init; }
    public Func<CancellationToken, Task<VolumeSnapshot>>? ReadVolume { get; init; }
    public Func<int, bool, CancellationToken, Task<string?>>? SetVolume { get; init; }
}

internal sealed class CodexQuotaSession(string executable, string home) : IQuotaSession
{
    private readonly CodexClient client = new(executable, home);
    public Task<UsageSnapshot> ReadAsync(CancellationToken cancellationToken)
        => Task.Run(() => client.ReadLimits(12000), cancellationToken);
    public void Dispose() => client.Dispose();
}
