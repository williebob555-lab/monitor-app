using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>
/// This computer's updates in plain words: installed version, when it last looked, what it found, a "Check for updates" button and,
/// when a verified update is waiting, an Install button (fingerprint when enforced). Nothing installs without that press.
/// </summary>
public sealed partial class UpdatesViewModel : SectionViewModel
{
    private readonly TimeProvider time;

    [ObservableProperty]
    private LocalUpdateStatusDto? status;

    public UpdatesViewModel(NodeConnection connection, TimeProvider? time = null)
        : base(connection, "Updates")
    {
        this.time = time ?? TimeProvider.System;
    }

    private ILocalUpdatesSource? Source => Client as ILocalUpdatesSource;

    /// <summary>
    /// The app window's own build and how to start it again, set by the desktop head. An update replaces the background
    /// service and the files, but an open window keeps running the old build until it is restarted, which is why the
    /// version seemed to change "randomly" while the window looked the same (Wilson, 2026-11-24).
    /// </summary>
    public static IAppRestarter? Restarter { get; set; }

    /// <summary>How long Install follows the service through its restart before saying it got no answer.</summary>
    public static TimeSpan InstallFollowLimit { get; set; } = TimeSpan.FromMinutes(5);

    public static TimeSpan InstallFollowInterval { get; set; } = TimeSpan.FromSeconds(2);

    public string InstalledText => Status is { } s
        ? Restarter is { RunningVersion: { } window } && window != s.InstalledVersion
            ? $"Installed: {s.InstalledVersion} (background service). This window is still {window}."
            : $"Installed version {s.InstalledVersion}"
        : "Checking...";

    /// <summary>True when the service already runs a newer build than this window: restarting the window finishes the update.</summary>
    public bool NeedsRestart => Status is { } s && Restarter is { RunningVersion: { } window } &&
        ReleaseOrder(s.InstalledVersion) > ReleaseOrder(window);

    public string RestartText => NeedsRestart
        ? $"Version {Status!.InstalledVersion} is installed. This window is still the old one ({Restarter!.RunningVersion}); restart Monitor App to use the new one."
        : string.Empty;

    public string LastCheckedText => Status switch
    {
        null => string.Empty,
        { LastCheckedUtc: { } at } => $"Last checked {Ago(time.GetUtcNow() - at)} ({at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}). It checks by itself every hour.",
        _ => "Not checked yet since Monitor App started. It checks by itself every hour.",
    };

    public string StateText => Status switch
    {
        null => string.Empty,
        { Stage: "Disabled" } s => $"Updates are turned off: {s.Detail}",
        { ApprovalPending: true } s => $"Downloaded, not installed yet: {s.TargetVersion} is verified and installs only when you press Install.",
        { Stage: "Staged" } s => $"Version {s.TargetVersion} is downloaded and verified, but this installation cannot install updates by itself. Install it from the release page.",
        { Stage: "Applying" } s => $"Installing {s.TargetVersion}... Monitor App restarts when it is done.",
        { Stage: "Failed" or "RolledBack" } s => $"Installing {s.TargetVersion} did not work, and the previous version was kept. {s.Detail}",
        _ => "You have the newest version.",
    };

    public string? CheckErrorText => Status?.LastError is { } error ? $"The last check did not finish: {error}" : null;

    public bool CanInstall => Status is { ApprovalPending: true, TargetVersion: not null };

    public string InstallLabel => Status?.TargetVersion is { } version ? $"Install {version}" : "Install";

    partial void OnStatusChanged(LocalUpdateStatusDto? value)
    {
        OnPropertyChanged(nameof(InstalledText));
        OnPropertyChanged(nameof(LastCheckedText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(CheckErrorText));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(InstallLabel));
        OnPropertyChanged(nameof(NeedsRestart));
        OnPropertyChanged(nameof(RestartText));
        InstallCommand.NotifyCanExecuteChanged();
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Source is not { } source)
        {
            return;
        }

        await RunAsync(async () => Status = await source.GetLocalUpdateStatusAsync(cancellationToken));
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (Source is not { } source)
        {
            return;
        }

        StatusMessage = "Checking for updates...";
        var before = Status?.LastCheckedUtc;
        if (await RunAsync(async () => Status = await source.CheckForUpdatesAsync()))
        {
            // Always answer the press: what the check found, or why it did not run just now.
            StatusMessage = Status switch
            {
                { LastError: { } error } => $"The check did not finish: {error}",
                { LastCheckedUtc: { } at } when at == before && before is not null =>
                    $"Checked {Ago(time.GetUtcNow() - at)}, so it did not check again yet (it waits 30 seconds between checks). {StateText}",
                _ => $"Checked just now. {StateText}",
            };
        }
        else
        {
            StatusMessage = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (Source is not { } source || Status is not { ApprovalPending: true, TargetVersion: { } version })
        {
            return;
        }

        StatusMessage = $"Starting the install of {version}...";
        if (!await RunAsync(async () =>
            {
                await source.ApproveUpdateAsync(version);
                Status = await source.GetLocalUpdateStatusAsync();
            }))
        {
            StatusMessage = null;
            return;
        }

        await FollowInstallAsync(source, version);
    }

    /// <summary>
    /// Stays with the install until it is done: the service stops (the computer does not answer for a moment), starts on
    /// the new build and checks itself. Then this window restarts onto the new build too, or says why it could not.
    /// </summary>
    private async Task FollowInstallAsync(ILocalUpdatesSource source, string version)
    {
        StatusMessage = $"Installing {version}. The background service stops and starts again; this takes up to a minute.";
        var deadline = time.GetUtcNow() + InstallFollowLimit;
        var sawServiceGone = false;
        while (time.GetUtcNow() < deadline)
        {
            await Task.Delay(InstallFollowInterval, time);
            LocalUpdateStatusDto current;
            try
            {
                current = await source.GetLocalUpdateStatusAsync();
            }
            catch (LocalApiException)
            {
                sawServiceGone = true;
                StatusMessage = $"Installing {version}: the background service is restarting…";
                continue;
            }

            Status = current;
            ErrorMessage = null;
            if (current.InstalledVersion == version)
            {
                if (Restarter is { } restarter && restarter.RunningVersion != version)
                {
                    StatusMessage = $"Version {version} is installed. Restarting this window on the new version…";
                    if (!restarter.Restart())
                    {
                        StatusMessage = $"Version {version} is installed. Close Monitor App and open it again to use the new version.";
                    }
                }
                else
                {
                    StatusMessage = $"Version {version} is installed.";
                }

                return;
            }

            if (current.Stage is "Failed" or "RolledBack")
            {
                StatusMessage = null;
                ErrorMessage = $"Installing {version} did not work, and {current.InstalledVersion} was kept. {current.Detail}";
                return;
            }

            if (sawServiceGone)
            {
                StatusMessage = $"Installing {version}: the service is back and checking itself…";
            }
        }

        StatusMessage = null;
        ErrorMessage = $"Installing {version} has not finished after {InstallFollowLimit.TotalMinutes:0} minutes. " +
            $"The service still reports {Status?.InstalledVersion ?? "no version"} ({Status?.Stage}). Its diagnostic log says what happened: http://127.0.0.1:55432/api/v1/diagnostics/log";
    }

    [RelayCommand]
    private void RestartApp()
    {
        if (Restarter is { } restarter && !restarter.Restart())
        {
            ErrorMessage = "Could not restart by itself. Close Monitor App and open it again.";
        }
    }

    /// <summary>Orders "0.1.31" style versions; anything unreadable sorts first so it never asks for a restart.</summary>
    private static Version ReleaseOrder(string? text) =>
        Version.TryParse(text?.Split('+', '-')[0], out var parsed) ? parsed : new Version(0, 0);

    private static string Ago(TimeSpan elapsed) => elapsed switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalMinutes: < 2 } => "1 minute ago",
        { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes} minutes ago",
        { TotalHours: < 2 } => "1 hour ago",
        { TotalDays: < 1 } => $"{(int)elapsed.TotalHours} hours ago",
        _ => $"{(int)elapsed.TotalDays} days ago",
    };
}
