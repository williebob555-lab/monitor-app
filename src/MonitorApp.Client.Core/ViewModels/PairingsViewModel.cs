using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>Pending pairing decisions and trust revocation. Revocation needs an explicit second step.</summary>
public sealed partial class PairingsViewModel : SectionViewModel
{
    [ObservableProperty]
    private TrustedPeerRow? pendingRevocation;

    public PairingsViewModel(NodeConnection connection)
        : base(connection, "Pairing and trust")
    {
    }

    public ObservableCollection<PendingPairingRow> Pending { get; } = [];

    public ObservableCollection<TrustedPeerRow> Peers { get; } = [];

    /// <summary>Per-phone permission panels; empty and hidden when the source cannot manage grants (the in-process phone runtime).</summary>
    public ObservableCollection<PeerGrantViewModel> Grants { get; } = [];

    public bool CanGrant => Client is ISecuritySource;

    public bool IsRevocationPending => PendingRevocation is not null;

    public string RevocationPrompt => PendingRevocation is null
        ? string.Empty
        : $"Revoke trust in {PendingRevocation.Name} (fingerprint {PendingRevocation.Fingerprint})? " +
            "The peer will no longer be trusted until it is paired again.";

    partial void OnPendingRevocationChanged(TrustedPeerRow? value)
    {
        OnPropertyChanged(nameof(IsRevocationPending));
        OnPropertyChanged(nameof(RevocationPrompt));
    }

    [RelayCommand]
    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(() => ReloadAsync(cancellationToken));
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
    private void RequestRevoke(TrustedPeerRow? peer)
    {
        if (peer is { CanRevoke: true })
        {
            PendingRevocation = peer;
            StatusMessage = null;
        }
    }

    [RelayCommand]
    private void CancelRevoke() => PendingRevocation = null;

    [RelayCommand]
    private async Task ConfirmRevokeAsync()
    {
        var peer = PendingRevocation;
        if (peer is null)
        {
            return;
        }

        PendingRevocation = null;
        await RunAsync(async () =>
        {
            await Client.RevokePeerAsync(peer.PeerId);
            StatusMessage = $"Revoked trust in {peer.Name}.";
            await ReloadAsync(CancellationToken.None);
        });
    }

    private async Task Decide(PendingPairingRow pairing, bool approve)
    {
        await RunAsync(async () =>
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
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var pending = await Client.GetPairingsAsync(cancellationToken);
        var peers = await Client.GetPeersAsync(cancellationToken);
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
            var row = TrustedPeerRow.From(peer);
            Peers.Add(row);
            if (CanGrant && row.CanRevoke)
            {
                // Keep the person's unsaved toggles across a refresh; current grants cannot be read back.
                Grants.Add(previous.TryGetValue(row.PeerId, out var kept)
                    ? kept
                    : new PeerGrantViewModel(row.PeerId, row.Name, () => Client as ISecuritySource));
            }
        }

        OnPropertyChanged(nameof(CanGrant));
    }
}
