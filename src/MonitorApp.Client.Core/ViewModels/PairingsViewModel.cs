using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>Pending pairing decisions and trust revocation. Revocation needs an explicit second step.</summary>
public sealed partial class PairingsViewModel : SectionViewModel
{
    /// <summary>"Add a phone" (reach switch and in-app QR); null when the source is not a computer's own node.</summary>
    public AddPhoneViewModel? AddPhone { get; }

    public bool CanAddPhone => AddPhone is not null;

    [ObservableProperty]
    private TrustedPeerCard? pendingRevocation;

    /// <summary>What the last removal said, per device, so the result stays on that device's card across a reload.</summary>
    private readonly Dictionary<string, (string Text, bool Failed)> removalOutcomes = new(StringComparer.Ordinal);

    /// <summary>
    /// "Add a device" on a phone or tablet (scan any code, or show this device's), on this same screen: one place to add,
    /// see and remove devices on every kind of device (Wilson, 2026-11-24). Null on a computer, which shows its code below.
    /// </summary>
    public PairDeviceViewModel? Add { get; }

    public bool CanAdd => Add is not null;

    public PairingsViewModel(NodeConnection connection)
        : base(connection, "Add or remove devices")
    {
        if (Client is IPairingInitiator)
        {
            Add = new PairDeviceViewModel(connection);
            Add.Paired += (_, _) => _ = RunAsync(() => ReloadAsync(CancellationToken.None));
        }

        if (Client is IPhonePairingHost)
        {
            AddPhone = new AddPhoneViewModel(() => Client as IPhonePairingHost);
            AddPhone.CodeShown += (_, _) => _ = WatchForPhoneAsync();
        }
    }

    /// <summary>After a code is shown, look for the phone's request every few seconds for ten minutes so it appears without a refresh.</summary>
    private async Task WatchForPhoneAsync()
    {
        var before = Pending.Count;
        for (var attempt = 0; attempt < 200 && Pending.Count <= before; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            try
            {
                await ReloadAsync(CancellationToken.None);
            }
            catch (LocalApiException)
            {
                return;
            }
        }
    }

    public ObservableCollection<PendingPairingRow> Pending { get; } = [];

    public ObservableCollection<TrustedPeerCard> Peers { get; } = [];

    /// <summary>Per-phone permission panels; empty and hidden when the source cannot manage grants (the in-process phone runtime).</summary>
    public ObservableCollection<PeerGrantViewModel> Grants { get; } = [];

    public bool CanGrant => Client is ISecuritySource;

    /// <summary>How removing a device is confirmed on this device: fingerprint, screen lock, or not at all (and how to change that).</summary>
    [ObservableProperty]
    private string removalCheck = string.Empty;

    public bool HasRemovalCheck => RemovalCheck.Length > 0;

    partial void OnRemovalCheckChanged(string value) => OnPropertyChanged(nameof(HasRemovalCheck));

    /// <summary>How long Remove may take before the card says it got no answer.</summary>
    public static TimeSpan RemovalTimeLimit { get; set; } = TimeSpan.FromSeconds(45);

    public bool IsRevocationPending => PendingRevocation is not null;

    public string RevocationPrompt => PendingRevocation?.Prompt ?? string.Empty;

    partial void OnPendingRevocationChanged(TrustedPeerCard? oldValue, TrustedPeerCard? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsConfirming = false;
        }

        if (newValue is not null)
        {
            newValue.IsConfirming = true;
        }

        OnPropertyChanged(nameof(IsRevocationPending));
        OnPropertyChanged(nameof(RevocationPrompt));
    }

    [RelayCommand]
    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(() => ReloadAsync(cancellationToken));
        if (AddPhone is not null)
        {
            await AddPhone.RefreshAsync(cancellationToken);
        }
    }

    [RelayCommand]
    private async Task ApproveAsync(PendingPairingRow? pairing)
    {
        if (pairing is not { CanDecide: true })
        {
            return;
        }

        await Decide(pairing, approve: true);
    }

    [RelayCommand]
    private async Task RejectAsync(PendingPairingRow? pairing)
    {
        if (pairing is not { CanDecide: true })
        {
            return;
        }

        await Decide(pairing, approve: false);
    }

    [RelayCommand]
    private void RequestRevoke(TrustedPeerCard? peer)
    {
        if (peer is { CanRevoke: true, IsWorking: false })
        {
            removalOutcomes.Remove(peer.PeerId);
            peer.SetOutcome(null, false);
            PendingRevocation = peer;
            StatusMessage = null;
        }
    }

    [RelayCommand]
    private void CancelRevoke() => PendingRevocation = null;

    [RelayCommand]
    private async Task ConfirmRevokeAsync(TrustedPeerCard? card = null)
    {
        var peer = card ?? PendingRevocation;
        if (peer is null || peer.IsWorking)
        {
            return;
        }

        PendingRevocation = null;
        peer.IsWorking = true;
        peer.SetOutcome($"Removing {peer.Name} from all your devices…", false);
        string? failure = null;
        using var limit = new CancellationTokenSource(RemovalTimeLimit);
        try
        {
            // WaitAsync as well as the token: a step that ignores cancellation still cannot hold the card at "Removing…".
            await Client.RevokePeerAsync(peer.PeerId, limit.Token).WaitAsync(RemovalTimeLimit + TimeSpan.FromSeconds(5));
        }
        catch (LocalApiException exception)
        {
            failure = exception.Message;
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            failure = $"no answer within {RemovalTimeLimit.TotalSeconds:0} seconds. It may still finish; this list shows the result when it does.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Any other failure is shown in its own words rather than leaving the card waiting forever (Wilson, 2026-11-24).
            failure = exception.Message;
        }
        finally
        {
            peer.IsWorking = false;
        }

        var outcome = failure is null
            ? ($"Removed. Every device stops trusting {peer.Name} as soon as it hears about it.", false)
            : ($"Not removed: {failure}", true);
        removalOutcomes[peer.PeerId] = outcome;
        peer.SetOutcome(outcome.Item1, outcome.Item2);
        StatusMessage = failure is null ? $"Removed {peer.Name} from all your devices." : null;
        await RunAsync(() => ReloadAsync(CancellationToken.None));
    }

    /// <summary>
    /// Updates each device's connection line in place, every few seconds while the screen is open, without rebuilding the
    /// cards (a removal in progress or an unsaved permission is never disturbed). Failures are ignored here; the next full
    /// refresh reports them.
    /// </summary>
    public async Task RefreshConnectionsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TrustedPeerDto> peers;
        IReadOnlyList<PendingPairingDto> pending;
        try
        {
            peers = await Client.GetPeersAsync(cancellationToken);
            pending = await Client.GetPairingsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is LocalApiException or OperationCanceledException)
        {
            return;
        }

        // A phone's request appears within seconds of the scan, not at the next full refresh (Wilson waited two minutes).
        var rows = pending.Select(PendingPairingRow.From).ToArray();
        if (!rows.SequenceEqual(Pending))
        {
            Pending.Clear();
            foreach (var row in rows)
            {
                Pending.Add(row);
            }
        }

        foreach (var peer in peers)
        {
            if (Peers.FirstOrDefault(card => card.PeerId == peer.PeerId) is { } card)
            {
                card.Connection = peer.Connection;
                card.Connected = peer.Connected;
            }
        }
    }

    private async Task Decide(PendingPairingRow pairing, bool approve)
    {
        StatusMessage = approve ? $"Approving {pairing.Name}…" : $"Rejecting {pairing.Name}…";
        var done = await RunAsync(async () =>
        {
            if (approve)
            {
                await Client.ApprovePairingAsync(pairing.PairingId);
                StatusMessage = $"Approved pairing with {pairing.Name} (fingerprint {pairing.Fingerprint}). " +
                    "The peer becomes trusted when it completes pairing.";
            }
            else
            {
                await Client.RejectPairingAsync(pairing.PairingId);
                StatusMessage = $"Rejected pairing with {pairing.Name}.";
            }

            await ReloadAsync(CancellationToken.None);
        });
        if (!done)
        {
            StatusMessage = null;
        }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var pending = await Client.GetPairingsAsync(cancellationToken);
        var peers = await Client.GetPeersAsync(cancellationToken);
        var stored = Client is ISecuritySource security
            ? (await security.GetControlGrantsAsync(cancellationToken)).ToDictionary(g => g.PeerId)
            : [];
        Pending.Clear();
        foreach (var pairing in pending)
        {
            Pending.Add(PendingPairingRow.From(pairing));
        }

        Peers.Clear();
        var previous = Grants.ToDictionary(g => g.PeerId);
        Grants.Clear();
        foreach (var peer in peers)
        {
            var row = new TrustedPeerCard(TrustedPeerRow.From(peer));
            if (removalOutcomes.TryGetValue(row.PeerId, out var outcome))
            {
                row.SetOutcome(outcome.Text, outcome.Failed);
            }

            if (PendingRevocation?.PeerId == row.PeerId && row.CanRevoke)
            {
                PendingRevocation = row;
            }

            Peers.Add(row);
            if (CanGrant && row.CanRevoke)
            {
                // Keep the person's unsaved toggles across a refresh; otherwise show what is stored.
                if (!previous.TryGetValue(row.PeerId, out var model))
                {
                    model = new PeerGrantViewModel(row.PeerId, row.Name, () => Client as ISecuritySource);
                }

                if (!model.IsDirty)
                {
                    model.Load(stored.GetValueOrDefault(row.PeerId));
                }

                Grants.Add(model);
            }
        }

        OnPropertyChanged(nameof(CanGrant));
        RemovalCheck = await DescribeRemovalCheckAsync(cancellationToken);
    }

    private async Task<string> DescribeRemovalCheckAsync(CancellationToken cancellationToken)
    {
        if (Client is IRemovalCheckSource described)
        {
            return described.RemovalCheck;
        }

        if (Client is not ISecuritySource security)
        {
            return string.Empty;
        }

        try
        {
            var status = await security.GetLocalPresenceStatusAsync(cancellationToken);
            return status.Enrolled
                ? "Removing a device asks for your fingerprint (or Windows Hello)."
                : "Fingerprint approval isn't set up on this computer, so removing a device is not double-checked. " +
                  "Set it up on the Security page; on Windows a Windows Hello PIN works too.";
        }
        catch (LocalApiException)
        {
            return string.Empty;
        }
    }
}
