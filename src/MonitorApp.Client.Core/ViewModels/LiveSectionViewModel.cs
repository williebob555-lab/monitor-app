using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>
/// A section that stays current while it is on screen: re-reads on each push from a source with live updates,
/// otherwise polls. While live it also declares viewing interest (when the source supports it) so the viewed
/// devices sample at their fast interval; interest is withdrawn when the loop ends.
/// </summary>
public abstract partial class LiveSectionViewModel(NodeConnection connection, string title)
    : SectionViewModel(connection, title)
{
    [ObservableProperty]
    private string updateMode = "Manual refresh";

    /// <summary>Runs until cancelled. Callers run it while the view is visible.</summary>
    public async Task RunLiveAsync(TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        var source = Client;
        IDisposable? viewing = null;
        try
        {
            viewing = await BeginViewingAsync(source, cancellationToken);
            if (source.SupportsLiveUpdates)
            {
                var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                });
                void OnChanged(object? sender, EventArgs args) => signal.Writer.TryWrite(true);
                source.StateChanged += OnChanged;
                try
                {
                    UpdateMode = "Live";
                    await RefreshAsync(cancellationToken);
                    while (await signal.Reader.WaitToReadAsync(cancellationToken))
                    {
                        signal.Reader.TryRead(out _);
                        await RefreshAsync(cancellationToken);
                    }
                }
                finally
                {
                    source.StateChanged -= OnChanged;
                }
            }
            else
            {
                UpdateMode = $"Polling every {pollInterval.TotalSeconds:0.#}s";
                await RefreshAsync(cancellationToken);
                using var timer = new PeriodicTimer(pollInterval);
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    await RefreshAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop when the view is hidden or the window closes.
        }
        finally
        {
            viewing?.Dispose();
            UpdateMode = "Manual refresh";
        }
    }

    private static async Task<IDisposable?> BeginViewingAsync(IPeerDataSource source, CancellationToken cancellationToken)
    {
        if (source is not IViewingSource viewingSource)
        {
            return null;
        }

        var scopes = new List<IDisposable> { viewingSource.BeginViewing(null) };
        try
        {
            foreach (var peer in await source.GetPeersAsync(cancellationToken))
            {
                if (string.Equals(peer.TrustState, "Active", StringComparison.Ordinal))
                {
                    scopes.Add(viewingSource.BeginViewing(peer.PeerId));
                }
            }
        }
        catch (LocalApiException)
        {
            // Viewing interest is an optimization; the view still works at the normal sampling interval.
        }

        return new CompositeDisposable(scopes);
    }

    private sealed class CompositeDisposable(List<IDisposable> items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
            {
                item.Dispose();
            }
        }
    }
}
