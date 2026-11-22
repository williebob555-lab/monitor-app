using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MonitorApp.Client.Core;
using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Client.Ui.Views;
using Xunit;

namespace MonitorApp.Client.Desktop.Tests;

public sealed class SecurityControlsTests
{
    /// <summary>One walk-through: banner while not set up, silent cancel, enrol clears it, grants save the exact flags, required-presence text.</summary>
    [AvaloniaFact]
    public async Task FingerprintBannerEnrolAndPhonePermissionsFlow()
    {
        var source = new FakeSecuritySource();
        using var model = new MainViewModel(source);
        var shell = new WorkspaceShell { RunEnvironmentLive = false, DataContext = model };
        var window = new Window { Width = 1200, Height = 800, Content = shell };
        window.Show();
        await model.ConnectAsync();
        Dispatcher.UIThread.RunJobs();

        var security = model.Security ?? throw new Xunit.Sdk.XunitException("source implements ISecuritySource but MainViewModel.Security is null");
        var banner = shell.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "FingerprintBanner");
        Assert.True(banner.IsVisible, $"banner should show while not enrolled; StatusText='{security.StatusText}' Error='{security.ErrorMessage}'");
        Assert.Equal(SecurityViewModel.BannerText, shell.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "BannerText").Text);

        // Dismiss is non-blocking and local.
        security.DismissBannerCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(banner.IsVisible, "dismissed banner should hide");

        // Cancelled enrolment: no message, still not enrolled.
        source.EnrollThrows = new LocalPresenceCancelledException("cancelled");
        await security.EnrollCommand.ExecuteAsync(null);
        Assert.Null(security.ErrorMessage);
        Assert.True(security.IsNotEnrolled, "cancel must not enrol");

        // Refused enrolment: friendly text.
        source.EnrollThrows = new LocalPresenceRefusedException("raw node text");
        await security.EnrollCommand.ExecuteAsync(null);
        Assert.Contains("refused", security.ErrorMessage);

        // Successful enrolment hides the banner permanently and flips status.
        source.EnrollThrows = null;
        await security.EnrollCommand.ExecuteAsync(null);
        Assert.True(security.IsEnrolled, $"enrolled expected; error='{security.ErrorMessage}'");
        Assert.Null(security.ErrorMessage);
        Assert.False(security.ShowBanner);

        // Phone permissions: only the switched flags are sent.
        var pairings = model.Sections.OfType<PairingsViewModel>().Single();
        await pairings.RefreshAsync();
        var grant = Assert.Single(pairings.Grants);
        Assert.Equal(FakeEnvironmentSource.ServerId, grant.PeerId);
        Assert.False(grant.MonitoringControls || grant.PairingApproval || grant.UpdateApproval, "deny by default");
        grant.MonitoringControls = true;
        grant.UpdateApproval = true;
        await grant.SaveCommand.ExecuteAsync(null);
        Assert.Equal((FakeEnvironmentSource.ServerId, true, false, true), source.LastGrant);
        Assert.Contains("Saved", grant.StatusMessage);

        // Granting needs presence: the friendly required-presence text shows on that peer.
        source.GrantThrows = new LocalPresenceRequiredException("raw");
        await grant.SaveCommand.ExecuteAsync(null);
        Assert.Contains("fingerprint", grant.ErrorMessage);
        window.Close();
    }

    [Fact]
    public void SourcesWithoutSecuritySupportHideTheControls()
    {
        using var model = new MainViewModel(new FakeEnvironmentSource());
        Assert.Null(model.Security);
        Assert.False(model.Sections.OfType<PairingsViewModel>().Single().CanGrant);
    }

    private sealed class FakeSecuritySource : FakeEnvironmentSource, ISecuritySource
    {
        private bool enrolled;

        public Exception? EnrollThrows { get; set; }

        public Exception? GrantThrows { get; set; }

        public (string Peer, bool Monitoring, bool Pairing, bool Update)? LastGrant { get; private set; }

        public Task<LocalPresenceStatusDto> GetLocalPresenceStatusAsync(CancellationToken ct = default) => Task.FromResult(Status());

        public Task<LocalPresenceStatusDto> EnrollLocalPresenceAsync(CancellationToken ct = default)
        {
            if (EnrollThrows is { } failure)
            {
                throw failure;
            }

            enrolled = true;
            return Task.FromResult(Status());
        }

        public Task<LocalPresenceStatusDto> RemoveLocalPresenceAsync(CancellationToken ct = default)
        {
            enrolled = false;
            return Task.FromResult(Status());
        }

        public Task SetControlGrantAsync(string peerId, bool monitoringControls, bool pairingApproval, bool updateApproval, CancellationToken ct = default)
        {
            if (GrantThrows is { } failure)
            {
                throw failure;
            }

            LastGrant = (peerId, monitoringControls, pairingApproval, updateApproval);
            return Task.CompletedTask;
        }

        public Task ApprovePhonePresenceEnrollmentAsync(string enrollmentId, CancellationToken ct = default) => Task.CompletedTask;

        private LocalPresenceStatusDto Status() =>
            new("Required", enrolled, enrolled, enrolled ? "ABCD" : null, enrolled ? DateTimeOffset.UtcNow : null, 60);
    }
}
