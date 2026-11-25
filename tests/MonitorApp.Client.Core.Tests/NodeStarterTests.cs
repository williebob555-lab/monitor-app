using MonitorApp.Client.Core;
using MonitorApp.Client.Core.ViewModels;
using Xunit;

namespace MonitorApp.Client.Core.Tests;

/// <summary>
/// Windows gave up on the service during a slow boot and never retried (2026-11-25): the window says the service is not
/// running, offers to start it, and loads again once it runs.
/// </summary>
[Collection(UpdatesStatics.Name)]
public sealed class NodeStarterTests
{
    [Fact]
    public async Task AStoppedServiceIsNamedAndCanBeStartedFromTheWindow()
    {
        var starter = new FakeStarter();
        var before = SectionViewModel.NodeStarter;
        SectionViewModel.NodeStarter = starter;
        try
        {
            using var connection = new NodeConnection(new ProbeSource());
            var section = new ProbeSection(connection) { Down = true };
            await section.RefreshAsync();
            Assert.True(section.NodeDown);
            Assert.True(section.CanStartNode);
            Assert.Contains("isn't running", section.ErrorMessage, StringComparison.Ordinal);

            starter.OnStart = () => section.Down = false;
            await section.StartNodeCommand.ExecuteAsync(null);

            Assert.Equal(1, starter.Starts);
            Assert.False(section.NodeDown);
            Assert.False(section.CanStartNode);
            Assert.Null(section.ErrorMessage);
            Assert.Equal(1, section.Loads);
        }
        finally
        {
            SectionViewModel.NodeStarter = before;
        }
    }

    [Fact]
    public async Task DecliningTheWindowsPromptSaysWhereElseToStartIt()
    {
        var before = SectionViewModel.NodeStarter;
        SectionViewModel.NodeStarter = new FakeStarter { Result = false };
        try
        {
            using var connection = new NodeConnection(new ProbeSource());
            var section = new ProbeSection(connection) { Down = true };
            await section.RefreshAsync();
            await section.StartNodeCommand.ExecuteAsync(null);
            Assert.True(section.NodeDown);
            Assert.Contains("Windows Services", section.ErrorMessage, StringComparison.Ordinal);
        }
        finally
        {
            SectionViewModel.NodeStarter = before;
        }
    }

    private sealed class FakeStarter : INodeServiceStarter
    {
        public int Starts { get; private set; }

        public bool Result { get; init; } = true;

        public Action? OnStart { get; set; }

        public Task<bool> StartAsync(CancellationToken cancellationToken = default)
        {
            Starts++;
            OnStart?.Invoke();
            return Task.FromResult(Result);
        }
    }

    private sealed class ProbeSection(NodeConnection connection) : SectionViewModel(connection, "Probe")
    {
        public bool Down { get; set; }

        public int Loads { get; private set; }

        public override Task RefreshAsync(CancellationToken cancellationToken = default) => RunAsync(() =>
        {
            if (Down)
            {
                throw new NodeUnreachableException(new Uri("http://127.0.0.1:55432/"), new HttpRequestException("refused"));
            }

            Loads++;
            return Task.CompletedTask;
        });
    }

    private sealed class ProbeSource : IPeerDataSource
    {
        public string Description => "probe";

        public bool SupportsLiveUpdates => false;

        public event EventHandler? StateChanged { add { } remove { } }

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
