using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels.Shell;

/// <summary>
/// The bottom timeline strip (UI spec section 10): a scrubber over the last <see cref="WindowHours"/> hours that moves the
/// environment to a past moment and back to live. Scrubbing previews the time; the move is committed on release.
/// </summary>
public sealed partial class TimelineViewModel : ObservableObject
{
    public const double ScrubMaximum = 1000;

    private readonly EnvironmentViewModel environment;
    private readonly TimeProvider timeProvider;
    private static readonly TimeSpan EventRefreshInterval = TimeSpan.FromSeconds(30);
    private DateTimeOffset lastEventsLoad = DateTimeOffset.MinValue;
    private bool syncing;
    private double committedPosition = ScrubMaximum;

    [ObservableProperty]
    private double windowHours = 24;

    [ObservableProperty]
    private double scrubPosition = ScrubMaximum;

    [ObservableProperty]
    private string previewText = "Now";

    public TimelineViewModel(EnvironmentViewModel environment, TimeProvider? timeProvider = null)
    {
        this.environment = environment;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        environment.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EnvironmentViewModel.AsOf))
            {
                SyncFromEnvironment();
            }
            else if (args.PropertyName == nameof(EnvironmentViewModel.Scene)
                && this.timeProvider.GetUtcNow() - lastEventsLoad > EventRefreshInterval)
            {
                lastEventsLoad = this.timeProvider.GetUtcNow();
                _ = RefreshEventsAsync();
            }
        };
    }

    /// <summary>Events inside the window, oldest first, with their 0..1 position along the strip.</summary>
    public ObservableCollection<TimelineMarker> Markers { get; } = [];

    public IReadOnlyList<TimelineEvent> Events => Markers.Select(m => m.Event).ToList();

    /// <summary>Re-reads events for the current window from the data source.</summary>
    [RelayCommand]
    public async Task RefreshEventsAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var from = now - TimeSpan.FromHours(WindowHours);
        var events = await TimelineEventSource.LoadAsync(environment.DataSource, from, now, cancellationToken);
        SetEvents(events);
    }

    /// <summary>Places already-derived events on the strip (events outside the window are dropped).</summary>
    public void SetEvents(IEnumerable<TimelineEvent> events)
    {
        var now = timeProvider.GetUtcNow();
        var span = TimeSpan.FromHours(WindowHours);
        Markers.Clear();
        foreach (var e in events.OrderBy(e => e.Time))
        {
            var fraction = 1 - (now - e.Time) / span;
            if (fraction is >= 0 and <= 1)
            {
                Markers.Add(new TimelineMarker(e, fraction));
            }
        }

        OnPropertyChanged(nameof(Events));
    }

    /// <summary>Scrubs to the event's time and selects the node it is about.</summary>
    [RelayCommand]
    public async Task JumpToAsync(TimelineEvent? timelineEvent)
    {
        if (timelineEvent is null)
        {
            return;
        }

        await environment.GoToAsync(timelineEvent.Time);
        if (timelineEvent.SubjectNodeId is { } id && environment.Scene.Find(id) is not null)
        {
            environment.SelectedNodeId = id;
        }
    }

    public bool IsHistorical => environment.IsHistorical;

    public string RangeText => $"Last {WindowHours.ToString("0.#", CultureInfo.InvariantCulture)} h";

    partial void OnWindowHoursChanged(double value)
    {
        OnPropertyChanged(nameof(RangeText));
        SyncFromEnvironment();
        SetEvents(Markers.Select(m => m.Event).ToList());
    }

    partial void OnScrubPositionChanged(double value)
    {
        if (!syncing)
        {
            PreviewText = Describe(TimeAt(value));
        }
    }

    /// <summary>The moment a scrub position stands for: the right end is now, the left end is <see cref="WindowHours"/> ago.</summary>
    public DateTimeOffset TimeAt(double position)
    {
        var fraction = Math.Clamp(position / ScrubMaximum, 0, 1);
        var now = timeProvider.GetUtcNow();
        return now - TimeSpan.FromHours(WindowHours * (1 - fraction));
    }

    /// <summary>Moves the environment to the previewed time. Reaching the right end means live, not "recorded now".</summary>
    [RelayCommand]
    public async Task CommitAsync()
    {
        // A drag ends with several events (thumb, pointer, key); only the first one has anything to commit.
        if (Math.Abs(ScrubPosition - committedPosition) < 0.001)
        {
            return;
        }

        committedPosition = ScrubPosition;
        if (ScrubPosition >= ScrubMaximum)
        {
            await environment.ReturnToNowAsync();
            return;
        }

        await environment.GoToAsync(TimeAt(ScrubPosition));
    }

    /// <summary>Steps the examined moment by <paramref name="delta"/>, starting from now when live. Never past now.</summary>
    public async Task StepAsync(TimeSpan delta)
    {
        var now = timeProvider.GetUtcNow();
        var target = (environment.AsOf ?? now) + delta;
        if (target >= now)
        {
            await environment.ReturnToNowAsync();
            return;
        }

        var earliest = now - TimeSpan.FromHours(WindowHours);
        await environment.GoToAsync(target < earliest ? earliest : target);
    }

    [RelayCommand]
    public Task StepBackAsync() => StepAsync(TimeSpan.FromHours(-1));

    [RelayCommand]
    public Task StepForwardAsync() => StepAsync(TimeSpan.FromHours(1));

    [RelayCommand]
    public Task ReturnToLiveAsync() => environment.ReturnToNowAsync();

    private void SyncFromEnvironment()
    {
        OnPropertyChanged(nameof(IsHistorical));
        syncing = true;
        try
        {
            if (environment.AsOf is { } asOf)
            {
                var span = TimeSpan.FromHours(WindowHours);
                var fraction = 1 - (timeProvider.GetUtcNow() - asOf) / span;
                ScrubPosition = Math.Clamp(fraction, 0, 1) * ScrubMaximum;
                PreviewText = Describe(asOf);
            }
            else
            {
                ScrubPosition = ScrubMaximum;
                PreviewText = "Now";
            }
        }
        finally
        {
            syncing = false;
        }

        committedPosition = ScrubPosition;
    }

    private string Describe(DateTimeOffset time)
    {
        var now = timeProvider.GetUtcNow();
        if (now - time < TimeSpan.FromSeconds(30))
        {
            return "Now";
        }

        var local = time.ToLocalTime();
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            + " (" + InspectorViewModel.Ago(now - time) + ")";
    }
}
