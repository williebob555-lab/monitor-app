using MonitorApp.Client.Core;
using MonitorApp.Client.Core.ViewModels;
using Xunit;

namespace MonitorApp.Client.Core.Tests;

public sealed class ViewModelTests
{
    private static readonly DateTimeOffset Now = NodeFixture.Now;

    private static NodeConnection Connect(NodeFixture node) => new(node.Address);

    [Fact]
    public async Task OverviewShowsIdentityFingerprintAndTrustStates()
    {
        await using var node = await NodeFixture.CreateAsync();
        using var connection = Connect(node);
        var overview = new OverviewViewModel(connection);

        await overview.RefreshAsync();

        Assert.Null(overview.ErrorMessage);
        Assert.Equal("peer-local", overview.LocalPeerId);
        Assert.Equal(node.LocalFingerprint, overview.LocalFingerprint);
        Assert.Equal(1, overview.PendingPairingCount);
        var peer = Assert.Single(overview.Peers);
        Assert.Equal("Trusted workstation", peer.Name);
        Assert.Equal("Active", peer.TrustState);
    }

    [Fact]
    public async Task CurrentStateNeverShowsUnavailableReadingsAsZero()
    {
        await using var node = await NodeFixture.CreateAsync();
        await node.AddNumberAsync(Now.AddMinutes(-1), 0.5, signal: "memory.used");
        // The unavailable payload carries a numeric zero; the table must still show a state.
        await node.AddUnavailableAsync(Now.AddMinutes(-1), signal: "cpu.load");
        using var connection = Connect(node);
        var state = new StateViewModel(connection);

        await state.RefreshAsync();

        var cpu = state.Rows.Single(row => row.Signal == "cpu.load");
        Assert.False(cpu.HasValue);
        Assert.StartsWith("Unavailable", cpu.Value, StringComparison.Ordinal);
        Assert.NotEqual("0", cpu.Value);
        Assert.Equal("Unavailable", cpu.Freshness);
        Assert.Equal(string.Empty, cpu.Unit);
        var memory = state.Rows.Single(row => row.Signal == "memory.used");
        Assert.True(memory.HasValue);
        Assert.Equal("50%", memory.Value);
        Assert.Equal("Updated 60s ago", memory.Status);
        Assert.Equal("Not available on this device", cpu.Status);
        Assert.Equal("Fresh", memory.Freshness);
    }

    [Fact]
    public async Task HistoryUsesCatalogPickersPresetWindowAndBreaksTheChartAtUnavailableReadings()
    {
        await using var node = await NodeFixture.CreateAsync();
        await node.AddNumberAsync(Now.AddDays(-3), 9); // outside the 24h preset
        await node.AddNumberAsync(Now.AddHours(-3), 1);
        await node.AddNumberAsync(Now.AddHours(-2), 2);
        await node.AddUnavailableAsync(Now.AddHours(-1));
        await node.AddNumberAsync(Now.AddMinutes(-30), 4);
        await node.AddNumberAsync(Now.AddHours(-2), 70, "host-b", "memory.used");
        using var connection = Connect(node);
        var history = new HistoryViewModel(connection, () => Now);

        await history.RefreshAsync();
        Assert.Equal(["(all)", "host-a", "host-b"], history.Picker.Subjects);
        history.Picker.SelectedSubject = "host-a";
        Assert.Equal(["(all)", "cpu.load"], history.Picker.Signals);
        history.Picker.SelectedSignal = "cpu.load";
        await history.LoadAsync();

        Assert.Null(history.ErrorMessage);
        Assert.Equal(4, history.Rows.Count);
        Assert.DoesNotContain(history.Rows, row => row.Value == "9");
        Assert.Equal(3, history.Chart.PlottedCount);
        Assert.Equal(1, history.Chart.GapCount);
        Assert.Equal(2, history.Chart.Segments.Count);
        Assert.Equal([1.0, 2.0], history.Chart.Segments[0].Select(point => point.Y));
        Assert.Equal([4.0], history.Chart.Segments[1].Select(point => point.Y));
        Assert.Contains("gaps", history.ChartNote, StringComparison.Ordinal);

        history.SelectedPreset = history.Presets.Single(preset => preset.Preset == HistoryPreset.LastHour);
        await history.LoadAsync();
        Assert.Equal([4.0], history.Chart.Segments.Single().Select(point => point.Y));
        history.SelectedPreset = history.Presets.Single(preset => preset.Preset == HistoryPreset.Last7Days);
        await history.LoadAsync();
        Assert.Equal(5, history.Rows.Count);
    }

    [Fact]
    public async Task HistoryDoesNotChartWithoutASingleSignalSelected()
    {
        await using var node = await NodeFixture.CreateAsync();
        await node.AddNumberAsync(Now.AddMinutes(-5), 1);
        using var connection = Connect(node);
        var history = new HistoryViewModel(connection, () => Now);

        await history.RefreshAsync();

        Assert.Single(history.Rows);
        Assert.False(history.Chart.HasData);
        Assert.Contains("Select one subject", history.ChartNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryCustomRangeValidationFromNodeAndFromInputIsVisible()
    {
        await using var node = await NodeFixture.CreateAsync();
        using var connection = Connect(node);
        var history = new HistoryViewModel(connection, () => Now);
        history.SelectedPreset = history.Presets.Single(preset => preset.Preset == HistoryPreset.Custom);

        history.CustomFrom = "not a date";
        history.CustomTo = "2026-11-21T10:00:00Z";
        await history.LoadAsync();
        Assert.Contains("ISO 8601", history.ErrorMessage, StringComparison.Ordinal);

        history.CustomFrom = "2026-11-21T12:00:00Z";
        history.CustomTo = "2026-11-21T10:00:00Z";
        await history.LoadAsync();
        Assert.Contains("later than from", history.ErrorMessage, StringComparison.Ordinal);

        history.CustomFrom = "2026-11-21T10:00:00Z";
        history.CustomTo = "2026-11-21T12:00:00Z";
        await history.LoadAsync();
        Assert.Null(history.ErrorMessage);
    }

    [Fact]
    public async Task AsOfReconstructsTheReadingThatWasLatestAtThatMoment()
    {
        await using var node = await NodeFixture.CreateAsync();
        await node.AddNumberAsync(Now.AddHours(-5), 10);
        await node.AddNumberAsync(Now.AddHours(-1), 20);
        using var connection = Connect(node);
        var asOf = new AsOfViewModel(connection, () => Now)
        {
            AsOfText = Now.AddHours(-3).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };

        await asOf.LoadAsync();

        Assert.Null(asOf.ErrorMessage);
        Assert.Equal("1000%", Assert.Single(asOf.Rows).Value);

        asOf.LookbackHoursText = "0";
        await asOf.LoadAsync();
        Assert.Contains("Lookback", asOf.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApprovingAPendingPairingMarksItApprovedWithoutClaimingTrustAndNamesItsFingerprint()
    {
        await using var node = await NodeFixture.CreateAsync();
        using var connection = Connect(node);
        var pairings = new PairingsViewModel(connection);
        await pairings.RefreshAsync();
        var pending = Assert.Single(pairings.Pending);
        Assert.False(string.IsNullOrWhiteSpace(pending.Fingerprint));

        await pairings.ApproveCommand.ExecuteAsync(pending);

        Assert.Null(pairings.ErrorMessage);
        var approved = Assert.Single(pairings.Pending);
        Assert.Equal("Approved", approved.ApprovalState);
        Assert.False(approved.CanDecide);
        Assert.DoesNotContain(pairings.Peers, peer => peer.PeerId == "peer-pending");
        Assert.Contains(pending.Fingerprint, pairings.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectingAPairingRemovesItWithoutTrustingThePeer()
    {
        await using var node = await NodeFixture.CreateAsync();
        using var connection = Connect(node);
        var pairings = new PairingsViewModel(connection);
        await pairings.RefreshAsync();

        await pairings.RejectCommand.ExecuteAsync(Assert.Single(pairings.Pending));

        Assert.Empty(pairings.Pending);
        Assert.DoesNotContain(pairings.Peers, peer => peer.PeerId == "peer-pending");
    }

    [Fact]
    public async Task RevokingATrustedPeerRequiresConfirmationAndCanBeCancelled()
    {
        await using var node = await NodeFixture.CreateAsync();
        using var connection = Connect(node);
        var pairings = new PairingsViewModel(connection);
        await pairings.RefreshAsync();
        var trusted = pairings.Peers.Single(peer => peer.PeerId == "peer-trusted");

        pairings.RequestRevokeCommand.Execute(trusted);
        Assert.True(pairings.IsRevocationPending);
        Assert.Contains(trusted.Fingerprint, pairings.RevocationPrompt, StringComparison.Ordinal);
        await pairings.RefreshAsync();
        Assert.Equal("Active", pairings.Peers.Single(peer => peer.PeerId == "peer-trusted").TrustState);

        pairings.CancelRevokeCommand.Execute(null);
        Assert.False(pairings.IsRevocationPending);
        Assert.Equal("Active", pairings.Peers.Single(peer => peer.PeerId == "peer-trusted").TrustState);

        pairings.RequestRevokeCommand.Execute(trusted);
        await pairings.ConfirmRevokeCommand.ExecuteAsync(null);
        Assert.False(pairings.IsRevocationPending);
        var revoked = pairings.Peers.Single(peer => peer.PeerId == "peer-trusted");
        Assert.Equal("Revoked", revoked.TrustState);
        Assert.False(revoked.CanRevoke);
    }

    [Fact]
    public async Task RemoveNeverStaysAtRemovingWhenTheDeviceFailsOrNeverAnswers()
    {
        await using var node = await NodeFixture.CreateAsync();
        using var inner = node.NewClient();
        var broken = new LiveSource(inner) { Revoke = _ => throw new InvalidOperationException("The database is locked.") };
        using var connection = new NodeConnection(broken);
        var pairings = new PairingsViewModel(connection);
        await pairings.RefreshAsync();
        var trusted = pairings.Peers.Single(peer => peer.PeerId == "peer-trusted");

        pairings.RequestRevokeCommand.Execute(trusted);
        await pairings.ConfirmRevokeCommand.ExecuteAsync(null);
        trusted = pairings.Peers.Single(peer => peer.PeerId == "peer-trusted");
        Assert.False(trusted.IsWorking);
        Assert.Contains("The database is locked.", trusted.Outcome, StringComparison.Ordinal);

        var previous = PairingsViewModel.RemovalTimeLimit;
        PairingsViewModel.RemovalTimeLimit = TimeSpan.FromMilliseconds(300);
        try
        {
            broken.Revoke = _ => new TaskCompletionSource().Task;
            pairings.RequestRevokeCommand.Execute(trusted);
            await pairings.ConfirmRevokeCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(30));
            trusted = pairings.Peers.Single(peer => peer.PeerId == "peer-trusted");
            Assert.False(trusted.IsWorking);
            Assert.Contains("no answer within", trusted.Outcome, StringComparison.Ordinal);
        }
        finally
        {
            PairingsViewModel.RemovalTimeLimit = previous;
        }
    }

    [Fact]
    public async Task MainViewModelReportsAnUnreachableNodeAndRecoversWhenPointedAtARunningOne()
    {
        await using var node = await NodeFixture.CreateAsync();
        var dead = new Uri("http://127.0.0.1:1/");
        using var main = new MainViewModel(dead, uri => LocalApiClient.Create(uri, TimeSpan.FromSeconds(5)));

        await main.ConnectAsync();
        Assert.StartsWith("Not connected", main.ConnectionStatus, StringComparison.Ordinal);
        Assert.Contains("Cannot reach", main.Overview.ErrorMessage, StringComparison.Ordinal);

        main.AddressText = node.Address.ToString();
        await main.ConnectAsync();
        Assert.StartsWith("Connected", main.ConnectionStatus, StringComparison.Ordinal);
        Assert.Null(main.Overview.ErrorMessage);
        Assert.Equal("peer-local", main.Overview.LocalPeerId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("ftp://127.0.0.1/")]
    public async Task MainViewModelRejectsAddressesThatAreNotHttp(string text)
    {
        using var main = new MainViewModel();
        main.AddressText = text;

        await main.ConnectAsync();

        Assert.NotNull(main.AddressError);
        Assert.Equal("Not connected yet.", main.ConnectionStatus);
    }

    [Fact]
    public async Task CurrentStatePollsWhenTheSourceCannotPushAndPicksUpNewReadings()
    {
        await using var node = await NodeFixture.CreateAsync();
        using var connection = Connect(node);
        var state = new StateViewModel(connection);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var live = state.RunLiveAsync(TimeSpan.FromMilliseconds(100), cts.Token);
        await WaitUntilAsync(() => state.UpdateMode.StartsWith("Polling", StringComparison.Ordinal) && state.Summary != "Not loaded", cts.Token);
        Assert.Empty(state.Rows);
        await node.AddNumberAsync(Now.AddMinutes(-1), 3);
        await WaitUntilAsync(() => state.Rows.Count == 1, cts.Token);
        await cts.CancelAsync();
        await live;

        Assert.Equal("300%", state.Rows[0].Value);
        Assert.Equal("Manual refresh", state.UpdateMode);
    }

    [Fact]
    public async Task CurrentStateUpdatesOnPushFromALiveCapableSourceWithoutPolling()
    {
        await using var node = await NodeFixture.CreateAsync();
        using var inner = node.NewClient();
        var source = new LiveSource(inner);
        using var connection = new NodeConnection(source);
        var state = new StateViewModel(connection);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var live = state.RunLiveAsync(TimeSpan.FromHours(1), cts.Token);
        await WaitUntilAsync(() => state.UpdateMode == "Live" && state.Summary != "Not loaded", cts.Token);
        await node.AddNumberAsync(Now.AddMinutes(-1), 8);
        Assert.Empty(state.Rows);
        source.Raise();
        await WaitUntilAsync(() => state.Rows.Count == 1, cts.Token);
        await cts.CancelAsync();
        await live;

        Assert.Equal("800%", state.Rows[0].Value);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(25, token);
        }
    }

    /// <summary>Adapter that adds a push signal in front of a real data source.</summary>
    private sealed class LiveSource(IPeerDataSource inner) : IPeerDataSource
    {
        public string Description => inner.Description;

        public bool SupportsLiveUpdates => true;

        public event EventHandler? StateChanged;

        public void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);

        public Task<RuntimeDto> GetRuntimeAsync(CancellationToken ct = default) => inner.GetRuntimeAsync(ct);

        public Task<IReadOnlyList<TrustedPeerDto>> GetPeersAsync(CancellationToken ct = default) => inner.GetPeersAsync(ct);

        public Task<IReadOnlyList<PendingPairingDto>> GetPairingsAsync(CancellationToken ct = default) => inner.GetPairingsAsync(ct);

        public Task<CatalogDto> GetCatalogAsync(CancellationToken ct = default) => inner.GetCatalogAsync(ct);

        public Task<StateDto> GetStateAsync(
            DateTimeOffset? asOf = null, int? lookbackSeconds = null, string? subjectEntityId = null, string? signalId = null, CancellationToken ct = default) =>
            inner.GetStateAsync(asOf, lookbackSeconds, subjectEntityId, signalId, ct);

        public Task<HistoryResult> GetHistoryAsync(HistoryQuery query, int maxPages = 20, int pageSize = 1000, CancellationToken ct = default) =>
            inner.GetHistoryAsync(query, maxPages, pageSize, ct);

        public Task ApprovePairingAsync(string pairingId, CancellationToken ct = default) => inner.ApprovePairingAsync(pairingId, ct);

        public Task RejectPairingAsync(string pairingId, CancellationToken ct = default) => inner.RejectPairingAsync(pairingId, ct);

        public Func<string, Task>? Revoke { get; set; }

        public Task RevokePeerAsync(string peerId, CancellationToken ct = default) =>
            Revoke is { } revoke ? revoke(peerId) : inner.RevokePeerAsync(peerId, ct);
    }

    [Fact]
    public void ChartSeriesPadsFlatRangesAndHasNoDataForUnavailableOnlyInput()
    {
        var flat = ChartSeries.Build([Point(Now, 5), Point(Now.AddMinutes(1), 5)]);
        var none = ChartSeries.Build([Point(Now, null)]);

        Assert.True(flat.MaxY > flat.MinY);
        Assert.False(none.HasData);
        Assert.Equal(1, none.GapCount);
    }

    private static HistoryPointDto Point(DateTimeOffset at, double? value) => new(
        "p", "s", 1, "host-a", "cpu.load", at, at, "adapter", "Replicated",
        value is null ? "Unavailable" : "Valid",
        value is null ? "Unavailable" : "Fresh",
        0,
        value is null ? null : new ObservationValueDto("number", value, null, null),
        null,
        null);
}
