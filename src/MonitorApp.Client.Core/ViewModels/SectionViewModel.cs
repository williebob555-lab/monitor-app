using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>
/// The data source the client talks to. View models read <see cref="Source"/> at call time so changing the
/// address re-points every view without rebuilding them.
/// </summary>
public sealed class NodeConnection : IDisposable
{
    private readonly Func<Uri, IPeerDataSource>? factory;

    /// <summary>An address-based (HTTP) connection that can be re-pointed with <see cref="Connect"/>.</summary>
    public NodeConnection(Uri address, Func<Uri, IPeerDataSource>? factory = null)
    {
        this.factory = factory ?? (uri => LocalApiClient.Create(uri));
        Source = this.factory(address);
    }

    /// <summary>A fixed source such as the in-process mobile runtime.</summary>
    public NodeConnection(IPeerDataSource source)
    {
        Source = source;
    }

    public IPeerDataSource Source { get; private set; }

    public bool HasAddress => factory is not null;

    public void Connect(Uri address)
    {
        var factory = this.factory ?? throw new InvalidOperationException("This connection has no address.");
        var previous = Source;
        Source = factory(address);
        (previous as IDisposable)?.Dispose();
    }

    public void Dispose() => (Source as IDisposable)?.Dispose();
}

/// <summary>Starts this computer's Monitor App service when it is not running (Windows asks the user to allow it).</summary>
public interface INodeServiceStarter
{
    /// <summary>
    /// Asks to start the service and waits until Windows reports it running. False when the request was declined or the
    /// service did not reach running within about a minute.
    /// </summary>
    Task<bool> StartAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A top-level view of the client (Overview, State, ...). Adding a view means adding one subclass and one
/// entry in <see cref="MainViewModel.Sections"/>; the desktop shell binds sections generically.
/// </summary>
public abstract partial class SectionViewModel(NodeConnection connection, string title) : ObservableObject
{
    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeedback), nameof(HasError), nameof(HasStatusOnly))]
    private string? errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeedback), nameof(HasStatusOnly))]
    private string? statusMessage;

    /// <summary>Set by the desktop app on Windows; null where the window cannot start the service.</summary>
    public static INodeServiceStarter? NodeStarter { get; set; }

    /// <summary>The last call found nothing listening on this computer: the Monitor App service is not running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartNode))]
    private bool nodeDown;

    public bool CanStartNode => NodeDown && NodeStarter is not null;

    [RelayCommand]
    private async Task StartNodeAsync()
    {
        if (NodeStarter is not { } starter)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = "Starting the Monitor App service… Windows may ask you to allow it.";
        if (!await starter.StartAsync())
        {
            StatusMessage = null;
            ErrorMessage = "The Monitor App service did not start. Start \"Monitor App peer node\" in Windows Services, or try again.";
            return;
        }

        NodeDown = false;
        StatusMessage = "The Monitor App service is running.";
        await RefreshAsync();
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasStatusOnly => !HasError && !string.IsNullOrEmpty(StatusMessage);

    /// <summary>True while there is a result or an error the shell's feedback bar should show, wherever the page is scrolled.</summary>
    public bool HasFeedback => HasError || HasStatusOnly;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void DismissFeedback()
    {
        ErrorMessage = null;
        StatusMessage = null;
    }

    public string Title { get; } = title;

    protected NodeConnection Connection { get; } = connection;

    protected IPeerDataSource Client => Connection.Source;

    /// <summary>Reloads this view from the node. Never throws for API failures; they land in <see cref="ErrorMessage"/>.</summary>
    public abstract Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs <paramref name="work"/>, mapping API failures to a visible message. Returns true on success.</summary>
    protected async Task<bool> RunAsync(Func<Task> work)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await work();
            NodeDown = false;
            return true;
        }
        catch (NodeUnreachableException)
        {
            NodeDown = true;
            ErrorMessage = NodeStarter is null
                ? "The Monitor App service on this computer isn't running, so nothing is recorded or shared."
                : "The Monitor App service on this computer isn't running, so nothing is recorded or shared. Start it below.";
            return false;
        }
        catch (LocalApiException exception)
        {
            NodeDown = false;
            ErrorMessage = exception.Message;
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
