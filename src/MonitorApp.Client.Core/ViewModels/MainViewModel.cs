using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
        // Further views (for example Alerts) are added here and shown by the shell without other changes.
        Sections =
        [
            Overview,
            new StateViewModel(connection),
            new HistoryViewModel(connection),
            new AsOfViewModel(connection),
            new PairingsViewModel(connection),
        ];
        selectedSection = Overview;
    }

    public IReadOnlyList<SectionViewModel> Sections { get; }

    public OverviewViewModel Overview { get; }

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
