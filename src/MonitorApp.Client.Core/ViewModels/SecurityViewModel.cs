using System.Collections.ObjectModel;
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

    /// <summary>Phone fingerprint-key enrollments waiting for approval on this computer.</summary>
    public ObservableCollection<PhoneEnrollmentRow> PendingEnrollments { get; } = [];

    public bool HasPendingEnrollments => PendingEnrollments.Count > 0;

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

        await RunAsync(async () =>
        {
            Status = await source.GetLocalPresenceStatusAsync(cancellationToken);
            await ReloadEnrollmentsAsync(source, cancellationToken);
        });
    }

    private async Task ReloadEnrollmentsAsync(ISecuritySource source, CancellationToken cancellationToken)
    {
        var pending = await source.ListPendingPresenceEnrollmentsAsync(cancellationToken);
        PendingEnrollments.Clear();
        foreach (var enrollment in pending)
        {
            PendingEnrollments.Add(PhoneEnrollmentRow.From(enrollment));
        }

        OnPropertyChanged(nameof(HasPendingEnrollments));
    }

    [RelayCommand]
    private async Task ApproveEnrollmentAsync(PhoneEnrollmentRow? enrollment)
    {
        if (enrollment is null || Source is not { } source)
        {
            return;
        }

        StatusMessage = null;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await source.ApprovePhonePresenceEnrollmentAsync(enrollment.EnrollmentId);
            StatusMessage = $"Approved {enrollment.Title}.";
            await ReloadEnrollmentsAsync(source, CancellationToken.None);
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

/// <summary>A phone's pending fingerprint-key enrollment as shown for approval.</summary>
public sealed record PhoneEnrollmentRow(string EnrollmentId, string Title, string Fingerprint, string Requested)
{
    public static PhoneEnrollmentRow From(PresenceEnrollmentDto dto)
    {
        var name = string.IsNullOrWhiteSpace(dto.DisplayName) ? "A phone" : dto.DisplayName.Trim();
        var hex = dto.FingerprintSha256.Replace(" ", string.Empty);
        var shown = hex.Length > 16 ? hex[..16] : hex;
        var grouped = string.Join(' ', Enumerable.Range(0, (shown.Length + 3) / 4).Select(i => shown.Substring(i * 4, Math.Min(4, shown.Length - i * 4))));
        return new PhoneEnrollmentRow(
            dto.EnrollmentId,
            $"{name}'s fingerprint key",
            grouped,
            $"Requested {dto.RequestedAt.ToLocalTime():g}");
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

    [ObservableProperty]
    private string? lastChanged;

    private bool loading;

    public PeerGrantViewModel(string peerId, string name, Func<ISecuritySource?> source)
    {
        PeerId = peerId;
        Name = name;
        this.source = source;
    }

    public string PeerId { get; }

    /// <summary>True when the toggles were edited since the last load or save, so a refresh must not overwrite them.</summary>
    public bool IsDirty { get; private set; }

    public bool HasLastChanged => LastChanged is not null;

    partial void OnLastChangedChanged(string? value) => OnPropertyChanged(nameof(HasLastChanged));

    partial void OnMonitoringControlsChanged(bool value) => MarkDirty();

    partial void OnPairingApprovalChanged(bool value) => MarkDirty();

    partial void OnUpdateApprovalChanged(bool value) => MarkDirty();

    private void MarkDirty()
    {
        if (!loading)
        {
            IsDirty = true;
        }
    }

    /// <summary>Shows a stored grant (null = none stored, everything off) and clears the dirty flag.</summary>
    public void Load(ControlGrantDto? grant)
    {
        loading = true;
        try
        {
            MonitoringControls = grant?.MonitoringControls ?? false;
            PairingApproval = grant?.PairingApproval ?? false;
            UpdateApproval = grant?.UpdateApproval ?? false;
            LastChanged = grant?.UpdatedAt is { } at ? $"Last changed {at.ToLocalTime():g}" : null;
        }
        finally
        {
            loading = false;
            IsDirty = false;
        }
    }

    public string Name { get; }

    public static string MonitoringCopy => "Monitoring controls: lets this phone pause or resume collection, change sampling and mute or acknowledge alerts. Off unless you switch it on.";

    public static string PairingCopy => "Approve pairings: lets this phone approve or reject other devices that want to pair. Off unless you switch it on.";

    public static string UpdateCopy => "Approve updates: lets this phone approve installing a staged update. Off unless you switch it on.";

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
            IsDirty = false;
            LastChanged = $"Last changed {DateTimeOffset.Now:g}";
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
