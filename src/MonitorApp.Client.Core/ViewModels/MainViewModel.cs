using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitorApp.Client.Core.ViewModels.Shell;

namespace MonitorApp.Client.Core.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly NodeConnection connection;

    [ObservableProperty]
    private string addressText;

    [ObservableProperty]
    private string? addressError;

    [ObservableProperty]
    private SectionViewModel selectedSection;

    [ObservableProperty]
    private string connectionStatus = "Not connected yet.";

    public MainViewModel(Uri? address = null, Func<Uri, IPeerDataSource>? clientFactory = null)
        : this(new NodeConnection(address ?? LocalApiClient.DefaultAddress, clientFactory))
    {
    }

    /// <summary>For heads without an address, such as a mobile app over its in-process peer.</summary>
    public MainViewModel(IPeerDataSource source)
        : this(new NodeConnection(source))
    {
    }

    private MainViewModel(NodeConnection connection)
    {
        this.connection = connection;
        addressText = connection.HasAddress ? connection.Source.Description : string.Empty;
        Overview = new OverviewViewModel(connection);
        Devices = new DevicesViewModel(connection);
        Environment = new EnvironmentViewModel(connection);
        var state = new StateViewModel(connection);
        var history = new HistoryViewModel(connection);
        var asOf = new AsOfViewModel(connection);
        var sections = new List<SectionViewModel>
        {
            Environment,
            Devices,
            new AlertsViewModel(connection),
            state,
            history,
            asOf,
            // History mode's home ("What happened"); the three tools above stay reachable from its "Browse history".
            new ActivityViewModel(connection, [state, history, asOf]),
            new PairingsViewModel(connection),
        };
        // Only sources that manage this computer's fingerprint get the security section and the banner.
        if (connection.Source is ISecuritySource)
        {
            Security = new SecurityViewModel(connection);
            sections.Add(Security);
        }

        // Only sources that can send controls (the phone) can set this device's alert preferences.
        if (connection.Source is IControlSource)
        {
            sections.Add(new AlertPreferencesViewModel(connection));
        }

        // Only sources that can initiate pairing (the phone) get the pairing-entry screen.
        if (connection.Source is IPairingInitiator)
        {
            var pair = new PairDeviceViewModel(connection);
            pair.Paired += (_, _) => _ = Task.WhenAll(Overview.RefreshAsync(), Environment.RefreshAsync());
            sections.Add(pair);
        }

        sections.Add(Overview);
        Sections = sections;
        selectedSection = Environment;
        Shell = new ShellViewModel(this, connection, Environment);
    }

    public IReadOnlyList<SectionViewModel> Sections { get; }

    public OverviewViewModel Overview { get; }

    /// <summary>This computer's fingerprint status and banner; null when the source cannot manage it.</summary>
    public SecurityViewModel? Security { get; }

    /// <summary>The ecosystem workspace; the default section.</summary>
    public EnvironmentViewModel Environment { get; }

    /// <summary>The device list (command-center view), reachable from Settings.</summary>
    public DevicesViewModel Devices { get; }

    /// <summary>Workspace shell state: mode, panels, breadcrumbs, search, inspector, timeline.</summary>
    public ShellViewModel Shell { get; }

    /// <summary>The sections a compact (phone) layout keeps one tap away in its bottom bar; the rest are in the menu.</summary>
    public IReadOnlyList<SectionViewModel> PrimarySections => Sections.Take(4).ToArray();

    /// <summary>True when the user can choose a node address (HTTP heads); false for fixed in-process sources.</summary>
    public bool ShowsAddress => connection.HasAddress;

    public StateViewModel State => Sections.OfType<StateViewModel>().Single();

    /// <summary>Applies the typed address and loads the overview, which doubles as the connection check.</summary>
    [RelayCommand]
    public async Task ConnectAsync()
    {
        if (!connection.HasAddress)
        {
            await Overview.RefreshAsync();
            if (Security is not null)
            {
                await Security.RefreshAsync();
            }

            ConnectionStatus = Overview.ErrorMessage is null
                ? $"Connected to {connection.Source.Description}."
                : $"Not connected: {Overview.ErrorMessage}";
            return;
        }

        if (!TryParseAddress(AddressText, out var address))
        {
            AddressError = "Enter a full http or https address, for example http://127.0.0.1:55432.";
            return;
        }

        AddressError = null;
        connection.Connect(address);
        ConnectionStatus = $"Connecting to {address}...";
        await Overview.RefreshAsync();
        if (Security is not null)
        {
            await Security.RefreshAsync();
        }

        ConnectionStatus = Overview.ErrorMessage is null
            ? $"Connected to {address}."
            : $"Not connected: {Overview.ErrorMessage}";
        if (!ReferenceEquals(SelectedSection, Overview) && Overview.ErrorMessage is null)
        {
            await SelectedSection.RefreshAsync();
        }
    }

    partial void OnSelectedSectionChanged(SectionViewModel value) => _ = value.RefreshAsync();

    public void Dispose() => connection.Dispose();

    private static bool TryParseAddress(string? text, out Uri address)
    {
        if (Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https"
            && !string.IsNullOrEmpty(parsed.Host))
        {
            address = parsed;
            return true;
        }

        address = LocalApiClient.DefaultAddress;
        return false;
    }
}
