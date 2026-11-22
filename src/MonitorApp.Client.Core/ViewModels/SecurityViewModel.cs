using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>Maps fingerprint-related failures to friendly text. A cancelled verification is deliberately silent.</summary>
public static class SecurityMessages
{
    public static string? For(Exception exception) => exception switch
    {
        LocalPresenceCancelledException => null,
        LocalPresenceRequiredException => "This needs fingerprint approval, and fingerprint approval isn't set up or available on this computer. " +
            "Set it up under \"This computer's fingerprint\" in Settings, then try again.",
        LocalPresenceRefusedException => "The fingerprint check was refused. Try again, and make sure it is this computer's enrolled fingerprint.",
        LocalApiException api => api.Message,
        _ => throw new InvalidOperationException("Not a security failure.", exception),
    };
}

/// <summary>This computer's fingerprint (local presence) status, enrol and remove, plus the shell's "not set up" banner.</summary>
public sealed partial class SecurityViewModel : SectionViewModel
{
    public const string BannerText = "Fingerprint approval isn't set up on this computer. High-risk actions from your phone can't be approved here.";

    [ObservableProperty]
    private LocalPresenceStatusDto? status;

    [ObservableProperty]
    private bool bannerDismissed;

    public SecurityViewModel(NodeConnection connection)
        : base(connection, "This computer's fingerprint")
    {
    }

    private ISecuritySource? Source => Client as ISecuritySource;

    public bool IsLoaded => Status is not null;

    public bool IsEnrolled => Status is { Enrolled: true };

    public bool IsNotEnrolled => Status is { Enrolled: false };

    public bool ShowBanner => IsNotEnrolled && !BannerDismissed;

    public string StatusText => Status switch
    {
        null => "Checking fingerprint approval...",
        { Enrolled: true } s => s.Enforced
            ? "Fingerprint approval is set up and required for high-risk actions."
            : "A fingerprint is enrolled, but it is not yet required for high-risk actions.",
        _ => "Not set up. High-risk actions from your phone can't be approved on this computer.",
    };

    partial void OnStatusChanged(LocalPresenceStatusDto? value)
    {
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(IsEnrolled));
        OnPropertyChanged(nameof(IsNotEnrolled));
        OnPropertyChanged(nameof(ShowBanner));
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnBannerDismissedChanged(bool value) => OnPropertyChanged(nameof(ShowBanner));

    [RelayCommand]
    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Source is not { } source)
        {
            return;
        }

        await RunAsync(async () => Status = await source.GetLocalPresenceStatusAsync(cancellationToken));
    }

    [RelayCommand]
    private void DismissBanner() => BannerDismissed = true;

    [RelayCommand]
    private Task EnrollAsync() => ChangeAsync(s => s.EnrollLocalPresenceAsync(), "Fingerprint approval is now set up on this computer.");

    [RelayCommand]
    private Task RemoveAsync() => ChangeAsync(s => s.RemoveLocalPresenceAsync(), "Fingerprint approval was removed from this computer.");

    private async Task ChangeAsync(Func<ISecuritySource, Task<LocalPresenceStatusDto>> action, string done)
    {
        if (Source is not { } source)
        {
            return;
        }

        StatusMessage = null;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            Status = await action(source);
            StatusMessage = done;
            BannerDismissed = false;
        }
        catch (Exception exception) when (exception is LocalApiException)
        {
            ErrorMessage = SecurityMessages.For(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>One paired phone's permissions on this computer. Everything is denied unless switched on and saved.</summary>
public sealed partial class PeerGrantViewModel : ObservableObject
{
    private readonly Func<ISecuritySource?> source;

    [ObservableProperty]
    private bool monitoringControls;

    [ObservableProperty]
    private bool pairingApproval;

    [ObservableProperty]
    private bool updateApproval;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private bool isBusy;

    public PeerGrantViewModel(string peerId, string name, Func<ISecuritySource?> source)
    {
        PeerId = peerId;
        Name = name;
        this.source = source;
    }

    public string PeerId { get; }

    public string Name { get; }

    public static string MonitoringCopy => "Monitoring controls: lets this phone pause or resume collection, change sampling and mute or acknowledge alerts. Off by default.";

    public static string PairingCopy => "Approve pairings: lets this phone approve or reject other devices that want to pair. Off by default.";

    public static string UpdateCopy => "Approve updates: lets this phone approve installing a staged update. Off by default.";

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (source() is not { } api)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = null;
        ErrorMessage = null;
        try
        {
            await api.SetControlGrantAsync(PeerId, MonitoringControls, PairingApproval, UpdateApproval);
            StatusMessage = $"Saved permissions for {Name}.";
        }
        catch (LocalApiException exception)
        {
            ErrorMessage = SecurityMessages.For(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
