using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

// "Add a phone or tablet" (T085): two phones or tablets join each other face to face, each scanning the other's QR.
public sealed partial class PairDeviceViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShownCode), nameof(ShownQrRows))]
    private MeshCodeDto? shownCode;

    [ObservableProperty]
    private string? shownCodeCaption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingDevice), nameof(PendingDevicePrompt), nameof(PendingDeviceAction))]
    private MeshCodePreviewDto? pendingDevice;

    private string? pendingCode;

    /// <summary>True on a phone or tablet: "Show my code" is offered only then (a computer shows its code on Pairing).</summary>
    public bool CanAddMeshDevices => Client is IMeshJoinSource;

    public bool HasShownCode => ShownCode is not null;

    public IReadOnlyList<string> ShownQrRows => ShownCode?.QrRows ?? [];

    public bool HasPendingDevice => PendingDevice is not null;

    public string PendingDevicePrompt => PendingDevice is { } device
        ? $"Does the other screen ({device.DeviceName}) show {device.VerificationCode}? Only continue if it does."
        : string.Empty;

    public string PendingDeviceAction => PendingDevice is { IsJoinRequest: true } ? "Add it" : "Join";

    public string ThisDeviceCodeText => Client is IMeshJoinSource source
        ? $"This device's check code is {source.ThisDeviceVerificationCode}. The other device shows it when it scans this one."
        : string.Empty;

    /// <summary>The new device: show a join code for a device that is already set up to scan.</summary>
    [RelayCommand]
    private Task ShowJoinCodeAsync()
    {
        if (Client is not IMeshJoinSource source) return Task.CompletedTask;
        PendingDevice = null;
        return RunAsync(() =>
        {
            ShownCode = source.CreateJoinCode();
            ShownCodeCaption = "On a phone or tablet that is already set up, open Add or remove devices, tap \"Scan a code\" and scan this. " +
                "Then scan the code it shows back. This code works for ten minutes.";
            StatusMessage = null;
            return Task.CompletedTask;
        });
    }

    /// <summary>Either side: scan the other device's code. A join code is from a new device; a welcome is the reply to this one.</summary>
    [RelayCommand]
    private Task ScanDeviceCodeAsync()
    {
        if (Client is not IMeshJoinSource source) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            if (await source.ScanMeshCodeAsync() is not { } code) return;
            PendingDevice = source.PreviewMeshCode(code);
            pendingCode = code;
            StatusMessage = null;
        });
    }

    [RelayCommand]
    private void CancelPendingDevice()
    {
        PendingDevice = null;
        pendingCode = null;
    }

    /// <summary>After the owner checked the codes match: the fingerprint prompt, then add the device or finish joining.</summary>
    [RelayCommand]
    private Task ConfirmPendingDeviceAsync()
    {
        if (Client is not IMeshJoinSource source || PendingDevice is not { } device || pendingCode is not { } code) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            if (device.IsJoinRequest)
            {
                StatusMessage = $"Adding {device.DeviceName}...";
                ShownCode = await source.AdmitDeviceAsync(code);
                ShownCodeCaption = $"Now let {device.DeviceName} scan this code to finish. Your other devices will trust it once it connects.";
                StatusMessage = $"{device.DeviceName} is added on this device.";
            }
            else
            {
                StatusMessage = $"Joining {device.DeviceName}'s devices...";
                var name = await source.JoinWithWelcomeAsync(code);
                ShownCode = null;
                ShownCodeCaption = null;
                StatusMessage = $"Joined. This device now trusts {name} and the devices it trusts, and connects to them.";
                Paired?.Invoke(this, EventArgs.Empty);
            }

            PendingDevice = null;
            pendingCode = null;
        });
    }
}
