using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>
/// Pair this device with a desktop: a typed address and one-time code, or a pasted invitation. The desktop user
/// must still approve the request there. Camera QR scanning is a later addition to the same commands.
/// </summary>
public sealed partial class PairDeviceViewModel : SectionViewModel
{
    [ObservableProperty]
    private string addressText = string.Empty;

    [ObservableProperty]
    private string codeText = string.Empty;

    [ObservableProperty]
    private string invitationText = string.Empty;

    public PairDeviceViewModel(NodeConnection connection)
        : base(connection, "Add a device")
    {
    }

    /// <summary>Raised after a pairing succeeds so the shell can refresh what depends on the trusted peers.</summary>
    public event EventHandler? Paired;

    /// <summary>True when the source can scan a pairing QR with a camera; the view shows the Scan QR button only then.</summary>
    public bool CanScanQr => Client is IQrPairingSource { CanScanQr: true };

    public override Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    [RelayCommand]
    private async Task PairWithCodeAsync()
    {
        if (Client is not IPairingInitiator initiator)
        {
            ErrorMessage = "This device cannot start pairing.";
            return;
        }

        StatusMessage = "Contacting the desktop. Approve this device on the desktop when asked.";
        if (await RunAsync(async () =>
            {
                var peer = await initiator.PairWithCodeAsync(AddressText, CodeText);
                StatusMessage = $"Paired with {peer}.";
                CodeText = string.Empty;
            }))
        {
            return;
        }

        StatusMessage = null;
    }

    [RelayCommand]
    private async Task PairWithInvitationAsync()
    {
        if (Client is not IPairingInitiator initiator)
        {
            ErrorMessage = "This device cannot start pairing.";
            return;
        }

        StatusMessage = "Contacting the desktop. Approve this device on the desktop when asked.";
        if (await RunAsync(async () =>
            {
                var peer = await initiator.PairWithInvitationAsync(InvitationText);
                StatusMessage = $"Paired with {peer}.";
                InvitationText = string.Empty;
            }))
        {
            return;
        }

        StatusMessage = null;
    }

    /// <summary>
    /// One Scan button for every kind of code (Wilson, 2026-11-24: one way to pair, not one per kind of device). A computer's
    /// code pairs over the network and waits for Accept there; a phone's or tablet's code goes on to the face-to-face check.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScanQr))]
    private async Task ScanQrAsync()
    {
        StatusMessage = null;
        if (Client is IMeshJoinSource mesh && Client is IPairingInitiator initiator)
        {
            PendingDevice = null;
            await RunAsync(async () =>
            {
                if (await mesh.ScanAnyCodeAsync() is not { } code) return;
                if (mesh.IsDeviceCode(code))
                {
                    PendingDevice = mesh.PreviewMeshCode(code);
                    pendingCode = code;
                    return;
                }

                StatusMessage = "Code read. Asking the computer to add this device. Tap Accept on the computer when it asks; this waits up to 5 minutes.";
                var peer = await initiator.PairWithInvitationAsync(code);
                StatusMessage = $"Paired with {peer}.";
                Paired?.Invoke(this, EventArgs.Empty);
            });
            return;
        }

        if (Client is not IQrPairingSource scanner)
        {
            return;
        }

        await RunAsync(async () =>
        {
            // The scanner reports from a background thread; the message is set on the screen's own thread.
            var screen = SynchronizationContext.Current;
            void Progress(string text)
            {
                if (screen is null)
                {
                    StatusMessage = text;
                }
                else
                {
                    screen.Post(_ => StatusMessage = text, null);
                }
            }

            if (await scanner.ScanAndPairAsync(Progress) is { } peer)
            {
                StatusMessage = $"Paired with {peer}.";
                Paired?.Invoke(this, EventArgs.Empty);
            }
        });
    }
}
