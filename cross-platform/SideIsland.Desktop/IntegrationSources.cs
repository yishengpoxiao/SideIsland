using CodexIsland;
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
    public Func<int[], CancellationToken, Task<PortQuerySnapshot>>? ReadPorts { get; init; }
    public Func<string, Task>? WritePortReport { get; init; }
}

internal sealed class CodexQuotaSession(string executable, string home) : IQuotaSession
{
    private readonly CodexClient client = new(executable, home);
    public Task<UsageSnapshot> ReadAsync(CancellationToken cancellationToken)
        => Task.Run(() => client.ReadLimits(12000), cancellationToken);
    public void Dispose() => client.Dispose();
}
