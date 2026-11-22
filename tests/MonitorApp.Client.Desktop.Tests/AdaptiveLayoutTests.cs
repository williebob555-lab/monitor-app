using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MonitorApp.Client.Core;
using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Client.Ui.Views;
using Xunit;

namespace MonitorApp.Client.Desktop.Tests;

/// <summary>The shared shell at phone and desktop widths: right layout, every section reachable and rendered.</summary>
public sealed class AdaptiveLayoutTests
{
    private static readonly Dictionary<Type, Type> ViewFor = new()
    {
        [typeof(DevicesViewModel)] = typeof(DevicesView),
        [typeof(AlertsViewModel)] = typeof(AlertsView),
        [typeof(StateViewModel)] = typeof(StateView),
        [typeof(HistoryViewModel)] = typeof(HistoryView),
        [typeof(AsOfViewModel)] = typeof(AsOfView),
        [typeof(PairingsViewModel)] = typeof(PairingsView),
        [typeof(PairDeviceViewModel)] = typeof(PairDeviceView),
        [typeof(OverviewViewModel)] = typeof(OverviewView),
    };

    [AvaloniaFact]
    public async Task PhoneWidthUsesBottomBarAndMenuAndEverySectionIsReachable()
    {
        using var model = new MainViewModel(new FakePairingSource());
        var (window, view) = Show(model, 400, 800);
        await model.ConnectAsync();
        Pump();

        Assert.True(view.IsCompact);
        Assert.True(Find<Button>(view, "MenuButton").IsVisible);
        Assert.True(Find<Grid>(view, "BottomBar").IsVisible);
        var split = Find<SplitView>(view, "Split");
        Assert.Equal(SplitViewDisplayMode.Overlay, split.DisplayMode);
        Assert.False(split.IsPaneOpen);
        Assert.Contains(model.Sections, section => section is PairDeviceViewModel);

        var primary = Find<ListBox>(view, "PrimaryList");
        Assert.Equal(model.PrimarySections.Count, primary.ItemCount);
        var nav = Find<ListBox>(view, "NavList");
        foreach (var section in model.Sections)
        {
            // Navigate the way a finger does: choose the entry in the menu overlay.
            Find<Button>(view, "MenuButton").Command?.Execute(null);
            split.IsPaneOpen = true;
            var changed = !ReferenceEquals(model.SelectedSection, section);
            nav.SelectedItem = section;
            Pump();
            Assert.Same(section, model.SelectedSection);
            Assert.True(!changed || !split.IsPaneOpen, "Choosing a new section closes the overlay.");
            AssertRendered(view, section);
        }

        Assert.Equal(model.Sections.Count, nav.ItemCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DesktopWidthKeepsSideNavigationOpenAndEverySectionRenders()
    {
        using var model = new MainViewModel(new FakeSource());
        var (window, view) = Show(model, 1100, 760);
        await model.ConnectAsync();
        Pump();

        Assert.False(view.IsCompact);
        Assert.False(Find<Button>(view, "MenuButton").IsVisible);
        Assert.False(Find<Grid>(view, "BottomBar").IsVisible);
        var split = Find<SplitView>(view, "Split");
        Assert.Equal(SplitViewDisplayMode.Inline, split.DisplayMode);
        Assert.True(split.IsPaneOpen);
        Assert.DoesNotContain(model.Sections, section => section is PairDeviceViewModel);

        foreach (var section in model.Sections)
        {
            model.SelectedSection = section;
            Pump();
            AssertRendered(view, section);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void ResizingBetweenWidthsSwitchesLayout()
    {
        using var model = new MainViewModel(new FakeSource());
        var (window, view) = Show(model, 1100, 760);
        Assert.False(view.IsCompact);
        window.Width = 400;
        Pump();
        Assert.True(view.IsCompact);
        window.Width = 1000;
        Pump();
        Assert.False(view.IsCompact);
        window.Close();
    }

    private static (Window Window, MainView View) Show(MainViewModel model, double width, double height)
    {
        var view = new MainView();
        var window = new Window { Width = width, Height = height, Content = view, DataContext = model };
        window.Show();
        Pump();
        return (window, view);
    }

    private static void AssertRendered(MainView view, SectionViewModel section)
    {
        Pump();
        var expected = ViewFor[section.GetType()];
        Assert.Contains(view.GetVisualDescendants(), visual => visual.GetType() == expected);
    }

    private static T Find<T>(MainView view, string name)
        where T : Control =>
        view.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Minimal in-memory sources so these tests need no node; only the phone-like one can start pairing.</summary>
    private class FakeSource : IPeerDataSource, IAlertSource
    {
        public string Description => "fake";

        public bool SupportsLiveUpdates => false;

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public Task<RuntimeDto> GetRuntimeAsync(CancellationToken ct = default) =>
            Task.FromResult(new RuntimeDto("Running", "test", new LocalPeerDto("peer-fake", "AB"), 0, 0));

        public Task<IReadOnlyList<TrustedPeerDto>> GetPeersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TrustedPeerDto>>([]);

        public Task<IReadOnlyList<PendingPairingDto>> GetPairingsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PendingPairingDto>>([]);

        public Task<CatalogDto> GetCatalogAsync(CancellationToken ct = default) =>
            Task.FromResult(new CatalogDto([], []));

        public Task<StateDto> GetStateAsync(
            DateTimeOffset? asOf = null, int? lookbackSeconds = null, string? subjectEntityId = null,
            string? signalId = null, CancellationToken ct = default) =>
            Task.FromResult(new StateDto(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(-1), 1000, "raw", false, []));

        public Task<HistoryResult> GetHistoryAsync(
            HistoryQuery query, int maxPages = 20, int pageSize = 1000, CancellationToken ct = default) =>
            Task.FromResult(new HistoryResult([], "raw", 1, false));

        public Task ApprovePairingAsync(string pairingId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RejectPairingAsync(string pairingId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RevokePeerAsync(string peerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AlertDto>> GetAlertsAsync(int limit = 100, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AlertDto>>([]);
    }

    private sealed class FakePairingSource : FakeSource, IPairingInitiator
    {
        public Task<string> PairWithCodeAsync(string address, string code, CancellationToken ct = default) =>
            Task.FromResult("peer-desktop");

        public Task<string> PairWithInvitationAsync(string invitation, CancellationToken ct = default) =>
            Task.FromResult("peer-desktop");
    }
}
