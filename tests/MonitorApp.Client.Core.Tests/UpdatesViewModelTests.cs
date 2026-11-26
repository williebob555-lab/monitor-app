using MonitorApp.Client.Core;
using MonitorApp.Client.Core.ViewModels;
using Xunit;

namespace MonitorApp.Client.Core.Tests;

/// <summary>
/// Install follows the service through its restart and then restarts the window on the new build (Wilson, 2026-11-24:
/// "it says it will restart, but it doesn't, and just randomly changes the version number").
/// </summary>
[Collection(UpdatesStatics.Name)]
public sealed class UpdatesViewModelTests
{
    [Fact]
    public async Task InstallWaitsThroughTheServiceRestartThenRestartsTheWindowOnTheNewBuild()
    {
        var source = new ScriptedUpdates("0.1.30");
        var restarter = new CountingRestarter("0.1.30");
        using var connection = new NodeConnection(source);
        var updates = new UpdatesViewModel(connection);
        using var _ = UpdatesStatics.Use(restarter, TimeSpan.FromMilliseconds(10));
        await updates.RefreshAsync();
        Assert.True(updates.CanInstall);
        Assert.False(updates.NeedsRestart);

        // After Install the service is gone for two looks, then answers on the new build.
        source.AfterApprove = new(["gone", "gone", "0.1.31"]);
        await updates.InstallCommand.ExecuteAsync(null);

        Assert.Equal(1, restarter.Restarts);
        Assert.Null(updates.ErrorMessage);
        Assert.Contains("Restarting this window", updates.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWindowLeftOnAnOlderBuildSaysSoAndOffersARestart()
    {
        var source = new ScriptedUpdates("0.1.31", staged: null);
        var restarter = new CountingRestarter("0.1.30");
        using var connection = new NodeConnection(source);
        var updates = new UpdatesViewModel(connection);
        using var _ = UpdatesStatics.Use(restarter, TimeSpan.FromMilliseconds(10));
        await updates.RefreshAsync();

        Assert.True(updates.NeedsRestart);
        Assert.Contains("This window is still 0.1.30", updates.InstalledText, StringComparison.Ordinal);
        updates.RestartAppCommand.Execute(null);
        Assert.Equal(1, restarter.Restarts);
    }

    [Fact]
    public async Task AFailedInstallSaysWhyInsteadOfWaiting()
    {
        var source = new ScriptedUpdates("0.1.30");
        using var connection = new NodeConnection(source);
        var updates = new UpdatesViewModel(connection);
        using var _ = UpdatesStatics.Use(new CountingRestarter("0.1.30"), TimeSpan.FromMilliseconds(10));
        await updates.RefreshAsync();

        source.AfterApprove = new(["gone", "failed"]);
        await updates.InstallCommand.ExecuteAsync(null);

        Assert.Contains("did not work", updates.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("health check timed out", updates.ErrorMessage, StringComparison.Ordinal);
    }

    private sealed class CountingRestarter(string running) : IAppRestarter
    {
        public int Restarts { get; private set; }

        public string? RunningVersion => running;

        public bool Restart()
        {
            Restarts++;
            return true;
        }
    }

    private sealed class ScriptedUpdates(string installed, string? staged = "0.1.31") : IPeerDataSource, ILocalUpdatesSource
    {
        private bool approved;

        public Queue<string> AfterApprove { get; set; } = new();

        public string Description => "scripted";

        public bool SupportsLiveUpdates => false;

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public Task<LocalUpdateStatusDto> GetLocalUpdateStatusAsync(CancellationToken ct = default)
        {
            if (!approved)
            {
                return Task.FromResult(staged is null
                    ? new LocalUpdateStatusDto("approve", installed, "Applied", installed, null, DateTimeOffset.UtcNow, null, false)
                    : new LocalUpdateStatusDto("approve", installed, "Staged", staged, null, DateTimeOffset.UtcNow, null, true));
            }

            return AfterApprove.Count == 0 ? Task.FromResult(Applying()) : AfterApprove.Dequeue() switch
            {
                "applying" => Task.FromResult(Applying()),
                "gone" => throw new NodeUnreachableException(new Uri("http://127.0.0.1:55432/"), new HttpRequestException("refused")),
                "failed" => Task.FromResult(new LocalUpdateStatusDto("approve", installed, "RolledBack", staged, "The health check timed out.", DateTimeOffset.UtcNow, null, false)),
                var version => Task.FromResult(new LocalUpdateStatusDto("approve", version, "Applied", version, null, DateTimeOffset.UtcNow, null, false)),
            };
        }

        private LocalUpdateStatusDto Applying() => new("approve", installed, "Applying", staged, null, DateTimeOffset.UtcNow, null, false);

        public Task<LocalUpdateStatusDto> CheckForUpdatesAsync(CancellationToken ct = default) => GetLocalUpdateStatusAsync(ct);

        public Task ApproveUpdateAsync(string targetVersion, CancellationToken ct = default)
        {
            approved = true;
            AfterApprove = new Queue<string>(["applying", .. AfterApprove]);
            return Task.CompletedTask;
        }

        public Task<RuntimeDto> GetRuntimeAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TrustedPeerDto>> GetPeersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TrustedPeerDto>>([]);

        public Task<IReadOnlyList<PendingPairingDto>> GetPairingsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PendingPairingDto>>([]);

        public Task<CatalogDto> GetCatalogAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StateDto> GetStateAsync(
            DateTimeOffset? asOf = null, int? lookbackSeconds = null, string? subjectEntityId = null, string? signalId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<HistoryResult> GetHistoryAsync(HistoryQuery query, int maxPages = 20, int pageSize = 1000, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ApprovePairingAsync(string pairingId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RejectPairingAsync(string pairingId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RevokePeerAsync(string peerId, CancellationToken ct = default) => Task.CompletedTask;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UpdatesStatics
{
    public const string Name = "updates statics";

    public static IDisposable Use(IAppRestarter restarter, TimeSpan interval)
    {
        var before = (UpdatesViewModel.Restarter, UpdatesViewModel.InstallFollowInterval);
        UpdatesViewModel.Restarter = restarter;
        UpdatesViewModel.InstallFollowInterval = interval;
        return new Restore(() => (UpdatesViewModel.Restarter, UpdatesViewModel.InstallFollowInterval) = before);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
